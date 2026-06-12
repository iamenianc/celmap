using ClosedXML.Excel;

namespace CelMap.Core;

public sealed class TargetWriter : ITargetWriter
{
    private readonly IWorkbookReader _reader;

    public TargetWriter(IWorkbookReader reader) => _reader = reader;

    public WriteResult Write(WriteRequest req)
    {
        Directory.CreateDirectory(req.OutputDirectory);

        string outputPath = BuildOutputPath(req.TargetFilePath, req.OutputDirectory);
        File.Copy(req.TargetFilePath, outputPath, overwrite: true);

        var source = _reader.ReadSheet(req.SourceFilePath, req.SourceSheetName);
        var warnings = new List<string>();

        // source data rows are everything below the header row
        int sourceDataStart = req.SourceHeaderRow + 1;
        int dataRowCount = source.RowCount - sourceDataStart;

        if (dataRowCount <= 0)
        {
            warnings.Add("Source sheet has no data rows below the header.");
            return new WriteResult(outputPath, 0, warnings);
        }

        using var wb = new XLWorkbook(outputPath);
        var ws = wb.Worksheet(req.TargetSheetName);

        // find the 1-based row number in the target sheet that corresponds to
        // the target header row (which is expressed as 0-based index into the
        // used range — we need the actual sheet row number)
        var usedRange = ws.RangeUsed();
        int targetFirstUsedRow = usedRange?.FirstRow().RowNumber() ?? 1;
        int targetHeaderSheetRow = targetFirstUsedRow + req.TargetHeaderRow;

        // Overwrite: start on the row right below the header, replacing data rows.
        // Append: start on the first empty row after the last used row (PRD §2.5),
        // never above the header (an empty template appends right below it).
        int targetDataStartSheetRow;
        if (req.Mode == WriteMode.Append)
        {
            int lastUsedRow = usedRange?.LastRow().RowNumber() ?? targetHeaderSheetRow;
            targetDataStartSheetRow = Math.Max(lastUsedRow + 1, targetHeaderSheetRow + 1);
        }
        else
        {
            targetDataStartSheetRow = targetHeaderSheetRow + 1;
        }

        // ClosedXML column numbers are 1-based; our ColumnMap keys/values are
        // 0-based indices into SheetData, which starts at firstCol of the used range.
        int sourceFirstCol = GetFirstUsedCol(req.SourceFilePath, req.SourceSheetName);
        int targetFirstCol = GetFirstUsedCol(req.TargetFilePath, req.TargetSheetName);

        int rowsWritten = 0;
        for (int dataRow = 0; dataRow < dataRowCount; dataRow++)
        {
            int sourceRowIdx = sourceDataStart + dataRow;
            int targetSheetRow = targetDataStartSheetRow + dataRow;

            foreach (var (srcColIdx, tgtColIdx) in req.ColumnMap)
            {
                var value = source.GetCell(sourceRowIdx, srcColIdx);
                int tgtSheetCol = targetFirstCol + tgtColIdx;
                var cell = ws.Cell(targetSheetRow, tgtSheetCol);
                WriteVerbatim(cell, value);
            }
            rowsWritten++;
        }

        wb.Save();
        return new WriteResult(outputPath, rowsWritten, warnings);
    }

    private static void WriteVerbatim(IXLCell cell, CellValue value)
    {
        if (value.IsEmpty) return;

        switch (value.Type)
        {
            case CellValueType.Text:     cell.SetValue((string)value.Raw!); break;
            case CellValueType.Number:   cell.SetValue((double)value.Raw!); break;
            case CellValueType.Boolean:  cell.SetValue((bool)value.Raw!);   break;
            case CellValueType.DateTime: cell.SetValue((DateTime)value.Raw!); break;
            case CellValueType.Error:    cell.SetValue((string)value.Raw!); break;
        }
    }

    private int GetFirstUsedCol(string filePath, string sheetName)
    {
        using var wb = new XLWorkbook(filePath);
        var ws = wb.Worksheet(sheetName);
        return ws.RangeUsed()?.FirstColumn().ColumnNumber() ?? 1;
    }

    private static string BuildOutputPath(string targetFilePath, string outputDir)
    {
        string name = Path.GetFileNameWithoutExtension(targetFilePath);
        string ext  = Path.GetExtension(targetFilePath);
        return Path.Combine(outputDir, $"{name} (mapped){ext}");
    }
}
