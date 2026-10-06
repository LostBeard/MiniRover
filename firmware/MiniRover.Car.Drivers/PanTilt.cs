using System;

namespace MiniRover.Car.Drivers
{
    /// <summary>
    /// Camera pan/tilt servos on PCA9685 channels 0 (pan) and 1 (tilt). Angles are in degrees with 90 =
    /// centre. Pulse mapping 500..2500 us over 0..180 degrees matches Freenove's 06.3 firmware
    /// (counts 102..512 at 50 Hz). Per-servo trim corrects the spline-tooth offset after mounting; limits
    /// keep the head off the chassis (Freenove limits tilt to 80..180).
    /// </summary>
    public sealed class PanTilt
    {
        public const double Center = 90;
        const double MinPulseUs = 500;
        const double MaxPulseUs = 2500;

        readonly Pca9685 _pwm;

        public double PanTrim { get; set; }
        public double TiltTrim { get; set; }
        public double PanMin { get; set; } = 0;
        public double PanMax { get; set; } = 180;
        public double TiltMin { get; set; } = 80;
        public double TiltMax { get; set; } = 180;

        public double Pan { get; private set; } = Center;
        public double Tilt { get; private set; } = Center;

        public PanTilt(Pca9685 pwm)
        {
            _pwm = pwm;
        }

        /// <summary>Both servos to 90 degrees plus trim: the head points straight ahead and level.
        /// Run before mounting the camera head (Docs/servo-calibration.md).</summary>
        public void CenterBoth()
        {
            SetPan(Center);
            SetTilt(Center);
        }

        public void SetPan(double degrees)
        {
            Pan = Clamp(degrees, PanMin, PanMax);
            _pwm.SetPulseMicroseconds(BoardPins.ServoPanChannel, ToPulse(Pan + PanTrim));
        }

        public void SetTilt(double degrees)
        {
            Tilt = Clamp(degrees, TiltMin, TiltMax);
            _pwm.SetPulseMicroseconds(BoardPins.ServoTiltChannel, ToPulse(Tilt + TiltTrim));
        }

        /// <summary>Stops sending pulses so the servos go limp (no holding current, no buzz).</summary>
        public void Relax()
        {
            _pwm.SetDuty(BoardPins.ServoPanChannel, 0);
            _pwm.SetDuty(BoardPins.ServoTiltChannel, 0);
        }

        static double ToPulse(double degrees)
        {
            degrees = Clamp(degrees, 0, 180);
            return MinPulseUs + (MaxPulseUs - MinPulseUs) * degrees / 180.0;
        }

        static double Clamp(double v, double lo, double hi) => v < lo ? lo : (v > hi ? hi : v);
    }
}
