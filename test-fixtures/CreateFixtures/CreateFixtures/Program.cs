using ClosedXML.Excel;
using System.Text;
using System.Text.Json;

// Dev tooling for CelMap fixtures. Two modes:
//   dotnet run -- convert     Regenerate src/CelMap.Core/synonyms.json from
//                             UAT Files/generictable.xlsx (the org's curated table).
//   dotnet run -- fixtures    (Re)create the demo .xlsx fixtures under test-fixtures/.
// Default (no arg) = convert.

const string Repo = @"C:\Users\ianch\sourecode\repos\CelMap";

string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "convert";
switch (mode)
{
    case "convert":  Convert();  break;
    case "fixtures": Fixtures(); break;
    default:
        Console.Error.WriteLine($"Unknown mode '{mode}'. Use 'convert' or 'fixtures'.");
        Environment.Exit(1);
        break;
}

// ── Converter: generictable.xlsx → synonyms.json ─────────────────────────────
// Col A = Generic Target (org code), Col B = Source label variant, Col C = Notes.
//  - Group by target; each col-B variant becomes an alias of that target.
//  - MERGE groups that share any label (the table fragments one concept across
//    rows, e.g. the Category-No family) so no label lands in two groups.
//  - STRICT (identity/key — never fuzzy-guess) if a row is noted "strict match"
//    OR the target is a known ID/key field.
//  - Drop bare conflicting short codes (FUL/Loading/Term/Threshold) — one→many;
//    those are handled by qualified_rules.json instead.
static void Convert()
{
    string tablePath = Path.Combine(Repo, "UAT Files", "generictable.xlsx");
    string outPath   = Path.Combine(Repo, "src", "CelMap.Core", "synonyms.json");

    var skipSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "FUL", "Loading", "Term", "Threshold" };
    var strictKeyTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "MemberID", "GroupID", "EmployeeRef", "GSCCategoryNo", "Category No",
          "Category Number", "TFN" };

    using var wb = new XLWorkbook(tablePath);
    var ws = wb.Worksheet("Sheet1");
    int last = ws.RangeUsed()!.LastRow().RowNumber();

    var rows = new List<(string Target, string Source, bool Strict)>();
    for (int r = 2; r <= last; r++)
    {
        string target = ws.Cell(r, 1).GetString().Trim();
        string source = ws.Cell(r, 2).GetString().Trim();
        string notes  = ws.Cell(r, 3).GetString();
        if (target.Length == 0 || source.Length == 0) continue;
        if (skipSources.Contains(source)) continue;

        bool strict = notes.Contains("strict", StringComparison.OrdinalIgnoreCase)
                      || strictKeyTargets.Contains(target);
        rows.Add((target, source, strict));
    }

    // Union-find over labels so co-occurring labels (and groups sharing a label) merge.
    var parent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    string Find(string x)
    {
        parent.TryAdd(x, x);
        while (!string.Equals(parent[x], x, StringComparison.OrdinalIgnoreCase))
        { parent[x] = parent[parent[x]]; x = parent[x]; }
        return x;
    }
    void Union(string a, string b)
    { var ra = Find(a); var rb = Find(b); if (!string.Equals(ra, rb, StringComparison.OrdinalIgnoreCase)) parent[ra] = rb; }

    foreach (var (t, s, _) in rows) Union(t, s);

    var members = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
    var strictByRep = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
    void Add(string label)
    {
        string rep = Find(label);
        if (!members.TryGetValue(rep, out var list)) { list = new(); members[rep] = list; }
        if (!list.Contains(label, StringComparer.OrdinalIgnoreCase)) list.Add(label);
    }
    foreach (var (t, s, strict) in rows)
    {
        Add(t); Add(s);
        string rep = Find(t);
        strictByRep[rep] = strictByRep.GetValueOrDefault(rep) || strict;
    }

    var groups = members.Keys
        .Select(rep => (Names: members[rep], Strict: strictByRep.GetValueOrDefault(rep)))
        .OrderBy(g => g.Names[0], StringComparer.OrdinalIgnoreCase)
        .ToList();

    var sb = new StringBuilder();
    sb.AppendLine("{");
    sb.AppendLine("  \"groups\": [");
    for (int i = 0; i < groups.Count; i++)
    {
        var g = groups[i];
        string names = string.Join(", ", g.Names.Select(v => JsonSerializer.Serialize(v)));
        string comma = i < groups.Count - 1 ? "," : "";
        sb.AppendLine(g.Strict
            ? $"    {{ \"names\": [ {names} ], \"strict\": true }}{comma}"
            : $"    [ {names} ]{comma}");
    }
    sb.AppendLine("  ]");
    sb.AppendLine("}");
    File.WriteAllText(outPath, sb.ToString());

    int strictCount = groups.Count(g => g.Strict);
    Console.WriteLine($"Wrote {groups.Count} alias groups ({strictCount} strict) to {outPath}");
    Console.WriteLine($"Skipped bare conflicting sources: {string.Join(", ", skipSources)}");
}

// ── Demo fixtures (regenerable; not committed) ───────────────────────────────
static void Fixtures()
{
    string dir = Path.Combine(Repo, "test-fixtures");
    Directory.CreateDirectory(dir);

    // Basic preserve-formatting pair.
    SaveBook(Path.Combine(dir, "source.xlsx"), "Sales", ws =>
    {
        Header(ws, "CustomerName", "Amount", "Date");
        ws.Cell(2, 1).Value = "Acme Corp"; ws.Cell(2, 2).Value = 1500.50; ws.Cell(2, 3).Value = new DateTime(2026, 6, 1);
        ws.Cell(3, 1).Value = "Globex";    ws.Cell(3, 2).Value = 2200.00; ws.Cell(3, 3).Value = new DateTime(2026, 6, 5);
    });
    SaveBook(Path.Combine(dir, "target.xlsx"), "Report", ws =>
    {
        Header(ws, "CustomerName", "Amount");
        ws.Cell(1, 1).Style.Font.Bold = true; ws.Cell(1, 2).Style.Font.Bold = true;
        ws.Column(1).Width = 30; ws.Column(2).Width = 15;
    });

    // Insurance qualifier demo: GIP≡GSC, Death≡GL.
    SaveBook(Path.Combine(dir, "ins_source.xlsx"), "Client", ws =>
        Header(ws, "GIP Category", "Death FUL", "Income Protection Loading", "TPD Term", "CategoryNo"));
    SaveBook(Path.Combine(dir, "ins_template.xlsx"), "Template", ws =>
    {
        Header(ws, "GSCCategoryNo", "GLFUL", "GSCLoading", "TPDTerm", "TPDCategoryNo");
        for (int c = 1; c <= 5; c++) ws.Cell(1, c).Style.Font.Bold = true;
    });

    Console.WriteLine($"Wrote demo fixtures to {dir}");
}

static void SaveBook(string path, string sheet, Action<IXLWorksheet> build)
{
    using var wb = new XLWorkbook();
    build(wb.AddWorksheet(sheet));
    wb.SaveAs(path);
}
static void Header(IXLWorksheet ws, params string[] headers)
{
    for (int c = 0; c < headers.Length; c++) ws.Cell(1, c + 1).Value = headers[c];
}
