using System.Text.Json;
using Android.Content;

namespace ChestCounter.Droid;

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
        return TallyRecord.Build(t, Playlist, sample);
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
            if (_tally == null && SessionRules.StartsNewSession(SessionRules.LastActivityUtc(Tracker.Data), ReportWriter.SessionStartUtc, now))
            {
                // First chest after hours without playing: a new play session.
                StartSession(now);
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

    // ---- Session and reset ---------------------------------------------------------------------

    private const string SessionKey = "session_start_ticks";

    /// <summary>
    /// The session survives the app's process (Android stops it whenever it likes): its start is saved, and only a reset
    /// or hours without playing start a new one.
    /// </summary>
    private void LoadSession()
    {
        ReportWriter.SessionSlack = TimeSpan.Zero;
        var ticks = Prefs.GetLong(SessionKey, 0);
        if (ticks <= 0)
        {
            StartSession(DateTime.UtcNow);
        }
        else
        {
            ReportWriter.SessionStartUtc = new DateTime(ticks, DateTimeKind.Utc);
        }
    }

    private const string SessionNumberKey = "session_number";

    /// <summary>1 for the first session, one more at each new session (shown on the overlay: "Match 2 · Session 3").</summary>
    public int SessionNumber => Math.Max(1, Prefs.GetInt(SessionNumberKey, 1));

    /// <summary>Number of the match in the session (the one being counted, else the last one); 0 before the first.</summary>
    public int MatchNumber { get; private set; }

    private void StartSession(DateTime utc)
    {
        var first = Prefs.GetLong(SessionKey, 0) <= 0;
        ReportWriter.SessionStartUtc = utc;
        Prefs.Edit()!.PutLong(SessionKey, utc.Ticks)
            .PutInt(SessionNumberKey, SessionRules.NextSessionNumber(SessionNumber, first)).Apply();
    }

    /// <summary>"Session" back to 0. The match being counted is saved first: it stays in the total and the history.</summary>
    public void ResetSession()
    {
        lock (_lock)
        {
            SaveMatch();
            StartSession(DateTime.UtcNow);
            ReportWriter.WriteAll(Store, Config, Tracker.Data);
            _overlay = BuildOverlay();
            Status = L.T("Nouvelle session : la session repart de 0.", "New session: the session starts again from 0.", "Nueva sesión: la sesión vuelve a 0.");
        }
        Changed?.Invoke();
    }

    /// <summary>Every count back to 0 (matches, chests added by hand, the match being counted), after a backup.</summary>
    public CounterReset.Summary ResetAll()
    {
        CounterReset.Summary summary;
        lock (_lock)
        {
            _tally = null;
            SaveTally();
            summary = CounterReset.ResetAll(Store, Tracker.Data);
            StartSession(DateTime.UtcNow);
            ReportWriter.WriteAll(Store, Config, Tracker.Data);
            _overlay = BuildOverlay();
            Status = L.T($"Compteur remis à 0 ({summary.Chests} coffres effacés). « Annuler la remise à 0 » les rend.",
                $"Counter reset ({summary.Chests} chests cleared). \"Undo the reset\" brings them back.",
                $"Contador a 0 ({summary.Chests} cofres borrados). «Deshacer el reinicio» los recupera.");
        }
        Changed?.Invoke();
        return summary;
    }

    public bool CanUndoReset => CounterReset.CanRestore(Store);

    /// <summary>Brings back what the last full reset cleared, with what was counted since.</summary>
    public bool UndoReset()
    {
        bool done;
        lock (_lock)
        {
            done = CounterReset.Restore(Store, Tracker.Data);
            ReportWriter.WriteAll(Store, Config, Tracker.Data);
            _overlay = BuildOverlay();
            if (done)
            {
                Status = L.T("Remise à 0 annulée.", "Reset undone.", "Reinicio deshecho.");
            }
        }
        Changed?.Invoke();
        return done;
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
