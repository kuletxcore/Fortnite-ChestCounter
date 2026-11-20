using Android;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Webkit;
using Android.Widget;

namespace ChestCounter.Droid;

/// <summary>
/// The counter's screen: a row of actions (play Fortnite, keep counting, where the replays come from) over the same
/// report as the PC version.
/// </summary>
[Activity(Label = "@string/app_name", MainLauncher = true, Exported = true, Icon = "@drawable/ic_launcher",
    Theme = "@android:style/Theme.DeviceDefault.NoActionBar", LaunchMode = LaunchMode.SingleTask,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
    DataSchemes = new[] { "content", "file" }, DataMimeType = "application/octet-stream")]
[IntentFilter(new[] { Intent.ActionSend, Intent.ActionSendMultiple }, Categories = new[] { Intent.CategoryDefault },
    DataMimeType = "application/octet-stream")]
public sealed class MainActivity : Activity
{
    private const int PickFolder = 1;
    private const int PickFiles = 2;
    private const int OverlayPermission = 3;

    private static readonly Color Bg = Color.ParseColor("#0F1B24");
    private static readonly Color Gold = Color.ParseColor("#F0C057");
    private static readonly Color Ink = Color.ParseColor("#EFE2C4");

    private Engine _engine = null!;
    private WebView _web = null!;
    private TextView _status = null!;
    private LinearLayout _actions = null!;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        _engine = Engine.Get(this);
        Window!.SetStatusBarColor(Bg);
        Window.SetNavigationBarColor(Bg);
        BuildLayout();
        _engine.Changed += OnEngineChanged;

        if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu && CheckSelfPermission(Manifest.Permission.PostNotifications) != Permission.Granted)
        {
            RequestPermissions(new[] { Manifest.Permission.PostNotifications }, 0);
        }

        if (!HandleShared(Intent))
        {
            ScanInBackground();
        }
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        HandleShared(intent);
    }

    protected override void OnResume()
    {
        base.OnResume();
        RefreshActions();
        LoadReport();
    }

    protected override void OnDestroy()
    {
        _engine.Changed -= OnEngineChanged;
        base.OnDestroy();
    }

    // ---- Layout ---------------------------------------------------------------------------------

    private int Dp(int v) => (int)(v * Resources!.DisplayMetrics!.Density);

    private void BuildLayout()
    {
        var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        root.SetBackgroundColor(Bg);
        root.SetFitsSystemWindows(true);

        var header = new LinearLayout(this) { Orientation = Orientation.Vertical };
        header.SetPadding(Dp(16), Dp(12), Dp(16), Dp(4));
        var title = new TextView(this) { Text = L.T("Compteur de coffres", "Chest Counter", "Contador de cofres"), TextSize = 22 };
        title.SetTextColor(Gold);
        title.SetTypeface(Typeface.DefaultBold, TypefaceStyle.Bold);
        _status = new TextView(this) { TextSize = 13 };
        _status.SetTextColor(Ink);
        header.AddView(title);
        header.AddView(_status);
        root.AddView(header);

        var scroller = new HorizontalScrollView(this) { HorizontalScrollBarEnabled = false };
        _actions = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        _actions.SetPadding(Dp(12), Dp(4), Dp(12), Dp(8));
        scroller.AddView(_actions);
        root.AddView(scroller);

        _web = new WebView(this);
        _web.SetBackgroundColor(Bg);
        var s = _web.Settings;
        s.JavaScriptEnabled = true;
        s.DomStorageEnabled = true;
        s.AllowFileAccess = true;
        s.AllowContentAccess = false;
        _web.SetWebViewClient(new ReportClient(this));
        root.AddView(_web, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f));

        SetContentView(root);
    }

    private void RefreshActions()
    {
        _actions.RemoveAllViews();
        var fortnite = PackageManager!.GetLaunchIntentForPackage(ReplaySync.FortnitePackage);
        if (fortnite != null)
        {
            AddAction(L.T("▶ Jouer à Fortnite", "▶ Play Fortnite", "▶ Jugar a Fortnite"), primary: true, () =>
            {
                CounterService.Start(this);
                StartActivity(fortnite);
            });
        }
        AddAction(CounterService.Running ? L.T("■ Arrêter le compteur", "■ Stop counting", "■ Detener el contador") : L.T("● Compter en arrière-plan", "● Count in background", "● Contar en segundo plano"),
            primary: fortnite == null, () =>
            {
                if (CounterService.Running) CounterService.Stop(this); else CounterService.Start(this);
                _web.PostDelayed(RefreshActions, 300);
            });
        if (!CanDrawOverlays)
        {
            AddAction(L.T("Autoriser l'overlay", "Allow the overlay", "Permitir el overlay"), false, AskOverlay);
        }
        // Counting by hand (Fortnite on Android records no replays): also here when the overlay is not allowed.
        AddAction(_engine.InMatch ? L.T($"🧰 +1 (partie : {_engine.LiveChests})", $"🧰 +1 (match: {_engine.LiveChests})", $"🧰 +1 (partida: {_engine.LiveChests})") : "🧰 +1", false, _engine.AddChest);
        AddAction("★ +1", false, _engine.AddTreasure);
        if (_engine.InMatch)
        {
            AddAction(L.T("⏹ Fin de partie", "⏹ End match", "⏹ Fin de partida"), false, _engine.EndMatch);
        }
        // Automatic detection: screen capture (Android asks each time) + the cue taught with 🎯 on the overlay.
        AddAction(CounterService.Capturing ? L.T("👁 Détection auto : activée", "👁 Auto-detect: on", "👁 Detección auto: activada") : L.T("👁 Détection auto", "👁 Auto-detect", "👁 Detección auto"), false, () =>
        {
            if (CounterService.Capturing)
                StartService(new Intent(this, typeof(CounterService)).SetAction(CounterService.ActionStopCapture));
            else
                StartActivity(new Intent(this, typeof(CaptureConsentActivity)));
            _web.PostDelayed(RefreshActions, 800);
        });
        var detector = GetSharedPreferences("detector", FileCreationMode.Private)!;
        var threshold = detector.GetFloat("threshold", 0.75f);
        var level = Array.FindIndex(CounterService.Sensitivities, t => Math.Abs(t - threshold) < 0.01f);
        string[] names = { L.T("souple", "loose", "flexible"), L.T("normale", "normal", "normal"), L.T("stricte", "strict", "estricta") };
        AddAction(L.T("Détection : ", "Detection: ", "Detección: ") + names[level < 0 ? 1 : level], false, () =>
        {
            var next = CounterService.Sensitivities[((level < 0 ? 1 : level) + 1) % CounterService.Sensitivities.Length];
            detector.Edit()!.PutFloat("threshold", next).Apply();
            if (CounterService.Running)
                StartService(new Intent(this, typeof(CounterService)).SetAction(CounterService.ActionCueChanged));
            RefreshActions();
        });
        AddAction(L.T("Mode : ", "Mode: ", "Modo: ") + Pretty.Playlist(_engine.Playlist), false, _engine.NextPlaylist);
        AddAction(L.T("Importer des replays PC", "Import PC replays", "Importar repeticiones del PC"), false, PickReplayFiles);
        AddAction(ReplaySync.GrantedTree(this) == null ? L.T("Dossier de replays PC", "PC replays folder", "Carpeta de repeticiones del PC") : L.T("Changer de dossier", "Change folder", "Cambiar carpeta"), false, PickReplayFolder);
        AddAction(L.T("Actualiser", "Refresh", "Actualizar"), false, () => ScanInBackground(force: true));
        AddAction(L.NativeName(NextLanguage), false, () =>
        {
            _engine.SetLanguage(NextLanguage);
            Recreate();
        });
        AddAction(L.T("Aide", "Help", "Ayuda"), false, ShowHelp);
        _status.Text = _engine.Busy ? L.T("Lecture des replays…", "Reading replays…", "Leyendo repeticiones…") : _engine.Status;
    }

    private string NextLanguage => L.Codes[(Array.IndexOf(L.Codes, L.Code) + 1) % L.Codes.Length];

    private void AddAction(string text, bool primary, Action onClick)
    {
        var b = new Button(this) { Text = text, TextSize = 14 };
        b.SetAllCaps(false);
        b.SetTextColor(primary ? Bg : Ink);
        var bg = new Android.Graphics.Drawables.GradientDrawable();
        bg.SetCornerRadius(Dp(18));
        bg.SetColor(primary ? Gold : Color.ParseColor("#1E2F3B"));
        bg.SetStroke(Dp(1), Color.ParseColor("#C9A24F"));
        b.Background = bg;
        b.SetPadding(Dp(14), 0, Dp(14), 0);
        b.SetMinHeight(Dp(48));
        b.Click += (_, _) => onClick();
        var lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, Dp(44)) { RightMargin = Dp(8) };
        _actions.AddView(b, lp);
    }

    private void LoadReport()
    {
        var path = _engine.ReportPath;
        if (File.Exists(path))
        {
            var scroll = _web.ScrollY;
            _web.LoadUrl("file://" + path);
            _web.PostDelayed(() => _web.ScrollTo(0, scroll), 250);
        }
    }

    private void OnEngineChanged() => RunOnUiThread(() =>
    {
        RefreshActions();
        LoadReport();
    });

    private void ScanInBackground(bool force = false)
    {
        _status.Text = L.T("Lecture des replays…", "Reading replays…", "Leyendo repeticiones…");
        Task.Run(() => _engine.SyncAndScan(ignoreQuietPeriod: force));
    }

    // ---- Replays: folder, files, shares ---------------------------------------------------------

    private void PickReplayFolder()
    {
        new AlertDialog.Builder(this)
            .SetTitle(L.T("Dossier des replays", "Replays folder", "Carpeta de repeticiones"))!
            .SetMessage(L.T(
                "Choisis un dossier où tu copies les replays de ton PC (%LOCALAPPDATA%\\FortniteGame\\Saved\\Demos), par exemple synchronisé avec une appli de synchronisation. Les nouveaux replays y sont comptés automatiquement.",
                "Pick a folder you copy your PC's replays to (%LOCALAPPDATA%\\FortniteGame\\Saved\\Demos), for example one kept in sync by a sync app. New replays there are counted automatically.",
                "Elige una carpeta donde copies las repeticiones de tu PC (%LOCALAPPDATA%\\FortniteGame\\Saved\\Demos), por ejemplo una sincronizada con una app de sincronización. Las nuevas repeticiones se cuentan automáticamente."))!
            .SetPositiveButton(L.T("Choisir", "Pick", "Elegir"), (_, _) =>
            {
                var i = new Intent(Intent.ActionOpenDocumentTree);
                i.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantPersistableUriPermission);
                StartActivityForResult(i, PickFolder);
            })!
            .SetNegativeButton(L.T("Annuler", "Cancel", "Cancelar"), (_, _) => { })!
            .Show();
    }

    private void PickReplayFiles()
    {
        var i = new Intent(Intent.ActionOpenDocument);
        i.AddCategory(Intent.CategoryOpenable);
        i.SetType("*/*");
        i.PutExtra(Intent.ExtraAllowMultiple, true);
        StartActivityForResult(i, PickFiles);
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode == OverlayPermission)
        {
            RefreshActions();
            return;
        }
        if (resultCode != Result.Ok || data == null)
        {
            return;
        }
        if (requestCode == PickFolder && data.Data != null)
        {
            try
            {
                ReplaySync.SaveTree(this, data.Data);
                ScanInBackground(force: true);
            }
            catch (Exception ex)
            {
                Toast.MakeText(this, ex.Message, ToastLength.Long)!.Show();
            }
        }
        else if (requestCode == PickFiles)
        {
            Import(UrisOf(data));
        }
    }

    private static List<Android.Net.Uri> UrisOf(Intent data)
    {
        var uris = new List<Android.Net.Uri>();
        if (data.ClipData != null)
        {
            for (var k = 0; k < data.ClipData.ItemCount; k++)
            {
                if (data.ClipData.GetItemAt(k)?.Uri is { } u) uris.Add(u);
            }
        }
        if (data.Data != null && !uris.Contains(data.Data))
        {
            uris.Add(data.Data);
        }
        return uris;
    }

    /// <summary>A replay opened or shared with the app (file manager, a download from the PC...).</summary>
    private bool HandleShared(Intent? intent)
    {
        if (intent == null)
        {
            return false;
        }
        var uris = new List<Android.Net.Uri>();
        if (intent.Action == Intent.ActionView && intent.Data != null)
        {
            uris.Add(intent.Data);
        }
        else if (intent.Action is Intent.ActionSend or Intent.ActionSendMultiple)
        {
#pragma warning disable CA1422
            if (intent.GetParcelableExtra(Intent.ExtraStream) is Android.Net.Uri one) uris.Add(one);
            var many = intent.GetParcelableArrayListExtra(Intent.ExtraStream);
#pragma warning restore CA1422
            if (many != null) foreach (var o in many) if (o is Android.Net.Uri u) uris.Add(u);
            uris.AddRange(UrisOf(intent).Where(u => !uris.Contains(u)));
        }
        if (uris.Count == 0)
        {
            return false;
        }
        Import(uris);
        return true;
    }

    private void Import(List<Android.Net.Uri> uris)
    {
        if (uris.Count == 0) return;
        _status.Text = L.T("Import des replays…", "Importing replays…", "Importando repeticiones…");
        Task.Run(() => _engine.Import(uris));
    }

    // ---- Overlay permission, help ---------------------------------------------------------------

    private bool CanDrawOverlays => Build.VERSION.SdkInt < BuildVersionCodes.M || Android.Provider.Settings.CanDrawOverlays(this);

    private void AskOverlay()
    {
        var i = new Intent(Android.Provider.Settings.ActionManageOverlayPermission, Android.Net.Uri.Parse("package:" + PackageName));
        StartActivityForResult(i, OverlayPermission);
    }

    private void ShowHelp()
    {
        new AlertDialog.Builder(this)
            .SetTitle(L.T("Comment ça marche", "How it works", "Cómo funciona"))!
            .SetMessage(L.T(
                "Fortnite sur Android n'enregistre pas de replays : ici, tu comptes tes coffres en touchant le compteur flottant.\n\n" +
                "• « Jouer à Fortnite » lance le jeu et affiche le compteur par-dessus.\n" +
                "• 🧰 +1 à chaque coffre ouvert (appui long : annuler le dernier). Avec la fouille automatique, pense à toucher 🧰 quand le coffre s'ouvre.\n" +
                "• ★ quand un coffre te donne un trésor.\n" +
                "• ⏹ à la fin de la partie (sinon elle se termine seule après 35 min sans coffre).\n" +
                "• Déplace le compteur en faisant glisser les chiffres. +1 coffre est aussi dans la notification.\n\n" +
                "Détection automatique (👁) : le compteur regarde l'écran (Android te le demande à chaque fois) et compte un coffre quand le repère de fouille apparaît puis disparaît, même avec la fouille automatique. Apprends-lui ce repère une fois : touche 🎯 près d'un coffre, attends le décompte pendant que la fouille s'affiche, puis entoure l'anneau de fouille ou le bouton Fouiller. Le chiffre après 👁 indique la ressemblance (1 = identique). Rien n'est enregistré ni envoyé.\n\n" +
                "Chaque coffre garde son heure : ceux d'une Heure du trésor comptent avec leurs chances augmentées. Choisis le mode joué (« Mode ») : seul le Battle Royale compte.\n\n" +
                "Tu joues aussi sur PC ? Importe ses replays : leurs coffres sont comptés automatiquement.",
                "Fortnite on Android records no replays: here you count your chests by tapping the floating counter.\n\n" +
                "• \"Play Fortnite\" starts the game with the counter over it.\n" +
                "• 🧰 +1 for each chest opened (long press: undo the last one). With auto-search on, remember to tap 🧰 when the chest opens.\n" +
                "• ★ when a chest gives you a treasure.\n" +
                "• ⏹ when the match is over (otherwise it ends by itself after 35 minutes without a chest).\n" +
                "• Drag the figures to move the counter. +1 chest is also in the notification.\n\n" +
                "Auto-detect (👁): the counter watches the screen (Android asks you each time) and counts a chest when the search cue shows and then goes away, auto-search included. Teach it the cue once: tap 🎯 next to a chest, let the countdown end while the search shows, then draw a box around the search ring or the Search button. The figure after 👁 is how close the spot looks to the cue (1 = identical). Nothing is recorded or sent.\n\n" +
                "Each chest keeps its time: those of a Treasure Hour count with their boosted odds. Pick the mode you play (\"Mode\"): only Battle Royale counts.\n\n" +
                "Also playing on PC? Import its replays: their chests are counted automatically.",
                "Fortnite en Android no guarda repeticiones: aquí cuentas tus cofres tocando el contador flotante.\n\n" +
                "• «Jugar a Fortnite» abre el juego con el contador encima.\n" +
                "• 🧰 +1 por cada cofre abierto (pulsación larga: deshacer el último). Con la búsqueda automática, recuerda tocar 🧰 cuando se abra el cofre.\n" +
                "• ★ cuando un cofre te da un tesoro.\n" +
                "• ⏹ al terminar la partida (si no, termina sola tras 35 min sin cofres).\n" +
                "• Arrastra las cifras para mover el contador. +1 cofre también está en la notificación.\n\n" +
                "Detección automática (👁): el contador mira la pantalla (Android te lo pide cada vez) y cuenta un cofre cuando la señal de búsqueda aparece y luego desaparece, también con la búsqueda automática. Enséñale la señal una vez: toca 🎯 junto a un cofre, deja que termine la cuenta atrás mientras se ve la búsqueda y enmarca el anillo de búsqueda o el botón Buscar. La cifra tras 👁 indica el parecido (1 = idéntico). No se graba ni se envía nada.\n\n" +
                "Cada cofre guarda su hora: los de una Hora del tesoro cuentan con sus probabilidades aumentadas. Elige el modo que juegas («Modo»): solo cuenta Battle Royale.\n\n" +
                "¿También juegas en PC? Importa sus repeticiones: sus cofres se cuentan automáticamente."))!
            .SetPositiveButton("OK", (_, _) => { })!
            .Show();
    }

    /// <summary>The report is a local page: links to the web (odds source, GitHub...) open in the browser.</summary>
    private sealed class ReportClient : WebViewClient
    {
        private readonly Activity _activity;
        public ReportClient(Activity activity) => _activity = activity;

        public override bool ShouldOverrideUrlLoading(WebView? view, IWebResourceRequest? request)
        {
            var url = request?.Url;
            if (url != null && (url.Scheme == "https" || url.Scheme == "http"))
            {
                _activity.StartActivity(new Intent(Intent.ActionView, url));
                return true;
            }
            return false;
        }
    }
}
