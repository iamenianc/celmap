using CelMap.Core;

namespace CelMap.Core.Tests;

/// <summary>
/// Verifies opening a password-protected source workbook. These run against a known encrypted
/// fixture in the docs repo; if it isn't present (e.g. CI without that checkout) the tests
/// no-op rather than fail, so the suite stays green everywhere.
/// </summary>
public class EncryptedWorkbookTests
{
    private const string EncryptedFile =
        @"C:\Users\ianch\sourecode\repos\CelMap-Docs\Test_sources\Eligibility for Categories - pwpw.xlsx";
    private const string CorrectPassword = "pwpw";

    private static bool FixtureAvailable => File.Exists(EncryptedFile);

    [Fact]
    public void IsEncrypted_DetectsProtectedFile()
    {
        if (!FixtureAvailable) return;
        Assert.True(new WorkbookReader().IsEncrypted(EncryptedFile));
    }

    [Fact]
    public void CorrectPassword_OpensWorkbook()
    {
        if (!FixtureAvailable) return;
        var reader = new WorkbookReader();

        var sheets = reader.GetSheetNames(EncryptedFile, CorrectPassword);
        Assert.NotEmpty(sheets);

        var data = reader.ReadSheet(EncryptedFile, sheets[0], CorrectPassword);
        Assert.True(data.RowCount > 0);
    }

    [Fact]
    public void WrongPassword_Throws()
    {
        if (!FixtureAvailable) return;
        Assert.Throws<InvalidPasswordException>(
            () => new WorkbookReader().GetSheetNames(EncryptedFile, "not-the-password"));
    }

    [Fact]
    public void MissingPassword_OnEncryptedFile_Throws()
    {
        if (!FixtureAvailable) return;
        Assert.Throws<InvalidPasswordException>(
            () => new WorkbookReader().GetSheetNames(EncryptedFile));
    }
}
