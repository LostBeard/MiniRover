using MiniRover.Client;
using Xunit;

namespace MiniRover.Client.Tests;

public class CarSettingsTests
{
    [Fact]
    public void Parses_the_cars_lines_including_info_keys()
    {
        var s = CarSettings.Parse("drive.limit=0.600\ncamera.flip=1\ninfo.firmware=0.1.0\ninfo.faults=\n\nbad line\n");
        Assert.Equal(0.6, s.GetDouble("drive.limit", 1), 6);
        Assert.True(s.GetBool("camera.flip"));
        Assert.Equal("0.1.0", s.Get("info.firmware"));
        Assert.Equal("", s.Get("info.faults"));
        Assert.Equal(7, s.GetDouble("missing", 7));
        Assert.False(s.Values.ContainsKey("bad line"));
    }

    [Fact]
    public void Formats_numbers_with_a_dot_whatever_the_culture()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Equal("pan.trim=-2.5", CarSettings.Format("pan.trim", -2.5));
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void Battery_calibration_scales_the_coefficient()
    {
        // The car reads 7.60 V, a multimeter says 7.98 V: the coefficient grows by the same ratio.
        Assert.Equal(3.7 * 7.98 / 7.60, CarSettings.CalibratedBatteryCoefficient(3.7, 7.60, 7.98), 9);
        Assert.Throws<ArgumentException>(() => CarSettings.CalibratedBatteryCoefficient(3.7, 0, 7.98));
    }
}
