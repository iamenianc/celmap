namespace CelMap.Core;

public enum MatchStatus
{
    /// <summary>Score above threshold, single clear winner — applied automatically.</summary>
    Auto,
    /// <summary>Best score is below threshold — left unset, flagged for manual review.</summary>
    NeedsReview,
    /// <summary>Two or more source columns scored similarly — candidates returned, not auto-picked.</summary>
    Ambiguous,
    /// <summary>No source column scored anything meaningful.</summary>
    Unmatched
}

/// <summary>How a candidate matched — higher tiers are certainties and outrank
/// lower ones even at equal score. Qualified &gt; Exact &gt; Alias &gt; Fuzzy.
/// <see cref="Qualified"/> is a token-gated domain rule (e.g. GSC + Category →
/// GSCCategoryNo) and is the most authoritative.</summary>
public enum MatchKind { Fuzzy = 0, Alias = 1, Exact = 2, Qualified = 3 }

/// <summary>A scored source-column candidate for a given target column.</summary>
public sealed record MatchCandidate(HeaderColumn SourceColumn, int Score, MatchKind Kind = MatchKind.Fuzzy);

/// <summary>Mapping outcome for a single target column.</summary>
public sealed record TargetColumnMapping(
    HeaderColumn TargetColumn,
    HeaderColumn? MatchedSource,   // null unless Status == Auto
    int Score,                     // score of the best candidate (0 if none)
    MatchStatus Status,
    IReadOnlyList<MatchCandidate> Candidates,  // ranked, for review/ambiguous
    bool SourceColumnIsEmpty       // true when the matched source column has no data
);

/// <summary>The full source→target mapping for one run.</summary>
public sealed record MappingResult(IReadOnlyList<TargetColumnMapping> Mappings)
{
    /// <summary>The applied column map (source col index → target col index) for the writer.</summary>
    public IReadOnlyDictionary<int, int> ToColumnMap()
    {
        var map = new Dictionary<int, int>();
        foreach (var m in Mappings)
        {
            if (m.Status == MatchStatus.Auto && m.MatchedSource is { } src)
                map[src.ColumnIndex] = m.TargetColumn.ColumnIndex;
        }
        return map;
    }
}
