using System.Text.Json;
using Android.Content;

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

public sealed partial class Engine
{
    /// <summary>No tap for this long: the previous match is over, a new tap starts a new one.</summary>
    private static readonly TimeSpan MatchIdleLimit = TimeSpan.FromMinutes(35);

    private LiveTally? _tally;

    private string TallyPath => Path.Combine(Store.Folder, "live-match.json");

    private ISharedPreferences Prefs => _context.GetSharedPreferences("tally", FileCreationMode.Private)!;

    /// <summary>Mode a match counted by hand is saved as.</summary>
    public string Playlist
    {
        get
        {
            var p = Prefs.GetString("playlist", null);
            return TallyModes.Playlists.Contains(p) ? p! : "Playlist_DefaultSquad";
        }
        set => Prefs.Edit()!.PutString("playlist", value).Apply();
    }

    public void NextPlaylist()
    {
        Playlist = TallyModes.Playlists[(Array.IndexOf(TallyModes.Playlists, Playlist) + 1) % TallyModes.Playlists.Length];
        Changed?.Invoke();
    }

    /// <summary>A match is being counted.</summary>
    public bool InMatch => _tally != null;

    public int LiveChests => _tally?.Chests.Count ?? 0;

    private void LoadTally()
    {
        try
        {
            _tally = File.Exists(TallyPath) ? JsonSerializer.Deserialize<LiveTally>(File.ReadAllText(TallyPath)) : null;
        }
        catch
        {
            _tally = null;
        }
        // Left from a match long ago (the app was closed during it): save it.
        if (_tally != null && DateTime.UtcNow - _tally.LastTapUtc > MatchIdleLimit)
        {
            SaveMatch();
        }
    }

    private void SaveTally()
    {
        if (_tally == null)
        {
            File.Delete(TallyPath);
            return;
        }
        var tmp = TallyPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_tally));
        File.Move(tmp, TallyPath, overwrite: true);
    }

    /// <summary>The match in progress as a match record (what the overlay shows as "current match").</summary>
    private MatchRecord? LiveRecord() => _tally == null ? null : ToRecord(_tally);

    private MatchRecord ToRecord(LiveTally t)
    {
        // Same account as the matches already counted (PC replays imported, or earlier matches), else "me".
        var current = Profiles.Current(Tracker.Data, Config);
        var sample = current == null ? null : Tracker.Data.Matches.FirstOrDefault(m => Profiles.Key(m) == current.Key);
        var m = new MatchRecord
        {
            SessionId = "android-" + t.StartUtc.Ticks.ToString("x"),
            ReplayFile = "",
            AnalyzedAtUtc = DateTime.UtcNow,
            MatchStartUtc = t.StartUtc,
            DurationSeconds = (int)Math.Max(0, (t.LastTapUtc - t.StartUtc).TotalSeconds),
            Playlist = Playlist,
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

    private void Tap(Action<LiveTally, float> add)
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            if (_tally != null && now - _tally.LastTapUtc > MatchIdleLimit)
            {
                SaveMatch();
            }
            _tally ??= new LiveTally { StartUtc = now };
            add(_tally, (float)(now - _tally.StartUtc).TotalSeconds);
            _tally.LastTapUtc = now;
            SaveTally();
            _overlay = BuildOverlay();
        }
        Changed?.Invoke();
    }

    public void AddChest() => Tap((t, s) => t.Chests.Add(s));

    public void AddTreasure() => Tap((t, s) => t.Treasures.Add(s));

    /// <summary>Removes the last chest tapped by mistake.</summary>
    public void UndoChest()
    {
        lock (_lock)
        {
            if (_tally == null || _tally.Chests.Count == 0)
            {
                return;
            }
            _tally.Chests.RemoveAt(_tally.Chests.Count - 1);
            SaveTally();
            _overlay = BuildOverlay();
        }
        Changed?.Invoke();
    }

    /// <summary>The match is over: it joins the history (a match with nothing tapped is dropped).</summary>
    public void EndMatch()
    {
        lock (_lock)
        {
            SaveMatch();
            _overlay = BuildOverlay();
        }
        Changed?.Invoke();
    }

    private void SaveMatch()
    {
        if (_tally == null)
        {
            return;
        }
        if (_tally.Chests.Count > 0 || _tally.Treasures.Count > 0)
        {
            var record = ToRecord(_tally);
            Tracker.Data.Matches.Add(record);
            Store.SaveData(Tracker.Data);
            Status = L.T($"Partie enregistrée : {record.ChestsOpened} coffre(s).", $"Match saved: {record.ChestsOpened} chest(s).", $"Partida guardada: {record.ChestsOpened} cofre(s).");
        }
        _tally = null;
        SaveTally();
        ReportWriter.WriteAll(Store, Config, Tracker.Data);
    }
}
