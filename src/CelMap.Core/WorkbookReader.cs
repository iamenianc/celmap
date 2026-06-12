using ClosedXML.Excel;

namespace CelMap.Core;

public sealed class WorkbookReader : IWorkbookReader
{
    public IReadOnlyList<string> GetSheetNames(string filePath)
    {
        using var wb = new XLWorkbook(filePath);
        return wb.Worksheets.Select(ws => ws.Name).ToList();
    }

    public SheetData ReadSheet(string filePath, string sheetName)
    {
        using var wb = new XLWorkbook(filePath);
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
