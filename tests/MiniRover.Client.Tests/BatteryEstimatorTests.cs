using MiniRover.Client;
using Xunit;

namespace MiniRover.Client.Tests;

public class BatteryEstimatorTests
{
    [Fact]
    public void Steady_discharge_gives_the_remaining_time()
    {
        var e = new BatteryEstimator();
        // 1% per minute from 60%: after 5 minutes at 55%, about 55 minutes left.
        for (int i = 0; i <= 50; i++) e.Add(i / 10.0, 60 - i / 10.0, moving: false);
        Assert.Equal(55, e.MinutesLeft!.Value, 1);
    }

    [Fact]
    public void Not_enough_history_or_no_drop_means_no_estimate()
    {
        var e = new BatteryEstimator();
        for (int i = 0; i <= 10; i++) e.Add(i / 10.0, 60 - i / 10.0, false); // only 1 minute
        Assert.Null(e.MinutesLeft);

        var flat = new BatteryEstimator();
        for (int i = 0; i <= 60; i++) flat.Add(i / 10.0, 60, false); // 6 minutes, no drop
        Assert.Null(flat.MinutesLeft);
    }

    [Fact]
    public void Readings_under_load_are_ignored()
    {
        var e = new BatteryEstimator();
        for (int i = 0; i <= 50; i++)
        {
            e.Add(i / 10.0, 60 - i / 10.0, false);
            e.Add(i / 10.0 + 0.05, 20, moving: true); // sagging readings while driving would wreck the trend
        }
        Assert.Equal(55, e.MinutesLeft!.Value, 1);
    }

    [Fact]
    public void Old_samples_leave_the_window()
    {
        var e = new BatteryEstimator { WindowMinutes = 5 };
        for (int i = 0; i <= 100; i++) e.Add(i / 10.0, i < 50 ? 90 - i : 40 - (i - 50) / 10.0, false);
        // A steep early drop (10%/min) is outside the 5-minute window; the last 5 minutes fall 1%/min to 35%.
        Assert.Equal(35, e.MinutesLeft!.Value, 1);
    }
}
