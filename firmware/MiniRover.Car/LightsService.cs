using System;
using System.Threading;
using MiniRover.Car.Drivers;

namespace MiniRover.Car
{
    /// <summary>
    /// Light patterns for the 12 RGB LEDs, animated on the car so they keep going if the app disconnects and cost no
    /// network traffic. A frame is sent only when it changes (GPIO32 is shared with the battery sense, see Gpio32).
    ///
    /// Patterns that need to know where an LED sits (headlights, brake lights, turn signals, police by side) use the
    /// "led.corners" setting, filled in by the app's calibration (it lights one group at a time and asks which
    /// corner it is on). Until then they fall back to position-free versions.
    /// </summary>
    public sealed class LightsService
    {
        // Same numbers as the wire protocol (MiniRover.Protocol.CarLink.Lights*).
        public const int ModeOff = Protocol.CarLink.LightsOff;
        public const int ModeSolid = Protocol.CarLink.LightsSolid;
        public const int ModeHeadlights = Protocol.CarLink.LightsHeadlights; // front white, rear dim red + brake lights, turn signals
        public const int ModeHazard = Protocol.CarLink.LightsHazard;
        public const int ModePolice = Protocol.CarLink.LightsPolice;
        public const int ModeRainbow = Protocol.CarLink.LightsRainbow;
        public const int ModeBattery = Protocol.CarLink.LightsBattery;       // a bar of lit LEDs, green to red, showing the charge
        public const int ModeIdentify = Protocol.CarLink.LightsIdentify;     // param 0..3: one group of three (calibration); 100+i: one LED

        // Corner codes in led.corners (one character per LED).
        const char FrontLeft = '0', FrontRight = '1', RearLeft = '2', RearRight = '3';

        readonly Car _car;
        readonly Gpio32 _leds;
        readonly byte[] _frame = new byte[Gpio32.LedCount * 3];
        readonly byte[] _sent = new byte[Gpio32.LedCount * 3];
        readonly object _lock = new object();
        readonly AutoResetEvent _changed = new AutoResetEvent(false);

        int _mode = ModeOff; // off by default: lit LEDs drain the batteries for nothing (the eyes show the car is on)
        byte _r = 40, _g = 40, _b = 40;
        int _param;
        bool _wasMoving;
        long _brakeUntilMs;

        public LightsService(Car car)
        {
            _car = car;
            _leds = car.Leds;
        }

        public int Mode => _mode;

        public void Start()
        {
            new Thread(Run).Start();
        }

        /// <summary>Selects a pattern. <paramref name="r"/>/<paramref name="g"/>/<paramref name="b"/> are the colour for
        /// <see cref="ModeSolid"/>; <paramref name="param"/> is used by <see cref="ModeIdentify"/>.</summary>
        public void Set(int mode, byte r, byte g, byte b, int param)
        {
            lock (_lock)
            {
                _mode = mode;
                _r = r;
                _g = g;
                _b = b;
                _param = param;
            }
            _changed.Set(); // a still pattern may be sleeping long: show the new one now
        }

        void Run()
        {
            while (true)
            {
                try
                {
                    Render(Environment.TickCount64);
                    if (!SameAsSent())
                    {
                        for (int i = 0; i < Gpio32.LedCount; i++)
                        {
                            _leds.SetPixel(i, _frame[i * 3], _frame[i * 3 + 1], _frame[i * 3 + 2]);
                        }
                        _leds.Show();
                        Array.Copy(_frame, _sent, _frame.Length);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("Lights: " + ex.Message);
                }
                // Off and solid never change by themselves: no need to render 20 times a second (the car's interpreter
                // is the busiest thing on it while video streams). Set() wakes this at once.
                int mode = _mode;
                _changed.WaitOne(mode == ModeOff || mode == ModeSolid ? 1000 : 50, false);
            }
        }

        bool SameAsSent()
        {
            for (int i = 0; i < _frame.Length; i++) if (_frame[i] != _sent[i]) return false;
            return true;
        }

        void Px(int i, int r, int g, int b)
        {
            _frame[i * 3] = (byte)r;
            _frame[i * 3 + 1] = (byte)g;
            _frame[i * 3 + 2] = (byte)b;
        }

        void Fill(int r, int g, int b)
        {
            for (int i = 0; i < Gpio32.LedCount; i++) Px(i, r, g, b);
        }

        void Render(long now)
        {
            int mode, param;
            byte r, g, b;
            lock (_lock)
            {
                mode = _mode;
                r = _r;
                g = _g;
                b = _b;
                param = _param;
            }
            string corners = _car.Settings.LedCorners;
            bool calibrated = corners.Length == Gpio32.LedCount && corners.IndexOf('-') < 0;
            bool blink = (now / 333) % 2 == 0; // 1.5 Hz, like a car's indicators

            switch (mode)
            {
                case ModeOff:
                    Fill(0, 0, 0);
                    break;
                case ModeSolid:
                    Fill(r, g, b);
                    break;
                case ModeHazard:
                    if (blink) Fill(255, 110, 0); else Fill(0, 0, 0);
                    break;
                case ModePolice:
                    {
                        bool phase = (now / 250) % 2 == 0;
                        for (int i = 0; i < Gpio32.LedCount; i++)
                        {
                            bool leftHalf = calibrated ? (corners[i] == FrontLeft || corners[i] == RearLeft) : i < Gpio32.LedCount / 2;
                            if (leftHalf == phase) Px(i, 255, 0, 0); else Px(i, 0, 0, 255);
                        }
                    }
                    break;
                case ModeRainbow:
                    for (int i = 0; i < Gpio32.LedCount; i++) Hue(i, (int)((i * 30 + now / 8) % 360));
                    break;
                case ModeBattery:
                    RenderBattery();
                    break;
                case ModeIdentify:
                    Fill(0, 0, 0);
                    if (param >= 100) { if (param - 100 < Gpio32.LedCount) Px(param - 100, 255, 255, 255); }
                    else for (int i = param * 3; i < param * 3 + 3 && i < Gpio32.LedCount; i++) Px(i, 255, 255, 255);
                    break;
                default: // ModeHeadlights
                    RenderHeadlights(now, corners, calibrated, blink);
                    break;
            }
        }

        void RenderHeadlights(long now, string corners, bool calibrated, bool blink)
        {
            if (!calibrated)
            {
                Fill(160, 160, 160); // positions unknown: plain white until the app calibrates the corners
                return;
            }
            DriveService drive = _car.Drive;
            double left = drive != null ? drive.LastLeft : 0, right = drive != null ? drive.LastRight : 0;
            bool moving = drive != null && drive.Moving;
            // Brake lights: bright for a second when the car stops.
            if (_wasMoving && !moving) _brakeUntilMs = now + 1000;
            _wasMoving = moving;
            bool braking = now < _brakeUntilMs;
            // Turn signals: while turning noticeably, on the inside of the turn.
            bool turnLeft = moving && right - left > 0.3, turnRight = moving && left - right > 0.3;

            for (int i = 0; i < Gpio32.LedCount; i++)
            {
                char c = corners[i];
                bool front = c == FrontLeft || c == FrontRight;
                bool leftSide = c == FrontLeft || c == RearLeft;
                if ((turnLeft && leftSide) || (turnRight && !leftSide))
                {
                    if (blink) Px(i, 255, 110, 0); else if (front) Px(i, 255, 255, 255); else Px(i, braking ? 255 : 50, 0, 0);
                }
                else if (front) Px(i, 255, 255, 255);
                else Px(i, braking ? 255 : 50, 0, 0);
            }
        }

        void RenderBattery()
        {
            BatteryService battery = _car.Battery;
            if (battery == null || battery.NoBattery)
            {
                Fill(0, 0, 40);
                return;
            }
            // Per-cell 3.3 V (empty) to 4.2 V (full) as 0..12 LEDs; green when full, red when nearly empty.
            double perCell = battery.Volts / 2;
            int lit = (int)((perCell - 3.3) / 0.9 * Gpio32.LedCount + 0.5);
            if (lit < 1) lit = 1;
            if (lit > Gpio32.LedCount) lit = Gpio32.LedCount;
            int red = 255 * (Gpio32.LedCount - lit) / Gpio32.LedCount, green = 255 * lit / Gpio32.LedCount;
            for (int i = 0; i < Gpio32.LedCount; i++)
            {
                if (i < lit) Px(i, red, green, 0); else Px(i, 0, 0, 0);
            }
        }

        // Full-saturation colour on the hue wheel (0..359).
        void Hue(int i, int h)
        {
            int sector = h / 60, f = (h % 60) * 255 / 60;
            switch (sector)
            {
                case 0: Px(i, 255, f, 0); break;
                case 1: Px(i, 255 - f, 255, 0); break;
                case 2: Px(i, 0, 255, f); break;
                case 3: Px(i, 0, 255 - f, 255); break;
                case 4: Px(i, f, 0, 255); break;
                default: Px(i, 255, 0, 255 - f); break;
            }
        }
    }
}
