using System;

namespace MiniRover.Car.Drivers
{
    /// <summary>
    /// The four DC motors, driven through PCA9685 channel pairs (IN1 high = forward, IN2 high = backward).
    /// Speeds are -1..1. The gearmotors stall below about 40% duty, so any non-zero command is mapped onto
    /// [<see cref="MinimumDuty"/>, 4095] (Freenove's firmware clamps to 1600 for the same reason) - otherwise
    /// the bottom of the stick does nothing but hum.
    /// </summary>
    public sealed class Motors
    {
        public const int FrontLeft = 0;  // M1
        public const int RearLeft = 1;   // M2
        public const int FrontRight = 2; // M3
        public const int RearRight = 3;  // M4
        public const int Count = 4;

        static readonly int[] s_in1 = { BoardPins.M1In1, BoardPins.M2In1, BoardPins.M3In1, BoardPins.M4In1 };
        static readonly int[] s_in2 = { BoardPins.M1In2, BoardPins.M2In2, BoardPins.M3In2, BoardPins.M4In2 };

        readonly Pca9685 _pwm;
        readonly bool[] _invert = new bool[Count];
        readonly double[] _gain = { 1, 1, 1, 1 };
        readonly double[] _current = new double[Count];

        /// <summary>Duty count that just overcomes stall; non-zero speeds start here.</summary>
        public int MinimumDuty { get; set; } = 1600;

        public Motors(Pca9685 pwm)
        {
            _pwm = pwm;
            StopAll();
        }

        /// <summary>True if any motor is currently commanded to move. Battery sampling uses it to tag
        /// readings taken under load (voltage sag).</summary>
        public bool AnyRunning
        {
            get
            {
                for (int i = 0; i < Count; i++)
                {
                    if (_current[i] != 0) return true;
                }
                return false;
            }
        }

        /// <summary>Reverses one motor, for a motor wired the other way round.</summary>
        public void SetInverted(int motor, bool inverted) => _invert[motor] = inverted;

        /// <summary>Scales one motor (0..1) to straighten a car that drifts.</summary>
        public void SetGain(int motor, double gain) => _gain[motor] = Clamp(gain, 0, 1);

        public double GetSpeed(int motor) => _current[motor];

        public void Set(int motor, double speed)
        {
            speed = Clamp(speed, -1, 1);
            _current[motor] = speed;

            double s = speed * _gain[motor];
            if (_invert[motor]) s = -s;

            int duty = 0;
            if (s != 0)
            {
                duty = MinimumDuty + (int)Math.Round(Math.Abs(s) * (Pca9685.Resolution - 1 - MinimumDuty));
            }

            // Drive the "off" input low first so both inputs are never high at once (brake/short through
            // the driver) during the two separate channel writes.
            if (s > 0)
            {
                _pwm.SetDuty(s_in2[motor], 0);
                _pwm.SetDuty(s_in1[motor], duty);
            }
            else if (s < 0)
            {
                _pwm.SetDuty(s_in1[motor], 0);
                _pwm.SetDuty(s_in2[motor], duty);
            }
            else
            {
                _pwm.SetDuty(s_in1[motor], 0);
                _pwm.SetDuty(s_in2[motor], 0);
            }
        }

        /// <summary>Skid steering: left side and right side speeds.</summary>
        public void SetSides(double left, double right)
        {
            Set(FrontLeft, left);
            Set(RearLeft, left);
            Set(FrontRight, right);
            Set(RearRight, right);
        }

        public void StopAll()
        {
            for (int i = 0; i < Count; i++)
            {
                Set(i, 0);
            }
        }

        static double Clamp(double v, double lo, double hi) => v < lo ? lo : (v > hi ? hi : v);
    }
}
