using System.Text.RegularExpressions;
using Xunit;

namespace ChestCounter.Tests;

/// <summary>
/// RELEASE_NOTES.md is the text of the GitHub release and, built into the exe, the "what's new" shown after an update:
/// each language must keep its bold heading and bullet list, and name the version the apps are built as.
/// </summary>
public class ReleaseNotesTests
{
    private static string Notes => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "RELEASE_NOTES.md"));

    private static string VersionOf(string file, string tag) =>
        Regex.Match(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "versions", file)), $"<{tag}>([^<]+)</{tag}>").Groups[1].Value;

    [Theory]
    [InlineData("en")]
    [InlineData("fr")]
    [InlineData("es")]
    public void EachLanguageHasWhatsNew(string language)
    {
        var bullets = ReleaseNotes.WhatsNew(Notes, language);
        Assert.True(bullets.Count >= 3, $"{language}: {bullets.Count} bullets");
    }

    [Fact]
    public void LanguagesHaveTheSameNumberOfBullets()
    {
        var counts = new[] { "en", "fr", "es" }.Select(l => ReleaseNotes.WhatsNew(Notes, l).Count).Distinct();
        Assert.Single(counts);
    }

    [Fact]
    public void NotesNameTheVersionBeingBuilt()
    {
        var version = VersionOf("exe.csproj", "Version");
        Assert.Contains($"What's new in {version}", Notes);
        Assert.Contains($"Nouveautés de la {version}", Notes);
        Assert.Contains($"Novedades de la {version}", Notes);
    }

    [Fact]
    public void ExeAndApkHaveTheSameVersion()
    {
        var exe = VersionOf("exe.csproj", "Version");
        Assert.Equal(exe, VersionOf("android.csproj", "Version"));
        Assert.Equal(exe, VersionOf("android.csproj", "ApplicationDisplayVersion"));
    }

    [Fact]
    public void NotesMentionBothDownloads()
    {
        Assert.Contains("ChestCounter.exe", Notes);
        Assert.Contains("ChestCounter.apk", Notes);
    }
}
