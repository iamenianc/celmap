using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using CelMap.Core;

namespace CelMap.Core.Crypto;

/// <summary>
/// Decrypts a password-protected (ECMA-376) Office workbook to its plain ZIP-based bytes.
/// Implements the MS-OFFCRYPTO password verification + package decryption for the two
/// schemes Excel produces: Agile encryption (AES-CBC, XML descriptor) and Standard
/// encryption (AES-ECB, binary header). Uses only System.Security.Cryptography — no deps.
/// </summary>
public static class OfficeCrypto
{
    /// <summary>True if the bytes are an OLE/CFB compound file (the wrapper around an encrypted
    /// Office doc) rather than a plain ZIP-based .xlsx (which begins with "PK").</summary>
    public static bool IsEncrypted(byte[] data) =>
        data.Length >= 8
        && data[0] == 0xD0 && data[1] == 0xCF && data[2] == 0x11 && data[3] == 0xE0
        && data[4] == 0xA1 && data[5] == 0xB1 && data[6] == 0x1A && data[7] == 0xE1;

    /// <summary>Decrypt the compound-file bytes with the password, returning the inner xlsx
    /// (ZIP) bytes. Throws <see cref="InvalidPasswordException"/> when the password is wrong and
    /// <see cref="NotSupportedException"/> for an encryption scheme we don't handle.</summary>
    public static byte[] Decrypt(byte[] compoundFileBytes, string password)
    {
        var cf = CompoundFile.Open(compoundFileBytes);
        byte[] info = cf.ReadStream("EncryptionInfo")
            ?? throw new NotSupportedException("Encrypted workbook has no EncryptionInfo stream.");
        byte[] package = cf.ReadStream("EncryptedPackage")
            ?? throw new NotSupportedException("Encrypted workbook has no EncryptedPackage stream.");

        ushort versionMajor = (ushort)(info[0] | (info[1] << 8));
        ushort versionMinor = (ushort)(info[2] | (info[3] << 8));

        // Agile encryption is 4.4; Standard is 2.x/3.x/4.2 with the AES flag set.
        if (versionMajor == 4 && versionMinor == 4)
            return AgileDecrypt(info, package, password);
        return StandardDecrypt(info, package, password);
    }

    // ---- Agile encryption (AES-CBC, SHA-based key derivation, XML descriptor) ----

    private static byte[] AgileDecrypt(byte[] info, byte[] package, string password)
    {
        // The XML descriptor starts after the 8-byte version/flags header.
        string xml = Encoding.UTF8.GetString(info, 8, info.Length - 8);
        var doc = XDocument.Parse(xml);
        XNamespace e = "http://schemas.microsoft.com/office/2006/encryption";
        XNamespace p = "http://schemas.microsoft.com/office/2006/keyEncryptor/password";

        var keyData = doc.Root!.Element(e + "keyData")!;
        var encKey = doc.Root!.Descendants(p + "encryptedKey").First();

        int blockSize = (int)keyData.Attribute("blockSize")!;
        int keyBits = (int)encKey.Attribute("keyBits")!;
        int hashSize = (int)encKey.Attribute("hashSize")!;
        int spinCount = (int)encKey.Attribute("spinCount")!;
        string hashAlg = (string)encKey.Attribute("hashAlgorithm")!;

        byte[] salt = Convert.FromBase64String((string)encKey.Attribute("saltValue")!);
        byte[] encVerifierInput = Convert.FromBase64String((string)encKey.Attribute("encryptedVerifierHashInput")!);
        byte[] encVerifierValue = Convert.FromBase64String((string)encKey.Attribute("encryptedVerifierHashValue")!);
        byte[] encKeyValue = Convert.FromBase64String((string)encKey.Attribute("encryptedKeyValue")!);

        // Verify the password using the two block keys defined by MS-OFFCRYPTO.
        byte[] verifierHashInputKey = DeriveKey(password, salt, spinCount, hashAlg, BlockVerifierInput, keyBits / 8);
        byte[] verifierHashValueKey = DeriveKey(password, salt, spinCount, hashAlg, BlockVerifierValue, keyBits / 8);

        byte[] verifierInput = AesCbcDecrypt(encVerifierInput, verifierHashInputKey, salt);
        byte[] verifierValue = AesCbcDecrypt(encVerifierValue, verifierHashValueKey, salt);
        byte[] computed = Hash(hashAlg, verifierInput);
        if (!computed.Take(hashSize).SequenceEqual(verifierValue.Take(hashSize)))
            throw new InvalidPasswordException();

        // Derive the actual package key, then decrypt the package in segments.
        byte[] keyValueKey = DeriveKey(password, salt, spinCount, hashAlg, BlockKeyValue, keyBits / 8);
        byte[] secretKey = AesCbcDecrypt(encKeyValue, keyValueKey, salt);

        byte[] dataSalt = Convert.FromBase64String((string)keyData.Attribute("saltValue")!);
        return AgileDecryptPackage(package, secretKey, dataSalt, hashAlg, blockSize);
    }

    private static byte[] AgileDecryptPackage(byte[] package, byte[] key, byte[] saltValue,
                                              string hashAlg, int blockSize)
    {
        long totalSize = BitConverter.ToInt64(package, 0);   // first 8 bytes = plaintext length
        const int segment = 4096;
        using var ms = new MemoryStream();
        int dataOffset = 8;
        int segmentIndex = 0;
        int remaining = package.Length - dataOffset;

        for (int pos = 0; pos < remaining; pos += segment, segmentIndex++)
        {
            int len = Math.Min(segment, remaining - pos);
            // Per-segment IV = Hash(saltValue || LE(segmentIndex)) truncated to the block size.
            byte[] blockKey = BitConverter.GetBytes(segmentIndex);
            byte[] iv = Hash(hashAlg, Concat(saltValue, blockKey)).Take(blockSize).ToArray();

            var chunk = new byte[len];
            Array.Copy(package, dataOffset + pos, chunk, 0, len);
            byte[] plain = AesCbcDecrypt(chunk, key, iv);
            ms.Write(plain, 0, plain.Length);
        }

        byte[] all = ms.ToArray();
        return all.Length <= totalSize ? all : all[..(int)totalSize];
    }

    // Block keys (MS-OFFCRYPTO §2.3.4.10) used to specialise the derived key per purpose.
    private static readonly byte[] BlockVerifierInput = { 0xFE, 0xA7, 0xD2, 0x76, 0x3B, 0x4B, 0x9E, 0x79 };
    private static readonly byte[] BlockVerifierValue = { 0xD7, 0xAA, 0x0F, 0x6D, 0x30, 0x61, 0x34, 0x4E };
    private static readonly byte[] BlockKeyValue = { 0x14, 0x6E, 0x0B, 0xE7, 0xAB, 0xAC, 0xD0, 0xD6 };

    private static byte[] DeriveKey(string password, byte[] salt, int spinCount, string hashAlg,
                                    byte[] blockKey, int keyLengthBytes)
    {
        // H_0 = Hash(salt || UTF16LE(password)); H_n = Hash(LE(n) || H_{n-1}); final = Hash(H_final || blockKey).
        byte[] pwd = Encoding.Unicode.GetBytes(password);
        byte[] h = Hash(hashAlg, Concat(salt, pwd));
        for (int i = 0; i < spinCount; i++)
            h = Hash(hashAlg, Concat(BitConverter.GetBytes(i), h));
        h = Hash(hashAlg, Concat(h, blockKey));

        // Fit/pad to the required key length.
        if (h.Length >= keyLengthBytes) return h[..keyLengthBytes];
        var key = new byte[keyLengthBytes];
        Array.Fill(key, (byte)0x36);
        Array.Copy(h, key, h.Length);
        return key;
    }

    // ---- Standard encryption (AES-ECB key derivation, binary header) ----

    private static byte[] StandardDecrypt(byte[] info, byte[] package, string password)
    {
        // Header: 8 bytes version/flags, then 4-byte header size, then EncryptionHeader, then
        // EncryptionVerifier. We read the salt/verifier and derive an AES key via the legacy
        // iterated-SHA1 scheme, then decrypt the package with AES-ECB.
        int pos = 8;
        int headerSize = BitConverter.ToInt32(info, pos); pos += 4;
        int headerStart = pos;

        int flags = BitConverter.ToInt32(info, headerStart + 0);
        int algId = BitConverter.ToInt32(info, headerStart + 8);
        int keySize = BitConverter.ToInt32(info, headerStart + 20);   // in bits
        if (keySize == 0) keySize = 128;
        pos = headerStart + headerSize;

        int saltSize = BitConverter.ToInt32(info, pos); pos += 4;
        byte[] salt = info.Skip(pos).Take(saltSize).ToArray(); pos += saltSize;
        byte[] encVerifier = info.Skip(pos).Take(16).ToArray(); pos += 16;
        int verifierHashSize = BitConverter.ToInt32(info, pos); pos += 4;
        byte[] encVerifierHash = info.Skip(pos).Take(32).ToArray();

        byte[] key = DeriveStandardKey(password, salt, keySize / 8);

        byte[] verifier = AesEcbDecrypt(encVerifier, key);
        byte[] verifierHash = AesEcbDecrypt(encVerifierHash, key);
        byte[] computed = SHA1.HashData(verifier);
        if (!computed.Take(20).SequenceEqual(verifierHash.Take(20)))
            throw new InvalidPasswordException();

        long totalSize = BitConverter.ToInt64(package, 0);
        byte[] encData = package.Skip(8).ToArray();
        byte[] plain = AesEcbDecrypt(encData, key);
        return plain.Length <= totalSize ? plain : plain[..(int)totalSize];
    }

    private static byte[] DeriveStandardKey(string password, byte[] salt, int keyLengthBytes)
    {
        // MS-OFFCRYPTO §2.3.4.7: H0 = SHA1(salt || UTF16LE(pwd)); Hn = SHA1(LE(i) || H_{i-1})
        // for 50000 iterations; Hfinal = SHA1(Hn || 0x00000000); key = first bytes of
        // SHA1(Hfinal XOR 0x36-pad block) truncated to the key length.
        byte[] pwd = Encoding.Unicode.GetBytes(password);
        byte[] h = SHA1.HashData(Concat(salt, pwd));
        for (int i = 0; i < 50000; i++)
            h = SHA1.HashData(Concat(BitConverter.GetBytes(i), h));
        h = SHA1.HashData(Concat(h, new byte[4]));   // block 0

        var x1 = new byte[64];
        Array.Fill(x1, (byte)0x36);
        for (int i = 0; i < h.Length; i++) x1[i] ^= h[i];
        byte[] derived = SHA1.HashData(x1);
        if (derived.Length >= keyLengthBytes) return derived[..keyLengthBytes];

        var x2 = new byte[64];
        Array.Fill(x2, (byte)0x5C);
        for (int i = 0; i < h.Length; i++) x2[i] ^= h[i];
        byte[] derived2 = SHA1.HashData(x2);
        return Concat(derived, derived2)[..keyLengthBytes];
    }

    // ---- Primitives ----

    private static byte[] AesCbcDecrypt(byte[] data, byte[] key, byte[] iv)
    {
        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        aes.Key = key;
        aes.IV = iv.Length == 16 ? iv : Pad(iv, 16);
        using var dec = aes.CreateDecryptor();
        int usable = data.Length - (data.Length % 16);
        return dec.TransformFinalBlock(data, 0, usable);
    }

    private static byte[] AesEcbDecrypt(byte[] data, byte[] key)
    {
        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.Key = key;
        using var dec = aes.CreateDecryptor();
        int usable = data.Length - (data.Length % 16);
        return dec.TransformFinalBlock(data, 0, usable);
    }

    private static byte[] Hash(string algorithm, byte[] data) => algorithm.ToUpperInvariant() switch
    {
        "SHA1" or "SHA-1" => SHA1.HashData(data),
        "SHA256" or "SHA-256" => SHA256.HashData(data),
        "SHA384" or "SHA-384" => SHA384.HashData(data),
        "SHA512" or "SHA-512" => SHA512.HashData(data),
        _ => throw new NotSupportedException($"Unsupported hash algorithm '{algorithm}'.")
    };

    private static byte[] Concat(byte[] a, byte[] b)
    {
        var r = new byte[a.Length + b.Length];
        Buffer.BlockCopy(a, 0, r, 0, a.Length);
        Buffer.BlockCopy(b, 0, r, a.Length, b.Length);
        return r;
    }

    private static byte[] Pad(byte[] src, int length)
    {
        var r = new byte[length];
        Array.Copy(src, r, Math.Min(src.Length, length));
        return r;
    }
}
