using ChestCounter.Droid;
using Xunit;

namespace ChestCounter.Tests;

/// <summary>
/// The overlay's second line numbers the match and the session ("Match 2 · Session 3"); only the big figure counts
/// chests. Before, it showed chest counts there too ("Match 9 · Session 9"), which read as every figure counting chests.
/// </summary>
public sealed class NumberingTests : IDisposable
{
    private readonly DateTime _savedStart = ReportWriter.SessionStartUtc;
    private readonly TimeSpan _savedSlack = ReportWriter.SessionSlack;
    private static readonly DateTime Start = new(2026, 11, 21, 10, 0, 0, DateTimeKind.Utc);

    public NumberingTests()
    {
        L.Set("en");
        ReportWriter.SessionStartUtc = Start;
        ReportWriter.SessionSlack = TimeSpan.Zero;
    }

    public void Dispose()
    {
        ReportWriter.SessionStartUtc = _savedStart;
        ReportWriter.SessionSlack = _savedSlack;
    }

    private static LiveTally Tally(DateTime start, int chests) => new()
    {
        StartUtc = start,
        LastTapUtc = start.AddMinutes(chests),
        Chests = Enumerable.Range(1, chests).Select(i => i * 60f).ToList(),
    };

    private static TrackerData With(params LiveTally[] finished)
    {
        var d = new TrackerData();
        foreach (var t in finished) d.Matches.Add(TallyRecord.Build(t, "Playlist_DefaultSquad", null));
        return d;
    }

    [Fact]
    public void FirstMatchIsMatchOneWhateverItsChests()
    {
        // The screenshot: 9 chests in the first match of the session.
        var view = Profiles.View(With(), new Config());
        Assert.Equal(1, SessionRules.MatchNumber(view, inMatch: true));
        var live = TallyRecord.Build(Tally(Start.AddMinutes(1), 9), "Playlist_DefaultSquad", null);
        var o = OverlayModel.Build(view, live, true, false);
        Assert.Equal("9", o.Total); // the chest counter
        Assert.Equal(1, SessionRules.MatchNumber(view, inMatch: true)); // not 9
    }

    [Fact]
    public void ChestsDoNotChangeTheMatchNumber()
    {
        var view = Profiles.View(With(), new Config());
        foreach (var chests in new[] { 1, 5, 15, 40 })
        {
            Assert.Equal(1, SessionRules.MatchNumber(view, inMatch: true));
            Assert.Equal(chests.ToString(), OverlayModel.Build(view, TallyRecord.Build(Tally(Start.AddMinutes(1), chests), "Playlist_DefaultSquad", null), true, false).Total);
        }
    }

    [Fact]
    public void MatchesAreNumberedInTheSession()
    {
        var view = Profiles.View(With(Tally(Start.AddMinutes(1), 15), Tally(Start.AddMinutes(40), 3)), new Config());
        Assert.Equal(2, SessionRules.MatchNumber(view, inMatch: false)); // last match: 2
        Assert.Equal(3, SessionRules.MatchNumber(view, inMatch: true));  // playing the 3rd
    }

    [Fact]
    public void BeforeTheFirstMatchThereIsNoMatchNumber()
    {
        Assert.Equal(0, SessionRules.MatchNumber(Profiles.View(new TrackerData(), new Config()), inMatch: false));
    }

    [Fact]
    public void NewSessionStartsMatchesAgainFromOne()
    {
        var view = Profiles.View(With(Tally(Start.AddMinutes(1), 15), Tally(Start.AddMinutes(40), 3)), new Config());
        ReportWriter.SessionStartUtc = Start.AddHours(2); // reset
        Assert.Equal(0, SessionRules.MatchNumber(view, inMatch: false));
        Assert.Equal(1, SessionRules.MatchNumber(view, inMatch: true));
        Assert.Equal("18", OverlayModel.Build(view, null, false, false).Total); // the total is kept
    }

    [Theory]
    [InlineData(1, true, 1)]   // first session ever
    [InlineData(1, false, 2)]
    [InlineData(9, false, 10)]
    [InlineData(0, false, 1)]  // a broken stored value never gives session 0
    [InlineData(-4, false, 1)]
    public void SessionsAreNumbered(int current, bool firstEver, int expected)
    {
        Assert.Equal(expected, SessionRules.NextSessionNumber(current, firstEver));
    }
}
