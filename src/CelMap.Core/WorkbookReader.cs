using ClosedXML.Excel;
using CelMap.Core.Crypto;

namespace CelMap.Core;

public sealed class WorkbookReader : IWorkbookReader
{
    public bool IsEncrypted(string filePath)
    {
        // Read just the 8-byte OLE/CFB signature rather than the whole file.
        using var fs = File.OpenRead(filePath);
        Span<byte> head = stackalloc byte[8];
        int read = fs.Read(head);
        return read == 8 && OfficeCrypto.IsEncrypted(head.ToArray());
    }

    public IReadOnlyList<string> GetSheetNames(string filePath, string? password = null)
    {
        using var wb = OpenWorkbook(filePath, password);
        return wb.Worksheets.Select(ws => ws.Name).ToList();
    }

    public SheetData ReadSheet(string filePath, string sheetName, string? password = null)
    {
        using var wb = OpenWorkbook(filePath, password);
        var ws = wb.Worksheet(sheetName);

        if (ws.RangeUsed() is not { } used)
            return new SheetData(sheetName, Array.Empty<CellValue[]>());

        int firstRow = used.FirstRow().RowNumber();
        int lastRow  = used.LastRow().RowNumber();
        int firstCol = used.FirstColumn().ColumnNumber();
        int lastCol  = used.LastColumn().ColumnNumber();

        int rowCount = lastRow - firstRow + 1;
        int colCount = lastCol - firstCol + 1;

        var cells = new CellValue[rowCount][];
        for (int r = 0; r < rowCount; r++)
        {
            cells[r] = new CellValue[colCount];
            for (int c = 0; c < colCount; c++)
            {
                var cell = ws.Cell(firstRow + r, firstCol + c);
                cells[r][c] = ReadCell(cell);
            }
        }

        return new SheetData(sheetName, cells);
    }

    /// <summary>Open a workbook, transparently decrypting an encrypted (password-protected) file
    /// when a password is supplied. A plain .xlsx opens directly; an encrypted file without a
    /// password throws <see cref="InvalidPasswordException"/> so callers can prompt.</summary>
    private static XLWorkbook OpenWorkbook(string filePath, string? password)
    {
        byte[] raw = File.ReadAllBytes(filePath);
        if (!OfficeCrypto.IsEncrypted(raw))
            return new XLWorkbook(new MemoryStream(raw));

        if (string.IsNullOrEmpty(password))
            throw new InvalidPasswordException();   // encrypted, but no password given

        byte[] decrypted = OfficeCrypto.Decrypt(raw, password);
        return new XLWorkbook(new MemoryStream(decrypted));
    }

    private static CellValue ReadCell(IXLCell cell)
    {
        if (cell.IsEmpty())
            return CellValue.Empty;

        return cell.DataType switch
        {
            XLDataType.Text     => CellValue.FromText(cell.GetString()),
            XLDataType.Number   => CellValue.FromNumber(cell.GetDouble()),
            XLDataType.Boolean  => CellValue.FromBoolean(cell.GetBoolean()),
            XLDataType.DateTime => CellValue.FromDateTime(cell.GetDateTime()),
            XLDataType.Error    => CellValue.FromError(cell.GetError().ToString()),
            XLDataType.Blank    => CellValue.Empty,
            _                   => CellValue.FromText(cell.GetString()),
        };
    }
}
