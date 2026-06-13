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
    public void Write_ConstantColumn_FillsEveryDataRow_AsText()
    {
        var sourcePath = CreateWorkbook(wb =>
        {
            var ws = wb.AddWorksheet("Data");
            ws.Cell(1, 1).Value = "Name";
            ws.Cell(2, 1).Value = "Alice";
            ws.Cell(3, 1).Value = "Bob";
            ws.Cell(4, 1).Value = "Cara";
        });
        var targetPath = CreateWorkbook(wb =>
        {
            var ws = wb.AddWorksheet("Report");
            ws.Cell(1, 1).Value = "Name";
            ws.Cell(1, 2).Value = "Status";   // target col index 1, filled by a constant
        });

        var outputDir = Path.Combine(_tempDir, "output");
        var writer = new TargetWriter(new WorkbookReader());

        var request = new WriteRequest(
            sourcePath, "Data", 0,
            targetPath, "Report", 0,
            new Dictionary<int, int> { [0] = 0 },          // Name ← Name
            outputDir,
            ConstantColumns: new Dictionary<int, string> { [1] = "Active" });

        var result = writer.Write(request);

        using var wb = new XLWorkbook(result.OutputFilePath);
        var ws = wb.Worksheet("Report");
        // every data row of the constant column carries the literal
        Assert.Equal("Active", ws.Cell(2, 2).GetString());
        Assert.Equal("Active", ws.Cell(3, 2).GetString());
        Assert.Equal("Active", ws.Cell(4, 2).GetString());
        // and the mapped column still copied
        Assert.Equal("Alice", ws.Cell(2, 1).GetString());
    }

    [Fact]
    public void Write_ConstantColumn_IsoDate_WrittenAsRealDate()
    {
        var sourcePath = CreateWorkbook(wb =>
        {
            var ws = wb.AddWorksheet("Data");
            ws.Cell(1, 1).Value = "Name";
            ws.Cell(2, 1).Value = "Alice";
        });
        var targetPath = CreateWorkbook(wb =>
        {
            var ws = wb.AddWorksheet("Report");
            ws.Cell(1, 1).Value = "Name";
            ws.Cell(1, 2).Value = "ReviewDate";
        });

        var outputDir = Path.Combine(_tempDir, "output");
        var writer = new TargetWriter(new WorkbookReader());

        var request = new WriteRequest(
            sourcePath, "Data", 0,
            targetPath, "Report", 0,
            new Dictionary<int, int> { [0] = 0 },
            outputDir,
            ConstantColumns: new Dictionary<int, string> { [1] = "2026-06-13" });

        var result = writer.Write(request);

        using var wb = new XLWorkbook(result.OutputFilePath);
        var cell = wb.Worksheet("Report").Cell(2, 2);
        Assert.Equal(XLDataType.DateTime, cell.DataType);
        Assert.Equal(new DateTime(2026, 6, 13), cell.GetDateTime());
    }

    [Theory]
    [InlineData("Active", false)]
    [InlineData("2026-06-13", true)]
    [InlineData("2026/06/13", false)]   // wrong separator → text
    [InlineData("13-06-2026", false)]   // not ISO order → text
    [InlineData("2026-13-40", false)]   // ISO shape but invalid date → text
    public void ParseConstant_TreatsOnlyIsoDatesAsDates(string input, bool expectDate)
    {
        var value = TargetWriter.ParseConstant(input);
        Assert.Equal(expectDate ? CellValueType.DateTime : CellValueType.Text, value.Type);
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
    public void Write_AppendMode_WritesAfterLastUsedRow_LeavingExistingData()
    {
        var sourcePath = CreateWorkbook(wb =>
        {
            var ws = wb.AddWorksheet("Data");
            ws.Cell(1, 1).Value = "Name";   // header
            ws.Cell(2, 1).Value = "Carol";
            ws.Cell(3, 1).Value = "Dave";
        });

        // Target already has two data rows below its header.
        var targetPath = CreateWorkbook(wb =>
        {
            var ws = wb.AddWorksheet("Report");
            ws.Cell(1, 1).Value = "Name";
            ws.Cell(2, 1).Value = "Alice";
            ws.Cell(3, 1).Value = "Bob";
        });

        var outputDir = Path.Combine(_tempDir, "output");
        var writer = new TargetWriter(new WorkbookReader());

        var result = writer.Write(new WriteRequest(
            sourcePath, "Data", 0,
            targetPath, "Report", 0,
            new Dictionary<int, int> { [0] = 0 },
            outputDir,
            WriteMode.Append));

        Assert.Equal(2, result.RowsWritten);

        using var wb = new XLWorkbook(result.OutputFilePath);
        var ws = wb.Worksheet("Report");
        // existing data untouched
        Assert.Equal("Alice", ws.Cell(2, 1).GetString());
        Assert.Equal("Bob",   ws.Cell(3, 1).GetString());
        // new data appended after the last used row
        Assert.Equal("Carol", ws.Cell(4, 1).GetString());
        Assert.Equal("Dave",  ws.Cell(5, 1).GetString());
    }

    [Fact]
    public void Write_OverwriteMode_ReplacesDataRowsFromBelowHeader()
    {
        var sourcePath = CreateWorkbook(wb =>
        {
            var ws = wb.AddWorksheet("Data");
            ws.Cell(1, 1).Value = "Name";
            ws.Cell(2, 1).Value = "Carol";
        });

        var targetPath = CreateWorkbook(wb =>
        {
            var ws = wb.AddWorksheet("Report");
            ws.Cell(1, 1).Value = "Name";
            ws.Cell(2, 1).Value = "Alice";   // will be overwritten
        });

        var outputDir = Path.Combine(_tempDir, "output");
        var writer = new TargetWriter(new WorkbookReader());

        var result = writer.Write(new WriteRequest(
            sourcePath, "Data", 0,
            targetPath, "Report", 0,
            new Dictionary<int, int> { [0] = 0 },
            outputDir,
            WriteMode.Overwrite));   // default, but explicit for clarity

        using var wb = new XLWorkbook(result.OutputFilePath);
        var ws = wb.Worksheet("Report");
        Assert.Equal("Carol", ws.Cell(2, 1).GetString());   // replaced from row below header
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

    [Fact]
    public void Write_DynamicCategoryCovers_EvaluatesAndWritesCorrectFlags()
    {
        // Source data has members in category 1, 2, A, B
        var sourcePath = CreateWorkbook(wb =>
        {
            var ws = wb.AddWorksheet("Data");
            ws.Cell(1, 1).Value = "Name";
            ws.Cell(1, 2).Value = "Category";
            
            ws.Cell(2, 1).Value = "Alice";
            ws.Cell(2, 2).Value = "1";

            ws.Cell(3, 1).Value = "Bob";
            ws.Cell(3, 2).Value = "2";

            ws.Cell(4, 1).Value = "Charlie";
            ws.Cell(4, 2).Value = "a"; // test case insensitivity

            ws.Cell(5, 1).Value = "Dave";
            ws.Cell(5, 2).Value = "B";
        });

        // Target template has category, name, and cover flag columns
        var targetPath = CreateWorkbook(wb =>
        {
            var ws = wb.AddWorksheet("Report");
            ws.Cell(1, 1).Value = "Name";
            ws.Cell(1, 2).Value = "Category No";
            ws.Cell(1, 3).Value = "GSC";
            ws.Cell(1, 4).Value = "GL";
            ws.Cell(1, 5).Value = "TPD";
        });

        var outputDir = Path.Combine(_tempDir, "output");
        var writer = new TargetWriter(new WorkbookReader());

        // Setup parameters
        var defaultCovers = new HashSet<string> { "GL" };
        var categoryCovers = new Dictionary<string, IReadOnlySet<string>>
        {
            ["1"] = new HashSet<string> { "GSC", "GL" },
            ["A"] = new HashSet<string> { "GL", "TPD" }
        };
        var insParams = new InsuranceParams(defaultCovers, categoryCovers);

        var request = new WriteRequest(
            sourcePath, "Data", 0,
            targetPath, "Report", 0,
            new Dictionary<int, int> { [0] = 0, [1] = 1 }, // Map Name and Category
            outputDir,
            ConstantColumns: new Dictionary<int, string>
            {
                [2] = "[Dynamic GSC]",
                [3] = "[Dynamic GL]",
                [4] = "[Dynamic TPD]"
            },
            InsuranceParams: insParams
        );

        var result = writer.Write(request);

        using var wb = new XLWorkbook(result.OutputFilePath);
        var ws = wb.Worksheet("Report");

        // Alice (Category 1) -> GSC="Y", GL="Y", TPD="N"
        Assert.Equal("Alice", ws.Cell(2, 1).GetString());
        Assert.Equal("1", ws.Cell(2, 2).GetString());
        Assert.Equal("Y", ws.Cell(2, 3).GetString());
        Assert.Equal("Y", ws.Cell(2, 4).GetString());
        Assert.Equal("N", ws.Cell(2, 5).GetString());

        // Bob (Category 2) -> defaults to GL only -> GSC="N", GL="Y", TPD="N"
        Assert.Equal("Bob", ws.Cell(3, 1).GetString());
        Assert.Equal("2", ws.Cell(3, 2).GetString());
        Assert.Equal("N", ws.Cell(3, 3).GetString());
        Assert.Equal("Y", ws.Cell(3, 4).GetString());
        Assert.Equal("N", ws.Cell(3, 5).GetString());

        // Charlie (Category 'a') -> GSC="N", GL="Y", TPD="Y" (case-insensitive lookup of override 'A')
        Assert.Equal("Charlie", ws.Cell(4, 1).GetString());
        Assert.Equal("a", ws.Cell(4, 2).GetString());
        Assert.Equal("N", ws.Cell(4, 3).GetString());
        Assert.Equal("Y", ws.Cell(4, 4).GetString());
        Assert.Equal("Y", ws.Cell(4, 5).GetString());

        // Dave (Category 'B') -> defaults to GL only -> GSC="N", GL="Y", TPD="N"
        Assert.Equal("Dave", ws.Cell(5, 1).GetString());
        Assert.Equal("B", ws.Cell(5, 2).GetString());
        Assert.Equal("N", ws.Cell(5, 3).GetString());
        Assert.Equal("Y", ws.Cell(5, 4).GetString());
        Assert.Equal("N", ws.Cell(5, 5).GetString());
    }
}
