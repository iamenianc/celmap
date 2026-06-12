using CelMap.Core;

// CelMap.Cli source.xlsx target.xlsx [sourceSheet] [targetSheet] [srcHeaderRow=1] [tgtHeaderRow=1] [threshold=80]
if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: CelMap.Cli <source.xlsx> <target.xlsx> [sourceSheet] [targetSheet] [srcHeaderRow=1] [tgtHeaderRow=1] [threshold=80]");
    return 1;
}

string sourcePath = args[0];
string targetPath = args[1];

var reader = new WorkbookReader();
var sourceSheets = reader.GetSheetNames(sourcePath);
var targetSheets = reader.GetSheetNames(targetPath);

string sourceSheet = args.Length > 2 ? args[2] : sourceSheets[0];
string targetSheet = args.Length > 3 ? args[3] : targetSheets[0];
int srcHeaderRow   = args.Length > 4 ? int.Parse(args[4]) - 1 : 0;  // 1-based input → 0-based
int tgtHeaderRow   = args.Length > 5 ? int.Parse(args[5]) - 1 : 0;
int threshold      = args.Length > 6 ? int.Parse(args[6]) : 80;

Console.WriteLine($"Source : {sourcePath}  [{sourceSheet}]  header row {srcHeaderRow + 1}");
Console.WriteLine($"Target : {targetPath}  [{targetSheet}]  header row {tgtHeaderRow + 1}");
Console.WriteLine($"Threshold: {threshold}\n");

var sourceData = reader.ReadSheet(sourcePath, sourceSheet);
var targetData = reader.ReadSheet(targetPath, targetSheet);

var sourceHeaders = HeaderExtractor.Extract(sourceData, srcHeaderRow);
var targetHeaders = HeaderExtractor.Extract(targetData, tgtHeaderRow);

var aliases = AliasRules.LoadDefault();
var qualified = QualifiedRules.LoadDefault();
Console.WriteLine($"Alias groups loaded: {aliases.Groups.Count}; qualified rules: {qualified.Rules.Count}\n");

var matcher = new ColumnMatcher(aliases, qualified);
var result = matcher.Match(
    sourceHeaders, targetHeaders,
    new MatcherOptions(ConfidenceThreshold: threshold),
    src => sourceData.ColumnIsEmpty(src.ColumnIndex, srcHeaderRow));

// Scored table
Console.WriteLine($"{"TargetCol",-32} {"SourceCol",-32} {"Score",5}  Status");
Console.WriteLine(new string('-', 90));
foreach (var m in result.Mappings)
{
    string tgt = Truncate(m.TargetColumn.Label, 31);
    string src = m.MatchedSource is { } s
        ? Truncate(s.Label, 31)
        : m.Status == MatchStatus.Ambiguous
            ? Truncate("? " + string.Join(" | ", m.Candidates.Take(2).Select(c => $"{c.SourceColumn.Label}({c.Score})")), 31)
            : "—";
    Console.WriteLine($"{tgt,-32} {src,-32} {m.Score,5}  {m.Status}");
    if (m.SourceColumnIsEmpty)
        Console.WriteLine($"  [WARN] mapped source column '{m.MatchedSource!.Label}' is entirely empty.");
}

// Write the auto-applied mappings
var columnMap = result.ToColumnMap();
Console.WriteLine($"\nAuto-applied mappings: {columnMap.Count}");

if (columnMap.Count == 0)
{
    Console.WriteLine("Nothing to write (no auto matches above threshold).");
    return 0;
}

string outputDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "CelMap");

var writer = new TargetWriter(reader);
var writeResult = writer.Write(new WriteRequest(
    sourcePath, sourceSheet, srcHeaderRow,
    targetPath, targetSheet, tgtHeaderRow,
    columnMap, outputDir));

foreach (var w in writeResult.Warnings)
    Console.WriteLine($"[WARN] {w}");

Console.WriteLine($"\nDone. {writeResult.RowsWritten} rows written.");
Console.WriteLine($"Output: {writeResult.OutputFilePath}");
return 0;

static string Truncate(string s, int max) =>
    s.Length <= max ? s : s[..(max - 1)] + "…";
