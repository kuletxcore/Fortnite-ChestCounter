using ChestCounter.Droid;
using Xunit;

namespace ChestCounter.Tests;

/// <summary>Automatic chest detection of the Android app, on synthetic frames (a search ring drawn on noise).</summary>
public class CueMatcherTests
{
    private const int W = 640, H = 360, Pad = 64;
    private static int Stride => W * 4 + Pad; // rows padded, as ImageReader may do
    private readonly Random _rnd = new(1);

    private byte[] Frame(bool ring, int shift = 0, float bright = 1f)
    {
        var f = new byte[Stride * H];
        for (var y = 0; y < H; y++)
        {
            for (var x = 0; x < W; x++)
            {
                var i = y * Stride + x * 4;
                var v = (byte)_rnd.Next(40, 200);
                f[i] = v; f[i + 1] = (byte)(v / 2); f[i + 2] = (byte)(255 - v); f[i + 3] = 255;
            }
        }
        if (ring)
        {
            for (var y = 0; y < 30; y++)
            {
                for (var x = 0; x < 30; x++)
                {
                    var d = Math.Sqrt((x - 15) * (x - 15) + (y - 15) * (y - 15));
                    var i = (250 + y) * Stride + (500 + x + shift) * 4;
                    if (d > 9 && d < 13) f[i] = f[i + 1] = f[i + 2] = (byte)(250 * bright);
                    else if (d <= 9) f[i] = f[i + 1] = f[i + 2] = (byte)(30 * bright);
                }
            }
        }
        return f;
    }

    private static ChestCue Cue(byte[] f)
    {
        var g = new byte[30 * 30];
        for (var y = 0; y < 30; y++)
        {
            for (var x = 0; x < 30; x++)
            {
                var i = (250 + y) * Stride + (500 + x) * 4;
                g[y * 30 + x] = (byte)((f[i] * 77 + f[i + 1] * 150 + f[i + 2] * 29) >> 8);
            }
        }
        return new ChestCue { X = 500, Y = 250, W = 30, H = 30, FrameW = W, FrameH = H, Gray = Convert.ToBase64String(g) };
    }

    private CueMatcher Matcher() => CueMatcher.Create(Cue(Frame(true)))!;

    private static int Feed(CueMatcher m, ref long t, IEnumerable<byte[]> frames)
    {
        var n = 0;
        foreach (var f in frames)
        {
            if (m.Feed(f, Stride, 4, W, H, t)) n++;
            t += 110;
        }
        return n;
    }

    [Fact]
    public void BackgroundAloneCountsNothing()
    {
        var m = Matcher();
        long t = 0;
        Assert.Equal(0, Feed(m, ref t, Enumerable.Range(0, 10).Select(_ => Frame(false))));
        Assert.True(m.Score < 0.5f);
    }

    [Fact]
    public void ASearchCountsOneChest()
    {
        var m = Matcher();
        long t = 0;
        // Shown 660 ms, moving by up to 2 px and 20% darker, then gone.
        var frames = Enumerable.Range(0, 6).Select(i => Frame(true, i % 3, 0.8f)).Append(Frame(false));
        Assert.Equal(1, Feed(m, ref t, frames));
    }

    [Fact]
    public void AGlanceCountsNothing()
    {
        var m = Matcher();
        long t = 0;
        Assert.Equal(0, Feed(m, ref t, new[] { Frame(true), Frame(true), Frame(false) }));
    }

    [Fact]
    public void TwoSearchesCountTwoChests()
    {
        var m = Matcher();
        long t = 0;
        var search = Enumerable.Range(0, 5).Select(_ => Frame(true)).Append(Frame(false));
        var pause = Enumerable.Range(0, 12).Select(_ => Frame(false));
        Assert.Equal(2, Feed(m, ref t, search.Concat(pause).Concat(Enumerable.Range(0, 5).Select(_ => Frame(true)).Append(Frame(false)))));
    }

    [Fact]
    public void UnusableCuesAreRefused()
    {
        Assert.Null(CueMatcher.Create(new ChestCue { W = 4, H = 4, Gray = Convert.ToBase64String(new byte[16]) })); // one plain color
        Assert.Null(CueMatcher.Create(new ChestCue { W = 4, H = 4, Gray = Convert.ToBase64String(new byte[10]) })); // wrong size
        Assert.Null(CueMatcher.Create(new ChestCue { W = 4, H = 4, Gray = "not base64!" }));
    }
}
