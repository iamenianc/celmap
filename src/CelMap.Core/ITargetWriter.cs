namespace CelMap.Core;

public record WriteRequest(
    string SourceFilePath,
    string SourceSheetName,
    int SourceHeaderRow,        // 0-based row index within SheetData
    string TargetFilePath,
    string TargetSheetName,
    int TargetHeaderRow,        // 0-based row index within SheetData
    // col index in source -> col index in target (both 0-based within their SheetData)
    IReadOnlyDictionary<int, int> ColumnMap,
    string OutputDirectory
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
