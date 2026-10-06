using System;
using System.Device.I2c;
using System.Threading;

namespace MiniRover.Car.Drivers
{
    /// <summary>
    /// NXP PCA9685 16-channel 12-bit PWM controller (Freenove FNK0053: address 0x5F, drives both servos
    /// and the four motor driver inputs). One prescaler serves all 16 channels, so the car runs every
    /// channel at 50 Hz: servos need it, and the motors work at it (Freenove's main firmware does the same).
    /// </summary>
    public sealed class Pca9685 : IDisposable
    {
        const byte Mode1 = 0x00;
        const byte Mode2 = 0x01;
        const byte Led0OnL = 0x06;
        const byte AllLedOnL = 0xFA;
        const byte PreScale = 0xFE;

        const byte Mode1Restart = 0x80;
        const byte Mode1AutoIncrement = 0x20;
        const byte Mode1Sleep = 0x10;
        const byte Mode2OutDrv = 0x04; // totem-pole outputs

        const double OscillatorHz = 25_000_000;

        public const int Resolution = 4096;

        readonly I2cDevice _device;
        readonly byte[] _channelBuf = new byte[5];

        public double FrequencyHz { get; private set; }

        /// <summary>Length of one PWM period in microseconds at the current frequency.</summary>
        public double PeriodMicroseconds => 1_000_000.0 / FrequencyHz;

        public Pca9685(int address, double frequencyHz)
        {
            _device = SharedI2c.Open(address);
            SharedI2c.Write(_device, new byte[] { Mode2, Mode2OutDrv });
            SetFrequency(frequencyHz);
            AllOff();
        }

        public void SetFrequency(double frequencyHz)
        {
            // Datasheet 7.3.5: prescale = round(osc / (4096 * f)) - 1, valid 3..255.
            int prescale = (int)Math.Round(OscillatorHz / (Resolution * frequencyHz)) - 1;
            if (prescale < 3) prescale = 3;
            if (prescale > 255) prescale = 255;

            // The prescaler can only be written while the oscillator sleeps.
            SharedI2c.Write(_device, new byte[] { Mode1, Mode1Sleep | Mode1AutoIncrement });
            SharedI2c.Write(_device, new byte[] { PreScale, (byte)prescale });
            SharedI2c.Write(_device, new byte[] { Mode1, Mode1AutoIncrement });
            Thread.Sleep(1); // oscillator needs 500 us to stabilise before RESTART
            SharedI2c.Write(_device, new byte[] { Mode1, Mode1Restart | Mode1AutoIncrement });

            FrequencyHz = OscillatorHz / (Resolution * (prescale + 1));
        }

        /// <summary>Sets a channel's duty as a 0..4095 count (0 = always low, 4095+ = always high).</summary>
        public void SetDuty(int channel, int duty)
        {
            if (channel < 0 || channel > 15) throw new ArgumentOutOfRangeException(nameof(channel));

            int on, off;
            if (duty <= 0)
            {
                on = 0; off = 0x1000; // full-off bit
            }
            else if (duty >= Resolution - 1)
            {
                on = 0x1000; off = 0; // full-on bit
            }
            else
            {
                on = 0; off = duty;
            }

            // Reused buffer: guarded by the bus lock inside SharedI2c.Write, and channel writes come from
            // one owner (the drive/servo services), so concurrent callers never share it.
            lock (_channelBuf)
            {
                _channelBuf[0] = (byte)(Led0OnL + 4 * channel);
                _channelBuf[1] = (byte)(on & 0xFF);
                _channelBuf[2] = (byte)(on >> 8);
                _channelBuf[3] = (byte)(off & 0xFF);
                _channelBuf[4] = (byte)(off >> 8);
                SharedI2c.Write(_device, _channelBuf);
            }
        }

        /// <summary>Sets a channel's high time in microseconds (servo pulses).</summary>
        public void SetPulseMicroseconds(int channel, double microseconds)
        {
            SetDuty(channel, (int)Math.Round(microseconds * Resolution / PeriodMicroseconds));
        }

        public void AllOff()
        {
            SharedI2c.Write(_device, new byte[] { AllLedOnL, 0x00, 0x00, 0x00, 0x10 });
        }

        public void Dispose()
        {
            try { AllOff(); } catch { /* bus gone at shutdown: nothing left to stop */ }
            _device.Dispose();
        }
    }
}
