using System;
using System.Device.I2c;
using nanoFramework.Hardware.Esp32;

namespace MiniRover.Car.Drivers
{
    /// <summary>
    /// The one I2C bus the PCA9685, PCF8574 and LED matrix share. Every transaction goes through
    /// <see cref="Lock"/>: two threads interleaving bytes on one bus corrupts both transfers.
    /// </summary>
    public static class SharedI2c
    {
        public static readonly object Lock = new object();
        static bool s_pinsConfigured;

        public static I2cDevice Open(int address)
        {
            if (!s_pinsConfigured)
            {
                Configuration.SetPinFunction(BoardPins.I2cSda, DeviceFunction.I2C1_DATA);
                Configuration.SetPinFunction(BoardPins.I2cScl, DeviceFunction.I2C1_CLOCK);
                s_pinsConfigured = true;
            }
            return I2cDevice.Create(new I2cConnectionSettings(BoardPins.I2cBus, address, I2cBusSpeed.FastMode));
        }

        /// <summary>Writes and throws if the device did not acknowledge every byte, so a missing or
        /// unplugged board shows up as an error at the call that touched it.</summary>
        public static void Write(I2cDevice device, ReadOnlySpan<byte> data)
        {
            I2cTransferResult r;
            lock (Lock)
            {
                r = device.Write(data);
            }
            if (r.Status != I2cTransferStatus.FullTransfer)
            {
                throw new InvalidOperationException("I2C write to 0x" + device.ConnectionSettings.DeviceAddress.ToString("X2") + " failed: " + r.Status.ToString());
            }
        }

        public static void Read(I2cDevice device, Span<byte> data)
        {
            I2cTransferResult r;
            lock (Lock)
            {
                r = device.Read(data);
            }
            if (r.Status != I2cTransferStatus.FullTransfer)
            {
                throw new InvalidOperationException("I2C read from 0x" + device.ConnectionSettings.DeviceAddress.ToString("X2") + " failed: " + r.Status.ToString());
            }
        }
    }
}
