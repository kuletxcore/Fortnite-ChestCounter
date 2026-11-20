using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using Android.Runtime;
using Android.Views;
using Android.Widget;

namespace ChestCounter.Droid;

/// <summary>
/// Keeps the floating counter over Fortnite while it is played: chests are tapped by hand, or detected on screen when
/// the player turned screen capture on (👁, see <see cref="ScreenDetector"/>). PC replays of a synced folder are read
/// every few seconds too. Stopped from its notification or from the app.
/// </summary>
// Declared in AndroidManifest.xml (a special-use foreground service needs its <property> there).
[Register("io.github.chestcounter.android.CounterService")]
public sealed class CounterService : Service
{
    public const string ActionStop = "io.github.chestcounter.STOP";
    public const string ActionToggleOverlay = "io.github.chestcounter.TOGGLE_OVERLAY";
    public const string ActionAddChest = "io.github.chestcounter.ADD_CHEST";
    public const string ActionEndMatch = "io.github.chestcounter.END_MATCH";
    public const string ActionStartCapture = "io.github.chestcounter.START_CAPTURE";
    public const string ActionStopCapture = "io.github.chestcounter.STOP_CAPTURE";
    public const string ActionCueChanged = "io.github.chestcounter.CUE_CHANGED";
    private const string Channel = "counter";
    private const int NotificationId = 1;
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

    public static bool Running { get; private set; }

    /// <summary>Screen capture is on: chests are detected by themselves.</summary>
    public static bool Capturing => _detector != null;

    private static ScreenDetector? _detector;

    private Engine _engine = null!;
    private Handler _main = null!;
    private CancellationTokenSource? _cts;
    private OverlayWindow? _overlay;
    private bool _overlayWanted = true;

    public override IBinder? OnBind(Intent? intent) => null;

    public static void Start(Context c)
    {
        var i = new Intent(c, typeof(CounterService));
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
        {
            c.StartForegroundService(i);
        }
        else
        {
            c.StartService(i);
        }
    }

    public static void Stop(Context c) => c.StopService(new Intent(c, typeof(CounterService)));

    public override void OnCreate()
    {
        base.OnCreate();
        _engine = Engine.Get(this);
        _main = new Handler(Looper.MainLooper!);
        CreateChannel();
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        switch (intent?.Action)
        {
            case ActionStop:
                StopSelf();
                return StartCommandResult.NotSticky;
            case ActionToggleOverlay:
                _overlayWanted = !_overlayWanted;
                UpdateUi();
                return StartCommandResult.Sticky;
            case ActionAddChest when Running:
                _engine.AddChest();
                return StartCommandResult.Sticky;
            case ActionEndMatch when Running:
                _engine.EndMatch();
                return StartCommandResult.Sticky;
            case ActionStopCapture:
                StopCapture();
                return Running ? StartCommandResult.Sticky : StartCommandResult.NotSticky;
            case ActionCueChanged:
                if (_detector != null)
                {
                    _detector.Threshold = Sensitivity;
                    _detector.SetCue(ChestCue.Load(this));
                }
                UpdateUi();
                return Running ? StartCommandResult.Sticky : StartCommandResult.NotSticky;
        }

        var capture = intent?.Action == ActionStartCapture;
        GoForeground(capture);
        if (capture)
        {
            StartCapture(intent!);
        }

        if (!Running)
        {
            Running = true;
            _engine.Changed += OnChanged;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _ = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    _engine.SyncAndScan();
                    try
                    {
                        await Task.Delay(Interval, token);
                    }
                    catch (TaskCanceledException)
                    {
                        break;
                    }
                }
            }, token);
        }
        UpdateUi();
        return StartCommandResult.Sticky;
    }

    /// <summary>
    /// Foreground service: special use (the counter over the game), plus media projection while the screen is watched
    /// (Android 10 and later require that type before the capture starts).
    /// </summary>
    private void GoForeground(bool capture)
    {
        var notification = BuildNotification();
        if (Build.VERSION.SdkInt >= BuildVersionCodes.UpsideDownCake)
        {
            StartForeground(NotificationId, notification, capture || Capturing ? ForegroundService.TypeSpecialUse | ForegroundService.TypeMediaProjection : ForegroundService.TypeSpecialUse);
        }
        else if (Build.VERSION.SdkInt >= BuildVersionCodes.Q && (capture || Capturing))
        {
            StartForeground(NotificationId, notification, ForegroundService.TypeMediaProjection);
        }
        else
        {
            StartForeground(NotificationId, notification);
        }
    }

    private void StartCapture(Intent intent)
    {
        StopCapture();
#pragma warning disable CA1422
        var data = intent.GetParcelableExtra("data") as Intent;
#pragma warning restore CA1422
        if (data == null)
        {
            return;
        }
        try
        {
            var detector = new ScreenDetector(this, intent.GetIntExtra("code", 0), data) { Threshold = Sensitivity };
            detector.ChestDetected += () =>
            {
                _engine.AddChest();
                _main.Post(() => _overlay?.Flash());
            };
            detector.Stopped += () => _main.Post(StopCapture);
            _detector = detector;
            if (!detector.HasCue && ChestCue.Load(this) == null)
            {
                Toast.MakeText(this, L.T("Touche 🎯 près d'un coffre pour apprendre au compteur à le reconnaître.",
                    "Tap 🎯 next to a chest to teach the counter what a search looks like.",
                    "Toca 🎯 junto a un cofre para enseñar al contador cómo se ve una búsqueda."), ToastLength.Long)!.Show();
            }
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Start capture", ex);
            Toast.MakeText(this, ex.Message, ToastLength.Long)!.Show();
        }
        UpdateUi();
    }

    private void StopCapture()
    {
        var d = _detector;
        _detector = null;
        d?.Release();
        if (Running)
        {
            GoForeground(false);
            UpdateUi();
        }
    }

    /// <summary>Similarity from which the taught cue counts as shown (the app's "Detection" setting).</summary>
    public static readonly float[] Sensitivities = { 0.65f, 0.75f, 0.85f };

    public float Sensitivity => GetSharedPreferences("detector", FileCreationMode.Private)!.GetFloat("threshold", 0.75f);

    /// <summary>👁: asks Android for screen capture (its dialog), or stops it.</summary>
    internal void ToggleCapture()
    {
        if (Capturing)
        {
            StopCapture();
            return;
        }
        StartActivity(new Intent(this, typeof(CaptureConsentActivity)).AddFlags(ActivityFlags.NewTask));
    }

    /// <summary>🎯: in 3 seconds (time to stand at a chest), takes the screenshot the cue is drawn on.</summary>
    internal void Teach()
    {
        if (_detector == null)
        {
            ToggleCapture();
            return;
        }
        var detector = _detector;
        _overlay?.Countdown(3, () => detector.Grab(path => _main.Post(() =>
        {
            if (path != null)
            {
                StartActivity(new Intent(this, typeof(CalibrateActivity)).AddFlags(ActivityFlags.NewTask));
            }
        })));
    }

    internal float? DetectorScore => _detector is { HasCue: true } d ? d.Score : null;

    public override void OnDestroy()
    {
        Running = false;
        StopCapture();
        _engine.Changed -= OnChanged;
        _cts?.Cancel();
        _overlay?.Remove();
        _overlay = null;
        base.OnDestroy();
    }

    private void OnChanged() => _main.Post(UpdateUi);

    private void UpdateUi()
    {
        var view = _engine.Overlay();
        var canDraw = Build.VERSION.SdkInt < BuildVersionCodes.M || Android.Provider.Settings.CanDrawOverlays(this);
        if (_overlayWanted && canDraw)
        {
            _overlay ??= new OverlayWindow(this, _engine, this);
            _overlay.Show(view);
        }
        else
        {
            _overlay?.Remove();
            _overlay = null;
        }
        var nm = (NotificationManager)GetSystemService(NotificationService)!;
        nm.Notify(NotificationId, BuildNotification(view));
    }

    private void CreateChannel()
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.O)
        {
            return;
        }
        var ch = new NotificationChannel(Channel, L.T("Compteur", "Counter", "Contador"), NotificationImportance.Low);
        ((NotificationManager)GetSystemService(NotificationService)!).CreateNotificationChannel(ch);
    }

    private Notification BuildNotification(OverlayView? view = null)
    {
        view ??= _engine.Overlay();
        var open = PendingIntent.GetActivity(this, 0, new Intent(this, typeof(MainActivity)), PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
        var stop = PendingIntent.GetService(this, 1, new Intent(this, typeof(CounterService)).SetAction(ActionStop), PendingIntentFlags.Immutable);
        var toggle = PendingIntent.GetService(this, 2, new Intent(this, typeof(CounterService)).SetAction(ActionToggleOverlay), PendingIntentFlags.Immutable);
        var chest = PendingIntent.GetService(this, 3, new Intent(this, typeof(CounterService)).SetAction(ActionAddChest), PendingIntentFlags.Immutable);
        var end = PendingIntent.GetService(this, 4, new Intent(this, typeof(CounterService)).SetAction(ActionEndMatch), PendingIntentFlags.Immutable);
        var text = (_engine.InMatch ? L.T($"Partie : {_engine.LiveChests} · ", $"Match: {_engine.LiveChests} · ", $"Partida: {_engine.LiveChests} · ") : "")
            + L.T($"{view.Total} coffres · session {view.Session} · ★ {view.Treasures}",
            $"{view.Total} chests · session {view.Session} · ★ {view.Treasures}",
            $"{view.Total} cofres · sesión {view.Session} · ★ {view.Treasures}");
#pragma warning disable CA1422
        var b = Build.VERSION.SdkInt >= BuildVersionCodes.O ? new Notification.Builder(this, Channel) : new Notification.Builder(this);
#pragma warning restore CA1422
        return b.SetSmallIcon(Resource.Drawable.ic_chest)
            .SetContentTitle(L.T("Compteur de coffres actif", "Chest Counter is counting", "Contador de cofres activo"))
            .SetContentText(text)
            .SetContentIntent(open)
            .SetOngoing(true)
            .AddAction(new Notification.Action.Builder(null, L.T("+1 coffre", "+1 chest", "+1 cofre"), chest).Build())
            .AddAction(_engine.InMatch
                ? new Notification.Action.Builder(null, L.T("Fin de partie", "End match", "Fin de partida"), end).Build()
                : new Notification.Action.Builder(null, _overlayWanted ? L.T("Masquer l'overlay", "Hide overlay", "Ocultar overlay") : L.T("Afficher l'overlay", "Show overlay", "Mostrar overlay"), toggle).Build())
            .AddAction(new Notification.Action.Builder(null, L.T("Arrêter", "Stop", "Detener"), stop).Build())
            .Build()!;
    }
}

/// <summary>
/// The floating counter over Fortnite: the figures (total, current match, session, treasures) and the buttons to count
/// by hand. Tap 🧰 for each chest opened (long press: undo the last one), ★ for a treasure, ⏹ when the match is over.
/// 👁 turns the automatic detection on or off, 🎯 teaches it what a chest search looks like, ↺ (held) starts a new
/// session. Drag the figures to move it.
/// </summary>
internal sealed class OverlayWindow
{
    private readonly Context _context;
    private readonly Engine _engine;
    private readonly CounterService _service;
    private readonly Button _eye;
    private readonly Button _teach;
    private readonly Handler _main = new(Looper.MainLooper!);
    private OverlayView? _last;
    private string? _countdown;
    private long _flashUntil;
    private readonly IWindowManager _wm;
    private readonly LinearLayout _root;
    private readonly TextView _total;
    private readonly TextView _detail;
    private readonly Button _end;
    private readonly WindowManagerLayoutParams _lp;
    private bool _added;

    public OverlayWindow(Context context, Engine engine, CounterService service)
    {
        _context = context;
        _engine = engine;
        _service = service;
        _wm = context.GetSystemService(Context.WindowService).JavaCast<IWindowManager>()!;
        var prefs = context.GetSharedPreferences("overlay", FileCreationMode.Private)!;

        _root = new LinearLayout(context) { Orientation = Orientation.Horizontal };
        _root.SetGravity(GravityFlags.CenterVertical);
        _root.SetPadding(Dp(8), Dp(4), Dp(6), Dp(4));
        var bg = new Android.Graphics.Drawables.GradientDrawable();
        bg.SetColor(Color.Argb(120, 15, 27, 36));
        bg.SetCornerRadius(Dp(12));
        _root.Background = bg;

        var figures = new LinearLayout(context) { Orientation = Orientation.Vertical };
        _total = Label(22, Color.ParseColor("#F0C057"), TypefaceStyle.Bold);
        _detail = Label(12, Color.White, TypefaceStyle.Normal);
        figures.AddView(_total);
        figures.AddView(_detail);
        _root.AddView(figures);

        var chest = RoundButton("🧰+1", 64, Color.ParseColor("#F0C057"), Color.ParseColor("#0F1B24"));
        chest.Click += (_, _) => { chest.PerformHapticFeedback(FeedbackConstants.VirtualKey); _engine.AddChest(); };
        chest.LongClick += (_, _) => { chest.PerformHapticFeedback(FeedbackConstants.LongPress); _engine.UndoChest(); };
        var treasure = RoundButton("★", 44, Color.ParseColor("#6C3FB5"), Color.White);
        treasure.Click += (_, _) => { treasure.PerformHapticFeedback(FeedbackConstants.VirtualKey); _engine.AddTreasure(); };
        _end = RoundButton("⏹", 44, Color.ParseColor("#1E2F3B"), Color.White);
        _end.Click += (_, _) => _engine.EndMatch();
        _eye = RoundButton("👁", 40, Color.ParseColor("#1E2F3B"), Color.White);
        _eye.Click += (_, _) => _service.ToggleCapture();
        _teach = RoundButton("🎯", 40, Color.ParseColor("#1E2F3B"), Color.White);
        _teach.Click += (_, _) => _service.Teach();
        _root.AddView(chest);
        _root.AddView(treasure);
        _root.AddView(_end);
        // ↺ new session: held down, so that a tap in the heat of a match does not reset anything.
        var reset = RoundButton("↺", 36, Color.ParseColor("#1E2F3B"), Color.White);
        reset.Click += (_, _) => Toast.MakeText(_context, L.T("Garde le doigt sur ↺ pour remettre la session à 0.",
            "Hold ↺ to reset the session to 0.", "Mantén pulsado ↺ para poner la sesión a 0."), ToastLength.Short)!.Show();
        reset.LongClick += (_, _) =>
        {
            reset.PerformHapticFeedback(FeedbackConstants.LongPress);
            _engine.ResetSession();
            Toast.MakeText(_context, L.T("Session remise à 0.", "Session reset to 0.", "Sesión puesta a 0."), ToastLength.Short)!.Show();
        };
        _root.AddView(_eye);
        _root.AddView(_teach);
        _root.AddView(reset);
        Tick();

#pragma warning disable CA1422
        var type = Build.VERSION.SdkInt >= BuildVersionCodes.O ? WindowManagerTypes.ApplicationOverlay : WindowManagerTypes.Phone;
#pragma warning restore CA1422
        _lp = new WindowManagerLayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent, type,
            WindowManagerFlags.NotFocusable | WindowManagerFlags.LayoutNoLimits, Format.Translucent)
        {
            Gravity = GravityFlags.Top | GravityFlags.Start,
            X = prefs.GetInt("x", Dp(16)),
            Y = prefs.GetInt("y", Dp(120)),
        };

        // Drag the figures to move the counter; the position is kept.
        float downX = 0, downY = 0;
        int startX = 0, startY = 0;
        figures.Touch += (_, e) =>
        {
            switch (e.Event!.Action)
            {
                case MotionEventActions.Down:
                    downX = e.Event.RawX; downY = e.Event.RawY; startX = _lp.X; startY = _lp.Y;
                    break;
                case MotionEventActions.Move:
                    _lp.X = startX + (int)(e.Event.RawX - downX);
                    _lp.Y = startY + (int)(e.Event.RawY - downY);
                    if (_added) _wm.UpdateViewLayout(_root, _lp);
                    break;
                case MotionEventActions.Up:
                    prefs.Edit()!.PutInt("x", _lp.X).PutInt("y", _lp.Y).Apply();
                    break;
            }
            e.Handled = true;
        };
    }

    private TextView Label(float sp, Color color, TypefaceStyle style)
    {
        var t = new TextView(_context) { TextSize = sp };
        t.SetTextColor(color);
        t.SetTypeface(Typeface.DefaultFromStyle(style), style);
        t.SetShadowLayer(4, 0, 0, Color.Black);
        return t;
    }

    private Button RoundButton(string text, int sizeDp, Color fill, Color ink)
    {
        var b = new Button(_context) { Text = text, TextSize = sizeDp >= 60 ? 16 : 18 };
        b.SetAllCaps(false);
        b.SetTextColor(ink);
        b.SetPadding(0, 0, 0, 0);
        b.SetMinWidth(0);
        b.SetMinHeight(0);
        b.SetMinimumWidth(0);
        b.SetMinimumHeight(0);
        var bg = new Android.Graphics.Drawables.GradientDrawable();
        bg.SetShape(Android.Graphics.Drawables.ShapeType.Oval);
        bg.SetColor(fill);
        bg.SetStroke(Dp(1), Color.ParseColor("#C9A24F"));
        b.Background = bg;
        b.LayoutParameters = new LinearLayout.LayoutParams(Dp(sizeDp), Dp(sizeDp)) { LeftMargin = Dp(6) };
        return b;
    }

    private int Dp(int v) => (int)(v * _context.Resources!.DisplayMetrics!.Density);

    /// <summary>While the screen is watched: the similarity of the cue spot, refreshed twice a second.</summary>
    private void Tick()
    {
        if (_added && _last != null && CounterService.Capturing)
        {
            Show(_last);
        }
        _main.PostDelayed(Tick, 500);
    }

    /// <summary>A chest was detected: the total blinks green for a moment.</summary>
    public void Flash()
    {
        _flashUntil = SystemClock.ElapsedRealtime() + 1500;
        if (_last != null) Show(_last);
    }

    /// <summary>"3, 2, 1" on the counter, then the action (the screenshot of 🎯, without the countdown on it).</summary>
    public void Countdown(int seconds, Action then)
    {
        if (seconds <= 0)
        {
            _countdown = null;
            _root.Visibility = ViewStates.Invisible;
            // Hidden for the screenshot, shown again right after.
            _main.PostDelayed(() =>
            {
                then();
                _main.PostDelayed(() => _root.Visibility = ViewStates.Visible, 400);
            }, 250);
            return;
        }
        _countdown = $"🎯 {seconds}…";
        if (_last != null) Show(_last);
        _main.PostDelayed(() => Countdown(seconds - 1, then), 1000);
    }

    public void Show(OverlayView v)
    {
        _last = v;
        _total.Text = $"🧰 {v.Total}" + (v.CurrentLive ? "  ●" : "");
        var current = v.CurrentLive
            ? L.T($"Partie {v.Current}", $"Match {v.Current}", $"Partida {v.Current}")
            : v.Last != null ? L.T($"Dernière {v.Last}", $"Last {v.Last}", $"Última {v.Last}") : null;
        _detail.Text = (current != null ? current + " · " : "") + L.T($"Session {v.Session}", $"Session {v.Session}", $"Sesión {v.Session}") + $" · ★ {v.Treasures}"
            + (v.Squad != null ? $" · {OverlayModel.SquadLabel} {v.Squad}" : "");
        if (CounterService.Capturing)
        {
            var score = _service.DetectorScore;
            _detail.Text += score is { } sc ? $" · 👁 {Math.Max(0, sc):0.00}" : " · 👁 🎯?";
        }
        if (_countdown != null)
        {
            _detail.Text = _countdown;
        }
        _total.SetTextColor(SystemClock.ElapsedRealtime() < _flashUntil ? Color.ParseColor("#5BD18B") : Color.ParseColor("#F0C057"));
        _end.Visibility = _engine.InMatch ? ViewStates.Visible : ViewStates.Gone;
        _teach.Visibility = CounterService.Capturing ? ViewStates.Visible : ViewStates.Gone;
        ((Android.Graphics.Drawables.GradientDrawable)_eye.Background!).SetColor(CounterService.Capturing ? Color.ParseColor("#1F7F7A") : Color.ParseColor("#1E2F3B"));
        if (!_added)
        {
            _wm.AddView(_root, _lp);
            _added = true;
        }
    }

    public void Remove()
    {
        _main.RemoveCallbacksAndMessages(null);
        if (_added)
        {
            _wm.RemoveView(_root);
            _added = false;
        }
    }
}
