namespace ChestCounter.Droid;

/// <summary>
/// Compares the cue spot of each frame with the taught cue (normalized cross-correlation on grayscale, a few pixels of
/// play around the spot) and turns "shown for a moment, then gone" into one chest. No Android types: tested on desktop.
/// </summary>
public sealed class CueMatcher
{
    /// <summary>The cue must show at least this long (a glance at a chest is not a search).</summary>
    public const long MinVisibleMs = 350;

    /// <summary>Two chests can't be searched closer together than this.</summary>
    public const long CooldownMs = 1200;

    private const int Jitter = 3;

    private readonly ChestCue _cue;
    private readonly float[] _template;
    private readonly float _norm;
    private readonly float[] _patch;
    private long _visibleSince = -1;
    private long _lastCount = long.MinValue / 2;

    public float Threshold { get; set; } = 0.75f;

    /// <summary>Best similarity of the last frame, from -1 to 1.</summary>
    public float Score { get; private set; } = -1;

    private CueMatcher(ChestCue cue, float[] template, float norm)
    {
        _cue = cue;
        _template = template;
        _norm = norm;
        _patch = new float[template.Length];
    }

    /// <summary>Null when the cue can't be used (wrong size, or one plain color that matches anything).</summary>
    public static CueMatcher? Create(ChestCue cue)
    {
        byte[] gray;
        try
        {
            gray = Convert.FromBase64String(cue.Gray);
        }
        catch (FormatException)
        {
            return null;
        }
        if (cue.W <= 0 || cue.H <= 0 || gray.Length != cue.W * cue.H)
        {
            return null;
        }
        var t = gray.Select(b => (float)b).ToArray();
        var mean = t.Average();
        for (var i = 0; i < t.Length; i++) t[i] -= mean;
        var norm = MathF.Sqrt(t.Sum(v => v * v));
        // Less than ~2 gray levels of contrast on average: nothing to recognize.
        return norm / MathF.Sqrt(t.Length) < 2 ? null : new CueMatcher(cue, t, norm);
    }

    /// <summary>Looks at one RGBA frame. True when a chest search just ended.</summary>
    public bool Feed(byte[] rgba, int rowStride, int pixelStride, int width, int height, long nowMs)
    {
        var best = -1f;
        for (var dy = -Jitter; dy <= Jitter; dy++)
        {
            for (var dx = -Jitter; dx <= Jitter; dx++)
            {
                int x0 = _cue.X + dx, y0 = _cue.Y + dy;
                if (x0 < 0 || y0 < 0 || x0 + _cue.W > width || y0 + _cue.H > height)
                {
                    continue;
                }
                float sum = 0;
                var k = 0;
                for (var y = 0; y < _cue.H; y++)
                {
                    var row = (y0 + y) * rowStride + x0 * pixelStride;
                    for (var x = 0; x < _cue.W; x++)
                    {
                        var i = row + x * pixelStride;
                        float g = (rgba[i] * 77 + rgba[i + 1] * 150 + rgba[i + 2] * 29) >> 8;
                        _patch[k++] = g;
                        sum += g;
                    }
                }
                var mean = sum / _patch.Length;
                float dot = 0, norm = 0;
                for (var i = 0; i < _patch.Length; i++)
                {
                    var v = _patch[i] - mean;
                    dot += v * _template[i];
                    norm += v * v;
                }
                var score = norm < 1 ? 0 : dot / (MathF.Sqrt(norm) * _norm);
                if (score > best) best = score;
            }
        }
        Score = best;

        var visible = best >= Threshold;
        if (visible)
        {
            if (_visibleSince < 0) _visibleSince = nowMs;
            return false;
        }
        if (_visibleSince < 0)
        {
            return false;
        }
        // The cue went away: the search is over (or was given up, which can't be told apart: undo is a long press).
        var shown = nowMs - _visibleSince;
        _visibleSince = -1;
        if (shown >= MinVisibleMs && nowMs - _lastCount >= CooldownMs)
        {
            _lastCount = nowMs;
            return true;
        }
        return false;
    }
}
