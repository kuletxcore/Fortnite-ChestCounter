using ChestCounter.Droid;
using Xunit;

namespace ChestCounter.Tests;

/// <summary>A match counted by hand on Android must count like a match read from a replay.</summary>
public class TallyRecordTests
{
    public TallyRecordTests() => L.Set("en");

    private static LiveTally Tally(DateTime start) => new()
    {
        StartUtc = start,
        LastTapUtc = start.AddMinutes(10),
        Chests = { 60, 300, 600 },
        Treasures = { 300 },
    };

    [Fact]
    public void RecordCountsAsBattleRoyale()
    {
        var m = TallyRecord.Build(Tally(DateTime.UtcNow.AddMinutes(-20)), "Playlist_NoBuildBR_Squad", null);
        Assert.True(GameModes.Counts(m));
        Assert.Equal(3, m.ChestsOpened);
        Assert.Equal(3, m.Chests.Count);
        Assert.Single(m.TreasureFinds);
        Assert.Equal(600, m.DurationSeconds);
    }

    [Fact]
    public void RecordJoinsTheAccountAlreadyCounted()
    {
        var sample = new MatchRecord { PlayerName = "Player", EpicId = "abc123" };
        var m = TallyRecord.Build(Tally(DateTime.UtcNow), "Playlist_DefaultSolo", sample);
        Assert.Equal("abc123", m.EpicId);
        Assert.Equal(Profiles.Key(sample), Profiles.Key(m));
    }

    [Fact]
    public void RecordShowsInTotalsOverlayAndReport()
    {
        var data = new TrackerData();
        data.Matches.Add(TallyRecord.Build(Tally(DateTime.UtcNow.AddMinutes(-20)), "Playlist_DefaultSquad", null));
        var view = Profiles.View(data, new Config());
        Assert.Equal(3, view.TotalChests);
        Assert.Equal("3", OverlayModel.Build(view, null, false, false).Total);
        var html = ReportWriter.Report(new Config(), view, Profiles.List(data));
        Assert.Contains(TreasureNames.UnknownEn, html);
    }
}
