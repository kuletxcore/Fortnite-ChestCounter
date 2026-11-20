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
/// Keeps counting while Fortnite is played: every few seconds the new replays are copied and read, and a small
/// floating counter (like the PC overlay) is drawn over the game. Stopped from its notification or from the app.
/// </summary>
// Declared in AndroidManifest.xml (a special-use foreground service needs its <property> there).
[Register("io.github.chestcounter.android.CounterService")]
public sealed class CounterService : Service
{
    public const string ActionStop = "io.github.chestcounter.STOP";
    public const string ActionToggleOverlay = "io.github.chestcounter.TOGGLE_OVERLAY";
    private const string Channel = "counter";
    private const int NotificationId = 1;
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

    public static bool Running { get; private set; }

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
        }

        var notification = BuildNotification();
        if (Build.VERSION.SdkInt >= BuildVersionCodes.UpsideDownCake)
        {
            StartForeground(NotificationId, notification, ForegroundService.TypeSpecialUse);
        }
        else
        {
            StartForeground(NotificationId, notification);
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

    public override void OnDestroy()
    {
        Running = false;
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
            _overlay ??= new OverlayWindow(this);
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
        var text = L.T($"{view.Total} coffres · session {view.Session} · ★ {view.Treasures}",
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
            .AddAction(new Notification.Action.Builder(null, _overlayWanted ? L.T("Masquer l'overlay", "Hide overlay", "Ocultar overlay") : L.T("Afficher l'overlay", "Show overlay", "Mostrar overlay"), toggle).Build())
            .AddAction(new Notification.Action.Builder(null, L.T("Arrêter", "Stop", "Detener"), stop).Build())
            .Build()!;
    }
}

/// <summary>The floating counter: total chests, last match, session and treasures, outlined like the game's HUD.</summary>
internal sealed class OverlayWindow
{
    private readonly Context _context;
    private readonly IWindowManager _wm;
    private readonly LinearLayout _root;
    private readonly TextView _total;
    private readonly TextView _detail;
    private readonly WindowManagerLayoutParams _lp;
    private bool _added;

    public OverlayWindow(Context context)
    {
        _context = context;
        _wm = context.GetSystemService(Context.WindowService).JavaCast<IWindowManager>()!;
        var prefs = context.GetSharedPreferences("overlay", FileCreationMode.Private)!;

        _root = new LinearLayout(context) { Orientation = Orientation.Vertical };
        _root.SetPadding(Dp(8), Dp(4), Dp(8), Dp(4));
        var bg = new Android.Graphics.Drawables.GradientDrawable();
        bg.SetColor(Color.Argb(110, 15, 27, 36));
        bg.SetCornerRadius(Dp(10));
        _root.Background = bg;

        _total = Label(26, Color.ParseColor("#F0C057"), TypefaceStyle.Bold);
        _detail = Label(13, Color.White, TypefaceStyle.Normal);
        _root.AddView(_total);
        _root.AddView(_detail);

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

        // Drag to move; the position is kept.
        float downX = 0, downY = 0;
        int startX = 0, startY = 0;
        _root.Touch += (_, e) =>
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

    private int Dp(int v) => (int)(v * _context.Resources!.DisplayMetrics!.Density);

    public void Show(OverlayView v)
    {
        _total.Text = $"🧰 {v.Total}" + (v.CurrentLive ? "  ●" : "");
        var last = v.Last != null ? L.T($"Dernière {v.Last}", $"Last {v.Last}", $"Última {v.Last}") + " · " : "";
        _detail.Text = last + L.T($"Session {v.Session}", $"Session {v.Session}", $"Sesión {v.Session}") + $" · ★ {v.Treasures}"
            + (v.Squad != null ? $" · {OverlayModel.SquadLabel} {v.Squad}" : "");
        if (!_added)
        {
            _wm.AddView(_root, _lp);
            _added = true;
        }
    }

    public void Remove()
    {
        if (_added)
        {
            _wm.RemoveView(_root);
            _added = false;
        }
    }
}
