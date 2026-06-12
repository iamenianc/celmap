namespace CelMap.Core;

public static class SheetDataExtensions
{
    /// <summary>True if every cell below the header row in the given column is empty.</summary>
    public static bool ColumnIsEmpty(this SheetData sheet, int columnIndex, int headerRow)
    {
        for (int r = headerRow + 1; r < sheet.RowCount; r++)
        {
            if (!sheet.GetCell(r, columnIndex).IsEmpty)
                return false;
        }
        return true;
    }
}
