namespace CelMap.Core;

/// <summary>One column header: its label and 0-based column index within the sheet's used range.</summary>
public sealed record HeaderColumn(int ColumnIndex, string Label);

/// <summary>Pulls header labels out of a chosen header row in a SheetData.</summary>
public static class HeaderExtractor
{
    /// <param name="headerRow">0-based row index into the sheet's used range.</param>
    public static IReadOnlyList<HeaderColumn> Extract(SheetData sheet, int headerRow)
    {
        var headers = new List<HeaderColumn>(sheet.ColCount);
        for (int c = 0; c < sheet.ColCount; c++)
        {
            string label = sheet.GetCell(headerRow, c).ToString().Trim();
            headers.Add(new HeaderColumn(c, label));
        }
        return headers;
    }
}
