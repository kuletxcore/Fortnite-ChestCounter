using Xunit;

namespace ChestCounter.Tests;

/// <summary>
/// content/content.json is downloaded by every counter and built into both apps: a mistake makes players ignore the
/// file, and the apps refuse to start when the built-in copy is not valid. Checked here before it is pushed.
/// </summary>
public class ContentJsonTests
{
    private static string Json => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "content.json"));

    [Fact]
    public void RepositoryCopyIsValid()
    {
        Assert.Null(TreasureCatalog.Validate(Json));
    }

    [Fact]
    public void RepositoryCopyHasARevision()
    {
        Assert.True(TreasureCatalog.RevisionOf(Json) > 0);
    }

    [Fact]
    public void BuiltInCopyLoads()
    {
        // Reading any value parses the copy built into the assembly (it throws when that copy is not valid).
        Assert.NotEmpty(TreasureCatalog.Odds);
        Assert.NotEmpty(TreasureCatalog.Weeks);
    }

    [Fact]
    public void BrokenCopyIsRefused()
    {
        Assert.NotNull(TreasureCatalog.Validate(Json.Replace("\"schema\"", "\"schemaX\"")));
        Assert.NotNull(TreasureCatalog.Validate("{"));
        Assert.Null(TreasureCatalog.RevisionOf("{}"));
    }

    [Fact]
    public void WeeksDoNotOverlap()
    {
        var weeks = TreasureCatalog.Weeks.OrderBy(w => w.From).ToList();
        for (var i = 1; i < weeks.Count; i++)
        {
            Assert.True(weeks[i].From >= weeks[i - 1].To, $"week {weeks[i].Number} starts before week {weeks[i - 1].Number} ends");
        }
    }

    [Fact]
    public void PrizeIdsAreUnique()
    {
        var ids = TreasureCatalog.Weeks.SelectMany(w => w.Prizes).SelectMany(p => p.PrizeIds).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
