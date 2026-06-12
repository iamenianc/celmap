using ClosedXML.Excel;
using CelMap.Core;

namespace CelMap.Core.Tests;

public class TargetWriterTests : IDisposable
{
    private readonly string _tempDir;

    public TargetWriterTests()
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
    public void Write_CopiesValuesVerbatim_IntoTargetCopy()
    {
        var sourcePath = CreateWorkbook(wb =>
        {
            var ws = wb.AddWorksheet("Data");
            ws.Cell(1, 1).Value = "Name";      // header
            ws.Cell(2, 1).Value = "Alice";
            ws.Cell(3, 1).Value = "Bob";
        });

        var targetPath = CreateWorkbook(wb =>
        {
            var ws = wb.AddWorksheet("Report");
            ws.Cell(1, 1).Value = "Name";      // header
            ws.Cell(1, 1).Style.Font.Bold = true;  // formatting that must survive
        });

        var outputDir = Path.Combine(_tempDir, "output");
        var reader = new WorkbookReader();
        var writer = new TargetWriter(reader);

        var request = new WriteRequest(
            sourcePath, "Data", 0,
            targetPath, "Report", 0,
            new Dictionary<int, int> { [0] = 0 },
            outputDir);

        var result = writer.Write(request);

        Assert.Equal(2, result.RowsWritten);
        Assert.Empty(result.Warnings);
        Assert.True(File.Exists(result.OutputFilePath));

        // verify values were written
        using var wb = new XLWorkbook(result.OutputFilePath);
        var ws = wb.Worksheet("Report");
        Assert.Equal("Alice", ws.Cell(2, 1).GetString());
        Assert.Equal("Bob",   ws.Cell(3, 1).GetString());

        // verify target formatting was preserved
        Assert.True(ws.Cell(1, 1).Style.Font.Bold);
    }

    [Fact]
    public void Write_NeverModifiesOriginalTarget()
    {
        var sourcePath = CreateWorkbook(wb =>
        {
            var ws = wb.AddWorksheet("Src");
            ws.Cell(1, 1).Value = "Col";
            ws.Cell(2, 1).Value = "value";
        });

        var targetPath = CreateWorkbook(wb =>
        {
            var ws = wb.AddWorksheet("Tgt");
            ws.Cell(1, 1).Value = "Col";
        });

        long originalSize = new FileInfo(targetPath).Length;
        var outputDir = Path.Combine(_tempDir, "output");
        var reader = new WorkbookReader();
        var writer = new TargetWriter(reader);

        var request = new WriteRequest(
            sourcePath, "Src", 0,
            targetPath, "Tgt", 0,
            new Dictionary<int, int> { [0] = 0 },
            outputDir);

        writer.Write(request);

        Assert.Equal(originalSize, new FileInfo(targetPath).Length);
    }

    [Fact]
    public void Write_OutputGoesToConfiguredDirectory()
    {
        var sourcePath = CreateWorkbook(wb =>
        {
            var ws = wb.AddWorksheet("S");
            ws.Cell(1, 1).Value = "H";
            ws.Cell(2, 1).Value = "v";
        });
        var targetPath = CreateWorkbook(wb => { var ws = wb.AddWorksheet("T"); ws.Cell(1, 1).Value = "H"; });

        var outputDir = Path.Combine(_tempDir, "custom_output");
        var reader = new WorkbookReader();
        var writer = new TargetWriter(reader);

        var request = new WriteRequest(
            sourcePath, "S", 0, targetPath, "T", 0,
            new Dictionary<int, int> { [0] = 0 }, outputDir);

        var result = writer.Write(request);

        Assert.StartsWith(outputDir, result.OutputFilePath);
        Assert.True(File.Exists(result.OutputFilePath));
    }
}
