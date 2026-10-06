namespace MiniRover.Client;

/// <summary>
/// Estimates how long the car's batteries will last from the trend of its charge over recent minutes.
/// Readings taken while the motors run are skipped: the pack voltage sags under load and recovers when the car stops,
/// which would make the estimate swing. No estimate until the trend is long and deep enough to trust.
/// </summary>
public sealed class BatteryEstimator
{
    readonly List<(double Minutes, double Percent)> _samples = new();

    /// <summary>How far back the trend looks.</summary>
    public double WindowMinutes { get; set; } = 10;
    /// <summary>Minimum history before estimating.</summary>
    public double MinimumMinutes { get; set; } = 3;
    /// <summary>Minimum drop in charge over the window before estimating (a flat line says nothing).</summary>
    public double MinimumDropPercent { get; set; } = 2;

    /// <param name="minutes">A monotonic time in minutes (e.g. a stopwatch).</param>
    /// <param name="percent">Charge 0..100 (BatteryGauge).</param>
    /// <param name="moving">The car reported moving: the reading is skipped.</param>
    public void Add(double minutes, double percent, bool moving)
    {
        if (moving) return;
        _samples.Add((minutes, percent));
        _samples.RemoveAll(s => s.Minutes < minutes - WindowMinutes);
    }

    /// <summary>Minutes of charge left at the current rate, or null when there is not enough evidence yet.</summary>
    public double? MinutesLeft
    {
        get
        {
            if (_samples.Count < 5) return null;
            double span = _samples[^1].Minutes - _samples[0].Minutes;
            if (span < MinimumMinutes) return null;
            // Least-squares slope (percent per minute) and the fitted value at the last sample.
            double n = _samples.Count, mx = _samples.Average(s => s.Minutes), my = _samples.Average(s => s.Percent);
            double sxy = _samples.Sum(s => (s.Minutes - mx) * (s.Percent - my)), sxx = _samples.Sum(s => (s.Minutes - mx) * (s.Minutes - mx));
            if (sxx <= 0) return null;
            double slope = sxy / sxx;
            if (-slope * span < MinimumDropPercent) return null; // not discharging measurably
            double now = my + slope * (_samples[^1].Minutes - mx);
            return Math.Max(0, now / -slope);
        }
    }
}
