namespace CelMap.Core;

/// <summary>
/// Guesses which row holds the column headers. Many real workbooks (the Dyson UAT file is
/// the canonical example — headers on row 6, title/blurb rows above) do not put headers on
/// row 1, so a blind "row 1" default is a foot-gun. The user validates the guess against a
/// header preview, so this only has to be a sensible starting point.
///
/// Rule (deliberately simple and predictable): the header is the first row that begins an
/// <b>unbroken run of at least <see cref="MinRunRows"/> non-empty rows</b> — i.e. the top of
/// the contiguous data block, skipping stray title/blurb rows above it. If no such run
/// exists, fall back to the first non-empty row, then to row 0.
///
/// Pure heuristic over <see cref="SheetData"/>; no ClosedXML dependency, fully testable.
/// </summary>
public static class HeaderRowDetector
{
    /// <summary>How long a contiguous run of filled rows must be to count as the data block
    /// (the header row + several data rows beneath it).</summary>
    public const int MinRunRows = 5;

    public static int Detect(SheetData sheet, int maxScanRows = 20)
    {
        if (sheet.RowCount == 0 || sheet.ColCount == 0)
            return 0;

        int scanRows = Math.Min(maxScanRows, sheet.RowCount);

        int firstNonEmpty = -1;
        int run = 0;            // length of the current unbroken non-empty run
        int runStart = -1;      // row where the current run began

        for (int r = 0; r < scanRows; r++)
        {
            if (RowHasContent(sheet, r))
            {
                if (run == 0) runStart = r;
                run++;
                if (firstNonEmpty < 0) firstNonEmpty = r;
                // First row that starts a long-enough block wins.
                if (run >= MinRunRows) return runStart;
            }
            else
            {
                run = 0;
                runStart = -1;
            }
        }

        // No run of MinRunRows within the scan window: take the first non-empty row.
        return firstNonEmpty >= 0 ? firstNonEmpty : 0;
    }

    private static bool RowHasContent(SheetData sheet, int row)
    {
        for (int c = 0; c < sheet.ColCount; c++)
            if (!sheet.GetCell(row, c).IsEmpty)
                return true;
        return false;
    }
}
