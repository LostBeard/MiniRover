using System;
using System.Threading;
using nanoFramework.Hardware.Esp32.Rmt;

namespace MiniRover.Car.Drivers
{
    /// <summary>
    /// NEC-protocol IR receiver on GPIO0 (the kit's remote). The demodulating receiver's output idles high and
    /// pulls low for each 38 kHz burst, so a NEC frame arrives as: 9 ms low + 4.5 ms high (start), then 32 bits
    /// of 562 us low + (562 us high = 0 | 1687 us high = 1), LSB first. A held button sends "repeat" frames
    /// (9 ms low + 2.25 ms high). Decoding runs on its own thread; <see cref="CodeReceived"/> fires with the
    /// 32-bit code (Freenove's sketches print the same value, e.g. 0xFF10EF), or <see cref="RepeatCode"/>.
    /// </summary>
    public sealed class IrReceiver : IDisposable
    {
        public const uint RepeatCode = 0xFFFFFFFF;

        const int ResolutionHz = 1_000_000; // 1 tick = 1 us

        public delegate void CodeHandler(uint code);
        public event CodeHandler CodeReceived;

        readonly ReceiverChannel _rx;
        readonly Thread _thread;
        bool _running = true;

        public IrReceiver()
        {
            var settings = new ReceiverChannelSettings(BoardPins.IrReceiver)
            {
                ResolutionHz = ResolutionHz,
                // A gap longer than any NEC mark/space (the 9 ms leader is the longest) ends the frame.
                IdleThreshold = 12_000,
                ReceiveTimeout = TimeSpan.FromMilliseconds(500),
            };
            _rx = new ReceiverChannel(settings);
            _thread = new Thread(Loop);
            _thread.Start();
        }

        void Loop()
        {
            while (_running)
            {
                RmtSymbols symbols;
                try
                {
                    symbols = _rx.Receive();
                }
                catch (Exception ex)
                {
                    // Never let the decoder thread die silently: say why, back off, keep listening.
                    System.Diagnostics.Debug.WriteLine("IR receive failed: " + ex.Message);
                    Thread.Sleep(500);
                    continue;
                }
                if (symbols == null || symbols.Count == 0)
                {
                    continue;
                }
                uint code;
                if (TryDecode(symbols, out code))
                {
                    CodeReceived?.Invoke(code);
                }
            }
        }

        /// <summary>Decodes one NEC frame. Each RMT symbol is (low mark, high space) because the line idles high.</summary>
        public static bool TryDecode(RmtSymbols symbols, out uint code)
        {
            code = 0;
            RmtSymbol lead = symbols[0];
            int mark = lead.Level0 ? lead.Duration1 : lead.Duration0;
            int space = lead.Level0 ? lead.Duration0 : lead.Duration1;

            if (!Near(mark, 9000, 1500)) return false;
            if (Near(space, 2250, 500))
            {
                code = RepeatCode;
                return true;
            }
            if (!Near(space, 4500, 800) || symbols.Count < 33) return false;

            for (int bit = 0; bit < 32; bit++)
            {
                RmtSymbol s = symbols[1 + bit];
                int bitSpace = s.Level0 ? s.Duration0 : s.Duration1;
                if (Near(bitSpace, 1687, 400))
                {
                    code |= 1u << bit;
                }
                else if (!Near(bitSpace, 562, 300))
                {
                    return false;
                }
            }
            // Freenove's IRremote sketches print the code MSB-first as received on the wire.
            code = Reverse(code);
            return true;
        }

        static bool Near(int value, int target, int tolerance) => value >= target - tolerance && value <= target + tolerance;

        static uint Reverse(uint v)
        {
            uint r = 0;
            for (int i = 0; i < 32; i++)
            {
                r = (r << 1) | (v & 1);
                v >>= 1;
            }
            return r;
        }

        public void Dispose()
        {
            _running = false;
            _rx.Dispose();
        }
    }
}
