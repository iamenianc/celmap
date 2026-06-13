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

    /// <summary>How many data rows the given column actually spans: the count from the first
    /// row below the header down to its LAST non-empty cell (interior blanks still count, so a
    /// column with a gap isn't reported as shorter than it really is). 0 when the column is empty.</summary>
    public static int PopulatedRowSpan(this SheetData sheet, int columnIndex, int headerRow)
    {
        int lastNonEmpty = headerRow;   // nothing found yet
        for (int r = headerRow + 1; r < sheet.RowCount; r++)
        {
            if (!sheet.GetCell(r, columnIndex).IsEmpty)
                lastNonEmpty = r;
        }
        return lastNonEmpty - headerRow;
    }
}
