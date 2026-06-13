using ClosedXML.Excel;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CelMap.Core;

public sealed class TargetWriter : ITargetWriter
{
    private readonly IWorkbookReader _reader;

    public TargetWriter(IWorkbookReader reader) => _reader = reader;

    public WriteResult Write(WriteRequest req)
    {
        Directory.CreateDirectory(req.OutputDirectory);

        string outputPath = BuildOutputPath(req.TargetFilePath, req.OutputDirectory, req.GroupId);
        File.Copy(req.TargetFilePath, outputPath, overwrite: true);

        var source = _reader.ReadSheet(req.SourceFilePath, req.SourceSheetName, req.SourcePassword);
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

        // The target template is assumed blank below its header, so data always starts
        // on the row immediately below it (row 2 for a standard header-in-row-1 template).
        int targetDataStartSheetRow = targetHeaderSheetRow + 1;

        // ClosedXML column numbers are 1-based; our ColumnMap keys/values are
        // 0-based indices into SheetData, which starts at firstCol of the used range.
        int sourceFirstCol = GetFirstUsedCol(req.SourceFilePath, req.SourceSheetName, req.SourcePassword);
        int targetFirstCol = GetFirstUsedCol(req.TargetFilePath, req.TargetSheetName, null);

        // Resolve each typed constant once: text, unless it's a YYYY-MM-DD date.
        // Filter out dynamic cover columns from the static constants.
        var constants = (req.ConstantColumns ?? new Dictionary<int, string>())
            .Where(kv => !kv.Value.StartsWith("[Dynamic "))
            .ToDictionary(kv => kv.Key, kv => ParseConstant(kv.Value));

        // Find which columns are dynamic cover columns
        var dynamicCoverCols = (req.ConstantColumns ?? new Dictionary<int, string>())
            .Where(kv => kv.Value.StartsWith("[Dynamic "))
            .ToDictionary(kv => kv.Key, kv => kv.Value.Replace("[Dynamic ", "").Replace("]", "").Trim());

        // If we have dynamic cover columns, let's find the category column index
        int? targetCategoryColIdx = null;
        int? sourceCategoryColIdx = null;
        string? constantCategory = null;

        if (dynamicCoverCols.Count > 0 && req.InsuranceParams != null)
        {
            var targetSheetData = _reader.ReadSheet(req.TargetFilePath, req.TargetSheetName);
            var targetHeaders = HeaderExtractor.Extract(targetSheetData, req.TargetHeaderRow);
            var categorySynonyms = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "GSCCategoryNo", "Category No", "CategoryNo", "Cat No", "Category Number", "Cat Number", "Category Num", "Category"
            };
            var catHeader = targetHeaders.FirstOrDefault(h => categorySynonyms.Contains(h.Label));
            if (catHeader != null)
            {
                targetCategoryColIdx = catHeader.ColumnIndex;
                foreach (var kv in req.ColumnMap)
                {
                    if (kv.Value == targetCategoryColIdx.Value)
                    {
                        sourceCategoryColIdx = kv.Key;
                        break;
                    }
                }
                if (req.ConstantColumns != null && req.ConstantColumns.TryGetValue(targetCategoryColIdx.Value, out var constCat))
                {
                    constantCategory = constCat;
                }
            }
        }

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

            // Typed constants fill EVERY data row of their target column.
            foreach (var (tgtColIdx, value) in constants)
            {
                int tgtSheetCol = targetFirstCol + tgtColIdx;
                WriteVerbatim(ws.Cell(targetSheetRow, tgtSheetCol), value);
            }

            // Evaluate dynamic cover types if needed
            if (dynamicCoverCols.Count > 0 && req.InsuranceParams is { } insParams)
            {
                string category = "";
                if (sourceCategoryColIdx.HasValue)
                {
                    category = source.GetCell(sourceRowIdx, sourceCategoryColIdx.Value).ToString()?.Trim() ?? "";
                }
                else if (constantCategory != null)
                {
                    category = constantCategory.Trim();
                }

                // Check overrides for this category
                IReadOnlySet<string> activeCovers = insParams.DefaultCovers;
                foreach (var kv in insParams.CategoryCovers)
                {
                    if (string.Equals(kv.Key, category, StringComparison.OrdinalIgnoreCase))
                    {
                        activeCovers = kv.Value;
                        break;
                    }
                }

                foreach (var (tgtColIdx, coverType) in dynamicCoverCols)
                {
                    int tgtSheetCol = targetFirstCol + tgtColIdx;
                    bool isCovered = activeCovers.Contains(coverType);
                    var cell = ws.Cell(targetSheetRow, tgtSheetCol);
                    cell.SetValue(isCovered ? "Y" : "N");
                }
            }

            rowsWritten++;
        }

        wb.Save();
        return new WriteResult(outputPath, rowsWritten, warnings);
    }

    /// <summary>A typed constant is written as text, except: a strict YYYY-MM-DD date becomes a
    /// real date, and a strict integer (e.g. GroupID, InsurerID) becomes a real number so Excel
    /// types it as INT rather than text. Integers with leading zeros stay text (they're identifiers,
    /// not quantities, and the zeros must survive).</summary>
    public static CellValue ParseConstant(string text)
    {
        if (string.IsNullOrEmpty(text)) return CellValue.Empty;

        if (System.Text.RegularExpressions.Regex.IsMatch(text, @"^\d{4}-\d{2}-\d{2}$")
            && DateTime.TryParseExact(text, "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var date))
        {
            return CellValue.FromDateTime(date);
        }

        if (IsStrictInteger(text)
            && long.TryParse(text, System.Globalization.NumberStyles.AllowLeadingSign,
                System.Globalization.CultureInfo.InvariantCulture, out var intValue))
        {
            return CellValue.FromNumber(intValue);
        }

        return CellValue.FromText(text);
    }

    /// <summary>A pure integer literal: optional leading '-', then digits, with no leading zeros
    /// (a leading zero marks an identifier that must stay text). "0" itself is allowed.</summary>
    private static bool IsStrictInteger(string text) =>
        System.Text.RegularExpressions.Regex.IsMatch(text, @"^-?(0|[1-9]\d*)$");

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

    private int GetFirstUsedCol(string filePath, string sheetName, string? password)
    {
        // Decrypt an encrypted source the same way the reader does, so this works on
        // password-protected files too.
        byte[] raw = File.ReadAllBytes(filePath);
        using var stream = Crypto.OfficeCrypto.IsEncrypted(raw)
            ? new MemoryStream(Crypto.OfficeCrypto.Decrypt(raw, password ?? ""))
            : new MemoryStream(raw);
        using var wb = new XLWorkbook(stream);
        var ws = wb.Worksheet(sheetName);
        return ws.RangeUsed()?.FirstColumn().ColumnNumber() ?? 1;
    }

    /// <summary>Output file is named after the target template, optionally prefixed with the
    /// Group ID ("{GroupId}_{template}.xlsx"). An existing file of the same name is overwritten.</summary>
    private static string BuildOutputPath(string targetFilePath, string outputDir, string? groupId)
    {
        string name = Path.GetFileNameWithoutExtension(targetFilePath);
        string ext  = Path.GetExtension(targetFilePath);
        string prefix = string.IsNullOrWhiteSpace(groupId) ? "" : $"{groupId.Trim()}_";
        return Path.Combine(outputDir, $"{prefix}{name}{ext}");
    }
}
