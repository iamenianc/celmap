using System.Text;

namespace CelMap.Core.Crypto;

/// <summary>
/// Minimal reader for the OLE2 / Compound File Binary (MS-CFB) container that an encrypted
/// Office document is wrapped in. Only does what decryption needs: locate and read named
/// top-level streams (we need "EncryptionInfo" and "EncryptedPackage"). Not a general CFB
/// implementation — no storages/sub-directories beyond the root, no writing.
/// </summary>
internal sealed class CompoundFile
{
    private const int SectorShiftDefault = 9;          // 512-byte sectors (CFB v3)
    private const uint EndOfChain = 0xFFFFFFFE;
    private const uint FreeSector = 0xFFFFFFFF;
    private const uint FatSector = 0xFFFFFFFD;
    private const uint DifatSector = 0xFFFFFFFC;

    private readonly byte[] _data;
    private readonly int _sectorSize;
    private readonly int _miniSectorSize;
    private readonly uint[] _fat;
    private readonly uint[] _miniFat;
    private readonly List<DirEntry> _entries = new();
    private readonly byte[] _miniStream;

    private CompoundFile(byte[] data)
    {
        _data = data;

        int sectorShift = ReadU16(30);
        int miniSectorShift = ReadU16(32);
        _sectorSize = 1 << (sectorShift == 0 ? SectorShiftDefault : sectorShift);
        _miniSectorSize = 1 << (miniSectorShift == 0 ? 6 : miniSectorShift);

        uint numFatSectors = ReadU32(44);
        uint firstDirSector = ReadU32(48);
        uint firstMiniFatSector = ReadU32(60);
        uint numMiniFatSectors = ReadU32(64);
        uint firstDifatSector = ReadU32(68);
        uint numDifatSectors = ReadU32(72);

        // Build the DIFAT (list of FAT sector locations): first 109 entries live in the header.
        var fatSectorLocations = new List<uint>();
        for (int i = 0; i < 109; i++)
        {
            uint loc = ReadU32(76 + i * 4);
            if (loc == FreeSector || loc == EndOfChain) break;
            fatSectorLocations.Add(loc);
        }
        // Remaining DIFAT sectors (rare for our small files), chained.
        uint difatSec = firstDifatSector;
        for (uint n = 0; n < numDifatSectors && difatSec != EndOfChain && difatSec != FreeSector; n++)
        {
            int baseOff = SectorOffset(difatSec);
            int entriesPerSector = _sectorSize / 4 - 1;
            for (int i = 0; i < entriesPerSector; i++)
            {
                uint loc = ReadU32(baseOff + i * 4);
                if (loc != FreeSector && loc != EndOfChain) fatSectorLocations.Add(loc);
            }
            difatSec = ReadU32(baseOff + entriesPerSector * 4);
        }

        // Read the FAT.
        var fat = new List<uint>();
        foreach (uint sec in fatSectorLocations)
        {
            int off = SectorOffset(sec);
            for (int i = 0; i < _sectorSize / 4; i++)
                fat.Add(ReadU32(off + i * 4));
        }
        _fat = fat.ToArray();

        // Read the directory chain into entries.
        foreach (int dirOff in ReadChainOffsets(firstDirSector))
        {
            for (int e = 0; e + 128 <= _sectorSize; e += 128)
                _entries.Add(DirEntry.Parse(_data, dirOff + e));
        }

        // The mini-FAT and the mini-stream (root entry's stream holds all mini-sector data).
        var miniFat = new List<uint>();
        uint mfSec = firstMiniFatSector;
        for (uint n = 0; n < numMiniFatSectors && mfSec != EndOfChain && mfSec != FreeSector; n++)
        {
            int off = SectorOffset(mfSec);
            for (int i = 0; i < _sectorSize / 4; i++)
                miniFat.Add(ReadU32(off + i * 4));
            mfSec = _fat[mfSec];
        }
        _miniFat = miniFat.ToArray();

        var root = _entries[0];   // root storage entry; its "stream" is the mini-stream container
        _miniStream = ReadSectorChain(root.StartSector, (long)root.Size);
    }

    public static CompoundFile Open(byte[] data) => new(data);

    /// <summary>Read a named top-level stream's bytes, or null if not present.</summary>
    public byte[]? ReadStream(string name)
    {
        DirEntry? entry = _entries.FirstOrDefault(
            e => e.Type == 2 && string.Equals(e.Name, name, StringComparison.Ordinal));
        if (entry is null) return null;

        // Small streams live in the mini-stream addressed by the mini-FAT; large ones in the FAT.
        if (entry.Size < 4096)
            return ReadMiniChain(entry.StartSector, (long)entry.Size);
        return ReadSectorChain(entry.StartSector, (long)entry.Size);
    }

    private IEnumerable<int> ReadChainOffsets(uint startSector)
    {
        uint sec = startSector;
        var seen = new HashSet<uint>();
        while (sec != EndOfChain && sec != FreeSector && sec < (uint)_fat.Length && seen.Add(sec))
        {
            yield return SectorOffset(sec);
            sec = _fat[sec];
        }
    }

    private byte[] ReadSectorChain(uint startSector, long size)
    {
        using var ms = new MemoryStream();
        foreach (int off in ReadChainOffsets(startSector))
            ms.Write(_data, off, Math.Min(_sectorSize, _data.Length - off));
        return Trim(ms.ToArray(), size);
    }

    private byte[] ReadMiniChain(uint startMiniSector, long size)
    {
        using var ms = new MemoryStream();
        uint sec = startMiniSector;
        var seen = new HashSet<uint>();
        while (sec != EndOfChain && sec != FreeSector && sec < (uint)_miniFat.Length && seen.Add(sec))
        {
            int off = (int)sec * _miniSectorSize;
            if (off + _miniSectorSize <= _miniStream.Length)
                ms.Write(_miniStream, off, _miniSectorSize);
            sec = _miniFat[sec];
        }
        return Trim(ms.ToArray(), size);
    }

    private static byte[] Trim(byte[] buffer, long size) =>
        buffer.Length <= size ? buffer : buffer[..(int)size];

    private int SectorOffset(uint sector) => (int)((sector + 1) * _sectorSize);

    private int ReadU16(int offset) => _data[offset] | (_data[offset + 1] << 8);
    private uint ReadU32(int offset) =>
        (uint)(_data[offset] | (_data[offset + 1] << 8) | (_data[offset + 2] << 16) | (_data[offset + 3] << 24));

    private sealed record DirEntry(string Name, byte Type, uint StartSector, ulong Size)
    {
        public static DirEntry Parse(byte[] data, int offset)
        {
            int nameLen = data[offset + 64] | (data[offset + 65] << 8);   // bytes, incl. terminator
            int chars = Math.Max(0, nameLen / 2 - 1);
            string name = Encoding.Unicode.GetString(data, offset, chars * 2);
            byte type = data[offset + 66];   // 1=storage, 2=stream, 5=root
            uint start = (uint)(data[offset + 116] | (data[offset + 117] << 8)
                | (data[offset + 118] << 16) | (data[offset + 119] << 24));
            ulong size = System.BitConverter.ToUInt64(data, offset + 120);
            return new DirEntry(name, type, start, size);
        }
    }
}
