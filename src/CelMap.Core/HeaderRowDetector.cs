namespace CelMap.Core;

/// <summary>
/// Guesses which row holds the column headers. Many real workbooks (the Dyson UAT file is
/// the canonical example — headers on row 6, title/blurb rows above) do not put headers on
/// row 1, so a blind "row 1" default is a foot-gun. The user validates the guess against a
/// header preview, so this only has to be a sensible starting point.
///
/// Rule (deliberately simple and predictable): within the top <see cref="MaxScanRows"/> rows,
/// the header is the row with the <b>most filled cells</b>. The header spans every column of
/// the table, so it is wider than stray title/blurb/preamble rows above it (which fill only a
/// cell or two). Ties resolve to the first (topmost) such row.
///
/// Pure heuristic over <see cref="SheetData"/>; no ClosedXML dependency, fully testable.
/// </summary>
public static class HeaderRowDetector
{
    /// <summary>How far down the sheet to look for the header.</summary>
    public const int MaxScanRows = 20;

    public static int Detect(SheetData sheet, int maxScanRows = MaxScanRows)
    {
        if (sheet.RowCount == 0 || sheet.ColCount == 0)
            return 0;

        int scanRows = Math.Min(maxScanRows, sheet.RowCount);

        int bestRow = 0;
        int bestCount = -1;
        for (int r = 0; r < scanRows; r++)
        {
            int filled = FilledCells(sheet, r);
            // Strictly greater → ties keep the earlier (topmost) row.
            if (filled > bestCount)
            {
                bestCount = filled;
                bestRow = r;
            }
        }

        return bestRow;
    }

    private static int FilledCells(SheetData sheet, int row)
    {
        int filled = 0;
        for (int c = 0; c < sheet.ColCount; c++)
            if (!sheet.GetCell(row, c).IsEmpty)
                filled++;
        return filled;
    }
}
