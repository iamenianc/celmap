namespace CelMap.Core;

public interface IWorkbookReader
{
    IReadOnlyList<string> GetSheetNames(string filePath, string? password = null);
    SheetData ReadSheet(string filePath, string sheetName, string? password = null);

    /// <summary>True if the file is an encrypted (password-protected) workbook that needs a
    /// password to open — i.e. an OLE/CFB container rather than a plain ZIP-based .xlsx.</summary>
    bool IsEncrypted(string filePath);
}
