using System.Text.Json;
using System.Text.Json.Serialization;

namespace CelMap.Core;

/// <summary>
/// One synonym group: any label in <see cref="Names"/> is considered the same
/// column as any other in the group. E.g. ["DOB", "Date of Birth", "Birth Date"].
/// Matching is bidirectional and order-independent.
/// <para><see cref="Strict"/> groups refuse fuzzy fallback: if a target label
/// belongs to a strict group and no source column matches it exactly or by alias,
/// the column is left Unmatched (manual-only) rather than fuzzy-guessed. Loose
/// (the default) groups fall through to fuzzy when no alias/exact hit lands.</para>
/// </summary>
public sealed record AliasGroup(IReadOnlyList<string> Names, bool Strict = false);

/// <summary>
/// A set of synonym groups used to short-circuit fuzzy matching. An alias hit is
/// treated as an exact match (score 100). Loadable from / savable to JSON so rules
/// persist across runs; Tracer 5 profiles will layer per-profile aliases on top.
/// </summary>
public sealed class AliasRules
{
    // normalized label -> group key (the group's first normalized name)
    private readonly Dictionary<string, string> _labelToGroup;
    // normalized labels that belong to a strict group (fuzzy fallback disallowed)
    private readonly HashSet<string> _strictLabels;
    // group key -> the original (un-normalized) names in that group, for fuzzy expansion
    private readonly Dictionary<string, List<string>> _groupMembers;

    public IReadOnlyList<AliasGroup> Groups { get; }

    public AliasRules(IEnumerable<AliasGroup> groups)
    {
        Groups = groups.ToList();
        _labelToGroup = new Dictionary<string, string>();
        _strictLabels = new HashSet<string>();
        _groupMembers = new Dictionary<string, List<string>>();

        foreach (var group in Groups)
        {
            var normalized = group.Names
                .Select(Normalize)
                .Where(n => n.Length > 0)
                .Distinct()
                .ToList();
            if (normalized.Count == 0) continue;

            string key = normalized[0];
            foreach (var name in normalized)
            {
                _labelToGroup[name] = key;   // last-writer-wins on overlap
                if (group.Strict) _strictLabels.Add(name);
            }

            // Keep the original spellings for fuzzy expansion (with word boundaries intact).
            var members = _groupMembers.TryGetValue(key, out var existing) ? existing : new List<string>();
            foreach (var original in group.Names)
                if (!string.IsNullOrWhiteSpace(original))
                    members.Add(original);
            _groupMembers[key] = members;
        }
    }

    public static AliasRules Empty { get; } = new(Array.Empty<AliasGroup>());

    /// <summary>Default filename for the shipped synonym rules.</summary>
    public const string DefaultFileName = "synonyms.json";

    /// <summary>Loads the default <c>synonyms.json</c> sitting next to the running
    /// assembly. Returns <see cref="Empty"/> if it isn't present.</summary>
    public static AliasRules LoadDefault()
    {
        string dir = AppContext.BaseDirectory;
        return LoadFromFile(Path.Combine(dir, DefaultFileName));
    }

    /// <summary>True if the two labels are the same column by alias (or are literally
    /// the same normalized text — an exact name is the trivial alias of itself).</summary>
    public bool AreAliases(string a, string b)
    {
        string na = Normalize(a);
        string nb = Normalize(b);
        if (na.Length == 0 || nb.Length == 0) return false;
        if (na == nb) return true;

        return _labelToGroup.TryGetValue(na, out var ga) &&
               _labelToGroup.TryGetValue(nb, out var gb) &&
               ga == gb;
    }

    /// <summary>True if the given label belongs to a strict synonym group — meaning
    /// it must be matched exactly or by alias, and fuzzy fallback is not allowed.</summary>
    public bool IsStrict(string label) => _strictLabels.Contains(Normalize(label));

    /// <summary>All synonym spellings in the given label's group (original casing/spacing),
    /// for fuzzy expansion — so a target fuzzy-scores against every sibling synonym, not just
    /// its own literal. If the label is in no group, returns just the label itself.</summary>
    public IReadOnlyList<string> SynonymsOf(string label)
    {
        if (_labelToGroup.TryGetValue(Normalize(label), out var key) &&
            _groupMembers.TryGetValue(key, out var members))
            return members;
        return new[] { label };
    }

    // ── Persistence ──────────────────────────────────────────────────────────
    public static AliasRules LoadFromFile(string path)
    {
        if (!File.Exists(path)) return Empty;
        string json = File.ReadAllText(path);
        return FromJson(json);
    }

    public static AliasRules FromJson(string json)
    {
        var dto = JsonSerializer.Deserialize<AliasFileDto>(json, JsonOptions);
        if (dto?.Groups is null) return Empty;
        return new AliasRules(dto.Groups.Select(g => new AliasGroup(g.Names, g.Strict)));
    }

    public string ToJson() =>
        JsonSerializer.Serialize(
            new AliasFileDto
            {
                Groups = Groups
                    .Select(g => new AliasGroupDto { Names = g.Names.ToList(), Strict = g.Strict })
                    .ToList()
            },
            JsonOptions);

    public void SaveToFile(string path) => File.WriteAllText(path, ToJson());

    private static string Normalize(string s) =>
        s.Replace('\n', ' ').Replace('\r', ' ').Trim().ToLowerInvariant();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault,
    };

    private sealed class AliasFileDto
    {
        public List<AliasGroupDto> Groups { get; set; } = new();
    }

    /// <summary>Reads either a bare array <c>["a","b"]</c> (loose) or an object
    /// <c>{"names":["a","b"],"strict":true}</c>. Writes the object form.</summary>
    [System.Text.Json.Serialization.JsonConverter(typeof(AliasGroupDtoConverter))]
    private sealed class AliasGroupDto
    {
        public List<string> Names { get; set; } = new();
        public bool Strict { get; set; }
    }

    private sealed class AliasGroupDtoConverter : System.Text.Json.Serialization.JsonConverter<AliasGroupDto>
    {
        public override AliasGroupDto Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.StartArray)
            {
                var names = JsonSerializer.Deserialize<List<string>>(ref reader, options) ?? new();
                return new AliasGroupDto { Names = names, Strict = false };
            }

            if (reader.TokenType == JsonTokenType.StartObject)
            {
                var dto = new AliasGroupDto();
                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.EndObject) break;
                    if (reader.TokenType != JsonTokenType.PropertyName) continue;

                    string? prop = reader.GetString();
                    reader.Read();
                    if (string.Equals(prop, "names", StringComparison.OrdinalIgnoreCase))
                        dto.Names = JsonSerializer.Deserialize<List<string>>(ref reader, options) ?? new();
                    else if (string.Equals(prop, "strict", StringComparison.OrdinalIgnoreCase))
                        dto.Strict = reader.TokenType == JsonTokenType.True;
                    else
                        reader.Skip();
                }
                return dto;
            }

            throw new JsonException("Alias group must be an array or an object with 'names'.");
        }

        public override void Write(Utf8JsonWriter writer, AliasGroupDto value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WritePropertyName("names");
            JsonSerializer.Serialize(writer, value.Names, options);
            if (value.Strict)
                writer.WriteBoolean("strict", true);
            writer.WriteEndObject();
        }
    }
}
