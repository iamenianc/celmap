using CelMap.Core;

namespace CelMap.Core.Tests;

/// <summary>Guards the shipped synonyms.json (seeded from generictable.xlsx): it must
/// load, have no label appearing in two different groups (collisions make alias lookup
/// arbitrary), and preserve the agreed strict/loose and skip decisions.</summary>
public class ShippedSynonymsTests
{
    private static AliasRules Load()
    {
        // synonyms.json is copied next to the Core assembly (CopyToOutputDirectory).
        string path = Path.Combine(AppContext.BaseDirectory, "synonyms.json");
        Assert.True(File.Exists(path), $"synonyms.json not found at {path}");
        return AliasRules.FromJson(File.ReadAllText(path));
    }

    [Fact]
    public void Loads_WithGroups()
    {
        var rules = Load();
        Assert.NotEmpty(rules.Groups);
    }

    [Fact]
    public void HasNoCollisions_LabelInOnlyOneGroup()
    {
        var rules = Load();
        var seen = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < rules.Groups.Count; i++)
            foreach (var name in rules.Groups[i].Names)
            {
                string key = name.Trim();
                if (!seen.TryGetValue(key, out var list)) { list = new(); seen[key] = list; }
                if (!list.Contains(i)) list.Add(i);
            }

        var collisions = seen.Where(kv => kv.Value.Count > 1)
                             .Select(kv => $"'{kv.Key}' in groups [{string.Join(",", kv.Value)}]")
                             .ToList();

        Assert.True(collisions.Count == 0,
            "Label(s) appear in multiple groups (ambiguous alias lookup):\n  " +
            string.Join("\n  ", collisions));
    }

    [Theory]
    [InlineData("MemberID", "CerteID")]
    [InlineData("DOB", "D.O.B.")]
    [InlineData("Salary", "income")]
    [InlineData("Email", "Electronic Mail")]
    public void KnownPairs_AreAliases(string a, string b)
    {
        Assert.True(Load().AreAliases(a, b), $"expected '{a}' ~ '{b}'");
    }

    [Theory]
    [InlineData("MemberID")]
    [InlineData("GroupID")]
    [InlineData("EmployeeRef")]
    [InlineData("DJS")]
    [InlineData("ReviewDate")]
    [InlineData("TFN")]
    public void IdentityAndFlaggedTargets_AreStrict(string target)
    {
        Assert.True(Load().IsStrict(target), $"expected '{target}' to be strict");
    }

    [Theory]
    [InlineData("Salary")]   // strong-but-loose: fuzzy fallback OK
    [InlineData("Bonus")]
    [InlineData("State")]
    public void NonKeyTargets_AreLoose(string target)
    {
        Assert.False(Load().IsStrict(target), $"expected '{target}' to be loose");
    }

    [Theory]
    [InlineData("FUL")]
    [InlineData("Loading")]
    [InlineData("Term")]
    [InlineData("Threshold")]
    public void BareConflictingShortCodes_AreNotSeededAsAliases(string bare)
    {
        // These were skipped (one→many across GL/GSC/TPD). They must not resolve as an
        // alias of anything — i.e. no group claims them.
        var rules = Load();
        bool present = rules.Groups.Any(g =>
            g.Names.Any(n => string.Equals(n.Trim(), bare, StringComparison.OrdinalIgnoreCase)));
        Assert.False(present, $"'{bare}' should have been skipped from the seed");
    }

    // ── Shipped qualified_rules.json (GL/GSC/TPD split fields) ────────────────

    private static QualifiedRules LoadQualified()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "qualified_rules.json");
        Assert.True(File.Exists(path), $"qualified_rules.json not found at {path}");
        return QualifiedRules.FromJson(File.ReadAllText(path));
    }

    [Fact]
    public void ShippedQualifiedRules_Load()
    {
        Assert.NotEmpty(LoadQualified().Rules);
    }

    [Theory]
    // Benefit-type synonyms (insurance): GSC≡GIP≡Income Protection≡Salary Continuance;
    // GL≡Group Life≡Death. Each should qualify its own target's split field.
    [InlineData("GSCFUL", "GIP FUL")]
    [InlineData("GSCFUL", "Income Protection FUL")]
    [InlineData("GSCLoading", "Salary Continuance Loading")]
    [InlineData("GLFUL", "Death FUL")]
    [InlineData("GLThreshold", "Group Life Threshold")]
    [InlineData("TPDTerm", "TPD Term")]
    public void ShippedQualifiedRules_SynonymQualifiersWork(string target, string source)
    {
        Assert.True(LoadQualified().Qualifies(target, source),
            $"expected '{source}' to qualify '{target}'");
    }

    [Theory]
    // Bare split tokens with no benefit qualifier → concept-only → manual review.
    [InlineData("GSCFUL", "FUL")]
    [InlineData("GLLoading", "Loading")]
    public void ShippedQualifiedRules_BareToken_IsConceptOnly(string target, string source)
    {
        Assert.True(LoadQualified().IsAmbiguousConceptOnly(target, source));
        Assert.False(LoadQualified().Qualifies(target, source));
    }

    [Fact]
    public void ShippedQualifiedRules_GipDoesNotQualifyGlTarget()
    {
        Assert.False(LoadQualified().Qualifies("GLFUL", "GIP FUL")); // GIP≡GSC, not GL
    }
}
