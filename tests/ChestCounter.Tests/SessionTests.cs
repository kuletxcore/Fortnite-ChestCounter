using ChestCounter.Droid;
using Xunit;

namespace ChestCounter.Tests;

/// <summary>The "Session" figures and resetting them (the start of the session is moved, nothing else changes).</summary>
public sealed class SessionTests : IDisposable
{
    private readonly DateTime _savedStart = ReportWriter.SessionStartUtc;
    private readonly TimeSpan _savedSlack = ReportWriter.SessionSlack;
    private static readonly DateTime Now = new(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);

    public SessionTests() => L.Set("en");

    public void Dispose()
    {
        ReportWriter.SessionStartUtc = _savedStart;
        ReportWriter.SessionSlack = _savedSlack;
    }

    private static MatchRecord Match(DateTime start, int seconds, int chests) => new()
    {
        SessionId = Guid.NewGuid().ToString(),
        MatchStartUtc = start,
        DurationSeconds = seconds,
        ChestsOpened = chests,
        Playlist = "Playlist_DefaultSquad",
        Map = GameModes.SeasonMaps[0],
        PlayerName = "Me",
    };

    [Fact]
    public void MatchEndingAfterTheStartIsInTheSession()
    {
        ReportWriter.SessionStartUtc = Now;
        ReportWriter.SessionSlack = TimeSpan.Zero;
        Assert.True(ReportWriter.IsThisSession(Match(Now.AddMinutes(5), 600, 1)));
        Assert.True(ReportWriter.IsThisSession(Match(Now.AddMinutes(-5), 600, 1))); // started before, ended after
        Assert.False(ReportWriter.IsThisSession(Match(Now.AddMinutes(-30), 600, 1)));
    }

    [Fact]
    public void SlackKeepsAMatchThatJustEnded()
    {
        ReportWriter.SessionStartUtc = Now;
        var justBefore = Match(Now.AddMinutes(-11), 600, 1); // ended 1 minute before the start
        ReportWriter.SessionSlack = TimeSpan.FromMinutes(2); // the exe: started a moment after the match
        Assert.True(ReportWriter.IsThisSession(justBefore));
        ReportWriter.SessionSlack = TimeSpan.Zero; // Android: the player reset the session
        Assert.False(ReportWriter.IsThisSession(justBefore));
    }

    [Fact]
    public void UnspecifiedKindIsTakenAsUtc()
    {
        ReportWriter.SessionStartUtc = Now;
        ReportWriter.SessionSlack = TimeSpan.Zero;
        var m = Match(DateTime.SpecifyKind(Now.AddMinutes(1), DateTimeKind.Unspecified), 60, 1);
        Assert.True(ReportWriter.IsThisSession(m));
    }

    [Fact]
    public void ResettingTheSessionZeroesSessionButKeepsTotal()
    {
        ReportWriter.SessionSlack = TimeSpan.Zero;
        ReportWriter.SessionStartUtc = Now.AddHours(-1);
        var data = new TrackerData();
        data.Matches.Add(Match(Now.AddMinutes(-40), 600, 7));
        data.Matches.Add(Match(Now.AddMinutes(-20), 600, 5));
        var view = Profiles.View(data, new Config());

        var before = OverlayModel.Build(view, null, false, false);
        Assert.Equal("12", before.Session);
        Assert.Equal("12", before.Total);

        ReportWriter.SessionStartUtc = Now; // reset
        var after = OverlayModel.Build(view, null, false, false);
        Assert.Equal("0", after.Session);
        Assert.Equal("12", after.Total);
        Assert.Equal("5", after.Last); // the last match is still shown
    }

    [Fact]
    public void LiveMatchCountsInTheNewSession()
    {
        ReportWriter.SessionSlack = TimeSpan.Zero;
        ReportWriter.SessionStartUtc = Now;
        var view = Profiles.View(new TrackerData(), new Config());
        var live = TallyRecord.Build(new LiveTally { StartUtc = Now.AddMinutes(1), LastTapUtc = Now.AddMinutes(2), Chests = { 60, 120 } }, "Playlist_DefaultSolo", null);
        var o = OverlayModel.Build(view, live, true, false);
        Assert.Equal("2", o.Session);
        Assert.Equal("2", o.Current);
    }

    [Fact]
    public void ReportShowsTheResetSession()
    {
        ReportWriter.SessionSlack = TimeSpan.Zero;
        var data = new TrackerData();
        data.Matches.Add(Match(Now.AddHours(-3), 600, 9));

        ReportWriter.SessionStartUtc = Now.AddHours(-4);
        var before = ReportWriter.Report(new Config(), Profiles.View(data, new Config()), Profiles.List(data));
        Assert.Contains("opened or destroyed by you · 9 this session", before);

        ReportWriter.SessionStartUtc = Now;
        var after = ReportWriter.Report(new Config(), Profiles.View(data, new Config()), Profiles.List(data));
        Assert.Contains("opened or destroyed by you · 0 this session", after);
        Assert.Contains(">9</div>", after); // the total tile is unchanged
    }

    [Theory]
    [InlineData(null, -5, true)]   // nothing played, session opened 5 h ago
    [InlineData(null, -1, false)]  // session opened 1 h ago
    [InlineData(-3, -10, false)]   // played 3 h ago
    [InlineData(-5, -10, true)]    // last match 5 h ago
    [InlineData(-9, -1, false)]    // last match before a session reset 1 h ago: the reset counts as activity
    public void NewSessionAfterHoursWithoutPlaying(int? lastActivityHoursAgo, int sessionStartHoursAgo, bool expected)
    {
        DateTime? last = lastActivityHoursAgo is { } h ? Now.AddHours(h) : null;
        Assert.Equal(expected, SessionRules.StartsNewSession(last, Now.AddHours(sessionStartHoursAgo), Now));
    }

    [Fact]
    public void IdleGapBoundary()
    {
        Assert.True(SessionRules.StartsNewSession(Now - SessionRules.IdleGap, Now.AddDays(-1), Now));
        Assert.False(SessionRules.StartsNewSession(Now - SessionRules.IdleGap + TimeSpan.FromSeconds(1), Now.AddDays(-1), Now));
    }

    [Fact]
    public void LastActivityIsTheEndOfTheLatestMatch()
    {
        var data = new TrackerData();
        Assert.Null(SessionRules.LastActivityUtc(data));
        data.Matches.Add(Match(Now.AddHours(-2), 600, 1));
        data.Matches.Add(Match(Now.AddHours(-5), 600, 1));
        Assert.Equal(Now.AddHours(-2).AddSeconds(600), SessionRules.LastActivityUtc(data));
    }
}
