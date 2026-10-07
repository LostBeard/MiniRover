namespace MiniRover.Protocol
{
    /// <summary>
    /// Load compensation for the pack voltage. The motors pull the pack down hard while they run (measured on the
    /// car on battery power, wheels in the air: 7.80 V at rest, 7.29 V at 60 % drive; the camera stream and all 12
    /// LEDs moved it by 0.02 V or less) and it recovers within one 2 s sample when they stop. So the gauge learns how
    /// many volts each unit of motor load costs, from the car's own transitions between resting and driving, and adds
    /// that back while driving: the percentage keeps tracking the real charge on a long drive instead of sinking
    /// toward the sagged readings.
    ///
    /// Compiled into the firmware and the .NET client (unit tests): plain arithmetic, no generics.
    /// </summary>
    public sealed class BatterySag
    {
        /// <summary>A rested reading older than this does not teach the sag: the charge has moved on.</summary>
        public const long RestValidMs = 15000;
        /// <summary>Loads below this give too small a sag to measure against the ADC's noise (about 0.03 V).</summary>
        public const double MinimumLearnLoad = 0.15;
        /// <summary>Sag per unit load outside (0, this) is a bad sample (a pack swap, a stall spike), not the pack.</summary>
        public const double MaximumSagPerLoad = 3.0;
        const double LearnRate = 0.25;

        double _restVolts = -1;
        long _restMs;

        /// <summary>Learned volts of sag per unit of load (0 until learned).</summary>
        public double SagPerLoad { get; private set; }
        /// <summary>How many driving samples taught the sag so far.</summary>
        public int Learned { get; private set; }
        /// <summary>Enough samples to trust the compensation.</summary>
        public bool Ready => Learned >= 2;

        /// <summary>
        /// One sample. <paramref name="load"/> is 0 when the motors are off, else the mean motor duty (0..1). Returns
        /// the estimated resting voltage: the reading itself when rested, the reading plus the learned sag when driving.
        /// </summary>
        public double Update(double volts, double load, long nowMs)
        {
            if (load <= 0)
            {
                _restVolts = volts;
                _restMs = nowMs;
                return volts;
            }
            if (_restVolts > 0 && nowMs - _restMs <= RestValidMs && load >= MinimumLearnLoad)
            {
                double k = (_restVolts - volts) / load;
                if (k > 0 && k < MaximumSagPerLoad)
                {
                    SagPerLoad = Learned == 0 ? k : SagPerLoad + LearnRate * (k - SagPerLoad);
                    Learned++;
                }
            }
            return volts + SagPerLoad * load;
        }
    }
}
