namespace CelMap.Core;

public interface IWorkbookReader
{
    IReadOnlyList<string> GetSheetNames(string filePath);
    SheetData ReadSheet(string filePath, string sheetName);
}
