using System;
using System.Device.Adc;

namespace MiniRover.Car.Drivers
{
    /// <summary>
    /// The two photoresistors (R13, R14) form ONE voltage divider into GPIO33, so a single reading tells which
    /// side is brighter: equal light reads about mid-scale (Freenove: ~2048 of 4095), and the value moves up or
    /// down as the light shifts to one side.
    /// </summary>
    public sealed class LightSensor : IDisposable
    {
        readonly AdcChannel _channel;

        public LightSensor(AdcController adc)
        {
            _channel = adc.OpenChannel(BoardPins.LightAdcChannel);
        }

        /// <summary>Average raw 12-bit value of <paramref name="samples"/> readings.</summary>
        public int ReadRaw(int samples = 4)
        {
            int sum = 0;
            for (int i = 0; i < samples; i++)
            {
                sum += _channel.ReadValue();
            }
            return sum / samples;
        }

        public void Dispose() => _channel.Dispose();
    }
}
