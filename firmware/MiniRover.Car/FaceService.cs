using System;
using System.Threading;
using MiniRover.Car.Drivers;
using MiniRover.Protocol;

namespace MiniRover.Car
{
    /// <summary>
    /// The LED-matrix eyes, animated on the car (like the lights) so they keep going without the app and cost no
    /// network traffic. A frame goes over I2C only when it changes.
    ///
    /// Two layers: a system picture (pairing code, setup mode, flat battery) always wins; otherwise the app's choice
    /// shows: alive eyes (blink, look where the car steers, glance around when idle), a mood, scrolling text, or a
    /// raw frame. Artwork and font are in MiniRover.Protocol.EyeArt, shared with the app.
    /// </summary>
    public sealed class FaceService
    {
        const int FrameMs = 40;
        const int ScrollMs = 90;          // per column: about 2.5 letters a second, readable for a young reader
        const int IdleGlanceMs = 4000;    // stopped this long: start glancing around
        const int ModeCustom = 100;       // a raw frame from MsgEyes / HTTP (not a wire mode)

        readonly Car _car;
        readonly LedMatrix _matrix;
        readonly object _lock = new object();
        readonly Random _rng = new Random();
        readonly byte[] _sent = new byte[EyeArt.FrameBytes];
        bool _sentValid;

        int _mode = CarLink.FaceAlive;
        int _arg;
        string _text = "";
        long _textStart;
        byte[] _custom;
        byte[] _system;
        byte[] _overlay;

        // Alive-mode state (render thread only).
        long _nextBlink, _blinkUntil, _nextGlance, _movingAt;
        int _glanceX, _glanceY;

        public FaceService(Car car)
        {
            _car = car;
            _matrix = car.Matrix;
        }

        /// <summary>The app's mode (CarLink.Face*), or 100 while a raw frame shows.</summary>
        public int Mode => _mode;

        public void Start()
        {
            new Thread(Run).Start();
        }

        /// <summary>Selects what the eyes show (CarLink.FaceAlive / FaceMood / FaceText; see there for arg).</summary>
        public void Set(int mode, int arg, string text)
        {
            lock (_lock)
            {
                if (mode == CarLink.FaceText && (text == null || text.Length == 0)) mode = CarLink.FaceAlive;
                _mode = mode;
                _arg = arg;
                _text = text ?? "";
                _textStart = Environment.TickCount64;
            }
        }

        /// <summary>Shows one raw 16-byte frame until the next Set.</summary>
        public void ShowCustom(byte[] frame)
        {
            if (frame == null || frame.Length < EyeArt.FrameBytes) throw new ArgumentException(nameof(frame));
            var copy = new byte[EyeArt.FrameBytes];
            Array.Copy(frame, copy, EyeArt.FrameBytes);
            lock (_lock)
            {
                _custom = copy;
                _mode = ModeCustom;
            }
        }

        /// <summary>A system picture (setup mode, flat battery, no battery) that overrides the app's choice; null
        /// hands the eyes back.</summary>
        public void SetSystem(byte[] frame)
        {
            lock (_lock)
            {
                _system = frame;
            }
        }

        /// <summary>The BLE pairing code: above everything while a phone is pairing; null when done.</summary>
        public void SetOverlay(byte[] frame)
        {
            lock (_lock)
            {
                _overlay = frame;
            }
        }

        void Run()
        {
            while (true)
            {
                try
                {
                    byte[] frame = Render(Environment.TickCount64);
                    if (!_sentValid || !Same(frame, _sent))
                    {
                        _matrix.Show(frame);
                        Array.Copy(frame, _sent, EyeArt.FrameBytes);
                        _sentValid = true;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("Face: " + ex.Message);
                    _sentValid = false; // resend once the bus answers again
                    Thread.Sleep(1000);
                }
                Thread.Sleep(FrameMs);
            }
        }

        static bool Same(byte[] a, byte[] b)
        {
            for (int i = 0; i < EyeArt.FrameBytes; i++) if (a[i] != b[i]) return false;
            return true;
        }

        byte[] Render(long now)
        {
            int mode, arg;
            string text;
            long textStart;
            byte[] custom, system, overlay;
            lock (_lock)
            {
                mode = _mode;
                arg = _arg;
                text = _text;
                textStart = _textStart;
                custom = _custom;
                system = _system;
                overlay = _overlay;
            }
            if (overlay != null) return overlay;
            if (system != null) return system;

            switch (mode)
            {
                case ModeCustom:
                    return custom;
                case CarLink.FaceMood:
                    return EyeArt.Mood(arg);
                case CarLink.FaceText:
                    {
                        // A pass starts with the text just off the right edge and ends when it has left on the left.
                        int pass = EyeArt.Columns + EyeArt.TextColumns(text);
                        long step = (now - textStart) / ScrollMs;
                        if (arg > 0 && step >= (long)pass * arg)
                        {
                            Set(CarLink.FaceAlive, 0, null);
                            return Alive(now);
                        }
                        return EyeArt.Text(text, (int)(step % pass) - EyeArt.Columns);
                    }
                default:
                    return Alive(now);
            }
        }

        byte[] Alive(long now)
        {
            double left = 0, right = 0;
            if (_car.Drive != null)
            {
                left = _car.Drive.LastLeft;
                right = _car.Drive.LastRight;
            }
            int lookX = 0, lookY = 0;
            if (Math.Abs(left) + Math.Abs(right) > 0.05)
            {
                _movingAt = now;
                // Left side faster = turning to the car's right, which is the viewer's LEFT when facing the car's
                // eyes: the pupils move to smaller screen columns.
                double turn = left - right; // -2..2
                if (turn > 0.15) lookX = turn > 0.6 ? -2 : -1;
                else if (turn < -0.15) lookX = turn < -0.6 ? 2 : 1;
                if ((left + right) / 2 < -0.1) lookY = 1; // reversing: look down
            }
            else if (now - _movingAt > IdleGlanceMs)
            {
                if (now >= _nextGlance)
                {
                    // Mostly straight ahead, sometimes a look around.
                    bool centre = _rng.Next(5) < 2;
                    _glanceX = centre ? 0 : _rng.Next(2 * EyeArt.LookRange + 1) - EyeArt.LookRange;
                    _glanceY = centre ? 0 : _rng.Next(3) - 1;
                    _nextGlance = now + 1500 + _rng.Next(3500);
                }
                lookX = _glanceX;
                lookY = _glanceY;
            }

            if (now >= _nextBlink)
            {
                _blinkUntil = now + 130;
                // Every few seconds, now and then a double blink.
                _nextBlink = _rng.Next(6) == 0 ? now + 350 : now + 2500 + _rng.Next(4000);
            }
            return EyeArt.Alive(lookX, lookY, now < _blinkUntil);
        }
    }
}
