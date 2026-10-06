namespace MiniRover.Client;

/// <summary>
/// The car's WiFi signal (dBm from telemetry) as bars and a warning a young driver understands. The car is the weak
/// end of the link (a small PCB antenna), so its own reading is what tells you it is about to drop out.
/// </summary>
public static class WifiSignal
{
    /// <summary>0..4 bars; -1 when the car has not reported a signal.</summary>
    public static int Bars(int rssiDbm) => rssiDbm switch
    {
        0 => -1,          // unknown (not connected or older firmware)
        >= -60 => 4,
        >= -67 => 3,
        >= -75 => 2,
        >= -82 => 1,
        _ => 0,
    };

    /// <summary>A warning to show over the drive view, or null while the signal is fine.</summary>
    public static string? Warning(int rssiDbm) => Bars(rssiDbm) switch
    {
        1 => "WiFi getting weak: the car is near the edge of range",
        0 => "WiFi very weak: drive back toward the router",
        _ => null,
    };
}
