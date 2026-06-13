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
        // header + 6 data rows, all 2 cells wide → tie → topmost (row 0) wins
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

    /// <summary>The widest row wins even when narrower rows sit above it: a 4-column header
    /// below some 2-cell preamble rows is still picked over the preamble.</summary>
    [Fact]
    public void Detect_PicksWidestRowOverNarrowerRowsAbove()
    {
        var cells = new List<(int, int, CellValue)>
        {
            (0, 0, T("note")), (0, 1, T("x")),       // 2-cell preamble
            (1, 0, T("note2")), (1, 1, T("y")),      // 2-cell preamble
            // row 2 blank
        };
        for (int r = 3; r < 10; r++)            // 4-wide block starting at row 3
        {
            cells.Add((r, 0, T($"a{r}")));
            cells.Add((r, 1, T($"b{r}")));
            cells.Add((r, 2, T($"c{r}")));
            cells.Add((r, 3, T($"d{r}")));
        }
        Assert.Equal(3, HeaderRowDetector.Detect(Sheet(cells.ToArray())));
    }

    /// <summary>Sparse preamble (one filled cell per row) does not count as a data row, so it
    /// breaks the run rather than chaining into the real block below it. Mirrors a real file with
    /// a staircase of decoration cells (A1, B2, C3) above a wide contiguous table.</summary>
    [Fact]
    public void Detect_SparsePreambleAboveWideBlock_PicksBlockStart()
    {
        var cells = new List<(int, int, CellValue)>
        {
            (0, 0, T("A1")), (1, 1, T("B2")), (2, 2, T("C3")),   // staircase, 1 cell each
        };
        for (int r = 3; r < 12; r++)            // wide 9-row block starting at row 3
        {
            cells.Add((r, 0, T($"v{r}")));
            cells.Add((r, 1, N(r)));
        }
        Assert.Equal(3, HeaderRowDetector.Detect(Sheet(cells.ToArray())));
    }

    /// <summary>Equally wide rows tie → the topmost wins (here a blank row 0, then two 2-cell
    /// rows → row 1).</summary>
    [Fact]
    public void Detect_TiedWidth_PicksTopmostRow()
    {
        var sheet = Sheet(
            // row 0 blank
            (1, 0, T("A")), (1, 1, T("B")),
            (2, 0, N(1)),   (2, 1, N(2)));   // both rows 2 cells wide
        Assert.Equal(1, HeaderRowDetector.Detect(sheet));
    }

    [Fact]
    public void Detect_EmptySheet_FallsBackToZero()
    {
        var sheet = new SheetData("empty", Array.Empty<CellValue[]>());
        Assert.Equal(0, HeaderRowDetector.Detect(sheet));
    }
}
