using System.Collections.Generic;

namespace CelMap.Core;

public sealed record MatcherOptions(
    int ConfidenceThreshold = 90,   // min score (0–100) to auto-apply
    int AmbiguityMargin = 5,         // if 2nd-best is within this of best, it's ambiguous
    bool FuzzyEnabled = true,
    IReadOnlySet<string>? ActiveCovers = null
);

public interface IColumnMatcher
{
    MappingResult Match(
        IReadOnlyList<HeaderColumn> sourceHeaders,
        IReadOnlyList<HeaderColumn> targetHeaders,
        MatcherOptions options,
        Func<HeaderColumn, bool>? sourceColumnIsEmpty = null);
}
