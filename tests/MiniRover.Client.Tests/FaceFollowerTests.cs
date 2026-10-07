using MiniRover.Client;
using Xunit;

namespace MiniRover.Client.Tests;

public class FaceFollowerTests
{
    static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    [Fact]
    public void A_centred_face_moves_nothing()
    {
        var f = new FaceFollower();
        Assert.Equal((90.0, 90.0), f.Update(90, 90, 0.5, 0.5, Ms(0)));
        Assert.Equal((90.0, 90.0), f.Update(90, 90, 0.52, 0.47, Ms(1000))); // inside the dead zone
    }

    [Fact]
    public void Face_right_turns_right_and_face_low_looks_down()
    {
        // Pan above 90 looks left, tilt above 90 looks up (verified on the car by measuring the image shift).
        var f = new FaceFollower();
        var (pan, tilt) = f.Update(90, 100, 0.75, 0.8, Ms(0));
        Assert.True(pan < 90, $"pan {pan}");
        Assert.True(tilt < 100, $"tilt {tilt}");
        var g = new FaceFollower();
        (pan, tilt) = g.Update(90, 100, 0.25, 0.2, Ms(0));
        Assert.True(pan > 90 && tilt > 100, $"{pan} {tilt}");
    }

    [Fact]
    public void Steps_are_limited_and_angles_stay_in_the_servo_ranges()
    {
        var f = new FaceFollower { MaxStep = 5 };
        var (pan, tilt) = f.Update(90, 90, 1.0, 1.0, Ms(0));
        Assert.Equal(85, pan, 3);
        Assert.Equal(85, tilt, 3);
        var g = new FaceFollower();
        (pan, tilt) = g.Update(2, 81, 1.0, 1.0, Ms(0));
        Assert.Equal(FaceFollower.PanMin, pan);
        Assert.Equal(FaceFollower.TiltMin, tilt);
    }

    [Fact]
    public void Detections_right_after_a_move_are_ignored_until_the_camera_settles()
    {
        var f = new FaceFollower();
        var first = f.Update(90, 90, 0.8, 0.5, Ms(0));
        Assert.NotEqual(90.0, first.Pan);
        Assert.Equal(first, f.Update(first.Pan, first.Tilt, 0.8, 0.5, Ms(100)));   // frame may predate the move
        Assert.NotEqual(first, f.Update(first.Pan, first.Tilt, 0.8, 0.5, Ms(400)));
        f.Reset();
        var g = f.Update(80, 90, 0.8, 0.5, Ms(410));
        Assert.NotEqual(80.0, g.Pan);
    }

    [Fact]
    public void A_still_face_is_centred_within_a_few_steps_without_overshoot()
    {
        // Simulated camera with the measured field of view: the face sits at a fixed direction, and its position in
        // the frame follows from where the camera points.
        double faceDirX = 15, faceDirY = -8;   // degrees right of / below straight ahead (this face is above)
        double pan = 90, tilt = 90;
        var f = new FaceFollower();
        double prevErr = double.MaxValue;
        for (int step = 0; step < 12; step++)
        {
            double lookRight = 90 - pan, lookDown = 90 - tilt;
            double fx = 0.5 + (faceDirX - lookRight) / FaceFollower.FieldOfViewX;
            double fy = 0.5 + (faceDirY - lookDown) / FaceFollower.FieldOfViewY;
            (pan, tilt) = f.Update(pan, tilt, fx, fy, Ms(step * 400));
            double err = Math.Abs(fx - 0.5) + Math.Abs(fy - 0.5);
            Assert.True(err <= prevErr + 1e-9, $"error grew at step {step}: {err:F3} after {prevErr:F3}");
            prevErr = err;
        }
        Assert.InRange(90 - pan, faceDirX - 3, faceDirX + 3);
        Assert.InRange(90 - tilt, faceDirY - 3, faceDirY + 3);
        Assert.True(prevErr < 0.1, $"still {prevErr:F3} off centre");
    }
}
