using ILGPU;
using ILGPU.Runtime;
using ILGPU.Runtime.CPU;
using MiniRover.Video;
using Xunit;

namespace MiniRover.Video.Tests;

/// <summary>
/// The picture clean-up kernels, run on ILGPU's CPU accelerator (the same kernel code the browser runs on WebGPU).
/// Frames are packed RGBA8, R in the low byte.
/// </summary>
public sealed class VideoEnhancerTests : IDisposable
{
    readonly Context _context = Context.Create(b => b.CPU());
    readonly Accelerator _accelerator;

    public VideoEnhancerTests() => _accelerator = _context.CreateCPUAccelerator(0);

    public void Dispose()
    {
        _accelerator.Dispose();
        _context.Dispose();
    }

    static int Rgb(int r, int g, int b) => unchecked((int)0xFF000000) | (b << 16) | (g << 8) | r;
    static (int R, int G, int B) Split(int p) => (p & 0xFF, (p >> 8) & 0xFF, (p >> 16) & 0xFF);

    static readonly VideoEnhanceSettings AllOff = new() { Denoise = 0, Sharpen = 0, AutoLevels = false, WhiteBalance = false, FixTopRow = false };

    int[] Run(VideoEnhancer fx, int[] frame, int w, int h, VideoEnhanceSettings s, int outW = 0, int outH = 0)
    {
        fx.Prepare(w, h).CopyFromCPU(frame);
        fx.Process(s, outW, outH);
        _accelerator.Synchronize();
        var result = new int[fx.Output.IntExtent.X * fx.Output.IntExtent.Y];
        fx.Output.View.AsContiguous().CopyToCPU(result);
        return result;
    }

    static int[] Noise(int w, int h, int seed)
    {
        var rng = new Random(seed);
        var f = new int[w * h];
        for (int i = 0; i < f.Length; i++) f[i] = Rgb(rng.Next(256), rng.Next(256), rng.Next(256));
        return f;
    }

    [Fact]
    public void Every_stage_off_returns_the_frame_unchanged()
    {
        using var fx = new VideoEnhancer(_accelerator);
        var frame = Noise(40, 24, 1);
        Assert.Equal(frame, Run(fx, frame, 40, 24, AllOff));
    }

    [Fact]
    public void The_top_row_fix_copies_the_second_row()
    {
        using var fx = new VideoEnhancer(_accelerator);
        var frame = Noise(16, 8, 2);
        var result = Run(fx, frame, 16, 8, new VideoEnhanceSettings { Denoise = 0, Sharpen = 0, AutoLevels = false, WhiteBalance = false, FixTopRow = true });
        Assert.Equal(frame[16..32], result[0..16]);
        Assert.Equal(frame[16..], result[16..]);
    }

    [Fact]
    public void Upscaling_keeps_a_flat_picture_flat_and_has_the_requested_size()
    {
        using var fx = new VideoEnhancer(_accelerator);
        var flat = Enumerable.Repeat(Rgb(90, 140, 200), 32 * 24).ToArray();
        var result = Run(fx, flat, 32, 24, AllOff, 80, 60);
        Assert.Equal(80 * 60, result.Length);
        Assert.All(result, p => Assert.Equal(Rgb(90, 140, 200), p));
    }

    [Fact]
    public void Upscaling_follows_a_ramp_without_steps()
    {
        // A horizontal grey ramp, upscaled 3x: values must rise monotonically along each row (no blocks, no reversal).
        using var fx = new VideoEnhancer(_accelerator);
        int w = 32, h = 8;
        var ramp = new int[w * h];
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) ramp[y * w + x] = Rgb(x * 8, x * 8, x * 8);
        var result = Run(fx, ramp, w, h, AllOff, w * 3, h * 3);
        int row = 10 * w * 3;
        for (int x = 1; x < w * 3; x++) Assert.True(Split(result[row + x]).R >= Split(result[row + x - 1]).R, $"step down at x={x}");
        Assert.True(Split(result[row + w * 3 / 2]).R > 100 && Split(result[row + w * 3 / 2]).R < 160, "the middle of the ramp moved");
    }

    [Fact]
    public void Deblocking_smooths_a_small_step_on_the_8_pixel_grid_but_keeps_a_real_edge()
    {
        // Columns 0-7 at 60, 8-15 at 66 (a JPEG block step), 16-23 at 66, 24-31 at 200 (a real edge).
        using var fx = new VideoEnhancer(_accelerator);
        int w = 32, h = 16;
        var frame = new int[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int v = x < 8 ? 60 : (x < 24 ? 66 : 200);
                frame[y * w + x] = Rgb(v, v, v);
            }
        // Denoise only: deblock + spatial filter; no tone changes.
        var s = new VideoEnhanceSettings { Denoise = 0.5f, Sharpen = 0, AutoLevels = false, WhiteBalance = false, FixTopRow = false };
        var result = Run(fx, frame, w, h, s);
        int row = 8 * w;
        int step = Math.Abs(Split(result[row + 8]).R - Split(result[row + 7]).R);
        Assert.True(step <= 2, $"the 6-level block step is still {step} levels after deblocking");
        int edge = Split(result[row + 24]).R - Split(result[row + 23]).R;
        Assert.True(edge >= 120, $"the real edge (134 levels) was smoothed down to {edge}");
    }

    [Fact]
    public void White_balance_leaves_a_neutral_grey_scene_neutral()
    {
        using var fx = new VideoEnhancer(_accelerator);
        int w = 32, h = 24;
        var frame = new int[w * h];
        for (int i = 0; i < frame.Length; i++) { int v = 40 + (i % 7) * 20; frame[i] = Rgb(v, v, v); }
        var s = new VideoEnhanceSettings { Denoise = 0, Sharpen = 0, AutoLevels = false, WhiteBalance = true, FixTopRow = false };
        var result = Run(fx, frame, w, h, s);
        Assert.All(result, p => { var (r, g, b) = Split(p); Assert.True(Math.Abs(r - g) <= 1 && Math.Abs(g - b) <= 1, $"grey became {r},{g},{b}"); });
    }

    [Fact]
    public void White_balance_pulls_a_yellow_cast_toward_grey_in_the_mid_tones()
    {
        using var fx = new VideoEnhancer(_accelerator);
        int w = 32, h = 24;
        var frame = Enumerable.Repeat(Rgb(110, 120, 70), w * h).ToArray();  // yellowish mid-tone, like the car's room
        var s = new VideoEnhanceSettings { Denoise = 0, Sharpen = 0, AutoLevels = false, WhiteBalance = true, FixTopRow = false };
        var (r, g, b) = Split(Run(fx, frame, w, h, s)[w * 12 + 16]);
        Assert.True(g - b < (120 - 70) / 2, $"the cast is still strong: {r},{g},{b}");
        Assert.True(b > 70, "blue was not raised");
    }

    [Fact]
    public void Auto_levels_brightens_a_dark_frame()
    {
        using var fx = new VideoEnhancer(_accelerator);
        int w = 32, h = 24;
        var frame = new int[w * h];
        for (int i = 0; i < frame.Length; i++) { int v = 5 + (i % 11) * 9; frame[i] = Rgb(v, v, v); }   // 5..95 (a range narrower than 25% of full scale is deliberately not stretched)
        var s = new VideoEnhanceSettings { Denoise = 0, Sharpen = 0, AutoLevels = true, WhiteBalance = false, FixTopRow = false };
        var result = Run(fx, frame, w, h, s);
        double before = frame.Average(p => Split(p).G), after = result.Average(p => Split(p).G);
        Assert.True(after > before * 2, $"mean {before:F0} -> {after:F0}");
        Assert.True(result.Max(p => Split(p).G) >= 250, "the brightest pixels were not stretched to white");
    }

    [Fact]
    public void Temporal_denoise_smooths_flicker_on_a_still_scene()
    {
        // The whole frame flickers +-6 levels together, frame to frame: flat within a frame, so the spatial filter cannot
        // touch it and only the temporal filter (remembering earlier frames) can calm it.
        using var fx = new VideoEnhancer(_accelerator);
        int w = 24, h = 16;
        var rng = new Random(3);
        var s = new VideoEnhanceSettings { Denoise = 1f, Sharpen = 0, AutoLevels = false, WhiteBalance = false, FixTopRow = false };
        int[]? prevIn = null, prevOut = null;
        double flickerIn = 0, flickerOut = 0;
        for (int f = 0; f < 12; f++)
        {
            var frame = new int[w * h];
            int level = 100 + rng.Next(-6, 7);
            for (int i = 0; i < frame.Length; i++) frame[i] = Rgb(level, level, level);
            var result = Run(fx, frame, w, h, s);
            if (f >= 4 && prevIn != null && prevOut != null)
            {
                for (int i = 0; i < frame.Length; i++)
                {
                    flickerIn += Math.Abs(Split(frame[i]).G - Split(prevIn[i]).G);
                    flickerOut += Math.Abs(Split(result[i]).G - Split(prevOut[i]).G);
                }
            }
            prevIn = frame;
            prevOut = result;
        }
        Assert.True(flickerOut < flickerIn * 0.75, $"flicker {flickerIn:F0} -> {flickerOut:F0}");
    }
}
