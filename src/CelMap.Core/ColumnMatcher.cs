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

        // A source column only participates if it has a label AND has data underneath it.
        // Header-only columns with nothing below them (common in stacked/templated sheets —
        // e.g. a second "Member ID" header over an empty block) carry no values to map, so
        // they must not score, must not win, and must not create false ambiguity. Treat them
        // as if they don't exist.
        var named = sourceHeaders
            .Where(s => !string.IsNullOrWhiteSpace(s.Label))
            .Where(s => !(sourceColumnIsEmpty?.Invoke(s) ?? false))
            .ToList();

        foreach (var target in targetHeaders)
        {
            // Qualified-rule targets (e.g. GSCCategoryNo) are governed by token-gated
            // logic that overrides alias/fuzzy — handle them first.
            if (_qualified.GovernsTarget(target.Label))
            {
                mappings.Add(ClassifyQualified(target, named, options, sourceColumnIsEmpty));
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

        // Strict-group gate or fuzzy disabled gate: if this target belongs to a strict
        // synonym group OR fuzzy matching is disabled, and no exact/alias match was found,
        // the fuzzy match is suppressed and the column is left Unmatched.
        if ((_aliases.IsStrict(target.Label) || !options.FuzzyEnabled) && best.Kind == MatchKind.Fuzzy)
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
        MatcherOptions options,
        Func<HeaderColumn, bool>? sourceColumnIsEmpty)
    {
        // Category mapping resolution depending on parameter arguments:
        // GSCCategoryNo, GLCategoryNo, TPDCategoryNo
        bool isGscCat = string.Equals(target.Label, "GSCCategoryNo", StringComparison.OrdinalIgnoreCase);
        bool isGlCat = string.Equals(target.Label, "GLCategoryNo", StringComparison.OrdinalIgnoreCase);
        bool isTpdCat = string.Equals(target.Label, "TPDCategoryNo", StringComparison.OrdinalIgnoreCase);

        if (isGscCat || isGlCat || isTpdCat)
        {
            var conceptOnlySources = sources
                .Where(s => _qualified.IsAmbiguousConceptOnly(target.Label, s.Label))
                .ToList();

            if (conceptOnlySources.Count > 0)
            {
                var active = options.ActiveCovers ?? new HashSet<string>();
                bool gscActive = active.Contains("GSC");
                bool glActive = active.Contains("GL");
                bool tpdActive = active.Contains("TPD");

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

                if (shouldMapToThisTarget)
                {
                    var hitSource = conceptOnlySources[0];
                    bool empty = sourceColumnIsEmpty?.Invoke(hitSource) ?? false;
                    var candidates = new List<MatchCandidate> { new MatchCandidate(hitSource, 100, MatchKind.Qualified) };
                    return new TargetColumnMapping(target, hitSource, 100, MatchStatus.Auto, candidates, empty);
                }
            }
        }

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
        // For the EXACT check, collapse all whitespace so "Member ID" == "MemberID".
        string aTight = NormalizeTight(targetLabel);
        string bTight = NormalizeTight(source.Label);
        if (aTight.Length == 0 || bTight.Length == 0)
            return new MatchCandidate(source, 0, MatchKind.Fuzzy);

        // Exact (whitespace-insensitive) equality is the strongest certainty.
        if (aTight == bTight)
            return new MatchCandidate(source, 100, MatchKind.Exact);

        // Alias hit (e.g. DOB ↔ Date of Birth) is also a certainty, just below exact.
        if (_aliases.AreAliases(targetLabel, source.Label))
            return new MatchCandidate(source, 100, MatchKind.Alias);

        // For FUZZY scoring keep word boundaries: TokenSetRatio needs spaces to see shared
        // tokens, so "Email" vs "Email Address" scores 100 — collapsing them to one run
        // ("email" vs "emailaddress") would wrongly drop it to ~59.
        //
        // Score the source against the target's WHOLE synonym group and take the best: a
        // source that doesn't fuzz well to the literal target may fuzz strongly to a sibling
        // synonym (e.g. target "DOB" + group {Date of Birth, Birth Date} lets source
        // "Birth Dt" score against "Birth Date" instead of the unhelpful "DOB"). Stays Fuzzy
        // with the real ratio — not promoted to a certainty.
        string sourceLoose = NormalizeLoose(source.Label);
        int best = 0;
        foreach (var synonym in _aliases.SynonymsOf(targetLabel))
        {
            int score = Fuzz.TokenSetRatio(NormalizeLoose(synonym), sourceLoose);
            if (score > best) best = score;
        }
        return new MatchCandidate(source, best, MatchKind.Fuzzy);
    }

    /// <summary>Whitespace-INSENSITIVE form for the exact check: lower-cased, ALL whitespace
    /// removed, so "Member ID", "MemberID" and "member  id" collapse to one token. Punctuation
    /// kept as-is (aliases handle e.g. "D.O.B.").</summary>
    private static string NormalizeTight(string s)
    {
        Span<char> buffer = s.Length <= 256 ? stackalloc char[s.Length] : new char[s.Length];
        int n = 0;
        foreach (char c in s)
            if (!char.IsWhiteSpace(c))
                buffer[n++] = char.ToLowerInvariant(c);
        return new string(buffer[..n]);
    }

    /// <summary>Token-preserving form for fuzzy scoring: lower-cased, newlines folded to
    /// spaces, trimmed — but internal spaces kept so TokenSetRatio can match on shared words.</summary>
    private static string NormalizeLoose(string s) =>
        s.Replace('\n', ' ').Replace('\r', ' ').Trim().ToLowerInvariant();
}
