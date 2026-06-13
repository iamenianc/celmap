using System.Text.Json;
using System.Text.Json.Serialization;

namespace CelMap.Core;

/// <summary>
/// A token-gated mapping rule for ambiguous concepts that an alias can't safely cover.
/// A source column qualifies for <see cref="Target"/> only if its (normalized) header
/// satisfies every slot in <see cref="RequireAll"/>. Each slot is a set of ALTERNATIVES
/// (OR): the header must contain at least one alternative from each slot (AND across
/// slots). This lets one qualifier match several synonyms — e.g. the GSC benefit type
/// is "gsc" OR "gip" OR "income protection" OR "salary continuance".
/// <para>The shared, ambiguous base token(s) are listed in <see cref="Concept"/>: a
/// source header that has the concept but satisfies no full qualifier is surfaced for
/// manual review rather than auto-mapped.</para>
/// <para>Example (insurance): target "GSCCategoryNo" requires
/// [["category"], ["gsc","gip","income protection","salary continuance"]];
/// concept ["category"]. Source "GIP Category" → GSCCategoryNo (auto, GIP≡GSC).
/// Bare "CategoryNo" → NeedsReview.</para>
/// </summary>
public sealed record QualifiedRule(
    string Target,
    IReadOnlyList<IReadOnlyList<string>> RequireAll,  // AND of (OR of alternatives)
    IReadOnlyList<string> Concept);

public sealed class QualifiedRules
{
    public IReadOnlyList<QualifiedRule> Rules { get; }

    public QualifiedRules(IEnumerable<QualifiedRule> rules) => Rules = rules.ToList();

    public static QualifiedRules Empty { get; } = new(Array.Empty<QualifiedRule>());

    /// <summary>True if any rule references this target — i.e. the target is governed by
    /// qualified-match logic and must not be alias/fuzzy-matched blindly.</summary>
    public bool GovernsTarget(string target)
    {
        string t = Normalize(target);
        return Rules.Any(r => Normalize(r.Target) == t);
    }

    /// <summary>Does the source header satisfy the rule for this exact target?
    /// (contains every required token)</summary>
    public bool Qualifies(string target, string sourceHeader)
    {
        string t = Normalize(target);
        string s = Normalize(sourceHeader);
        var rule = Rules.FirstOrDefault(r => Normalize(r.Target) == t);
        if (rule is null || rule.RequireAll.Count == 0) return false;
        // Every slot must be satisfied by at least one of its alternatives.
        return rule.RequireAll.All(slot =>
            slot.Any(alt => s.Contains(Normalize(alt))));
    }

    /// <summary>True if the source header carries the ambiguous concept of the rule(s) for
    /// this target (so it's a candidate worth reviewing) but does NOT satisfy the full
    /// qualifier — i.e. it should be sent to manual review, not auto-mapped or ignored.</summary>
    public bool IsAmbiguousConceptOnly(string target, string sourceHeader)
    {
        string t = Normalize(target);
        string s = Normalize(sourceHeader);
        var rule = Rules.FirstOrDefault(r => Normalize(r.Target) == t);
        if (rule is null) return false;

        bool hasConcept = rule.Concept.Count > 0 &&
                          rule.Concept.All(tok => s.Contains(Normalize(tok)));
        return hasConcept && !Qualifies(target, sourceHeader);
    }

    /// <summary>
    /// Resolves any concept-only category mappings that depend on active insurance covers (GSCCategoryNo, GLCategoryNo, TPDCategoryNo).
    /// </summary>
    public HeaderColumn? ResolveActiveCategoryMapping(
        string targetLabel,
        IReadOnlyList<HeaderColumn> sources,
        IReadOnlySet<string> activeCovers)
    {
        bool isGscCat = string.Equals(targetLabel, "GSCCategoryNo", StringComparison.OrdinalIgnoreCase);
        bool isGlCat = string.Equals(targetLabel, "GLCategoryNo", StringComparison.OrdinalIgnoreCase);
        bool isTpdCat = string.Equals(targetLabel, "TPDCategoryNo", StringComparison.OrdinalIgnoreCase);

        if (!isGscCat && !isGlCat && !isTpdCat) return null;

        var conceptOnlySources = sources
            .Where(s => IsAmbiguousConceptOnly(targetLabel, s.Label))
            .ToList();

        if (conceptOnlySources.Count == 0) return null;

        bool gscActive = activeCovers.Contains("GSC");
        bool glActive = activeCovers.Contains("GL");
        bool tpdActive = activeCovers.Contains("TPD");

        bool shouldMapToThisTarget = false;
        if (gscActive)
        {
            shouldMapToThisTarget = isGscCat;
        }
        else
        {
            if (glActive && !tpdActive)
            {
                shouldMapToThisTarget = isGlCat;
            }
            else if (tpdActive && !glActive)
            {
                shouldMapToThisTarget = isTpdCat;
            }
        }

        return shouldMapToThisTarget ? conceptOnlySources[0] : null;
    }

    // ── Persistence ──────────────────────────────────────────────────────────
    public static QualifiedRules LoadFromFile(string path)
    {
        if (!File.Exists(path)) return Empty;
        return FromJson(File.ReadAllText(path));
    }

    public const string DefaultFileName = "qualified_rules.json";

    public static QualifiedRules LoadDefault()
    {
        string path = Path.Combine(AppContext.BaseDirectory, DefaultFileName);
        return LoadFromFile(path);
    }

    public static QualifiedRules FromJson(string json)
    {
        var dto = JsonSerializer.Deserialize<FileDto>(json, JsonOptions);
        if (dto?.Rules is null) return Empty;
        return new QualifiedRules(dto.Rules.Select(r => new QualifiedRule(
            r.Target ?? "",
            (r.RequireAll ?? new()).Select(slot => (IReadOnlyList<string>)slot.Values).ToList(),
            r.Concept ?? new())));
    }

    public string ToJson() => JsonSerializer.Serialize(
        new FileDto
        {
            Rules = Rules.Select(r => new RuleDto
            {
                Target = r.Target,
                RequireAll = r.RequireAll.Select(slot => new Slot { Values = slot.ToList() }).ToList(),
                Concept = r.Concept.ToList()
            }).ToList()
        }, JsonOptions);

    public void SaveToFile(string path) => File.WriteAllText(path, ToJson());

    private static string Normalize(string s) => HeaderNormalizer.NormalizeLoose(s);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed class FileDto { public List<RuleDto>? Rules { get; set; } }
    private sealed class RuleDto
    {
        public string? Target { get; set; }
        public List<Slot>? RequireAll { get; set; }
        public List<string>? Concept { get; set; }
    }

    /// <summary>One qualifier slot: a set of alternative tokens (OR). Authors may write
    /// it as a bare string (single alternative) or an array of strings.</summary>
    [JsonConverter(typeof(SlotConverter))]
    private sealed class Slot { public List<string> Values { get; set; } = new(); }

    private sealed class SlotConverter : JsonConverter<Slot>
    {
        public override Slot Read(ref Utf8JsonReader reader, Type t, JsonSerializerOptions o)
        {
            if (reader.TokenType == JsonTokenType.String)
                return new Slot { Values = new() { reader.GetString() ?? "" } };
            if (reader.TokenType == JsonTokenType.StartArray)
                return new Slot { Values = JsonSerializer.Deserialize<List<string>>(ref reader, o) ?? new() };
            throw new JsonException("requireAll slot must be a string or an array of strings.");
        }

        public override void Write(Utf8JsonWriter writer, Slot value, JsonSerializerOptions o)
        {
            // Emit a bare string when there's a single alternative, else an array.
            if (value.Values.Count == 1) writer.WriteStringValue(value.Values[0]);
            else JsonSerializer.Serialize(writer, value.Values, o);
        }
    }
}
