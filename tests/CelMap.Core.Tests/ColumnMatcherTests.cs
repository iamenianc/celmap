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
    public void FuzzyMatch_DoesNotReuse_SourceClaimedByACertainty()
    {
        // One source column ("Email") exact-matches the "Email" target and would also
        // fuzzy-match "Email Address" (TokenSetRatio 100). The certainty must win the
        // source outright; the fuzzy target must be left Unmatched rather than reuse it.
        var result = _matcher.Match(
            Headers("Email"),
            Headers("Email", "Email Address"),
            new MatcherOptions(ConfidenceThreshold: 90));

        var emailExact = result.Mappings.Single(m => m.TargetColumn.Label == "Email");
        var emailFuzzy = result.Mappings.Single(m => m.TargetColumn.Label == "Email Address");

        Assert.Equal(MatchStatus.Auto, emailExact.Status);
        Assert.Equal(0, emailExact.MatchedSource!.ColumnIndex);

        Assert.Equal(MatchStatus.Unmatched, emailFuzzy.Status);
        Assert.Null(emailFuzzy.MatchedSource);

        // The source is written to exactly one target.
        Assert.Single(result.ToColumnMap());
    }

    [Fact]
    public void TwoFuzzyTargets_CompetingForOneSource_OnlyFirstClaimsIt()
    {
        // No certainty here — two targets both fuzzy-match the single source. The first
        // (in target order) claims it; the second must not reuse the same source.
        var result = _matcher.Match(
            Headers("Customer Name"),
            Headers("Customer Naming", "Customer Naem"),
            new MatcherOptions(ConfidenceThreshold: 80));

        var autos = result.Mappings.Where(m => m.Status == MatchStatus.Auto).ToList();
        Assert.Single(autos);                          // only one target keeps the source
        Assert.Single(result.ToColumnMap());           // and it's written exactly once
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
    public void EmptySourceColumn_IsExcluded_NotMatched()
    {
        // A source column with a header but no data underneath carries nothing to map,
        // so it must not participate: the only source drops out and the target is left
        // Unmatched rather than matched-but-flagged.
        var result = _matcher.Match(
            Headers("Region"),
            Headers("Region"),
            new MatcherOptions(),
            sourceColumnIsEmpty: _ => true);

        var m = result.Mappings[0];
        Assert.Equal(MatchStatus.Unmatched, m.Status);
        Assert.Null(m.MatchedSource);
        Assert.False(m.SourceColumnIsEmpty);   // nothing empty was ever picked
    }

    [Fact]
    public void DuplicateHeader_OneEmpty_AutoMatchesTheNonEmptyOne()
    {
        // The Dyson UAT case: "Member ID" appears twice, one block empty. The empty
        // duplicate must be ignored so the populated one auto-matches instead of the
        // pair colliding into a false Ambiguous.
        var src = Headers("Member ID", "Member ID");   // col 0 populated, col 1 empty
        var result = _matcher.Match(
            src,
            Headers("Member ID"),
            new MatcherOptions(),
            sourceColumnIsEmpty: s => s.ColumnIndex == 1);

        var m = result.Mappings[0];
        Assert.Equal(MatchStatus.Auto, m.Status);
        Assert.Equal(0, m.MatchedSource!.ColumnIndex);
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
    public void Fuzzy_ScoresAgainstWholeSynonymGroup_NotJustLiteralTarget()
    {
        // Target "DOB" fuzzes terribly against a real source like "Birth Date" (different
        // letters entirely). But "Birth Date" is a synonym of DOB, so a near-miss spelling
        // "Birth Dt" should fuzzy-match strongly via the GROUP member "Birth Date" — even
        // though it's not an exact alias and scores ~nothing against the literal "DOB".
        var matcher = new ColumnMatcher(new AliasRules(new[]
        {
            new AliasGroup(new[] { "DOB", "Date of Birth", "Birth Date" }),
        }));

        var result = matcher.Match(
            Headers("Birth Dt"),
            Headers("DOB"),
            new MatcherOptions(ConfidenceThreshold: 80));

        var m = result.Mappings[0];
        // Without group expansion this would be a sub-threshold NeedsReview against "DOB".
        Assert.Equal(MatchStatus.Auto, m.Status);
        Assert.Equal("Birth Dt", m.MatchedSource!.Label);
        Assert.Equal(MatchKind.Fuzzy, m.Candidates[0].Kind);   // stays fuzzy, not promoted
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

    [Fact]
    public void Match_CategoryColumn_ResolvesBasedOnActiveCovers()
    {
        var matcher = new ColumnMatcher(AliasRules.Empty, QualifiedRules.LoadDefault());
        
        // Scenario 1: GSC is active -> maps to GSCCategoryNo
        var optionsGsc = new MatcherOptions(FuzzyEnabled: true, ActiveCovers: new HashSet<string> { "GSC", "GL", "TPD" });
        var resGsc = matcher.Match(Headers("CategoryNo"), Headers("GSCCategoryNo", "GLCategoryNo", "TPDCategoryNo"), optionsGsc);
        Assert.Equal(MatchStatus.Auto, resGsc.Mappings[0].Status); // GSCCategoryNo mapped
        Assert.Equal(MatchStatus.NeedsReview, resGsc.Mappings[1].Status); // GLCategoryNo needs review
        Assert.Equal(MatchStatus.NeedsReview, resGsc.Mappings[2].Status); // TPDCategoryNo needs review

        // Scenario 2: GL is active only -> maps to GLCategoryNo
        var optionsGl = new MatcherOptions(FuzzyEnabled: true, ActiveCovers: new HashSet<string> { "GL" });
        var resGl = matcher.Match(Headers("CategoryNo"), Headers("GSCCategoryNo", "GLCategoryNo", "TPDCategoryNo"), optionsGl);
        Assert.Equal(MatchStatus.NeedsReview, resGl.Mappings[0].Status); // GSCCategoryNo needs review
        Assert.Equal(MatchStatus.Auto, resGl.Mappings[1].Status); // GLCategoryNo mapped
        Assert.Equal(MatchStatus.NeedsReview, resGl.Mappings[2].Status); // TPDCategoryNo needs review

        // Scenario 3: GL and TPD both active (without GSC) -> ambiguous/unmapped -> needs review
        var optionsBoth = new MatcherOptions(FuzzyEnabled: true, ActiveCovers: new HashSet<string> { "GL", "TPD" });
        var resBoth = matcher.Match(Headers("CategoryNo"), Headers("GSCCategoryNo", "GLCategoryNo", "TPDCategoryNo"), optionsBoth);
        Assert.Equal(MatchStatus.NeedsReview, resBoth.Mappings[0].Status); // GSCCategoryNo needs review
        Assert.Equal(MatchStatus.NeedsReview, resBoth.Mappings[1].Status); // GLCategoryNo needs review
        Assert.Equal(MatchStatus.NeedsReview, resBoth.Mappings[2].Status); // TPDCategoryNo needs review
    }
}
