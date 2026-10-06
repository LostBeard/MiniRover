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

        /// <summary>What <see cref="RecoverBus"/> found at boot ("" = bus idle), for diagnostics.</summary>
        public static string RecoveryNote = "";

        /// <summary>
        /// I2C bus recovery (the standard 9-clock method). A slave interrupted mid-transfer (the ESP32 restarted, or a
        /// power dip) can keep holding SDA low; resetting the ESP32 does not reset it, and every later transfer on the
        /// bus fails. Clocking SCL until the slave lets SDA go, then sending a STOP, frees it. Runs on the raw pins, so
        /// only before they are handed to the I2C driver.
        /// </summary>
        static void RecoverBus()
        {
            using (var gpio = new System.Device.Gpio.GpioController())
            {
                var sda = gpio.OpenPin(BoardPins.I2cSda, System.Device.Gpio.PinMode.InputPullUp);
                if (sda.Read() == System.Device.Gpio.PinValue.High)
                {
                    gpio.ClosePin(BoardPins.I2cSda);
                    return; // idle bus: nothing to do
                }
                var scl = gpio.OpenPin(BoardPins.I2cScl, System.Device.Gpio.PinMode.Output);
                int clocks = 0;
                for (; clocks < 9 && sda.Read() == System.Device.Gpio.PinValue.Low; clocks++)
                {
                    scl.Write(System.Device.Gpio.PinValue.Low);
                    System.Threading.Thread.Sleep(1);
                    scl.Write(System.Device.Gpio.PinValue.High);
                    System.Threading.Thread.Sleep(1);
                }
                bool freed = sda.Read() == System.Device.Gpio.PinValue.High;
                // STOP: SDA low -> high while SCL is high.
                gpio.ClosePin(BoardPins.I2cSda);
                sda = gpio.OpenPin(BoardPins.I2cSda, System.Device.Gpio.PinMode.Output);
                sda.Write(System.Device.Gpio.PinValue.Low);
                System.Threading.Thread.Sleep(1);
                sda.Write(System.Device.Gpio.PinValue.High);
                gpio.ClosePin(BoardPins.I2cSda);
                gpio.ClosePin(BoardPins.I2cScl);
                RecoveryNote = freed ? "I2C bus was held by a device; freed after " + clocks + " clocks"
                                     : "I2C data line stays low after 9 clocks (board unpowered or a short?)";
            }
        }

        public static I2cDevice Open(int address)
        {
            if (!s_pinsConfigured)
            {
                try { RecoverBus(); } catch (Exception ex) { RecoveryNote = "I2C recovery skipped: " + ex.Message; }
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
