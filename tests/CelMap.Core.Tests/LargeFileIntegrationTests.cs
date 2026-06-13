using Xunit;
using ClosedXML.Excel;
using System.IO;
using System.Diagnostics;
using System;
using System.Linq;

namespace CelMap.Core.Tests;

public class LargeFileIntegrationTests : IDisposable
{
    private readonly string _tempDir;
    private const string LargeFilePath = @"C:\Users\ianch\sourecode\repos\CelMap-Docs\Test_sources\GenericMemberDataImportTemplate-test.xlsm";

    public LargeFileIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    [Fact]
    public void MapAndWrite_LargeFile_10000Rows_Succeeds()
    {
        // 1. Check if the large file exists
        if (!File.Exists(LargeFilePath))
        {
            Assert.Fail($"Large test file does not exist at {LargeFilePath}");
        }

        var reader = new WorkbookReader();
        var timer = Stopwatch.StartNew();

        // 2. Read the source sheet
        var sourceSheet = reader.ReadSheet(LargeFilePath, "DataCollection");
        Assert.Equal(10001, sourceSheet.RowCount); // 1 header + 10000 rows

        // 3. Create a target workbook with a subset of headers to test mapping
        var targetHeaders = new[] { "MemberID", "GroupID", "EmployeeRef", "Surname", "FirstName", "DOB", "Salary", "State", "Occupation", "Email" };
        var targetPath = Path.Combine(_tempDir, "target_template.xlsx");
        using (var wb = new XLWorkbook())
        {
            var ws = wb.AddWorksheet("TargetSheet");
            for (int i = 0; i < targetHeaders.Length; i++)
            {
                ws.Cell(1, i + 1).Value = targetHeaders[i];
            }
            wb.SaveAs(targetPath);
        }

        var targetSheet = reader.ReadSheet(targetPath, "TargetSheet");

        // 4. Extract headers and match
        var srcHeaders = HeaderExtractor.Extract(sourceSheet, 0);
        var tgtHeaders = HeaderExtractor.Extract(targetSheet, 0);

        var matcher = new ColumnMatcher();
        var matchResult = matcher.Match(srcHeaders, tgtHeaders, new MatcherOptions(ConfidenceThreshold: 80));

        // Proves that all target columns mapped successfully (exact match)
        Assert.Equal(targetHeaders.Length, matchResult.Mappings.Count(m => m.Status == MatchStatus.Auto));

        // 5. Write to the target workbook copy using TargetWriter
        var writer = new TargetWriter(reader);
        var outputDir = Path.Combine(_tempDir, "output");
        var writeRequest = new WriteRequest(
            LargeFilePath, "DataCollection", 0,
            targetPath, "TargetSheet", 0,
            matchResult.ToColumnMap(),
            outputDir
        );

        var writeResult = writer.Write(writeRequest);

        timer.Stop();
        Console.WriteLine($"Large file processing of 10,000 rows completed in {timer.ElapsedMilliseconds} ms.");

        // Assertions
        Assert.Equal(10000, writeResult.RowsWritten);
        Assert.Empty(writeResult.Warnings);
        Assert.True(File.Exists(writeResult.OutputFilePath));

        // Verify some written data
        using (var outputWb = new XLWorkbook(writeResult.OutputFilePath))
        {
            var ws = outputWb.Worksheet("TargetSheet");
            var range = ws.RangeUsed();
            Assert.NotNull(range);
            Assert.Equal(10001, range.LastRow().RowNumber()); // 1 header + 10000 data rows

            // Verify a few rows of data are correct
            // Row 2 is Mary Wilson
            Assert.Equal("170000", ws.Cell(2, 3).GetString()); // EmployeeRef
            Assert.Equal("Wilson", ws.Cell(2, 4).GetString()); // Surname
            Assert.Equal("Mary", ws.Cell(2, 5).GetString()); // FirstName
            Assert.Equal("mary.wilson@example.com", ws.Cell(2, 10).GetString()); // Email
        }
    }
}
