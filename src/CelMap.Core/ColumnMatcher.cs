using FuzzySharp;

namespace CelMap.Core;

public sealed class ColumnMatcher : IColumnMatcher
{
    private readonly AliasRules _aliases;
    private readonly QualifiedRules _qualified;

    public ColumnMatcher() : this(AliasRules.Empty, QualifiedRules.Empty) { }

    public ColumnMatcher(AliasRules aliases) : this(aliases, QualifiedRules.Empty) { }

    public ColumnMatcher(AliasRules aliases, QualifiedRules qualified)
    {
        _aliases = aliases;
        _qualified = qualified;
    }

    public MappingResult Match(
        IReadOnlyList<HeaderColumn> sourceHeaders,
        IReadOnlyList<HeaderColumn> targetHeaders,
        MatcherOptions options,
        Func<HeaderColumn, bool>? sourceColumnIsEmpty = null)
    {
        var mappings = new List<TargetColumnMapping>(targetHeaders.Count);

        var named = sourceHeaders.Where(s => !string.IsNullOrWhiteSpace(s.Label)).ToList();

        foreach (var target in targetHeaders)
        {
            // Qualified-rule targets (e.g. GSCCategoryNo) are governed by token-gated
            // logic that overrides alias/fuzzy — handle them first.
            if (_qualified.GovernsTarget(target.Label))
            {
                mappings.Add(ClassifyQualified(target, named, sourceColumnIsEmpty));
                continue;
            }

            // Score this target against every (named) source column. Rank by tier
            // first (Exact > Alias > Fuzzy), then by score — so a certainty always
            // outranks a coincidental fuzzy match of equal score.
            var candidates = named
                .Select(s => ScorePair(target.Label, s))
                .OrderByDescending(c => c.Kind)
                .ThenByDescending(c => c.Score)
                .ToList();

            mappings.Add(Classify(target, candidates, options, sourceColumnIsEmpty));
        }

        return new MappingResult(mappings);
    }

    private TargetColumnMapping Classify(
        HeaderColumn target,
        List<MatchCandidate> candidates,
        MatcherOptions options,
        Func<HeaderColumn, bool>? sourceColumnIsEmpty)
    {
        if (string.IsNullOrWhiteSpace(target.Label) || candidates.Count == 0 || candidates[0].Score == 0)
            return new TargetColumnMapping(target, null, 0, MatchStatus.Unmatched, candidates, false);

        var best = candidates[0];

        // Strict-group gate: if this target belongs to a strict synonym group, only an
        // exact or alias match may auto-apply. With no such hit, fuzzy is suppressed
        // entirely — the column is left Unmatched (manual-only), candidates hidden.
        if (_aliases.IsStrict(target.Label) && best.Kind == MatchKind.Fuzzy)
            return new TargetColumnMapping(
                target, null, 0, MatchStatus.Unmatched,
                Array.Empty<MatchCandidate>(), false);

        // Below threshold → flag for review, don't auto-apply.
        if (best.Score < options.ConfidenceThreshold)
            return new TargetColumnMapping(target, null, best.Score, MatchStatus.NeedsReview, candidates, false);

        // Ambiguity only applies WITHIN the best candidate's tier: a certainty
        // (exact/alias) is never made ambiguous by a lower-tier fuzzy match, even
        // at equal score. The runner-up must be the same kind AND score-close.
        var runnerUp = candidates.Count > 1 ? candidates[1] : null;
        if (runnerUp is not null &&
            runnerUp.Kind == best.Kind &&
            runnerUp.Score >= options.ConfidenceThreshold &&
            best.Score - runnerUp.Score <= options.AmbiguityMargin)
        {
            return new TargetColumnMapping(target, null, best.Score, MatchStatus.Ambiguous, candidates, false);
        }

        bool empty = sourceColumnIsEmpty?.Invoke(best.SourceColumn) ?? false;
        return new TargetColumnMapping(target, best.SourceColumn, best.Score, MatchStatus.Auto, candidates, empty);
    }

    /// <summary>Resolves a target governed by a qualified rule (token-gated). A source
    /// fully satisfying the rule's required tokens auto-applies; a source carrying only
    /// the ambiguous concept (e.g. bare "CategoryNo" with no GSC/GL/TPD) is forced to
    /// manual review; multiple full qualifiers are ambiguous; nothing relevant is
    /// Unmatched.</summary>
    private TargetColumnMapping ClassifyQualified(
        HeaderColumn target,
        List<HeaderColumn> sources,
        Func<HeaderColumn, bool>? sourceColumnIsEmpty)
    {
        var qualifiers = sources
            .Where(s => _qualified.Qualifies(target.Label, s.Label))
            .Select(s => new MatchCandidate(s, 100, MatchKind.Qualified))
            .ToList();

        if (qualifiers.Count == 1)
        {
            var hit = qualifiers[0];
            bool empty = sourceColumnIsEmpty?.Invoke(hit.SourceColumn) ?? false;
            return new TargetColumnMapping(
                target, hit.SourceColumn, 100, MatchStatus.Auto, qualifiers, empty);
        }

        if (qualifiers.Count > 1)
            // More than one source satisfies the qualifier → human picks.
            return new TargetColumnMapping(
                target, null, 100, MatchStatus.Ambiguous, qualifiers, false);

        // No full qualifier. Surface concept-only columns (the ambiguous base) for review.
        var conceptOnly = sources
            .Where(s => _qualified.IsAmbiguousConceptOnly(target.Label, s.Label))
            .Select(s => new MatchCandidate(s, 0, MatchKind.Fuzzy))
            .ToList();

        return conceptOnly.Count > 0
            ? new TargetColumnMapping(target, null, 0, MatchStatus.NeedsReview, conceptOnly, false)
            : new TargetColumnMapping(target, null, 0, MatchStatus.Unmatched, Array.Empty<MatchCandidate>(), false);
    }

    /// <summary>Scores one source column against a target label and tags how it matched.
    /// Exact (normalized-identical) and alias hits short-circuit to score 100 and a higher
    /// tier than fuzzy, skipping fuzzy entirely; otherwise token-set ratio so word order
    /// and extra tokens (units, parentheticals) don't tank the score.</summary>
    private MatchCandidate ScorePair(string targetLabel, HeaderColumn source)
    {
        string a = Normalize(targetLabel);
        string b = Normalize(source.Label);
        if (a.Length == 0 || b.Length == 0)
            return new MatchCandidate(source, 0, MatchKind.Fuzzy);

        // Exact normalized equality is the strongest certainty.
        if (a == b)
            return new MatchCandidate(source, 100, MatchKind.Exact);

        // Alias hit (e.g. DOB ↔ Date of Birth) is also a certainty, just below exact.
        if (_aliases.AreAliases(targetLabel, source.Label))
            return new MatchCandidate(source, 100, MatchKind.Alias);

        return new MatchCandidate(source, Fuzz.TokenSetRatio(a, b), MatchKind.Fuzzy);
    }

    private static string Normalize(string s) =>
        s.Replace('\n', ' ').Replace('\r', ' ').Trim().ToLowerInvariant();
}
