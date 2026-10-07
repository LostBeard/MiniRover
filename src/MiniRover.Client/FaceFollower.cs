namespace MiniRover.Client;

/// <summary>
/// Keeps a face in the middle of the picture by moving the camera's pan/tilt servos (the wheels never move).
///
/// The angle needed comes from where the face is in the frame and the camera's field of view. Detection lags the
/// servos (the frame was taken before the last move reached it), so each correction moves only part of the way and
/// the next one waits for a frame taken after the move settled: a fast, small step-and-look loop, no overshoot.
/// </summary>
public sealed class FaceFollower
{
    /// <summary>
    /// Field of view of the FNK0053 camera head, degrees. Measured on the car (OV2640, 320x240): turning the pan servo
    /// 20 degrees moved the scene 106 of 320 pixels, tilting 20 degrees moved it 111 of 240 pixels.
    /// </summary>
    public const double FieldOfViewX = 60, FieldOfViewY = 44;

    public const double PanMin = 0, PanMax = 180, TiltMin = 80, TiltMax = 180;

    /// <summary>Fraction of the measured offset corrected per step.</summary>
    public double Gain { get; set; } = 0.5;

    /// <summary>A face this close to the centre (fraction of the frame, each axis) is left where it is.</summary>
    public double DeadZone { get; set; } = 0.04;

    /// <summary>Largest move per step, degrees.</summary>
    public double MaxStep { get; set; } = 8;

    /// <summary>After a move, detections are ignored for this long: their frames may predate the move.</summary>
    public TimeSpan Settle { get; set; } = TimeSpan.FromMilliseconds(300);

    TimeSpan _lastMove = TimeSpan.MinValue;

    /// <summary>
    /// One detection. <paramref name="faceX"/>/<paramref name="faceY"/> are the face centre as a fraction of the frame
    /// as shown (0..1, x to the right, y down); <paramref name="now"/> is any monotonic clock. Returns the new servo
    /// angles (pan above 90 looks left, tilt above 90 looks up), unchanged when there is nothing to do.
    /// </summary>
    public (double Pan, double Tilt) Update(double pan, double tilt, double faceX, double faceY, TimeSpan now)
    {
        if (_lastMove != TimeSpan.MinValue && now - _lastMove < Settle) return (pan, tilt);
        double ex = faceX - 0.5, ey = faceY - 0.5;
        if (Math.Abs(ex) < DeadZone) ex = 0;
        if (Math.Abs(ey) < DeadZone) ey = 0;
        if (ex == 0 && ey == 0) return (pan, tilt);

        // Face to the right -> turn right (pan down); face low -> look down (tilt down).
        double dPan = Math.Clamp(ex * FieldOfViewX * Gain, -MaxStep, MaxStep);
        double dTilt = Math.Clamp(ey * FieldOfViewY * Gain, -MaxStep, MaxStep);
        double newPan = Math.Clamp(pan - dPan, PanMin, PanMax);
        double newTilt = Math.Clamp(tilt - dTilt, TiltMin, TiltMax);
        if (newPan != pan || newTilt != tilt) _lastMove = now;
        return (newPan, newTilt);
    }

    /// <summary>Forget the last move (tracking switched on again).</summary>
    public void Reset() => _lastMove = TimeSpan.MinValue;
}
