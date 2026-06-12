using CelMap.Core;

namespace CelMap.Core.Tests;

public class AliasRulesTests
{
    private static AliasRules Rules() => new(new[]
    {
        new AliasGroup(new[] { "DOB", "Date of Birth", "Birth Date" }),
        new AliasGroup(new[] { "Phone", "Phone Number", "Telephone" }),
    });

    [Fact]
    public void AreAliases_BidirectionalWithinGroup()
    {
        var r = Rules();
        Assert.True(r.AreAliases("DOB", "Date of Birth"));
        Assert.True(r.AreAliases("Date of Birth", "DOB"));   // reverse
        Assert.True(r.AreAliases("DOB", "Birth Date"));      // any pair in group
    }

    [Fact]
    public void AreAliases_IsCaseAndWhitespaceInsensitive()
    {
        var r = Rules();
        Assert.True(r.AreAliases("  dob ", "DATE OF BIRTH"));
    }

    [Fact]
    public void AreAliases_DifferentGroups_AreNotAliases()
    {
        var r = Rules();
        Assert.False(r.AreAliases("DOB", "Phone"));
    }

    [Fact]
    public void AreAliases_IdenticalNormalizedText_IsTrue_EvenWithoutRules()
    {
        Assert.True(AliasRules.Empty.AreAliases("CustomerName", "customername"));
    }

    [Fact]
    public void AreAliases_BlankInputs_AreFalse()
    {
        Assert.False(AliasRules.Empty.AreAliases("", "x"));
        Assert.False(AliasRules.Empty.AreAliases("x", "   "));
    }

    [Fact]
    public void JsonRoundTrip_PreservesGroups()
    {
        var original = Rules();
        string json = original.ToJson();
        var reloaded = AliasRules.FromJson(json);

        Assert.True(reloaded.AreAliases("DOB", "Date of Birth"));
        Assert.True(reloaded.AreAliases("Phone", "Telephone"));
        Assert.False(reloaded.AreAliases("DOB", "Phone"));
    }

    [Fact]
    public void FromJson_MissingOrEmpty_ReturnsEmptyRules()
    {
        Assert.Empty(AliasRules.FromJson("{}").Groups);
        Assert.Empty(AliasRules.FromJson("{\"groups\":[]}").Groups);
    }

    [Fact]
    public void Strict_DefaultsFalse_AndFlagsMembers()
    {
        var r = new AliasRules(new[]
        {
            new AliasGroup(new[] { "Gender", "Sex" }, Strict: true),
            new AliasGroup(new[] { "Phone", "Telephone" }),  // loose by default
        });

        Assert.True(r.IsStrict("Gender"));
        Assert.True(r.IsStrict("sex"));        // normalized
        Assert.False(r.IsStrict("Phone"));
        Assert.False(r.IsStrict("Unknown"));
    }

    [Fact]
    public void Json_ReadsBothBareArrayAndObjectForms()
    {
        // bare array = loose; object with strict:true = strict. Both in one file.
        string json = """
        {
          "groups": [
            [ "Phone", "Telephone" ],
            { "names": [ "Gender", "Sex" ], "strict": true }
          ]
        }
        """;
        var r = AliasRules.FromJson(json);

        Assert.True(r.AreAliases("Phone", "Telephone"));
        Assert.False(r.IsStrict("Phone"));      // bare array → loose
        Assert.True(r.AreAliases("Gender", "Sex"));
        Assert.True(r.IsStrict("Gender"));      // object → strict
    }

    [Fact]
    public void JsonRoundTrip_PreservesStrictFlag()
    {
        var original = new AliasRules(new[]
        {
            new AliasGroup(new[] { "Gender", "Sex" }, Strict: true),
            new AliasGroup(new[] { "Phone", "Telephone" }),
        });

        var reloaded = AliasRules.FromJson(original.ToJson());

        Assert.True(reloaded.IsStrict("Gender"));
        Assert.False(reloaded.IsStrict("Phone"));
    }
}
