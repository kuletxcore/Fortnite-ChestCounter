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
        if (Build.VERSION.SdkInt <= BuildVersionCodes.Q && CheckSelfPermission(Manifest.Permission.ReadExternalStorage) != Permission.Granted)
        {
            RequestPermissions(new[] { Manifest.Permission.ReadExternalStorage }, 0);
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
        AddAction(ReplaySync.GrantedTree(this) == null ? L.T("Dossier de Fortnite", "Fortnite folder", "Carpeta de Fortnite") : L.T("Changer de dossier", "Change folder", "Cambiar carpeta"), false, PickReplayFolder);
        AddAction(L.T("Importer des replays", "Import replays", "Importar repeticiones"), false, PickReplayFiles);
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
                "Choisis le dossier où Fortnite enregistre ses replays (Android/data/com.epicgames.fortnite/files/…/Saved/Demos), puis « Utiliser ce dossier ». Si Android refuse ce dossier, choisis un dossier où tu copies tes replays (depuis ton PC par exemple).",
                "Pick the folder where Fortnite saves its replays (Android/data/com.epicgames.fortnite/files/…/Saved/Demos), then \"Use this folder\". If Android refuses that folder, pick a folder you copy your replays to (from your PC, for example).",
                "Elige la carpeta donde Fortnite guarda sus repeticiones (Android/data/com.epicgames.fortnite/files/…/Saved/Demos), luego «Usar esta carpeta». Si Android rechaza esa carpeta, elige una carpeta donde copies tus repeticiones (desde tu PC, por ejemplo)."))!
            .SetPositiveButton(L.T("Choisir", "Pick", "Elegir"), (_, _) =>
            {
                var i = new Intent(Intent.ActionOpenDocumentTree);
                i.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantPersistableUriPermission);
                if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
                {
                    i.PutExtra(Android.Provider.DocumentsContract.ExtraInitialUri, ReplaySync.FortniteInitialUri);
                }
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
                "Le compteur lit les replays que Fortnite enregistre après chaque partie (Réglages → Jeu → Replays : activés). Il ne touche ni au jeu ni à sa mémoire, et ne fait que lire les fichiers.\n\n" +
                "• Fortnite sur Android : donne accès à son dossier de replays (« Dossier de Fortnite »). Selon la version d'Android, l'accès au dossier Android/data peut être refusé par le système.\n" +
                "• Replays du PC : copie-les sur le téléphone (%LOCALAPPDATA%\\FortniteGame\\Saved\\Demos sur le PC) et importe-les, ou choisis le dossier où tu les copies.\n\n" +
                "« Jouer à Fortnite » lance le jeu et garde le compteur actif : l'overlay flottant affiche tes coffres par-dessus le jeu (déplace-le du doigt). Les coffres d'une partie sont comptés quelques secondes après sa fin.",
                "The counter reads the replays Fortnite saves after each match (Settings → Game → Replays: On). It never touches the game or its memory: it only reads the files.\n\n" +
                "• Fortnite on Android: give access to its replays folder (\"Fortnite folder\"). Depending on the Android version, the system may refuse access to the Android/data folder.\n" +
                "• Replays from a PC: copy them to the phone (%LOCALAPPDATA%\\FortniteGame\\Saved\\Demos on the PC) and import them, or pick the folder you copy them to.\n\n" +
                "\"Play Fortnite\" starts the game and keeps the counter running: the floating overlay shows your chests over the game (drag it with a finger). A match's chests are counted a few seconds after it ends.",
                "El contador lee las repeticiones que Fortnite guarda tras cada partida (Ajustes → Juego → Repeticiones: activadas). Nunca toca el juego ni su memoria: solo lee los archivos.\n\n" +
                "• Fortnite en Android: da acceso a su carpeta de repeticiones («Carpeta de Fortnite»). Según la versión de Android, el sistema puede denegar el acceso a la carpeta Android/data.\n" +
                "• Repeticiones del PC: cópialas al teléfono (%LOCALAPPDATA%\\FortniteGame\\Saved\\Demos en el PC) e impórtalas, o elige la carpeta donde las copias.\n\n" +
                "«Jugar a Fortnite» abre el juego y mantiene el contador activo: el overlay flotante muestra tus cofres sobre el juego (muévelo con el dedo). Los cofres de una partida se cuentan unos segundos después de terminar."))!
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
