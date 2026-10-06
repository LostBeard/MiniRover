using MiniRover.Client;
using Xunit;

namespace MiniRover.Client.Tests;

public class WifiSignalTests
{
    [Theory]
    [InlineData(0, -1)]     // not reported
    [InlineData(-35, 4)]
    [InlineData(-60, 4)]
    [InlineData(-61, 3)]
    [InlineData(-75, 2)]
    [InlineData(-82, 1)]
    [InlineData(-83, 0)]
    [InlineData(-127, 0)]
    public void Bars_follow_the_thresholds(int rssi, int bars) => Assert.Equal(bars, WifiSignal.Bars(rssi));

    [Fact]
    public void Warns_only_near_the_edge_of_range()
    {
        Assert.Null(WifiSignal.Warning(0));    // unknown is not "weak"
        Assert.Null(WifiSignal.Warning(-70));
        Assert.Contains("weak", WifiSignal.Warning(-80));
        Assert.Contains("back", WifiSignal.Warning(-90));
    }
}
