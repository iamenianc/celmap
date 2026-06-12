using ClosedXML.Excel;
using CelMap.Core;

namespace CelMap.Core.Tests;

public class WorkbookReaderTests : IDisposable
{
    private readonly string _tempDir;

    public WorkbookReaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private string CreateWorkbook(Action<XLWorkbook> setup)
    {
        var path = Path.Combine(_tempDir, $"{Guid.NewGuid()}.xlsx");
        using var wb = new XLWorkbook();
        setup(wb);
        wb.SaveAs(path);
        return path;
    }

    [Fact]
    public void GetSheetNames_ReturnsAllSheets()
    {
        var path = CreateWorkbook(wb =>
        {
            wb.AddWorksheet("Alpha");
            wb.AddWorksheet("Beta");
        });

        var reader = new WorkbookReader();
        var names = reader.GetSheetNames(path);

        Assert.Equal(new[] { "Alpha", "Beta" }, names);
    }

    [Fact]
    public void ReadSheet_TextCell_PreservesValue()
    {
        var path = CreateWorkbook(wb =>
        {
            var ws = wb.AddWorksheet("Sheet1");
            ws.Cell(1, 1).Value = "Hello";
        });

        var reader = new WorkbookReader();
        var sheet = reader.ReadSheet(path, "Sheet1");

        Assert.Equal(CellValueType.Text, sheet.GetCell(0, 0).Type);
        Assert.Equal("Hello", sheet.GetCell(0, 0).Raw);
    }

    [Fact]
    public void ReadSheet_NumberCell_PreservesValue()
    {
        var path = CreateWorkbook(wb =>
        {
            var ws = wb.AddWorksheet("Sheet1");
            ws.Cell(1, 1).Value = 42.5;
        });

        var reader = new WorkbookReader();
        var sheet = reader.ReadSheet(path, "Sheet1");

        Assert.Equal(CellValueType.Number, sheet.GetCell(0, 0).Type);
        Assert.Equal(42.5, sheet.GetCell(0, 0).Raw);
    }

    [Fact]
    public void ReadSheet_EmptySheet_ReturnsEmptyData()
    {
        var path = CreateWorkbook(wb => wb.AddWorksheet("Empty"));

        var reader = new WorkbookReader();
        var sheet = reader.ReadSheet(path, "Empty");

        Assert.Equal(0, sheet.RowCount);
    }

    [Fact]
    public void ReadSheet_MixedRow_ReadsAllTypes()
    {
        var path = CreateWorkbook(wb =>
        {
            var ws = wb.AddWorksheet("Mixed");
            ws.Cell(1, 1).Value = "text";
            ws.Cell(1, 2).Value = 99;
            ws.Cell(1, 3).Value = true;
        });

        var reader = new WorkbookReader();
        var sheet = reader.ReadSheet(path, "Mixed");

        Assert.Equal(CellValueType.Text,    sheet.GetCell(0, 0).Type);
        Assert.Equal(CellValueType.Number,  sheet.GetCell(0, 1).Type);
        Assert.Equal(CellValueType.Boolean, sheet.GetCell(0, 2).Type);
    }
}
