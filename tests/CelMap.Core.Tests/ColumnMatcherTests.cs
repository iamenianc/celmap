using CelMap.Core;

namespace CelMap.Core.Tests;

public class ColumnMatcherTests
{
    private static HeaderColumn H(int i, string label) => new(i, label);

    private static IReadOnlyList<HeaderColumn> Headers(params string[] labels) =>
        labels.Select((l, i) => new HeaderColumn(i, l)).ToList();

    private readonly ColumnMatcher _matcher = new();

    [Fact]
    public void ExactMatch_IsAutoApplied_WithFullScore()
    {
        var result = _matcher.Match(
            Headers("CustomerName"),
            Headers("CustomerName"),
            new MatcherOptions());

        var m = Assert.Single(result.Mappings);
        Assert.Equal(MatchStatus.Auto, m.Status);
        Assert.Equal(100, m.Score);
        Assert.Equal(0, m.MatchedSource!.ColumnIndex);
    }

    [Fact]
    public void CaseAndWhitespaceDifferences_StillAutoMatch()
    {
        var result = _matcher.Match(
            Headers("  customer   NAME "),
            Headers("CustomerName"),
            new MatcherOptions());

        Assert.Equal(MatchStatus.Auto, result.Mappings[0].Status);
    }

    [Fact]
    public void BelowThreshold_IsNeedsReview_AndNotApplied()
    {
        var result = _matcher.Match(
            Headers("Banana"),
            Headers("Region"),
            new MatcherOptions(ConfidenceThreshold: 80));

        var m = result.Mappings[0];
        Assert.Equal(MatchStatus.NeedsReview, m.Status);
        Assert.Null(m.MatchedSource);
        Assert.Empty(result.ToColumnMap());
    }

    [Fact]
    public void TwoNearTiedCandidates_AreFlaggedAmbiguous_NotAutoPicked()
    {
        // Two source columns that score identically against the target.
        var result = _matcher.Match(
            // Two FUZZY candidates (neither is exact-equal or an alias of the target)
            // that score within the margin → ambiguous within the fuzzy tier.
            Headers("Customer Naming", "Customer Naem"),
            Headers("Customer Name"),
            new MatcherOptions(ConfidenceThreshold: 80, AmbiguityMargin: 10));

        var m = result.Mappings[0];
        Assert.Equal(MatchStatus.Ambiguous, m.Status);
        Assert.Null(m.MatchedSource);                 // not silently picked
        Assert.True(m.Candidates.Count >= 2);
        Assert.Empty(result.ToColumnMap());            // ambiguous is not written
    }

    [Fact]
    public void NoSourceColumns_YieldsUnmatched()
    {
        var result = _matcher.Match(
            Headers("ignored"),
            Headers("Region"),
            new MatcherOptions());

        // "ignored" vs "Region" scores low → NeedsReview, but with NO source labels at all:
        var empty = _matcher.Match(
            Headers(""),                 // blank source header
            Headers("Region"),
            new MatcherOptions());
        Assert.Equal(MatchStatus.Unmatched, empty.Mappings[0].Status);
    }

    [Fact]
    public void Matches_ByName_NotPosition()
    {
        // Source order is reversed relative to target; matching must follow names.
        var result = _matcher.Match(
            Headers("Email", "CustomerName"),  // positions 0,1
            Headers("CustomerName", "Email"),  // positions 0,1
            new MatcherOptions());

        var map = result.ToColumnMap();   // source idx -> target idx
        Assert.Equal(0, map[1]);          // source "CustomerName" (idx1) -> target idx0
        Assert.Equal(1, map[0]);          // source "Email" (idx0) -> target idx1
    }

    [Fact]
    public void EmptySourceColumn_IsFlagged()
    {
        var result = _matcher.Match(
            Headers("Region"),
            Headers("Region"),
            new MatcherOptions(),
            sourceColumnIsEmpty: _ => true);

        Assert.True(result.Mappings[0].SourceColumnIsEmpty);
    }

    [Fact]
    public void Candidates_AreRankedHighToLow()
    {
        var result = _matcher.Match(
            Headers("Customer Name", "Customer ID", "Region"),
            Headers("CustomerName"),
            new MatcherOptions());

        var cands = result.Mappings[0].Candidates;
        for (int i = 1; i < cands.Count; i++)
            Assert.True(cands[i - 1].Score >= cands[i].Score);
    }

    [Fact]
    public void AliasMatch_ScoresExact_AndAutoApplies()
    {
        var aliases = new AliasRules(new[]
        {
            new AliasGroup(new[] { "DOB", "Date of Birth" }),
        });
        var matcher = new ColumnMatcher(aliases);

        // Fuzzy alone would NOT score "DOB" vs "Date of Birth" at 100.
        var result = matcher.Match(
            Headers("DOB"),               // source
            Headers("Date of Birth"),     // target
            new MatcherOptions());

        var m = result.Mappings[0];
        Assert.Equal(100, m.Score);
        Assert.Equal(MatchStatus.Auto, m.Status);
        Assert.Equal(0, m.MatchedSource!.ColumnIndex);
    }

    [Fact]
    public void AliasMatch_BeatsWeakFuzzyDistractor()
    {
        var aliases = new AliasRules(new[]
        {
            new AliasGroup(new[] { "DOB", "Date of Birth" }),
        });
        var matcher = new ColumnMatcher(aliases);

        // "Region" is an unrelated distractor; the DOB alias (100) must win cleanly.
        var result = matcher.Match(
            Headers("Region", "DOB"),
            Headers("Date of Birth"),
            new MatcherOptions());

        var m = result.Mappings[0];
        Assert.Equal(100, m.Score);
        Assert.Equal(MatchStatus.Auto, m.Status);
        Assert.Equal("DOB", m.MatchedSource!.Label);
    }

    [Fact]
    public void AliasOutranksFuzzy100_NotAmbiguous()
    {
        // "Date" is a token-subset of "Date of Birth" (fuzzy 100); "DOB" is an alias (100).
        // The alias is a higher tier than fuzzy, so it wins cleanly — NOT ambiguous.
        var aliases = new AliasRules(new[]
        {
            new AliasGroup(new[] { "DOB", "Date of Birth" }),
        });
        var matcher = new ColumnMatcher(aliases);

        var result = matcher.Match(
            Headers("Date", "DOB"),
            Headers("Date of Birth"),
            new MatcherOptions());

        var m = result.Mappings[0];
        Assert.Equal(MatchStatus.Auto, m.Status);
        Assert.Equal("DOB", m.MatchedSource!.Label);
        Assert.Equal(MatchKind.Alias, m.Candidates[0].Kind);
    }

    [Fact]
    public void ExactOutranksAlias_WhenBothPresent()
    {
        // Target "Date of Birth"; source has both an exact "Date of Birth" and the
        // alias "DOB". Exact is the top tier, so it wins.
        var aliases = new AliasRules(new[]
        {
            new AliasGroup(new[] { "DOB", "Date of Birth" }),
        });
        var matcher = new ColumnMatcher(aliases);

        var result = matcher.Match(
            Headers("DOB", "Date of Birth"),
            Headers("Date of Birth"),
            new MatcherOptions());

        var m = result.Mappings[0];
        Assert.Equal(MatchStatus.Auto, m.Status);
        Assert.Equal("Date of Birth", m.MatchedSource!.Label);
        Assert.Equal(MatchKind.Exact, m.Candidates[0].Kind);
    }

    [Fact]
    public void TwoExactMatches_AreStillAmbiguous_WithinSameTier()
    {
        // Ambiguity still fires within a tier: two exact-equal source columns.
        var result = _matcher.Match(
            Headers("Region", "region"),   // both normalize-equal to target
            Headers("Region"),
            new MatcherOptions());

        Assert.Equal(MatchStatus.Ambiguous, result.Mappings[0].Status);
    }

    // ── Strict synonym groups ────────────────────────────────────────────────

    private static ColumnMatcher StrictGenderMatcher() => new(new AliasRules(new[]
    {
        new AliasGroup(new[] { "Gender", "Sex" }, Strict: true),
    }));

    [Fact]
    public void StrictGroup_NoExactOrAlias_SuppressesFuzzy_AndIsUnmatched()
    {
        // Source "Gener Code" fuzzy-matches "Gender" decently, but Gender is strict:
        // no exact/alias hit → must be Unmatched (manual-only), candidates hidden.
        var result = StrictGenderMatcher().Match(
            Headers("Gener Code"),
            Headers("Gender"),
            new MatcherOptions(ConfidenceThreshold: 70));

        var m = result.Mappings[0];
        Assert.Equal(MatchStatus.Unmatched, m.Status);
        Assert.Null(m.MatchedSource);
        Assert.Empty(m.Candidates);                 // fuzzy hidden entirely
        Assert.Empty(result.ToColumnMap());
    }

    [Fact]
    public void StrictGroup_ExactHit_StillAutoApplies()
    {
        var result = StrictGenderMatcher().Match(
            Headers("Gener Code", "Gender"),
            Headers("Gender"),
            new MatcherOptions(ConfidenceThreshold: 70));

        var m = result.Mappings[0];
        Assert.Equal(MatchStatus.Auto, m.Status);
        Assert.Equal("Gender", m.MatchedSource!.Label);
    }

    [Fact]
    public void StrictGroup_AliasHit_StillAutoApplies()
    {
        // "Sex" is an alias of strict "Gender" → still auto-applies.
        var result = StrictGenderMatcher().Match(
            Headers("Sex"),
            Headers("Gender"),
            new MatcherOptions());

        var m = result.Mappings[0];
        Assert.Equal(MatchStatus.Auto, m.Status);
        Assert.Equal("Sex", m.MatchedSource!.Label);
        Assert.Equal(MatchKind.Alias, m.Candidates[0].Kind);
    }

    [Fact]
    public void LooseGroup_NoExactOrAlias_StillAllowsFuzzy()
    {
        // Same scenario but the group is LOOSE → fuzzy fallback permitted.
        var loose = new ColumnMatcher(new AliasRules(new[]
        {
            new AliasGroup(new[] { "Gender", "Sex" }),   // not strict
        }));

        var result = loose.Match(
            Headers("Gender Xx"),
            Headers("Gender"),
            new MatcherOptions(ConfidenceThreshold: 70));

        Assert.Equal(MatchStatus.Auto, result.Mappings[0].Status);
    }

    [Fact]
    public void WithoutAliasRules_DobIsNotAnExactMatch()
    {
        // Guards that the alias win above is actually due to the rule, not fuzzy luck.
        var result = _matcher.Match(
            Headers("DOB"),
            Headers("Date of Birth"),
            new MatcherOptions(ConfidenceThreshold: 80));

        Assert.NotEqual(MatchStatus.Auto, result.Mappings[0].Status);
    }
}
