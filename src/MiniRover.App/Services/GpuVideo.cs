using ILGPU;
using ILGPU.Runtime;
using SpawnDev.ILGPU;
using SpawnDev.ILGPU.WebGPU;

namespace MiniRover.App.Services;

/// <summary>
/// The one WebGPU accelerator the app uses for video clean-up, created on first use and kept for the app's lifetime
/// (creating a device per drive page would leak GPU memory and take seconds each time). Null when the browser has no
/// usable WebGPU; <see cref="Status"/> says why.
/// </summary>
public static class GpuVideo
{
    static Task<Accelerator?>? _init;

    /// <summary>The device name, or why there is none.</summary>
    public static string Status { get; private set; } = "";

    public static Task<Accelerator?> GetAsync() => _init ??= InitAsync();

    static async Task<Accelerator?> InitAsync()
    {
        try
        {
            var context = await Context.CreateAsync(builder => builder.WebGPU());
            var devices = context.GetWebGPUDevices();
            if (devices.Count == 0)
            {
                Status = "this browser has no WebGPU";
                return null;
            }
            var accelerator = await devices[0].CreateAcceleratorAsync(context);
            Status = accelerator.Name;
            return accelerator;
        }
        catch (Exception ex)
        {
            Status = "WebGPU could not start: " + ex.Message;
            return null;
        }
    }
}
