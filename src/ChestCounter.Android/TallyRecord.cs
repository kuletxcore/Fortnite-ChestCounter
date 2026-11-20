namespace ChestCounter.Droid;

/// <summary>
/// The match being played, counted by hand. Fortnite on Android records no replays, so the player taps the floating
/// counter for each chest (and each treasure). Every tap keeps its time: the report then counts the chests opened
/// during a Treasure Hour with their boosted odds, exactly like the chests read from a PC replay.
/// </summary>
public sealed class LiveTally
{
    public DateTime StartUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastTapUtc { get; set; } = DateTime.UtcNow;
    /// <summary>Seconds since the start of the match, one per chest.</summary>
    public List<float> Chests { get; set; } = new();
    /// <summary>Seconds since the start of the match, one per treasure.</summary>
    public List<float> Treasures { get; set; } = new();
}

/// <summary>Battle Royale modes a match counted by hand can be saved as (the report only counts Battle Royale).</summary>
public static class TallyModes
{
    public static readonly string[] Playlists =
    {
        "Playlist_DefaultSolo", "Playlist_DefaultDuo", "Playlist_DefaultTrio", "Playlist_DefaultSquad",
        "Playlist_NoBuildBR_Solo", "Playlist_NoBuildBR_Duo", "Playlist_NoBuildBR_Trio", "Playlist_NoBuildBR_Squad",
    };
}

/// <summary>Turns a match counted by hand into the same record as a match read from a replay (no Android types: tested).</summary>
public static class TallyRecord
{
    /// <param name="sample">A match of the account being played, to save this one under the same profile (null: "me").</param>
    public static MatchRecord Build(LiveTally t, string playlist, MatchRecord? sample)
    {
        var m = new MatchRecord
        {
            SessionId = "android-" + t.StartUtc.Ticks.ToString("x"),
            ReplayFile = "",
            AnalyzedAtUtc = DateTime.UtcNow,
            MatchStartUtc = t.StartUtc,
            DurationSeconds = (int)Math.Max(0, (t.LastTapUtc - t.StartUtc).TotalSeconds),
            Playlist = playlist,
            Map = GameModes.SeasonMaps[0],
            PlayerName = sample?.PlayerName ?? L.T("Moi", "Me", "Yo"),
            EpicId = sample?.EpicId,
            IsReplayOwner = true,
            IsBattleRoyale = true,
            AnalyzerVersion = ReplayAnalyzer.CurrentVersion,
            ChestsOpened = t.Chests.Count,
        };
        foreach (var s in t.Chests)
        {
            m.Chests.Add(new ChestRecord { TimeSeconds = s, Kind = ChestKinds.Normal, Blueprint = "manual" });
        }
        foreach (var s in t.Treasures)
        {
            m.TreasureFinds.Add(new TreasureFind
            {
                Item = TreasureNames.UnknownFr,
                ObtainedAtSeconds = s,
                TimeExact = true,
                SourceKind = ChestKinds.Normal,
                SourceIsChest = true,
                SourceCertain = true,
            });
        }
        return m;
    }
}
