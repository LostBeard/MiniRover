using System.Diagnostics;
using ILGPU;
using ILGPU.Runtime;
using ILGPU.Runtime.CPU;
using MiniRover.Video;
using StbImageSharp;
using StbImageWriteSharp;

namespace MiniRover.ConsoleApp;

/// <summary>
/// Runs MiniRover.Video's GPU kernels on saved car frames (link "snap"), on ILGPU's CPU accelerator, and reports
/// what each setting did: brightness, a noise figure, and before/after images to look at.
///
/// Noise figure: with the camera still, the change from one frame to the next is almost all noise. The mean absolute
/// luma change divided by the mean luma is fair before and after brightening (both scale together).
/// </summary>
static class VideoFxCommand
{
    public static int Run(string inDir, string outDir, float denoise, float sharpen, bool levels, bool whiteBalance)
    {
        var files = Directory.GetFiles(inDir, "*.jpg").OrderBy(f => f, StringComparer.Ordinal).ToArray();
        if (files.Length == 0) { Console.Error.WriteLine("no .jpg frames in " + inDir); return 2; }
        Directory.CreateDirectory(outDir);

        using var context = Context.Create(b => b.CPU());
        using var accelerator = context.CreateCPUAccelerator(0);
        using var fx = new VideoEnhancer(accelerator);
        var settings = new VideoEnhanceSettings { Denoise = denoise, Sharpen = sharpen, AutoLevels = levels, WhiteBalance = whiteBalance };
        Console.WriteLine($"videofx: {files.Length} frames, denoise {denoise}, sharpen {sharpen}, levels {levels}, white balance {whiteBalance}");

        int[]? prevIn = null, prevOut = null;
        double noiseIn = 0, noiseOut = 0, lumaIn = 0, lumaOut = 0, blockIn = 0, blockOut = 0, chromaIn = 0, chromaOut = 0;
        int pairs = 0;
        var sw = new Stopwatch();
        for (int f = 0; f < files.Length; f++)
        {
            var img = ImageResult.FromMemory(File.ReadAllBytes(files[f]), StbImageSharp.ColorComponents.RedGreenBlueAlpha);
            int w = img.Width, h = img.Height;
            var pixels = new int[w * h];
            Buffer.BlockCopy(img.Data, 0, pixels, 0, pixels.Length * 4); // RGBA bytes = R in the low byte

            sw.Start();
            fx.Prepare(w, h).CopyFromCPU(pixels);
            fx.Process(settings);
            accelerator.Synchronize();
            sw.Stop();
            var result = new int[w * h];
            fx.Output.View.AsContiguous().CopyToCPU(result);

            // Skip the top row (the camera's broken line) in the measurements.
            if (prevIn != null && prevOut != null)
            {
                noiseIn += MeanAbsLumaDiff(pixels, prevIn, w, h);
                chromaIn += MeanAbsChromaDiff(pixels, prevIn, w, h);
                chromaOut += MeanAbsChromaDiff(result, prevOut, w, h);
                noiseOut += MeanAbsLumaDiff(result, prevOut, w, h);
                pairs++;
            }
            blockIn += Blockiness(pixels, w, h);
            blockOut += Blockiness(result, w, h);
            lumaIn += MeanLuma(pixels, w, h);
            lumaOut += MeanLuma(result, w, h);
            prevIn = pixels;
            prevOut = result;

            if (f == files.Length / 2 || f == files.Length - 1) WriteSideBySide(Path.Combine(outDir, $"compare{f:D3}.png"), pixels, result, w, h);
        }
        lumaIn /= files.Length;
        lumaOut /= files.Length;
        if (pairs > 0) { noiseIn /= pairs; noiseOut /= pairs; chromaIn /= pairs; chromaOut /= pairs; }
        Console.WriteLine($"  mean luma {lumaIn:F1} -> {lumaOut:F1}; blockiness {blockIn / files.Length:F3} -> {blockOut / files.Length:F3} (1 = no 8x8 grid)");
        Console.WriteLine($"  frame-to-frame change {noiseIn:F2} -> {noiseOut:F2} luma; relative noise {noiseIn / lumaIn * 100:F2}% -> {noiseOut / lumaOut * 100:F2}%");
        Console.WriteLine($"  colour noise (frame-to-frame chroma change) {chromaIn:F2} -> {chromaOut:F2} levels");
        Console.WriteLine($"  CPU accelerator time {sw.ElapsedMilliseconds / (double)files.Length:F1} ms/frame (not the browser's GPU time)");
        Console.WriteLine($"  images: {outDir} (left original, right processed)");
        return 0;
    }

    static int Luma(int p) => ((p & 0xFF) * 77 + ((p >> 8) & 0xFF) * 150 + ((p >> 16) & 0xFF) * 29) >> 8;

    static double MeanLuma(int[] a, int w, int h)
    {
        long s = 0;
        for (int i = w; i < w * h; i++) s += Luma(a[i]);
        return s / (double)(w * (h - 1));
    }

    static double MeanAbsLumaDiff(int[] a, int[] b, int w, int h)
    {
        if (a.Length != b.Length) return 0;
        long s = 0;
        for (int i = w; i < w * h; i++) s += Math.Abs(Luma(a[i]) - Luma(b[i]));
        return s / (double)(w * (h - 1));
    }


    /// <summary>Mean horizontal luma step across 8-pixel block boundaries divided by the mean step elsewhere (rows 1+):
    /// 1 = no JPEG block grid, higher = visible blocks.</summary>
    static double Blockiness(int[] a, int w, int h)
    {
        double atEdge = 0, inside = 0;
        long nEdge = 0, nInside = 0;
        for (int y = 1; y < h; y++)
        {
            for (int x = 0; x + 1 < w; x++)
            {
                int d = Math.Abs(Luma(a[y * w + x + 1]) - Luma(a[y * w + x]));
                if ((x & 7) == 7) { atEdge += d; nEdge++; } else { inside += d; nInside++; }
            }
        }
        return nEdge == 0 || inside == 0 ? 1 : (atEdge / nEdge) / (inside / nInside);
    }

    /// <summary>Mean frame-to-frame change of the colour (|Cb| + |Cr| difference, BT.601) - colour speckle on a still scene.</summary>
    static double MeanAbsChromaDiff(int[] a, int[] b, int w, int h)
    {
        if (a.Length != b.Length) return 0;
        double s = 0;
        for (int i = w; i < w * h; i++)
        {
            var (cba, cra) = Chroma(a[i]);
            var (cbb, crb) = Chroma(b[i]);
            s += Math.Abs(cba - cbb) + Math.Abs(cra - crb);
        }
        return s / (w * (h - 1));
    }

    static (double Cb, double Cr) Chroma(int p)
    {
        double r = p & 0xFF, g = (p >> 8) & 0xFF, b = (p >> 16) & 0xFF;
        return (-0.1687 * r - 0.3313 * g + 0.5 * b, 0.5 * r - 0.4187 * g - 0.0813 * b);
    }
    static void WriteSideBySide(string path, int[] left, int[] right, int w, int h)
    {
        var both = new byte[w * 2 * h * 4];
        for (int y = 0; y < h; y++)
        {
            Buffer.BlockCopy(left, y * w * 4, both, y * w * 2 * 4, w * 4);
            Buffer.BlockCopy(right, y * w * 4, both, (y * w * 2 + w) * 4, w * 4);
        }
        using var stream = File.Create(path);
        new ImageWriter().WritePng(both, w * 2, h, StbImageWriteSharp.ColorComponents.RedGreenBlueAlpha, stream);
    }
}
