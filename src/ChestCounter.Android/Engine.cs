using Android.Content;

namespace ChestCounter.Droid;

/// <summary>
/// The counter on Android: the same history, analysis and report as the Windows exe, in the app's private storage.
/// Replays are copied into <see cref="DemosFolder"/> (from the Fortnite folder the player granted, or imported by
/// hand), then read by the shared <see cref="Tracker"/>. Every call is serialized by one lock.
/// </summary>
public sealed class Engine
{
    private static Engine? _instance;
    private static readonly object InstanceLock = new();

    private readonly object _lock = new();
    private readonly Context _context;

    public Store Store { get; }
    public Config Config { get; }
    public Tracker Tracker { get; }
    public string DemosFolder { get; }

    /// <summary>Raised (on a worker thread) after a scan changed the figures.</summary>
    public event Action? Changed;

    /// <summary>Last line worth showing at the top of the app ("3 new matches", "Fortnite folder not granted"...).</summary>
    public string Status { get; private set; } = "";

    public bool Busy { get; private set; }

    public static Engine Get(Context context)
    {
        lock (InstanceLock)
        {
            return _instance ??= new Engine(context.ApplicationContext ?? context);
        }
    }

    private Engine(Context context)
    {
        _context = context;
        var root = context.FilesDir!.AbsolutePath;
        Store = new Store(Path.Combine(root, "data"));
        DemosFolder = Path.Combine(root, "Demos");
        Directory.CreateDirectory(DemosFolder);
        ErrorLog.FilePath = Store.ErrorLogPath;

        Config = Store.LoadConfig() ?? new Config();
        if (L.Normalize(Config.Language) == null)
        {
            Config.Language = L.SystemDefault;
        }
        L.Set(Config.Language);
        // Replays always live in the app's own folder on Android.
        Config.DemosFolder = DemosFolder;
        // A match in progress cannot be read on a phone (the replay is copied once finished).
        Config.LiveUpdate = false;
        Config.ApplyTimeSettings();
        Store.SaveConfig(Config);

        var data = Store.LoadData();
        Tracker = new Tracker(Store, Config, data);
        Tracker.UpgradeIfNeeded();

        TreasureCatalog.LoadCached(Store.Folder);
        TreasureImages.Folder = Path.Combine(Store.Folder, "images");
        ReportWriter.WriteAll(Store, Config, Tracker.Data);
        _overlay = BuildOverlay();

        // Treasure hunt data and pictures: in the background, the report is written again once they are in.
        _ = Task.Run(async () =>
        {
            var refreshed = await TreasureCatalog.RefreshAsync(Store.Folder);
            var images = TreasureImages.Missing() && await TreasureImages.DownloadMissingAsync() > 0;
            if (refreshed || images)
            {
                lock (_lock)
                {
                    ReportWriter.WriteAll(Store, Config, Tracker.Data);
                }
                Changed?.Invoke();
            }
        });
    }

    public string ReportPath => Store.ReportPath;

    private volatile OverlayView? _overlay;

    /// <summary>
    /// What the floating overlay shows. Read from the UI thread: the figures of the last scan, never waiting for a
    /// scan in progress (which can take seconds).
    /// </summary>
    public OverlayView Overlay() => _overlay ??= BuildOverlay();

    private OverlayView BuildOverlay() => OverlayModel.Build(Profiles.View(Tracker.Data, Config), null, Tracker.HasPending, Config.ShowSquadTotal);

    public void SetLanguage(string code)
    {
        lock (_lock)
        {
            Config.Language = code;
            L.Set(code);
            Store.SaveConfig(Config);
            ReportWriter.WriteAll(Store, Config, Tracker.Data);
            _overlay = BuildOverlay();
        }
        Changed?.Invoke();
    }

    /// <summary>Copies the new replays of the granted Fortnite folder, then counts every finished one.</summary>
    public int SyncAndScan(bool ignoreQuietPeriod = false)
    {
        lock (_lock)
        {
            Busy = true;
            try
            {
                var sync = ReplaySync.Sync(_context, DemosFolder);
                var before = Tracker.Data.Matches.Count;
                var added = Tracker.Scan(ignoreQuietPeriod);
                ReportWriter.WriteAll(Store, Config, Tracker.Data);
                _overlay = BuildOverlay();
                Status = StatusText(sync, added, Tracker.Data.Matches.Count - before);
                return added;
            }
            catch (Exception ex)
            {
                ErrorLog.Write("SyncAndScan", ex);
                Status = L.T("Erreur : ", "Error: ", "Error: ") + ex.Message;
                return 0;
            }
            finally
            {
                Busy = false;
                Changed?.Invoke();
            }
        }
    }

    /// <summary>Copies replay files the player picked or shared (from a PC, a file manager...), then counts them.</summary>
    public int Import(IEnumerable<Android.Net.Uri> uris)
    {
        var copied = 0;
        lock (_lock)
        {
            foreach (var uri in uris)
            {
                try
                {
                    if (ReplaySync.CopyDocument(_context, uri, DemosFolder))
                    {
                        copied++;
                    }
                }
                catch (Exception ex)
                {
                    ErrorLog.Write($"Import {uri}", ex);
                }
            }
        }
        var added = SyncAndScan(ignoreQuietPeriod: true);
        Status = L.T($"{copied} replay(s) importé(s), {added} nouvelle(s) partie(s).",
            $"{copied} replay(s) imported, {added} new match(es).",
            $"{copied} repetición(es) importada(s), {added} partida(s) nueva(s).");
        Changed?.Invoke();
        return added;
    }

    private static string StatusText(ReplaySync.Result sync, int added, int newMatches)
    {
        if (added > 0)
        {
            return L.T($"{added} nouvelle(s) partie(s) comptée(s).", $"{added} new match(es) counted.", $"{added} partida(s) nueva(s) contada(s).");
        }
        return sync switch
        {
            ReplaySync.Result.NoFolder => L.T("Choisis le dossier des replays de Fortnite, ou importe des replays.",
                "Pick Fortnite's replays folder, or import replays.",
                "Elige la carpeta de repeticiones de Fortnite, o importa repeticiones."),
            ReplaySync.Result.Lost => L.T("L'accès au dossier de Fortnite a été perdu : choisis-le à nouveau.",
                "Access to Fortnite's folder was lost: pick it again.",
                "Se perdió el acceso a la carpeta de Fortnite: elígela de nuevo."),
            _ => L.T("À jour. En attente de la prochaine partie.", "Up to date. Waiting for the next match.", "Al día. Esperando la próxima partida."),
        };
    }
}
