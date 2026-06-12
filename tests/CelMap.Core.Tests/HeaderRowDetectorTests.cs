using CelMap.Core;

namespace CelMap.Core.Tests;

public class HeaderRowDetectorTests
{
    /// <summary>Builds a SheetData from sparse cell tuples (mirrors HeaderExtractorTests).</summary>
    private static SheetData Sheet(params (int row, int col, CellValue val)[] cells)
    {
        int rows = cells.Max(c => c.row) + 1;
        int cols = cells.Max(c => c.col) + 1;
        var grid = new CellValue[rows][];
        for (int r = 0; r < rows; r++)
        {
            grid[r] = new CellValue[cols];
            for (int c = 0; c < cols; c++) grid[r][c] = CellValue.Empty;
        }
        foreach (var (r, c, v) in cells) grid[r][c] = v;
        return new SheetData("S", grid);
    }

    private static CellValue T(string s) => CellValue.FromText(s);
    private static CellValue N(double d) => CellValue.FromNumber(d);

    /// <summary>A full table that starts at row 0 → header is row 0.</summary>
    [Fact]
    public void Detect_TableFromRowZero_PicksRowZero()
    {
        var cells = new List<(int, int, CellValue)>();
        // header + 6 data rows (an unbroken run of 7 ≥ MinRunRows)
        for (int r = 0; r < 7; r++)
        {
            cells.Add((r, 0, r == 0 ? T("Name") : T($"n{r}")));
            cells.Add((r, 1, r == 0 ? T("Age") : N(r)));
        }
        Assert.Equal(0, HeaderRowDetector.Detect(Sheet(cells.ToArray())));
    }

    /// <summary>Title + blank rows above the block → header is the first row of the run.</summary>
    [Fact]
    public void Detect_SkipsTitleAndBlanks_PicksStartOfDataBlock()
    {
        var cells = new List<(int, int, CellValue)>
        {
            (0, 0, T("Member Data Collection 2023")),   // stray title row
            // row 1 blank — breaks any run
        };
        // header on row 2, then 6 data rows → run of 7 starting at row 2
        for (int r = 2; r < 9; r++)
        {
            cells.Add((r, 0, r == 2 ? T("MemberID") : N(1000 + r)));
            cells.Add((r, 1, r == 2 ? T("Surname") : T($"s{r}")));
        }
        Assert.Equal(2, HeaderRowDetector.Detect(Sheet(cells.ToArray())));
    }

    /// <summary>A short run (below MinRunRows) above the real block is skipped; the long run wins.</summary>
    [Fact]
    public void Detect_IgnoresShortRunBeforeRealBlock()
    {
        var cells = new List<(int, int, CellValue)>
        {
            (0, 0, T("note")), (1, 0, T("note2")),   // 2-row run, too short
            // row 2 blank
        };
        for (int r = 3; r < 10; r++)            // 7-row run starting at row 3
            cells.Add((r, 0, T($"v{r}")));
        Assert.Equal(3, HeaderRowDetector.Detect(Sheet(cells.ToArray())));
    }

    /// <summary>No run reaches MinRunRows → fall back to the first non-empty row.</summary>
    [Fact]
    public void Detect_NoLongRun_FallsBackToFirstNonEmptyRow()
    {
        var sheet = Sheet(
            // row 0 blank
            (1, 0, T("A")), (1, 1, T("B")),
            (2, 0, N(1)),   (2, 1, N(2)));   // only a 2-row run total
        Assert.Equal(1, HeaderRowDetector.Detect(sheet));
    }

    [Fact]
    public void Detect_EmptySheet_FallsBackToZero()
    {
        var sheet = new SheetData("empty", Array.Empty<CellValue[]>());
        Assert.Equal(0, HeaderRowDetector.Detect(sheet));
    }
}
