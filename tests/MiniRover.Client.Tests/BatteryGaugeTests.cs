using MiniRover.Client;
using Xunit;

namespace MiniRover.Client.Tests;

public class BatteryGaugeTests
{
    [Theory]
    [InlineData(8.40, 100)]
    [InlineData(9.00, 100)]  // a mis-calibrated divider never reads above full
    [InlineData(8.00, 80)]   // 4.00 V per cell
    [InlineData(6.40, 0)]    // the car's critical cutoff
    [InlineData(0.00, 0)]    // pack switched off (USB power only)
    public void Known_points(double volts, int percent) => Assert.Equal(percent, BatteryGauge.Percent(volts));

    [Fact]
    public void Monotonic_over_the_whole_range()
    {
        int last = -1;
        for (double v = 6.0; v <= 8.6; v += 0.01)
        {
            int p = BatteryGauge.Percent(v);
            Assert.True(p >= last, $"{v:F2} V read {p}% after {last}%");
            last = p;
        }
    }

    [Fact]
    public void Mid_pack_reads_higher_than_a_straight_line()
    {
        // 7.6 V: a 7.0-8.4 V line says 43%; the plateau puts a real pack nearer half.
        Assert.True(BatteryGauge.Percent(7.6) > 43);
    }
}
