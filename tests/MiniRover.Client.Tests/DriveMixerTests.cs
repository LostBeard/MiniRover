using MiniRover.Client;
using Xunit;

namespace MiniRover.Client.Tests;

public class DriveMixerTests
{
    [Fact]
    public void Centered_sticks_inside_the_deadzone_do_not_move_the_car()
    {
        var m = new DriveMixer();
        Assert.Equal((0, 0), m.Mix(0, 0));
        Assert.Equal((0, 0), m.Mix(0.05, -0.07)); // drift of a worn stick
    }

    [Fact]
    public void Full_forward_is_full_speed_on_both_sides_and_reverse_mirrors_it()
    {
        var m = new DriveMixer();
        Assert.Equal((100, 100), m.Mix(1, 0));
        Assert.Equal((-100, -100), m.Mix(-1, 0));
    }

    [Fact]
    public void Steering_right_speeds_up_the_left_wheels()
    {
        var m = new DriveMixer { SteerGain = 1 };
        var (l, r) = m.Mix(0, 1);
        Assert.Equal((100, -100), (l, r)); // spin in place
        (l, r) = m.Mix(0.5, 0.3);
        Assert.True(l > r && r > 0, $"gentle right turn: {l} {r}");
    }

    [Fact]
    public void A_fast_turn_keeps_its_ratio_instead_of_clipping_one_side()
    {
        var m = new DriveMixer { SteerGain = 1, Expo = 0, Deadzone = 0 };
        var (l, r) = m.Mix(1, 0.5); // 1.5 : 0.5 before normalizing
        Assert.Equal((100, 33), (l, r));
    }

    [Fact]
    public void Speed_limit_scales_everything_and_is_clamped()
    {
        var m = new DriveMixer { SpeedLimit = 0.4 };
        Assert.Equal((40, 40), m.Mix(1, 0));
        m.SpeedLimit = 7;
        Assert.Equal((100, 100), m.Mix(1, 0));
        m.SpeedLimit = -1;
        Assert.Equal((0, 0), m.Mix(1, 0));
    }

    [Fact]
    public void Shape_is_continuous_at_the_deadzone_edge_and_reaches_one()
    {
        var m = new DriveMixer();
        Assert.Equal(0, m.Shape(m.Deadzone));
        Assert.True(m.Shape(m.Deadzone + 0.01) is > 0 and < 0.02);
        Assert.Equal(1, m.Shape(1), 6);
        Assert.Equal(-1, m.Shape(-3), 6); // out-of-range input is clamped
        Assert.True(m.Shape(0.5) < 0.5, "expo gives finer control near the center");
    }
}
