using CelMap.Core;

namespace CelMap.Core.Tests;

public class HeaderExtractorTests
{
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

    [Fact]
    public void Extract_PullsLabelsFromGivenRow_TrimmingWhitespace()
    {
        var sheet = Sheet(
            (0, 0, CellValue.FromText("title")),       // row 0 is junk
            (2, 0, CellValue.FromText("  Name  ")),    // row 2 is the header
            (2, 1, CellValue.FromText("Amount")));

        var headers = HeaderExtractor.Extract(sheet, headerRow: 2);

        Assert.Equal("Name", headers[0].Label);
        Assert.Equal("Amount", headers[1].Label);
        Assert.Equal(0, headers[0].ColumnIndex);
        Assert.Equal(1, headers[1].ColumnIndex);
    }

    [Fact]
    public void Extract_EmptyHeaderCell_YieldsEmptyLabel()
    {
        var sheet = Sheet(
            (0, 0, CellValue.FromText("A")),
            (0, 1, CellValue.Empty));

        var headers = HeaderExtractor.Extract(sheet, 0);

        Assert.Equal("A", headers[0].Label);
        Assert.Equal("", headers[1].Label);
    }
}
