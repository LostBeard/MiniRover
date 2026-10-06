using System;
using System.Device.I2c;

namespace MiniRover.Car.Drivers
{
    /// <summary>
    /// The three reflective line-tracking sensors, read through a PCF8574 at 0x20.
    /// Bit 0 = left, bit 1 = middle, bit 2 = right; a 1 means black (or nothing) under that sensor.
    /// </summary>
    public sealed class LineSensors : IDisposable
    {
        public const int Left = 0x01;
        public const int Middle = 0x02;
        public const int Right = 0x04;

        readonly I2cDevice _device;
        readonly byte[] _buf = new byte[1];

        public LineSensors()
        {
            _device = SharedI2c.Open(BoardPins.Pcf8574Address);
            // PCF8574 quasi-bidirectional pins read correctly only when their output latch is high.
            SharedI2c.Write(_device, new byte[] { 0xFF });
        }

        /// <summary>Returns the 3 sensor bits (0..7).</summary>
        public int Read()
        {
            lock (_buf)
            {
                SharedI2c.Read(_device, _buf);
                return _buf[0] & 0x07;
            }
        }

        public void Dispose() => _device.Dispose();
    }
}
