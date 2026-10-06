using System;
using System.Threading;
using MiniRover.Car.Drivers;

namespace MiniRover.Car
{
    /// <summary>
    /// The only path to the motors. Every drive command carries a hold time; if no fresh command arrives
    /// before it runs out, the watchdog thread stops the motors. A dropped link, a closed browser tab or a
    /// crashed client therefore all end the same way: stopped. Battery protection and the speed limit are
    /// applied here too, so no client can bypass them.
    /// </summary>
    public sealed class DriveService
    {
        /// <summary>Longest a single command may keep the car moving.</summary>
        public const int MaxHoldMs = 1000;
        /// <summary>Default hold: a little over 8 missed frames at the 30 Hz control rate.</summary>
        public const int DefaultHoldMs = 300;

        readonly Motors _motors;
        readonly Settings _settings;
        readonly object _lock = new object();
        long _deadlineTicks;
        Thread _watchdog;

        /// <summary>Set by the battery service. Critical = refuse to drive; Low = halve the speed limit.</summary>
        public BatteryLevel BatteryLevel { get; set; } = BatteryLevel.Ok;

        /// <summary>Monotonic count of watchdog stops (a deadman trip with the motors running).</summary>
        public int DeadmanStops { get; private set; }

        public DriveService(Motors motors, Settings settings)
        {
            _motors = motors;
            _settings = settings;
        }

        public bool Moving => _motors.AnyRunning;

        public void Start()
        {
            _watchdog = new Thread(WatchdogLoop);
            _watchdog.Start();
        }

        /// <summary>Skid-steer command: left/right side speeds -1..1, held for <paramref name="holdMs"/>.</summary>
        public void Drive(double left, double right, int holdMs = DefaultHoldMs)
        {
            double limit = EffectiveLimit();
            if (limit <= 0)
            {
                Stop();
                return;
            }
            if (holdMs <= 0) holdMs = DefaultHoldMs;
            if (holdMs > MaxHoldMs) holdMs = MaxHoldMs;

            lock (_lock)
            {
                _motors.SetSides(Clamp(left) * limit, Clamp(right) * limit);
                _deadlineTicks = DateTime.UtcNow.Ticks + holdMs * TimeSpan.TicksPerMillisecond;
            }
        }

        /// <summary>One motor (wheel test / calibration), same hold rules.</summary>
        public void DriveMotor(int motor, double speed, int holdMs = DefaultHoldMs)
        {
            double limit = EffectiveLimit();
            if (limit <= 0)
            {
                Stop();
                return;
            }
            if (holdMs <= 0) holdMs = DefaultHoldMs;
            if (holdMs > MaxHoldMs) holdMs = MaxHoldMs;

            lock (_lock)
            {
                _motors.Set(motor, Clamp(speed) * limit);
                _deadlineTicks = DateTime.UtcNow.Ticks + holdMs * TimeSpan.TicksPerMillisecond;
            }
        }

        public void Stop()
        {
            lock (_lock)
            {
                _motors.StopAll();
                _deadlineTicks = 0;
            }
        }

        double EffectiveLimit()
        {
            if (BatteryLevel == BatteryLevel.Critical) return 0;
            double limit = _settings.SpeedLimit;
            if (limit > 1) limit = 1;
            if (limit < 0) limit = 0;
            if (BatteryLevel == BatteryLevel.Low) limit *= 0.5;
            return limit;
        }

        void WatchdogLoop()
        {
            while (true)
            {
                Thread.Sleep(20);
                lock (_lock)
                {
                    if (_deadlineTicks != 0 && DateTime.UtcNow.Ticks > _deadlineTicks)
                    {
                        if (_motors.AnyRunning)
                        {
                            DeadmanStops++;
                        }
                        _motors.StopAll();
                        _deadlineTicks = 0;
                    }
                }
            }
        }

        static double Clamp(double v) => v < -1 ? -1 : (v > 1 ? 1 : v);
    }

    public enum BatteryLevel
    {
        Ok = 0,
        Low = 1,
        Critical = 2,
    }

    public static class BatteryLevelNames
    {
        /// <summary>nanoFramework's enum ToString() prints the number, not the name.</summary>
        public static string Name(BatteryLevel level)
        {
            switch (level)
            {
                case BatteryLevel.Ok: return "Ok";
                case BatteryLevel.Low: return "Low";
                default: return "Critical";
            }
        }
    }
}
