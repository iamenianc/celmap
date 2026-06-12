using CelMap.Core;

namespace CelMap.Core.Tests;

public class QualifiedRulesTests
{
    private static IReadOnlyList<HeaderColumn> H(params string[] labels) =>
        labels.Select((l, i) => new HeaderColumn(i, l)).ToList();

    // helper: build a requireAll from slots, each slot a set of alternatives
    private static IReadOnlyList<IReadOnlyList<string>> Slots(params string[][] slots) =>
        slots.Select(s => (IReadOnlyList<string>)s.ToList()).ToList();

    private static QualifiedRules CategoryRules() => new(new[]
    {
        new QualifiedRule("GSCCategoryNo", Slots(new[]{"category"}, new[]{"gsc","gip","income protection","salary continuance"}), new[]{"category"}),
        new QualifiedRule("GLCategoryNo",  Slots(new[]{"category"}, new[]{"gl","group life","death"}),                            new[]{"category"}),
        new QualifiedRule("TPDCategoryNo", Slots(new[]{"category"}, new[]{"tpd","permanent disability"}),                         new[]{"category"}),
    });

    private ColumnMatcher Matcher() => new(AliasRules.Empty, CategoryRules());

    [Fact]
    public void QualifiedSource_AutoMapsToTheRightTarget()
    {
        // "GSC Category" satisfies category + gsc → GSCCategoryNo.
        var result = Matcher().Match(
            H("GSC Category"),
            H("GSCCategoryNo"),
            new MatcherOptions());

        var m = result.Mappings[0];
        Assert.Equal(MatchStatus.Auto, m.Status);
        Assert.Equal("GSC Category", m.MatchedSource!.Label);
        Assert.Equal(MatchKind.Qualified, m.Candidates[0].Kind);
    }

    [Fact]
    public void CategoryNoPlusGscToken_InOneHeader_Qualifies()
    {
        // "CategoryNo" + the word "GSC" present in the header.
        var result = Matcher().Match(
            H("GSC CategoryNo"),
            H("GSCCategoryNo"),
            new MatcherOptions());

        Assert.Equal(MatchStatus.Auto, result.Mappings[0].Status);
    }

    [Fact]
    public void BareCategoryNo_NoQualifier_ForcesManualReview()
    {
        // Bare "CategoryNo" has the concept but no GSC/GL/TPD → NeedsReview, NOT auto.
        var result = Matcher().Match(
            H("CategoryNo"),
            H("GSCCategoryNo"),
            new MatcherOptions());

        var m = result.Mappings[0];
        Assert.Equal(MatchStatus.NeedsReview, m.Status);
        Assert.Null(m.MatchedSource);
        Assert.Single(m.Candidates);                       // the concept-only column, surfaced
        Assert.Equal("CategoryNo", m.Candidates[0].SourceColumn.Label);
        Assert.Empty(result.ToColumnMap());                // never written
    }

    [Fact]
    public void WrongQualifier_DoesNotMatchTarget()
    {
        // "GL Category" must NOT auto-map to GSCCategoryNo (wrong qualifier);
        // it carries the concept though, so it's review-worthy for GSC.
        var result = Matcher().Match(
            H("GL Category"),
            H("GSCCategoryNo"),
            new MatcherOptions());

        Assert.Equal(MatchStatus.NeedsReview, result.Mappings[0].Status);
    }

    [Fact]
    public void GscAndGl_RouteToTheirOwnTargets()
    {
        var result = Matcher().Match(
            H("GSC Category", "GL Category"),
            H("GSCCategoryNo", "GLCategoryNo"),
            new MatcherOptions());

        var gsc = result.Mappings.First(m => m.TargetColumn.Label == "GSCCategoryNo");
        var gl  = result.Mappings.First(m => m.TargetColumn.Label == "GLCategoryNo");

        Assert.Equal("GSC Category", gsc.MatchedSource!.Label);
        Assert.Equal("GL Category",  gl.MatchedSource!.Label);
    }

    [Fact]
    public void TwoSourcesQualifyingSameTarget_AreAmbiguous()
    {
        var result = Matcher().Match(
            H("GSC Category", "GSC CategoryNo"),
            H("GSCCategoryNo"),
            new MatcherOptions());

        var m = result.Mappings[0];
        Assert.Equal(MatchStatus.Ambiguous, m.Status);
        Assert.Equal(2, m.Candidates.Count);
    }

    [Fact]
    public void NoRelevantSource_IsUnmatched()
    {
        var result = Matcher().Match(
            H("Salary", "DOB"),
            H("GSCCategoryNo"),
            new MatcherOptions());

        Assert.Equal(MatchStatus.Unmatched, result.Mappings[0].Status);
    }

    [Theory]
    // Insurance synonyms for the GSC benefit type all qualify GSCCategoryNo.
    [InlineData("GIP Category")]
    [InlineData("Income Protection Category")]
    [InlineData("Salary Continuance CategoryNo")]
    public void GscSynonymQualifiers_AllMapToGsc(string sourceLabel)
    {
        var result = Matcher().Match(
            H(sourceLabel),
            H("GSCCategoryNo"),
            new MatcherOptions());

        Assert.Equal(MatchStatus.Auto, result.Mappings[0].Status);
        Assert.Equal(sourceLabel, result.Mappings[0].MatchedSource!.Label);
    }

    [Fact]
    public void DeathIsGl_DeathCategory_MapsToGlCategoryNo()
    {
        // "Death" is a synonym for GL (Group Life).
        var result = Matcher().Match(
            H("Death Category"),
            H("GLCategoryNo"),
            new MatcherOptions());

        Assert.Equal(MatchStatus.Auto, result.Mappings[0].Status);
    }

    [Fact]
    public void GipQualifier_DoesNotMatchGlTarget()
    {
        // GIP ≡ GSC, so "GIP Category" must NOT auto-map to GLCategoryNo.
        var result = Matcher().Match(
            H("GIP Category"),
            H("GLCategoryNo"),
            new MatcherOptions());

        Assert.NotEqual(MatchStatus.Auto, result.Mappings[0].Status);
    }

    [Fact]
    public void JsonRoundTrip_PreservesRules_IncludingSynonymSlots()
    {
        var json = CategoryRules().ToJson();
        var reloaded = QualifiedRules.FromJson(json);

        Assert.True(reloaded.Qualifies("GSCCategoryNo", "GSC Category"));
        Assert.True(reloaded.Qualifies("GSCCategoryNo", "Income Protection Category")); // synonym survives
        Assert.False(reloaded.Qualifies("GSCCategoryNo", "CategoryNo"));
        Assert.True(reloaded.IsAmbiguousConceptOnly("GSCCategoryNo", "CategoryNo"));
        Assert.True(reloaded.GovernsTarget("GLCategoryNo"));
    }
}
