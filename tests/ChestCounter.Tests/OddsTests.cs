using Xunit;

namespace ChestCounter.Tests;

/// <summary>The chance of having obtained a treasure at least once: 1 − Π(1 − p × boost).</summary>
public class OddsTests
{
    private static readonly TreasureOdds Odds = TreasureCatalog.Odds.First();

    [Fact]
    public void PlainChestsFollowTheFormula()
    {
        var p = 1.0 / Odds.OneIn;
        Assert.Equal(100 * Math.Log(1 - p), TreasureCatalog.LogNever(Odds, [], 100), 9);
        Assert.Equal(100 * p, TreasureCatalog.Expected(Odds, [], 100), 9);
    }

    [Fact]
    public void NoChestNoChance()
    {
        Assert.Equal(0, TreasureCatalog.LogNever(Odds, [], 0));
        Assert.Equal(0, TreasureCatalog.LogNever(Odds, [], -5)); // a negative adjustment never gives a chance
    }

    [Fact]
    public void MoreChestsMoreChance()
    {
        Assert.True(TreasureCatalog.LogNever(Odds, [], 200) < TreasureCatalog.LogNever(Odds, [], 100));
    }

    [Fact]
    public void TreasureHourBoostsTheBoostedKinds()
    {
        var hour = TreasureCatalog.TreasureHours.FirstOrDefault(h => h.Boosts.Any(b => b.Value > 1));
        if (hour == null)
        {
            return; // no boosted Treasure Hour in the current content.json
        }
        var (kind, _) = hour.Boosts.First(b => b.Value > 1);
        var odds = TreasureCatalog.Odds.First(o => o.Kind == kind);
        var during = hour.StartUtc.AddMinutes(1);
        var before = hour.StartUtc.AddHours(-3);
        Assert.True(TreasureCatalog.Expected(odds, [during], 0) > TreasureCatalog.Expected(odds, [before], 0));
    }
}
