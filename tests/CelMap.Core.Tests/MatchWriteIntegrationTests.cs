using ClosedXML.Excel;
using CelMap.Core;

namespace CelMap.Core.Tests;

/// <summary>Proves the full Tracer 2 path: read → extract headers → match → write verbatim.</summary>
public class MatchWriteIntegrationTests : IDisposable
{
    private readonly string _tempDir;

    public MatchWriteIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private string Save(Action<IXLWorksheet> build, string sheet)
    {
        var path = Path.Combine(_tempDir, $"{Guid.NewGuid()}.xlsx");
        using var wb = new XLWorkbook();
        build(wb.AddWorksheet(sheet));
        wb.SaveAs(path);
        return path;
    }

    [Fact]
    public void ReadMatchWrite_PutsValuesUnderTheRightTargetColumns()
    {
        // Source: columns in a different ORDER than the target.
        var sourcePath = Save(ws =>
        {
            ws.Cell(1, 1).Value = "Email Address";
            ws.Cell(1, 2).Value = "Customer Name";
            ws.Cell(2, 1).Value = "a@acme.com";
            ws.Cell(2, 2).Value = "Acme";
            ws.Cell(3, 1).Value = "b@globex.com";
            ws.Cell(3, 2).Value = "Globex";
        }, "Sales");

        var targetPath = Save(ws =>
        {
            ws.Cell(1, 1).Value = "CustomerName";   // target col 1
            ws.Cell(1, 2).Value = "Email";          // target col 2
        }, "Report");

        var reader = new WorkbookReader();
        var source = reader.ReadSheet(sourcePath, "Sales");
        var target = reader.ReadSheet(targetPath, "Report");

        var srcHeaders = HeaderExtractor.Extract(source, 0);
        var tgtHeaders = HeaderExtractor.Extract(target, 0);

        var result = new ColumnMatcher().Match(
            srcHeaders, tgtHeaders, new MatcherOptions(ConfidenceThreshold: 80));

        var outputDir = Path.Combine(_tempDir, "out");
        var writeResult = new TargetWriter(reader).Write(new WriteRequest(
            sourcePath, "Sales", 0,
            targetPath, "Report", 0,
            result.ToColumnMap(), outputDir));

        Assert.Equal(2, writeResult.RowsWritten);

        using var wb = new XLWorkbook(writeResult.OutputFilePath);
        var ws = wb.Worksheet("Report");
        // CustomerName column (1) got the names; Email column (2) got the emails —
        // despite the source having them in the opposite order.
        Assert.Equal("Acme", ws.Cell(2, 1).GetString());
        Assert.Equal("a@acme.com", ws.Cell(2, 2).GetString());
        Assert.Equal("Globex", ws.Cell(3, 1).GetString());
        Assert.Equal("b@globex.com", ws.Cell(3, 2).GetString());
    }
}
