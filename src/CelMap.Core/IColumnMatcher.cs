using System.Collections.Generic;

namespace CelMap.Core;

public sealed record MatcherOptions(
    int ConfidenceThreshold = 90,   // min score (0–100) to auto-apply
    int AmbiguityMargin = 5,         // if 2nd-best is within this of best, it's ambiguous
    bool FuzzyEnabled = true,         // auto-apply strong fuzzy matches (>= ConfidenceThreshold)
    IReadOnlySet<string>? ActiveCovers = null,
    bool WeakFuzzyEnabled = false,    // also auto-apply weaker fuzzy matches down to WeakFuzzyThreshold
    int WeakFuzzyThreshold = 70       // min score (0–100) for a weak fuzzy match when WeakFuzzyEnabled
)
{
    /// <summary>The effective floor a fuzzy match must clear to auto-apply: the weak floor
    /// when weak fuzzy is enabled, otherwise the strong confidence threshold. Requires strong
    /// fuzzy to be on — weak fuzzy is a relaxation of it, not an independent mode.</summary>
    public int EffectiveFuzzyFloor =>
        FuzzyEnabled && WeakFuzzyEnabled ? WeakFuzzyThreshold : ConfidenceThreshold;
};

public interface IColumnMatcher
{
    MappingResult Match(
        IReadOnlyList<HeaderColumn> sourceHeaders,
        IReadOnlyList<HeaderColumn> targetHeaders,
        MatcherOptions options,
        Func<HeaderColumn, bool>? sourceColumnIsEmpty = null);
}
