namespace ChestCounter.Droid;

/// <summary>
/// Resetting the counter (no Android types: tested). Two levels:
/// <list type="bullet">
/// <item>the <b>session</b>: only the start of the "Session" figures moves to now, the history is untouched;</item>
/// <item><b>all counts</b>: the matches and the chests added by hand are cleared, after a backup that
/// <see cref="Restore"/> puts back (together with what was counted since).</item>
/// </list>
/// The collection ticks and the list of replays already read are kept: a reset must not bring back old replays as new
/// matches, and the treasures the player owns don't change.
/// </summary>
public static class CounterReset
{
    /// <summary>What a reset cleared, for the confirmation message.</summary>
    public sealed record Summary(int Matches, int Chests);

    public static string BackupPath(Store store) => Path.Combine(store.Folder, "data.before-reset.json");

    public static bool CanRestore(Store store) => File.Exists(BackupPath(store));

    /// <summary>Clears the counts of every profile, after saving them to <see cref="BackupPath"/> (the latest reset only).</summary>
    public static Summary ResetAll(Store store, TrackerData data)
    {
        var summary = new Summary(data.Matches.Count, data.CountedChests + data.ManualChests.Values.Sum());
        store.SaveData(data);
        File.Copy(store.DataPath, BackupPath(store), overwrite: true);

        data.Matches.Clear();
        data.ManualChests.Clear();
        data.ManualHistory.Clear();
        store.SaveData(data);
        return summary;
    }

    /// <summary>
    /// Undoes the last reset: the matches and chests added by hand from before it come back, merged with what was
    /// counted since (nothing counted after the reset is lost). False when there is no backup to restore.
    /// </summary>
    public static bool Restore(Store store, TrackerData data)
    {
        var path = BackupPath(store);
        if (!File.Exists(path) || store.LoadDataFile(path) is not { } backup)
        {
            return false;
        }

        var known = data.Matches.Select(m => m.SessionId).ToHashSet();
        data.Matches.InsertRange(0, backup.Matches.Where(m => !known.Contains(m.SessionId)));

        foreach (var (key, chests) in backup.ManualChests)
        {
            data.ManualChests[key] = data.ManualChests.GetValueOrDefault(key) + chests;
        }
        foreach (var (key, entries) in backup.ManualHistory)
        {
            if (!data.ManualHistory.TryGetValue(key, out var list))
            {
                data.ManualHistory[key] = list = new List<ManualEntry>();
            }
            list.InsertRange(0, entries);
        }
        foreach (var (file, state) in backup.ProcessedFiles)
        {
            data.ProcessedFiles.TryAdd(file, state);
        }

        store.SaveData(data);
        File.Delete(path);
        return true;
    }
}

/// <summary>When a new session starts by itself (no Android types: tested).</summary>
public static class SessionRules
{
    /// <summary>No match for this long: the next one starts a new play session.</summary>
    public static readonly TimeSpan IdleGap = TimeSpan.FromHours(4);

    /// <summary>End of the latest match counted (null: none yet).</summary>
    public static DateTime? LastActivityUtc(TrackerData data) => data.Matches.Count == 0
        ? null
        : data.Matches.Max(m => (m.MatchStartUtc ?? m.AnalyzedAtUtc).AddSeconds(m.DurationSeconds ?? 0));

    /// <summary>
    /// Number of the match in the session (1, 2, 3…): the finished matches of the session, plus the one being counted.
    /// 0 before the first match of the session.
    /// </summary>
    public static int MatchNumber(TrackerData view, bool inMatch) => view.Matches.Count(ReportWriter.IsThisSession) + (inMatch ? 1 : 0);

    /// <summary>Number of the next session: 1 for the first one ever, then one more each time.</summary>
    public static int NextSessionNumber(int current, bool firstEver) => firstEver ? 1 : Math.Max(1, current + 1);

    /// <summary>True when a match starting now opens a new session (the previous one is long over).</summary>
    public static bool StartsNewSession(DateTime? lastActivityUtc, DateTime sessionStartUtc, DateTime nowUtc) =>
        nowUtc - (lastActivityUtc is { } last && last > sessionStartUtc ? last : sessionStartUtc) >= IdleGap;
}
