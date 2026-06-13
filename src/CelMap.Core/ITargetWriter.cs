using System.Collections.Generic;

namespace CelMap.Core;

public record InsuranceParams(
    IReadOnlySet<string> DefaultCovers,
    IReadOnlyDictionary<string, IReadOnlySet<string>> CategoryCovers
);

public record WriteRequest(
    string SourceFilePath,
    string SourceSheetName,
    int SourceHeaderRow,        // 0-based row index within SheetData
    string TargetFilePath,
    string TargetSheetName,
    int TargetHeaderRow,        // 0-based row index within SheetData
    // col index in source -> col index in target (both 0-based within their SheetData)
    IReadOnlyDictionary<int, int> ColumnMap,
    string OutputDirectory,
    // target col index (0-based) -> literal value to write into EVERY data row of that column.
    // Independent of ColumnMap: a target column can be filled from a source OR a constant.
    IReadOnlyDictionary<int, string>? ConstantColumns = null,
    InsuranceParams? InsuranceParams = null,
    // Password for an encrypted source workbook; null/empty for a plain file.
    string? SourcePassword = null,
    // Group ID prefixed to the output file name ("{GroupId}_{template}.xlsx"); null/empty omits the prefix.
    string? GroupId = null
);

public record WriteResult(
    string OutputFilePath,
    int RowsWritten,
    IReadOnlyList<string> Warnings
);

public interface ITargetWriter
{
    WriteResult Write(WriteRequest request);
}

