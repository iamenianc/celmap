namespace CelMap.Core;

/// <summary>In-memory representation of one worksheet.</summary>
public sealed class SheetData
{
    public string SheetName { get; }
    // [row][col] — both zero-based
    public CellValue[][] Cells { get; }
    public int RowCount => Cells.Length;
    public int ColCount => Cells.Length > 0 ? Cells[0].Length : 0;

    public SheetData(string sheetName, CellValue[][] cells)
    {
        SheetName = sheetName;
        Cells = cells;
    }

    public CellValue GetCell(int row, int col)
    {
        if (row < 0 || row >= RowCount || col < 0 || col >= ColCount)
            return CellValue.Empty;
        return Cells[row][col];
    }
}
