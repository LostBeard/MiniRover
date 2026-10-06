using System;
using System.Device.Adc;
using nanoFramework.Hardware.Esp32.Rmt;

namespace MiniRover.Car.Drivers
{
    /// <summary>
    /// GPIO32 on the FNK0053 is wired to BOTH the WS2812 data line and the battery voltage divider
    /// (Freenove: "Because the battery voltage is not read frequently, this GPIO is also used to control the
    /// WS2812"). This class is the pin's single owner: it holds the LED frame, and a battery read releases the
    /// RMT LED channel, samples the ADC, then re-sends the frame so the LEDs never visibly change.
    /// Nothing else may open GPIO32.
    /// </summary>
    public sealed class Gpio32 : IDisposable
    {
        public const int LedCount = BoardPins.RgbLedCount;

        readonly object _lock = new object();
        readonly byte[] _grb = new byte[LedCount * 3];
        readonly byte[] _scaled = new byte[LedCount * 3];
        readonly AdcController _adc;
        LedTransmitChannel _leds;
        int _brightness = 64;

        /// <param name="adc">The board's one ADC1 controller (shared with the light sensor).</param>
        public Gpio32(AdcController adc)
        {
            _adc = adc;
            _leds = new LedTransmitChannel(BoardPins.RgbLeds, LedType.WS2812);
            Show();
        }

        /// <summary>Global brightness 0..255 applied when the frame is sent.</summary>
        public int Brightness
        {
            get => _brightness;
            set { _brightness = value < 0 ? 0 : (value > 255 ? 255 : value); }
        }

        /// <summary>Sets one LED's colour in the pending frame (call <see cref="Show"/> to send).</summary>
        public void SetPixel(int index, byte r, byte g, byte b)
        {
            if (index < 0 || index >= LedCount) throw new ArgumentOutOfRangeException(nameof(index));
            lock (_lock)
            {
                // WS2812 wire order is G, R, B.
                _grb[index * 3] = g;
                _grb[index * 3 + 1] = r;
                _grb[index * 3 + 2] = b;
            }
        }

        public void Fill(byte r, byte g, byte b)
        {
            for (int i = 0; i < LedCount; i++) SetPixel(i, r, g, b);
        }

        public void Show()
        {
            lock (_lock)
            {
                SendLocked();
            }
        }

        /// <summary>
        /// Averages <paramref name="samples"/> raw 12-bit readings of the battery divider. The LED channel is
        /// released for the duration and the last frame re-sent afterwards.
        /// </summary>
        public int ReadBatteryRaw(int samples = 8)
        {
            lock (_lock)
            {
                _leds.Dispose();
                _leds = null;
                int sum = 0;
                try
                {
                    using (AdcChannel ch = _adc.OpenChannel(BoardPins.BatteryAdcChannel))
                    {
                        for (int i = 0; i < samples; i++)
                        {
                            sum += ch.ReadValue();
                        }
                    }
                }
                finally
                {
                    _leds = new LedTransmitChannel(BoardPins.RgbLeds, LedType.WS2812);
                    SendLocked();
                }
                return sum / samples;
            }
        }

        void SendLocked()
        {
            for (int i = 0; i < _grb.Length; i++)
            {
                _scaled[i] = (byte)(_grb[i] * _brightness / 255);
            }
            _leds.SendLedData(_scaled, 1, true);
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_leds != null)
                {
                    Array.Clear(_grb, 0, _grb.Length);
                    try { SendLocked(); } catch { /* shutting down */ }
                    _leds.Dispose();
                    _leds = null;
                }
            }
        }
    }
}
