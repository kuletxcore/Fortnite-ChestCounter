using System.Text.Json;
using Android.Content;
using Android.Graphics;
using Android.Hardware.Display;
using Android.Media;
using Android.Media.Projection;
using Android.OS;
using Android.Runtime;

namespace ChestCounter.Droid;

/// <summary>Where the taught cue is kept (the cue itself is in CueMatcher.cs, without Android types).</summary>
public sealed partial class ChestCue
{
    public static string PathIn(Context c) => System.IO.Path.Combine(c.FilesDir!.AbsolutePath, "chest-cue.json");

    public static ChestCue? Load(Context c)
    {
        try
        {
            var path = PathIn(c);
            return File.Exists(path) ? JsonSerializer.Deserialize<ChestCue>(File.ReadAllText(path)) : null;
        }
        catch
        {
            return null;
        }
    }

    public void Save(Context c) => File.WriteAllText(PathIn(c), JsonSerializer.Serialize(this));
}

/// <summary>
/// Automatic chest detection: watches the screen (MediaProjection, the system asks the player each time it starts)
/// and adds a chest when the taught cue shows for a moment and then goes away, which also catches the chests that
/// auto-search opens without a tap. Only a few frames per second are looked at, at a reduced resolution, in memory:
/// nothing is recorded or saved, except the one screenshot the player takes to teach the cue.
/// </summary>
public sealed class ScreenDetector
{
    /// <summary>Short side of the captured frames, in pixels (Fortnite runs in landscape).</summary>
    private const int ShortSide = 360;
    private const long FrameIntervalMs = 110;

    private readonly Context _context;
    private readonly MediaProjection _projection;
    private readonly HandlerThread _thread;
    private readonly Handler _handler;
    private readonly ImageReader _reader;
    private readonly VirtualDisplay _display;
    private readonly int _w, _h;

    private CueMatcher? _matcher;
    private float _threshold = 0.75f;
    private byte[]? _bytes;
    private long _lastFrame;
    private Action<string?>? _grab;
    private readonly ProjectionCallback _callback;

    /// <summary>A chest was detected (called on the capture thread).</summary>
    public event Action? ChestDetected;

    /// <summary>The capture ended (the player stopped it from the system, or the projection was revoked).</summary>
    public event Action? Stopped;

    /// <summary>Similarity of the cue spot with the taught cue (-1 to 1), for the overlay. -1 = no cue taught.</summary>
    public float Score => _matcher?.Score ?? -1;

    /// <summary>Similarity from which the cue counts as shown (the "sensitivity" of the app).</summary>
    public float Threshold
    {
        get => _threshold;
        set
        {
            _threshold = value;
            if (_matcher != null) _matcher.Threshold = value;
        }
    }

    public int FrameWidth => _w;
    public int FrameHeight => _h;

    public ScreenDetector(Context context, int resultCode, Intent data)
    {
        _context = context;
        var mpm = (MediaProjectionManager)context.GetSystemService(Context.MediaProjectionService)!;
        _projection = mpm.GetMediaProjection(resultCode, data) ?? throw new InvalidOperationException("Screen capture refused");

        _thread = new HandlerThread("chest-capture");
        _thread.Start();
        _handler = new Handler(_thread.Looper!);

        // Android 14 and later require the callback before the virtual display.
        _callback = new ProjectionCallback(() => { Release(); Stopped?.Invoke(); });
        _projection.RegisterCallback(_callback, _handler);

        var metrics = context.Resources!.DisplayMetrics!;
        var longSide = Math.Max(metrics.WidthPixels, metrics.HeightPixels);
        var shortSide = Math.Min(metrics.WidthPixels, metrics.HeightPixels);
        _h = ShortSide;
        _w = (int)Math.Round(ShortSide * (double)longSide / shortSide);

        _reader = ImageReader.NewInstance(_w, _h, (ImageFormatType)1 /* RGBA_8888 */, 2);
        _reader.SetOnImageAvailableListener(new FrameListener(OnFrame), _handler);
        _display = _projection.CreateVirtualDisplay("ChestCounter", _w, _h, (int)metrics.DensityDpi / 2,
            (Android.Views.DisplayFlags)16 /* VIRTUAL_DISPLAY_FLAG_AUTO_MIRROR */, _reader.Surface, null, _handler)!;

        SetCue(ChestCue.Load(context));
    }

    public bool HasCue => _matcher != null;

    /// <summary>Uses a newly taught cue (null = none). A cue taught at another resolution or orientation is ignored.</summary>
    public void SetCue(ChestCue? cue) => _handler.Post(() =>
    {
        _matcher = cue != null && cue.FrameW == _w && cue.FrameH == _h ? CueMatcher.Create(cue) : null;
        if (_matcher != null) _matcher.Threshold = _threshold;
    });

    /// <summary>
    /// Saves the next frame (screenshot to teach the cue): calib.png to show to the player, calib.gray to cut the cue
    /// from. The callback gets the png path, or null if it failed.
    /// </summary>
    public void Grab(Action<string?> done) => _handler.Post(() => _grab = done);

    public static string CalibPng(Context c) => System.IO.Path.Combine(c.FilesDir!.AbsolutePath, "calib.png");
    public static string CalibGray(Context c) => System.IO.Path.Combine(c.FilesDir!.AbsolutePath, "calib.gray");

    private void OnFrame(ImageReader reader)
    {
        using var image = reader.AcquireLatestImage();
        if (image == null)
        {
            return;
        }
        var now = SystemClock.ElapsedRealtime();
        var grab = _grab;
        if (grab == null && (now - _lastFrame < FrameIntervalMs || _matcher == null))
        {
            return;
        }
        _lastFrame = now;

        var plane = image.GetPlanes()![0];
        var buffer = plane.Buffer!;
        var rowStride = plane.RowStride;
        var pixelStride = plane.PixelStride;
        var length = buffer.Remaining();
        if (_bytes == null || _bytes.Length < length)
        {
            _bytes = new byte[length];
        }
        buffer.Get(_bytes, 0, length);

        if (grab != null)
        {
            _grab = null;
            string? path = null;
            try
            {
                path = SaveCalibration(rowStride, pixelStride);
            }
            catch (Exception ex)
            {
                ErrorLog.Write("Grab", ex);
            }
            grab(path);
            return;
        }

        if (_matcher!.Feed(_bytes, rowStride, pixelStride, _w, _h, now))
        {
            ChestDetected?.Invoke();
        }
    }

    private string SaveCalibration(int rowStride, int pixelStride)
    {
        var gray = new byte[_w * _h];
        var pixels = new int[_w * _h];
        for (var y = 0; y < _h; y++)
        {
            for (var x = 0; x < _w; x++)
            {
                var i = y * rowStride + x * pixelStride;
                int r = _bytes![i], g = _bytes[i + 1], b = _bytes[i + 2];
                gray[y * _w + x] = (byte)((r * 77 + g * 150 + b * 29) >> 8);
                pixels[y * _w + x] = unchecked((int)0xFF000000) | (r << 16) | (g << 8) | b;
            }
        }
        File.WriteAllBytes(CalibGray(_context), gray);
        using var bitmap = Bitmap.CreateBitmap(pixels, _w, _h, Bitmap.Config.Argb8888!)!;
        var png = CalibPng(_context);
        using (var stream = File.Create(png))
        {
            bitmap.Compress(Bitmap.CompressFormat.Png!, 100, stream);
        }
        _context.GetSharedPreferences("detector", FileCreationMode.Private)!.Edit()!
            .PutInt("calibW", _w).PutInt("calibH", _h).Apply();
        return png;
    }

    private bool _released;

    public void Release()
    {
        if (_released)
        {
            return;
        }
        _released = true;
        try { _display.Release(); } catch { }
        try { _reader.Close(); } catch { }
        try { _projection.UnregisterCallback(_callback); } catch { }
        try { _projection.Stop(); } catch { }
        _thread.QuitSafely();
    }

    private sealed class FrameListener : Java.Lang.Object, ImageReader.IOnImageAvailableListener
    {
        private readonly Action<ImageReader> _onFrame;
        public FrameListener(Action<ImageReader> onFrame) => _onFrame = onFrame;
        public void OnImageAvailable(ImageReader? reader)
        {
            try
            {
                if (reader != null) _onFrame(reader);
            }
            catch (Exception ex)
            {
                ErrorLog.Write("Capture frame", ex);
            }
        }
    }

    private sealed class ProjectionCallback : MediaProjection.Callback
    {
        private readonly Action _stopped;
        public ProjectionCallback(Action stopped) => _stopped = stopped;
        public override void OnStop() => _stopped();
    }
}
