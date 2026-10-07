using ILGPU;
using ILGPU.Runtime;
using ILGPU.Runtime.CPU;
using MiniRover.Video;
using SpawnDev.ILGPU.ML;
using SpawnDev.ILGPU.ML.Pipelines;
using StbImageSharp;

namespace MiniRover.ConsoleApp;

/// <summary>
/// Runs the app's face-detection path on an image, on ILGPU's CPU accelerator: picture clean-up (VideoEnhancer),
/// then BlazeFace reading the cleaned frame straight from VideoEnhancer.FrameView, as the browser does on WebGPU.
/// The image is scaled to the car's 320x240 and darkened to the car's room-light brightness, then tried with the
/// clean-up off and on.
/// </summary>
static class FaceTestCommand
{
    public static async Task<int> RunAsync(string imagePath, string modelPath)
    {
        var img = ImageResult.FromMemory(File.ReadAllBytes(imagePath), ColorComponents.RedGreenBlueAlpha);
        const int w = 320, h = 240;
        // Fit (centre crop to 4:3) and scale to 320x240, nearest neighbour is fine for a detector test.
        double srcAspect = img.Width / (double)img.Height, aspect = w / (double)h;
        int cropW = srcAspect > aspect ? (int)(img.Height * aspect) : img.Width;
        int cropH = srcAspect > aspect ? img.Height : (int)(img.Width / aspect);
        int cx = (img.Width - cropW) / 2, cy = (img.Height - cropH) / 2;

        using var context = Context.Create(b => b.CPU());
        using var accelerator = context.CreateCPUAccelerator(0);
        using var session = InferenceSession.CreateFromFile(accelerator, File.ReadAllBytes(modelPath));
        using var faces = new FaceDetectionPipeline(session, accelerator);
        using var fx = new VideoEnhancer(accelerator);

        foreach (double brightness in new[] { 1.0, 0.35, 0.2 })
        {
            var frame = new int[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int sx = cx + x * cropW / w, sy = cy + y * cropH / h;
                    int k = (sy * img.Width + sx) * 4;
                    int r = (int)(img.Data[k] * brightness), g = (int)(img.Data[k + 1] * brightness), b = (int)(img.Data[k + 2] * brightness);
                    frame[y * w + x] = unchecked((int)0xFF000000) | (b << 16) | (g << 8) | r;
                }
            }
            foreach (bool cleanUp in new[] { false, true })
            {
                var s = cleanUp
                    ? new VideoEnhanceSettings()
                    : new VideoEnhanceSettings { Denoise = 0, Sharpen = 0, AutoLevels = false, WhiteBalance = false, FixTopRow = false };
                fx.ResetHistory();
                fx.Prepare(w, h).CopyFromCPU(frame);
                fx.Process(s);
                var result = await faces.DetectAsync(fx.FrameView, w, h, confidenceThreshold: 0.6f, maxFaces: 1);
                string found = result.FaceCount == 0
                    ? "no face"
                    : $"face at ({(result.Faces[0].X + result.Faces[0].Width / 2) / w:F2}, {(result.Faces[0].Y + result.Faces[0].Height / 2) / h:F2}), " +
                      $"{result.Faces[0].Width / w * 100:F0}% wide, confidence {result.Faces[0].Confidence:F2}";
                Console.WriteLine($"  brightness x{brightness:F2}, clean-up {(cleanUp ? "on " : "off")}: {found}");
            }
        }
        return 0;
    }
}
