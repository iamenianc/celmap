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
    case "inspect":  Inspect(args.Skip(1).ToArray()); break;
    case "populate": Populate(args.Skip(1).ToArray()); break;
    case "templates": CreateRandomTemplates(args.Skip(1).ToArray()); break;
    default:
        Console.Error.WriteLine($"Unknown mode '{mode}'. Use 'convert', 'fixtures', 'inspect', 'populate', or 'templates'.");
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

static void Inspect(string[] args)
{
    string path = args.Length > 0 ? args[0] : @"C:\Users\ianch\sourecode\repos\CelMap-Docs\Test_sources\GenericMemberDataImportTemplate-test.xlsm";
    Console.WriteLine($"Inspecting workbook: {path}");
    using var wb = new XLWorkbook(path);
    foreach (var ws in wb.Worksheets)
    {
        Console.WriteLine($"Sheet: {ws.Name}");
        var range = ws.RangeUsed();
        if (range != null)
        {
            var firstRow = range.FirstRow();
            Console.WriteLine($"  Used range: {range.RangeAddress.ToString()}");
            Console.WriteLine($"  Headers: {string.Join(", ", firstRow.Cells().Select(c => c.GetString()))}");
            
            // Print the first few rows
            int maxRow = Math.Min(range.LastRow().RowNumber(), 5);
            int lastCol = range.LastColumn().ColumnNumber();
            for (int r = 2; r <= maxRow; r++)
            {
                var rowCells = new List<string>();
                for (int c = 1; c <= lastCol; c++)
                {
                    rowCells.Add(ws.Cell(r, c).GetString());
                }
                if (r == 2)
                {
                    Console.WriteLine("  Row 2 Details:");
                    for (int c = 1; c <= lastCol; c++)
                    {
                        string headerVal = firstRow.Cell(c).GetString();
                        string cellVal = ws.Cell(r, c).GetString();
                        Console.WriteLine($"    [{c}] {headerVal} = '{cellVal}' (Type: {ws.Cell(r, c).DataType})");
                    }
                }
                else
                {
                    Console.WriteLine($"  Row {r}: {string.Join(", ", rowCells.Take(10))}... (Total cols: {rowCells.Count})");
                }
            }
        }
        else
        {
            Console.WriteLine("  Sheet is empty.");
        }
    }
}

static void Populate(string[] args)
{
    string path = args.Length > 0 ? args[0] : @"C:\Users\ianch\sourecode\repos\CelMap-Docs\Test_sources\GenericMemberDataImportTemplate-test.xlsm";
    int rowCount = 10000;
    if (args.Length > 1 && int.TryParse(args[1], out var parsedCount))
    {
        rowCount = parsedCount;
    }
    Console.WriteLine($"Populating workbook: {path} with {rowCount} rows of bogus data...");
    
    using var wb = new XLWorkbook(path);
    var ws = wb.Worksheet("DataCollection");
    
    // Clear existing data rows below header (row 1) without deleting row objects to avoid breaking formulas
    int lastRow = ws.LastRowUsed()?.RowNumber() ?? 1;
    if (lastRow > 1)
    {
        Console.WriteLine($"Clearing {lastRow - 1} existing rows...");
        ws.Range(2, 1, lastRow, 49).Clear(XLClearOptions.Contents);
    }
    
    var random = new Random(42); // Seeded for reproducibility
    
    var surnames = new[] { "Smith", "Jones", "Taylor", "Brown", "Wilson", "Johnson", "Davies", "Patel", "Wright", "Thompson", "Evans", "Walker", "White", "Roberts", "Green", "Hall", "Wood", "Harris", "Martin", "Jackson", "Clark", "Lewis", "Turner", "Hill", "Cooper", "Ward", "Morris", "King", "Baker", "Harrison" };
    var firstNames = new[] { "James", "John", "Robert", "Michael", "William", "David", "Richard", "Joseph", "Thomas", "Charles", "Christopher", "Daniel", "Matthew", "Anthony", "Mark", "Donald", "Steven", "Paul", "Andrew", "Joshua", "Mary", "Patricia", "Jennifer", "Linda", "Elizabeth", "Barbara", "Susan", "Jessica", "Sarah", "Karen" };
    var occClasses = new[] { "Professional", "White Collar", "Light Blue", "Heavy Blue", "NA" };
    var states = new[] { "NSW", "VIC", "QLD", "WA", "SA", "TAS", "ACT", "NT" };
    var occupations = new[] { "Manager", "Service Technician", "Analyst", "Developer", "Consultant", "Administrator", "Clerk", "Engineer" };
    var statuses = new[] { "Active", "Active", "Active", "Terminated" };
    var costCentres = new[] { "CC001", "CC002", "CC003", "CC004" };

    var reviewDate = new DateTime(2026, 1, 1);
    
    for (int i = 0; i < rowCount; i++)
    {
        int r = i + 2; // Data rows start at row 2
        
        string fn = firstNames[random.Next(firstNames.Length)];
        string sn = surnames[random.Next(surnames.Length)];
        string gender = random.Next(2) == 0 ? "M" : "F";
        
        var dob = new DateTime(2026 - random.Next(18, 65), random.Next(1, 13), random.Next(1, 29));
        var dateJoined = dob.AddYears(18).AddDays(random.Next(0, Math.Max(1, (reviewDate - dob.AddYears(18)).Days)));
        var djs = dateJoined.AddMonths(random.Next(1, 12));
        var salary = Math.Round(45000 + random.NextDouble() * 140000, 2);
        
        // MemberID
        ws.Cell(r, 1).SetValue(string.Empty);
        // GroupID
        ws.Cell(r, 2).SetValue(164);
        // ReviewDate
        ws.Cell(r, 3).SetValue(reviewDate);
        // GSCCategoryNo
        ws.Cell(r, 4).SetValue(1000);
        // GLCategoryNo
        ws.Cell(r, 5).SetValue(2001);
        // TPDCategoryNo
        ws.Cell(r, 6).SetValue(3001);
        // EmployeeRef
        ws.Cell(r, 7).SetValue((170000 + i).ToString());
        // Surname
        ws.Cell(r, 8).SetValue(sn);
        // FirstName
        ws.Cell(r, 9).SetValue(fn);
        // Gender
        ws.Cell(r, 10).SetValue(gender);
        // DOB
        ws.Cell(r, 11).SetValue(dob);
        // TFN
        ws.Cell(r, 12).SetValue(random.Next(100000000, 999999999).ToString());
        // DateJoinedCompany
        ws.Cell(r, 13).SetValue(dateJoined);
        // DJS
        ws.Cell(r, 14).SetValue(djs);
        // DET
        ws.Cell(r, 15).SetValue(string.Empty);
        // Salary
        ws.Cell(r, 16).SetValue(salary);
        // Superannuation
        ws.Cell(r, 17).SetValue(string.Empty);
        // Commission
        ws.Cell(r, 18).SetValue(string.Empty);
        // Bonus
        ws.Cell(r, 19).SetValue(string.Empty);
        // Allowance
        ws.Cell(r, 20).SetValue(string.Empty);
        // Overtime
        ws.Cell(r, 21).SetValue(string.Empty);
        // SuperBalance
        ws.Cell(r, 22).SetValue(string.Empty);
        // OwnershipPercent
        ws.Cell(r, 23).SetValue(string.Empty);
        // OccClass
        ws.Cell(r, 24).SetValue(occClasses[random.Next(occClasses.Length)]);
        // State
        ws.Cell(r, 25).SetValue(states[random.Next(states.Length)]);
        // Occupation
        ws.Cell(r, 26).SetValue(occupations[random.Next(occupations.Length)]);
        // EmployeeStatusStartDate
        ws.Cell(r, 27).SetValue(string.Empty);
        // EmployeeStatusEndDate
        ws.Cell(r, 28).SetValue(string.Empty);
        // EmployeeStatus
        ws.Cell(r, 29).SetValue(statuses[random.Next(statuses.Length)]);
        // Hours
        ws.Cell(r, 30).SetValue(random.Next(2) == 0 ? 38.0 : 40.0);
        // LeaveStatus
        ws.Cell(r, 31).SetValue(string.Empty);
        // LeaveStartDate
        ws.Cell(r, 32).SetValue(string.Empty);
        // LeaveEndDate
        ws.Cell(r, 33).SetValue(string.Empty);
        // GSCFUL
        ws.Cell(r, 34).SetValue(random.Next(10) == 0 ? "Y" : string.Empty);
        // GSCLoading
        if (random.Next(10) == 0)
            ws.Cell(r, 35).SetValue(Math.Round(random.NextDouble() * 2, 2));
        else
            ws.Cell(r, 35).SetValue(string.Empty);
        // GSCThreshold
        ws.Cell(r, 36).SetValue(string.Empty);
        // GSCTerm
        ws.Cell(r, 37).SetValue(string.Empty);
        // GLFUL
        ws.Cell(r, 38).SetValue(random.Next(10) == 0 ? "Y" : string.Empty);
        // GLLoading
        if (random.Next(10) == 0)
            ws.Cell(r, 39).SetValue(Math.Round(random.NextDouble() * 2, 2));
        else
            ws.Cell(r, 39).SetValue(string.Empty);
        // GLThreshold
        ws.Cell(r, 40).SetValue(string.Empty);
        // GLTerm
        ws.Cell(r, 41).SetValue(string.Empty);
        // TPDFUL
        ws.Cell(r, 42).SetValue(random.Next(10) == 0 ? "Y" : string.Empty);
        // TPDLoading
        if (random.Next(10) == 0)
            ws.Cell(r, 43).SetValue(Math.Round(random.NextDouble() * 2, 2));
        else
            ws.Cell(r, 43).SetValue(string.Empty);
        // TPDThreshold
        ws.Cell(r, 44).SetValue(string.Empty);
        // TPDTerm
        ws.Cell(r, 45).SetValue(string.Empty);
        // MemberNotes
        ws.Cell(r, 46).SetValue("Bogus member record");
        // Email
        ws.Cell(r, 47).SetValue($"{fn.ToLower()}.{sn.ToLower()}@example.com");
        // Limit 
        ws.Cell(r, 48).SetValue(string.Empty);
        // CostCentre 
        ws.Cell(r, 49).SetValue(costCentres[random.Next(costCentres.Length)]);
    }
    
    wb.Save();
    Console.WriteLine($"Successfully populated {rowCount} rows and saved workbook.");
}

static void CreateRandomTemplates(string[] args)
{
    string targetDir = args.Length > 0 ? args[0] : @"C:\Users\ianch\sourecode\repos\CelMap-Docs\Test_sources";
    Directory.CreateDirectory(targetDir);
    Console.WriteLine($"Generating random templates in {targetDir}...");

    var random = new Random(99);
    var surnames = new[] { "Smith", "Jones", "Taylor", "Brown", "Wilson", "Johnson", "Davies", "Patel", "Wright", "Thompson", "Evans", "Walker", "White", "Roberts", "Green", "Hall", "Wood", "Harris", "Martin", "Jackson", "Clark", "Lewis", "Turner", "Hill", "Cooper", "Ward", "Morris", "King", "Baker", "Harrison" };
    var firstNames = new[] { "James", "John", "Robert", "Michael", "William", "David", "Richard", "Joseph", "Thomas", "Charles", "Christopher", "Daniel", "Matthew", "Anthony", "Mark", "Donald", "Steven", "Paul", "Andrew", "Joshua", "Mary", "Patricia", "Jennifer", "Linda", "Elizabeth", "Barbara", "Susan", "Jessica", "Sarah", "Karen" };
    var occupations = new[] { "Manager", "Service Technician", "Analyst", "Developer", "Consultant", "Administrator", "Clerk", "Engineer" };
    var states = new[] { "NSW", "VIC", "QLD", "WA", "SA", "TAS", "ACT", "NT" };
    var occClasses = new[] { "Professional", "White Collar", "Light Blue", "Heavy Blue", "NA" };

    // --- Template 1: HR System Export ---
    string path1 = Path.Combine(targetDir, "Template_HR_System_Export.xlsx");
    SaveBook(path1, "HR_Export", ws =>
    {
        var headers = new[] { "Employee ID", "First Name", "Last Name", "Gender", "Birth Date", "Start Date", "Base Salary", "State Code", "Job Title", "Work Email" };
        Header(ws, headers);
        for (int i = 0; i < 75; i++)
        {
            int r = i + 2;
            string fn = firstNames[random.Next(firstNames.Length)];
            string sn = surnames[random.Next(surnames.Length)];
            ws.Cell(r, 1).SetValue($"EMP{10000 + i}");
            ws.Cell(r, 2).SetValue(fn);
            ws.Cell(r, 3).SetValue(sn);
            ws.Cell(r, 4).SetValue(random.Next(2) == 0 ? "Male" : "Female");
            ws.Cell(r, 5).SetValue(new DateTime(1965 + random.Next(40), random.Next(1, 13), random.Next(1, 28)));
            ws.Cell(r, 6).SetValue(new DateTime(2015 + random.Next(11), random.Next(1, 13), random.Next(1, 28)));
            ws.Cell(r, 7).SetValue(Math.Round(50000 + random.NextDouble() * 120000, 2));
            ws.Cell(r, 8).SetValue(states[random.Next(states.Length)]);
            ws.Cell(r, 9).SetValue(occupations[random.Next(occupations.Length)]);
            ws.Cell(r, 10).SetValue($"{fn.ToLower()}.{sn.ToLower()}@company.com");
        }
    });

    // --- Template 2: Insurance Broker List ---
    string path2 = Path.Combine(targetDir, "Template_Insurance_Broker_List.xlsx");
    SaveBook(path2, "Broker_List", ws =>
    {
        var headers = new[] { "Personnel Number", "Given Name", "Family Name", "M/F", "D.O.B.", "Join Date", "Earnings", "Classification", "Current Status", "Primary Email" };
        Header(ws, headers);
        for (int i = 0; i < 100; i++)
        {
            int r = i + 2;
            string fn = firstNames[random.Next(firstNames.Length)];
            string sn = surnames[random.Next(surnames.Length)];
            ws.Cell(r, 1).SetValue((200000 + i).ToString());
            ws.Cell(r, 2).SetValue(fn);
            ws.Cell(r, 3).SetValue(sn);
            ws.Cell(r, 4).SetValue(random.Next(2) == 0 ? "M" : "F");
            ws.Cell(r, 5).SetValue(new DateTime(1970 + random.Next(35), random.Next(1, 13), random.Next(1, 28)));
            ws.Cell(r, 6).SetValue(new DateTime(2018 + random.Next(8), random.Next(1, 13), random.Next(1, 28)));
            ws.Cell(r, 7).SetValue(Math.Round(60000 + random.NextDouble() * 100000, 2));
            ws.Cell(r, 8).SetValue(occClasses[random.Next(occClasses.Length)]);
            ws.Cell(r, 9).SetValue(random.Next(5) == 0 ? "Terminated" : "Active");
            ws.Cell(r, 10).SetValue($"{fn.ToLower()}.{sn.ToLower()}@broker.com");
        }
    });

    // --- Template 3: Simple Member Roster ---
    string path3 = Path.Combine(targetDir, "Template_Simple_Member_Roster.xlsx");
    SaveBook(path3, "Roster", ws =>
    {
        var headers = new[] { "Staff ID", "Firstname", "Lastname", "Sex", "Birthday", "Weekly Hours", "Contact Email" };
        Header(ws, headers);
        for (int i = 0; i < 50; i++)
        {
            int r = i + 2;
            string fn = firstNames[random.Next(firstNames.Length)];
            string sn = surnames[random.Next(surnames.Length)];
            ws.Cell(r, 1).SetValue($"ST{3000 + i}");
            ws.Cell(r, 2).SetValue(fn);
            ws.Cell(r, 3).SetValue(sn);
            ws.Cell(r, 4).SetValue(random.Next(2) == 0 ? "M" : "F");
            ws.Cell(r, 5).SetValue(new DateTime(1980 + random.Next(25), random.Next(1, 13), random.Next(1, 28)));
            ws.Cell(r, 6).SetValue(random.Next(2) == 0 ? 38 : 40);
            ws.Cell(r, 7).SetValue($"{fn.ToLower()}.{sn.ToLower()}@roster.com");
        }
    });

    Console.WriteLine("Random templates created successfully.");
}
