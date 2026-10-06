using System;
using System.Device.Gpio;
using System.Threading;
using nanoFramework.Hardware.Esp32.Rmt;

namespace MiniRover.Car.Drivers
{
    /// <summary>
    /// HC-SR04 ultrasonic sensor (Trig GPIO12, Echo GPIO15). Fitted only on the ultrasonic head, so
    /// <see cref="TryReadCentimeters"/> returning false repeatedly is the normal answer on a camera-head car.
    /// The echo pulse is timed by the RMT receiver at 1 us resolution; managed code only raises the trigger.
    /// </summary>
    public sealed class Sonar : IDisposable
    {
        const int ResolutionHz = 1_000_000;          // 1 tick = 1 us
        const double MicrosecondsPerCm = 58.3;       // round trip at 343 m/s
        const int MaxEchoUs = 25_000;                // ~4.3 m, past the sensor's 4 m rating

        readonly GpioController _gpio;
        readonly GpioPin _trigger;
        readonly ReceiverChannel _echo;
        DateTime _last = DateTime.MinValue;

        public Sonar(GpioController gpio)
        {
            _gpio = gpio;
            _trigger = gpio.OpenPin(BoardPins.SonarTrigger, PinMode.Output);
            _trigger.Write(PinValue.Low);

            var settings = new ReceiverChannelSettings(BoardPins.SonarEcho)
            {
                ResolutionHz = ResolutionHz,
                // The echo line idles low; a frame ends once it has been idle longer than any valid echo.
                // RMT thresholds are in NANOSECONDS (not ticks): 30 ms.
                IdleThreshold = 30_000_000,
                // The echo is a plain pulse, not a carrier: no demodulation (the base ESP32 RMT has none anyway).
                EnableDemodulation = false,
                ReceiveTimeout = TimeSpan.FromMilliseconds(40),
            };
            _echo = new ReceiverChannel(settings);
        }

        /// <summary>One measurement. False when nothing echoed back in range (or no sensor is fitted).</summary>
        public bool TryReadCentimeters(out double centimeters)
        {
            centimeters = 0;

            // The datasheet asks for >= 60 ms between measurements so stray echoes die out.
            TimeSpan since = DateTime.UtcNow - _last;
            if (since.TotalMilliseconds < 60)
            {
                Thread.Sleep(60 - (int)since.TotalMilliseconds);
            }
            _last = DateTime.UtcNow;

            _echo.Start();
            // Two managed GPIO writes take well over the 10 us minimum trigger pulse.
            _trigger.Write(PinValue.High);
            _trigger.Write(PinValue.Low);
            RmtSymbols symbols;
            try
            {
                symbols = _echo.Receive();
            }
            catch (TimeoutException)
            {
                // Receive() throws when no echo arrives in the window: no obstacle in range, or no sensor fitted.
                symbols = null;
            }
            finally
            {
                _echo.Stop();
            }

            if (symbols == null || symbols.Count == 0)
            {
                return false;
            }

            RmtSymbol first = symbols[0];
            int highUs = first.Level0 ? first.Duration0 : (first.Level1 ? first.Duration1 : 0);
            if (highUs <= 0 || highUs > MaxEchoUs)
            {
                return false;
            }
            centimeters = highUs / MicrosecondsPerCm;
            return true;
        }

        public void Dispose()
        {
            _echo.Dispose();
            _trigger.Dispose();
        }
    }
}
