namespace MiniRover.Client;

/// <summary>
/// State of charge for the car's 2S Li-ion pack from its voltage (Docs/battery.md). Li-ion discharge is not a straight
/// line: a quick drop off the charger, a long plateau, then a cliff, so a linear 7.0-8.4 V map reads low mid-pack and
/// high near empty. This is a typical 18650 open-circuit curve per cell, interpolated.
/// </summary>
public static class BatteryGauge
{
    // (volts per cell, percent), ascending. 3.2 V is the car's critical cutoff, so it reads 0 here.
    static readonly (double V, double Pct)[] Curve =
    [
        (3.20, 0), (3.40, 5), (3.55, 12), (3.65, 22), (3.72, 35), (3.78, 48),
        (3.85, 60), (3.92, 70), (4.00, 80), (4.08, 90), (4.15, 97), (4.20, 100),
    ];

    /// <summary>Percent 0..100 for a pack voltage (two cells in series).</summary>
    public static int Percent(double packVolts)
    {
        double v = packVolts / 2;
        if (v <= Curve[0].V) return 0;
        if (v >= Curve[^1].V) return 100;
        for (int i = 1; i < Curve.Length; i++)
        {
            if (v <= Curve[i].V)
            {
                var (v0, p0) = Curve[i - 1];
                var (v1, p1) = Curve[i];
                return (int)Math.Round(p0 + (p1 - p0) * (v - v0) / (v1 - v0));
            }
        }
        return 100;
    }
}
