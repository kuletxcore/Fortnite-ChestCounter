using ChestCounter.Droid;
using Xunit;

namespace ChestCounter.Tests;

/// <summary>"Reset all counts" and its undo, on a real data folder.</summary>
public sealed class CounterResetTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "cc-reset-" + Guid.NewGuid().ToString("N"));
    private readonly Store _store;

    public CounterResetTests()
    {
        L.Set("en");
        _store = new Store(_folder);
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { }
    }

    private static MatchRecord Match(string id, int chests, DateTime start) => new()
    {
        SessionId = id,
        MatchStartUtc = start,
        DurationSeconds = 600,
        ChestsOpened = chests,
        Playlist = "Playlist_DefaultSquad",
        Map = GameModes.SeasonMaps[0],
        PlayerName = "Me",
    };

    /// <summary>3 matches (30 chests), 100 chests added by hand, 2 replays already read, 1 treasure ticked.</summary>
    private TrackerData Sample()
    {
        var d = new TrackerData();
        var t = new DateTime(2026, 11, 18, 18, 0, 0, DateTimeKind.Utc);
        d.Matches.Add(Match("a", 5, t));
        d.Matches.Add(Match("b", 10, t.AddHours(1)));
        d.Matches.Add(Match("c", 15, t.AddHours(2)));
        d.ManualChests["Me"] = 100;
        d.ManualHistory["Me"] = new List<ManualEntry> { new() { Id = "x1", Chests = 100 } };
        d.ProcessedFiles["UnsavedReplay-1.replay|10|1"] = "a";
        d.ProcessedFiles["UnsavedReplay-2.replay|10|2"] = "skipped";
        d.Obtained["Me"] = new List<string> { "w1-outfit" };
        _store.SaveData(d);
        return d;
    }

    private static int Total(TrackerData d) => Profiles.View(d, new Config()).TotalChests;

    [Fact]
    public void ResetClearsEveryCount()
    {
        var d = Sample();
        Assert.Equal(130, Total(d));

        var summary = CounterReset.ResetAll(_store, d);

        Assert.Equal(new CounterReset.Summary(3, 130), summary);
        Assert.Empty(d.Matches);
        Assert.Empty(d.ManualChests);
        Assert.Empty(d.ManualHistory);
        Assert.Equal(0, Total(d));
        Assert.Equal("0", OverlayModel.Build(Profiles.View(d, new Config()), null, false, false).Total);
    }

    [Fact]
    public void ResetKeepsReadReplaysAndCollection()
    {
        var d = Sample();
        CounterReset.ResetAll(_store, d);
        // Replays already read must not come back as new matches; owned treasures don't change.
        Assert.Equal(2, d.ProcessedFiles.Count);
        Assert.Equal(new[] { "w1-outfit" }, d.Obtained["Me"]);
    }

    [Fact]
    public void ResetIsSavedToDisk()
    {
        var d = Sample();
        CounterReset.ResetAll(_store, d);
        var reloaded = new Store(_folder).LoadData();
        Assert.Empty(reloaded.Matches);
        Assert.Empty(reloaded.ManualChests);
        Assert.Equal(2, reloaded.ProcessedFiles.Count);
    }

    [Fact]
    public void ResetKeepsABackupOfEverything()
    {
        var d = Sample();
        CounterReset.ResetAll(_store, d);
        Assert.True(CounterReset.CanRestore(_store));
        var backup = _store.LoadDataFile(CounterReset.BackupPath(_store))!;
        Assert.Equal(3, backup.Matches.Count);
        Assert.Equal(100, backup.ManualChests["Me"]);
    }

    [Fact]
    public void ResetIncludesUnsavedChanges()
    {
        var d = Sample();
        d.Matches.Add(Match("d", 4, DateTime.UtcNow)); // counted but not saved yet
        var summary = CounterReset.ResetAll(_store, d);
        Assert.Equal(4, summary.Matches);
        Assert.Equal(4, _store.LoadDataFile(CounterReset.BackupPath(_store))!.Matches.Count);
    }

    [Fact]
    public void UndoBringsEverythingBack()
    {
        var d = Sample();
        CounterReset.ResetAll(_store, d);

        Assert.True(CounterReset.Restore(_store, d));

        Assert.Equal(130, Total(d));
        Assert.Equal(new[] { "a", "b", "c" }, d.Matches.Select(m => m.SessionId));
        Assert.Equal(100, d.ManualChests["Me"]);
        Assert.Single(d.ManualHistory["Me"]);
        Assert.False(CounterReset.CanRestore(_store));
        Assert.Equal(130, Total(new Store(_folder).LoadData())); // saved
    }

    [Fact]
    public void UndoKeepsWhatWasCountedSince()
    {
        var d = Sample();
        CounterReset.ResetAll(_store, d);
        d.Matches.Add(Match("new", 8, DateTime.UtcNow));
        d.ManualChests["Me"] = 20;
        d.ManualHistory["Me"] = new List<ManualEntry> { new() { Id = "x2", Chests = 20 } };

        Assert.True(CounterReset.Restore(_store, d));

        Assert.Equal(new[] { "a", "b", "c", "new" }, d.Matches.Select(m => m.SessionId));
        Assert.Equal(120, d.ManualChests["Me"]);
        Assert.Equal(new[] { "x1", "x2" }, d.ManualHistory["Me"].Select(e => e.Id));
        Assert.Equal(30 + 8 + 120, Total(d));
    }

    [Fact]
    public void UndoDoesNotDuplicateMatches()
    {
        var d = Sample();
        CounterReset.ResetAll(_store, d);
        d.Matches.Add(Match("b", 10, DateTime.UtcNow)); // the same replay read again after the reset
        CounterReset.Restore(_store, d);
        Assert.Equal(3, d.Matches.Count);
        Assert.Single(d.Matches, m => m.SessionId == "b");
    }

    [Fact]
    public void UndoBringsBackReplaysReadBeforeTheReset()
    {
        var d = Sample();
        CounterReset.ResetAll(_store, d);
        d.ProcessedFiles.Clear();
        d.ProcessedFiles["UnsavedReplay-3.replay|10|3"] = "e";
        CounterReset.Restore(_store, d);
        Assert.Equal(3, d.ProcessedFiles.Count);
    }

    [Fact]
    public void UndoWithoutBackupChangesNothing()
    {
        var d = Sample();
        Assert.False(CounterReset.CanRestore(_store));
        Assert.False(CounterReset.Restore(_store, d));
        Assert.Equal(130, Total(d));
    }

    [Fact]
    public void UndoTwiceOnlyWorksOnce()
    {
        var d = Sample();
        CounterReset.ResetAll(_store, d);
        Assert.True(CounterReset.Restore(_store, d));
        Assert.False(CounterReset.Restore(_store, d));
        Assert.Equal(3, d.Matches.Count);
    }

    [Fact]
    public void SecondResetKeepsTheLatestBackup()
    {
        var d = Sample();
        CounterReset.ResetAll(_store, d);
        d.Matches.Add(Match("after", 2, DateTime.UtcNow));
        CounterReset.ResetAll(_store, d);

        // Only the last reset can be undone: it brings back what it cleared (the match counted in between).
        CounterReset.Restore(_store, d);
        Assert.Equal(new[] { "after" }, d.Matches.Select(m => m.SessionId));
    }

    [Fact]
    public void UnreadableBackupIsNotRestored()
    {
        var d = Sample();
        CounterReset.ResetAll(_store, d);
        File.WriteAllText(CounterReset.BackupPath(_store), "{ not json");
        Assert.False(CounterReset.Restore(_store, d));
        Assert.Empty(d.Matches);
    }

    [Fact]
    public void ResetOfAnEmptyCounter()
    {
        var d = new TrackerData();
        var summary = CounterReset.ResetAll(_store, d);
        Assert.Equal(new CounterReset.Summary(0, 0), summary);
        Assert.True(CounterReset.Restore(_store, d));
        Assert.Empty(d.Matches);
    }

    [Fact]
    public void ResetCountsEveryProfile()
    {
        var d = Sample();
        d.Matches.Add(new MatchRecord { SessionId = "other", ChestsOpened = 50, PlayerName = "Friend", Playlist = "Playlist_DefaultSolo", MatchStartUtc = DateTime.UtcNow });
        d.ManualChests["Friend"] = 10;
        var summary = CounterReset.ResetAll(_store, d);
        Assert.Equal(new CounterReset.Summary(4, 190), summary);
        Assert.Empty(Profiles.List(d));
    }
}
