namespace MiniRover.Client;

/// <summary>
/// Turns a driver's intent (throttle and steering, each -1..1, from touch, gamepad or keyboard) into wheel speeds for
/// the car's skid steering. Pure math, no I/O, so it is unit-tested.
/// </summary>
public sealed class DriveMixer
{
    /// <summary>Stick travel ignored around the center (worn gamepad sticks never rest at exactly 0).</summary>
    public double Deadzone { get; set; } = 0.08;
    /// <summary>0 = linear, 1 = cubic: fine control near the center, full speed at the edge.</summary>
    public double Expo { get; set; } = 0.35;
    /// <summary>Top speed, 0..1 (kid mode lowers it). The car applies its own limit on top of this.</summary>
    public double SpeedLimit { get; set; } = 1.0;
    /// <summary>How much steering counts against throttle; below 1 makes turns gentler.</summary>
    public double SteerGain { get; set; } = 0.8;

    /// <summary>Applies the deadzone (rescaled so the output still reaches 1) and the expo curve.</summary>
    public double Shape(double x)
    {
        x = Math.Clamp(x, -1, 1);
        double a = Math.Abs(x);
        if (a <= Deadzone) return 0;
        a = (a - Deadzone) / (1 - Deadzone);
        a = (1 - Expo) * a + Expo * a * a * a;
        return Math.CopySign(a, x);
    }

    /// <summary>Wheel speeds in percent (-100..100). Steering right (positive) speeds up the left wheels.</summary>
    public (int Left, int Right) Mix(double throttle, double steer)
    {
        double t = Shape(throttle), s = Shape(steer) * SteerGain;
        double l = t + s, r = t - s;
        // Keep the ratio between the sides when one would exceed full speed, so a fast turn stays the same turn.
        double max = Math.Max(Math.Abs(l), Math.Abs(r));
        if (max > 1) { l /= max; r /= max; }
        double limit = Math.Clamp(SpeedLimit, 0, 1);
        return ((int)Math.Round(l * limit * 100), (int)Math.Round(r * limit * 100));
    }
}
