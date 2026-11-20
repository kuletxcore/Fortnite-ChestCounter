using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Media.Projection;
using Android.OS;
using Android.Views;
using Android.Widget;

namespace ChestCounter.Droid;

/// <summary>
/// Invisible screen that asks Android for screen capture (the system dialog, required each time capture starts) and
/// hands the answer to the counter service. Started from the overlay's 👁 button or from the app.
/// </summary>
[Activity(Theme = "@android:style/Theme.Translucent.NoTitleBar", Exported = false, ExcludeFromRecents = true,
    LaunchMode = LaunchMode.SingleInstance, NoHistory = false)]
public sealed class CaptureConsentActivity : Activity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        var mpm = (MediaProjectionManager)GetSystemService(MediaProjectionService)!;
        StartActivityForResult(mpm.CreateScreenCaptureIntent(), 1);
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (resultCode == Result.Ok && data != null)
        {
            var i = new Intent(this, typeof(CounterService)).SetAction(CounterService.ActionStartCapture)
                .PutExtra("code", (int)resultCode).PutExtra("data", data);
            if (Build.VERSION.SdkInt >= BuildVersionCodes.O) StartForegroundService(i); else StartService(i);
        }
        Finish();
    }
}

/// <summary>
/// Teaching the chest cue: the screenshot taken by 🎯 is shown, the player draws a box around what only shows while a
/// chest is being searched (the search ring, the Search button...), and that spot is what the detector watches.
/// </summary>
[Activity(Theme = "@android:style/Theme.DeviceDefault.NoActionBar.Fullscreen", Exported = false,
    ScreenOrientation = ScreenOrientation.SensorLandscape, ExcludeFromRecents = true)]
public sealed class CalibrateActivity : Activity
{
    private SelectView _view = null!;
    private Bitmap? _bitmap;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        _bitmap = BitmapFactory.DecodeFile(ScreenDetector.CalibPng(this));
        if (_bitmap == null)
        {
            Finish();
            return;
        }

        var root = new FrameLayout(this);
        root.SetBackgroundColor(Color.Black);
        _view = new SelectView(this, _bitmap);
        root.AddView(_view);

        var bar = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        bar.SetGravity(GravityFlags.CenterVertical);
        bar.SetBackgroundColor(Color.Argb(200, 15, 27, 36));
        bar.SetPadding(24, 12, 24, 12);
        var hint = new TextView(this)
        {
            Text = L.T("Entoure ce qui s'affiche seulement pendant la fouille d'un coffre (anneau de fouille, bouton Fouiller…).",
                "Draw a box around what only shows while a chest is being searched (search ring, Search button…).",
                "Enmarca lo que solo aparece mientras se busca un cofre (anillo de búsqueda, botón Buscar…)."),
            TextSize = 14,
        };
        hint.SetTextColor(Color.ParseColor("#EFE2C4"));
        bar.AddView(hint, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        var cancel = new Button(this) { Text = L.T("Annuler", "Cancel", "Cancelar") };
        cancel.Click += (_, _) => Finish();
        var save = new Button(this) { Text = L.T("Enregistrer", "Save", "Guardar") };
        save.Click += (_, _) => Save();
        bar.AddView(cancel);
        bar.AddView(save);
        root.AddView(bar, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent, GravityFlags.Bottom));
        SetContentView(root);
    }

    private void Save()
    {
        var r = _view.SelectionInBitmap();
        if (r == null || r.Width() < 6 || r.Height() < 6)
        {
            Toast.MakeText(this, L.T("Dessine un cadre plus grand.", "Draw a bigger box.", "Dibuja un recuadro más grande."), ToastLength.Short)!.Show();
            return;
        }
        // Keep the cue small: the detector compares it a few times per second.
        var w = Math.Min(r.Width(), 80);
        var h = Math.Min(r.Height(), 80);
        var x0 = r.CenterX() - w / 2;
        var y0 = r.CenterY() - h / 2;
        var full = File.ReadAllBytes(ScreenDetector.CalibGray(this));
        int fw = _bitmap!.Width, fh = _bitmap.Height;
        var cut = new byte[w * h];
        for (var y = 0; y < h; y++)
        {
            Array.Copy(full, (y0 + y) * fw + x0, cut, y * w, w);
        }
        new ChestCue { X = x0, Y = y0, W = w, H = h, FrameW = fw, FrameH = fh, Gray = Convert.ToBase64String(cut) }.Save(this);
        StartService(new Intent(this, typeof(CounterService)).SetAction(CounterService.ActionCueChanged));
        Toast.MakeText(this, L.T("Repère enregistré : les coffres sont comptés tout seuls.", "Cue saved: chests are now counted by themselves.", "Señal guardada: los cofres se cuentan solos."), ToastLength.Long)!.Show();
        Finish();
    }

    protected override void OnDestroy()
    {
        _bitmap?.Recycle();
        base.OnDestroy();
    }

    /// <summary>The screenshot, fitted to the screen, with the box the player drags.</summary>
    private sealed class SelectView : View
    {
        private readonly Bitmap _bitmap;
        private readonly Paint _box = new() { Color = Color.ParseColor("#F0C057"), StrokeWidth = 4 };
        private readonly Paint _shade = new() { Color = Color.Argb(110, 0, 0, 0) };
        private RectF _dest = new();
        private float _x0, _y0, _x1, _y1;
        private bool _has;

        public SelectView(Context c, Bitmap bitmap) : base(c)
        {
            _bitmap = bitmap;
            _box.SetStyle(Paint.Style.Stroke);
        }

        protected override void OnDraw(Canvas canvas)
        {
            base.OnDraw(canvas);
            var scale = Math.Min((float)Width / _bitmap.Width, (float)Height / _bitmap.Height);
            var w = _bitmap.Width * scale;
            var h = _bitmap.Height * scale;
            _dest = new RectF((Width - w) / 2, (Height - h) / 2, (Width + w) / 2, (Height + h) / 2);
            canvas.DrawBitmap(_bitmap, null, _dest, null);
            if (_has)
            {
                var r = new RectF(Math.Min(_x0, _x1), Math.Min(_y0, _y1), Math.Max(_x0, _x1), Math.Max(_y0, _y1));
                canvas.DrawRect(0, 0, Width, r.Top, _shade);
                canvas.DrawRect(0, r.Bottom, Width, Height, _shade);
                canvas.DrawRect(0, r.Top, r.Left, r.Bottom, _shade);
                canvas.DrawRect(r.Right, r.Top, Width, r.Bottom, _shade);
                canvas.DrawRect(r, _box);
            }
        }

        public override bool OnTouchEvent(MotionEvent? e)
        {
            if (e == null) return false;
            switch (e.Action)
            {
                case MotionEventActions.Down:
                    _x0 = _x1 = e.GetX();
                    _y0 = _y1 = e.GetY();
                    _has = true;
                    break;
                case MotionEventActions.Move:
                case MotionEventActions.Up:
                    _x1 = e.GetX();
                    _y1 = e.GetY();
                    break;
            }
            Invalidate();
            return true;
        }

        /// <summary>The box in screenshot pixels, clamped to the picture.</summary>
        public Rect? SelectionInBitmap()
        {
            if (!_has || _dest.Width() <= 0) return null;
            var scale = _bitmap.Width / _dest.Width();
            int Bx(float v) => Math.Clamp((int)((v - _dest.Left) * scale), 0, _bitmap.Width);
            int By(float v) => Math.Clamp((int)((v - _dest.Top) * scale), 0, _bitmap.Height);
            return new Rect(Bx(Math.Min(_x0, _x1)), By(Math.Min(_y0, _y1)), Bx(Math.Max(_x0, _x1)), By(Math.Max(_y0, _y1)));
        }
    }
}
