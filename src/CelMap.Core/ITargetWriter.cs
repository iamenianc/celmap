namespace CelMap.Core;

/// <summary>Where written data lands relative to existing target rows (PRD §2.5).</summary>
public enum WriteMode
{
    /// <summary>Write from the row immediately below the target header, replacing existing data rows.</summary>
    Overwrite,
    /// <summary>Write from the first empty row after the last used row, leaving existing data in place.</summary>
    Append
}

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
    WriteMode Mode = WriteMode.Overwrite
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
