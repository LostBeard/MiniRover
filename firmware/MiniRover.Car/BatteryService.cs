using System;
using System.Threading;
using MiniRover.Car.Drivers;

namespace MiniRover.Car
{
    /// <summary>
    /// Samples the 2S Li-ion pack every 2 s through the shared GPIO32 divider and keeps a smoothed, sag-aware
    /// estimate. Under motor load the pack sags for a moment, so samples taken while driving get a small weight
    /// and can never trip a warning on their own; protection levels change only on rested readings.
    /// </summary>
    public sealed class BatteryService
    {
        const int PeriodMs = 2000;
        const double AdcFullScaleVolts = 3.3; // raw-count scaling; the coefficient absorbs the ESP32 ADC's error
        const double AdcMaxCount = 4095;

        // Per-cell thresholds (2 cells in series), applied to rested readings.
        const double LowPerCell = 3.40;
        const double CriticalPerCell = 3.20;
        const double RecoverPerCell = 3.55; // hysteresis: a rested pack must climb back above this to clear Low
        public const double NoBatteryVolts = 2.0; // below this the car is on USB only
        const int ConfirmSamples = 3;

        readonly Gpio32 _pin;
        readonly DriveService _drive;
        readonly Settings _settings;
        Thread _thread;
        int _lowCount, _criticalCount;

        public int LastRaw { get; private set; }
        /// <summary>Latest single reading, volts.</summary>
        public double LastVolts { get; private set; }
        /// <summary>Smoothed pack voltage, volts.</summary>
        public double Volts { get; private set; }
        public bool LastSampleUnderLoad { get; private set; }
        public bool NoBattery { get; private set; }
        public BatteryLevel Level { get; private set; } = BatteryLevel.Ok;

        public delegate void PowerHandler();
        /// <summary>Fires when a pack appears after USB-only running (power switch turned on).</summary>
        public event PowerHandler PowerRestored;

        public BatteryService(Gpio32 pin, DriveService drive, Settings settings)
        {
            _pin = pin;
            _drive = drive;
            _settings = settings;
        }

        public void Start()
        {
            Sample(); // first reading before anything can drive
            _thread = new Thread(() =>
            {
                while (true)
                {
                    Thread.Sleep(PeriodMs);
                    try
                    {
                        Sample();
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine("Battery sample failed: " + ex.Message);
                    }
                }
            });
            _thread.Start();
        }

        void Sample()
        {
            bool underLoad = _drive.Moving;
            int raw = _pin.ReadBatteryRaw();
            double volts = raw / AdcMaxCount * AdcFullScaleVolts * _settings.BatteryCoefficient;

            LastRaw = raw;
            LastVolts = volts;
            LastSampleUnderLoad = underLoad;
            bool wasNoBattery = NoBattery;
            NoBattery = volts < NoBatteryVolts;
            if (wasNoBattery && !NoBattery)
            {
                Volts = volts; // restart smoothing from the real pack voltage
                PowerRestored?.Invoke();
            }

            double alpha = underLoad ? 0.03 : 0.3;
            Volts = Volts <= 0 ? volts : Volts + alpha * (volts - Volts);

            if (NoBattery)
            {
                // USB-only power cannot drive the motors or servos properly; refuse to drive.
                SetLevel(BatteryLevel.Critical);
                return;
            }
            if (underLoad)
            {
                return; // sagging readings never change the protection level
            }

            double perCell = volts / 2;
            _criticalCount = perCell < CriticalPerCell ? _criticalCount + 1 : 0;
            _lowCount = perCell < LowPerCell ? _lowCount + 1 : 0;

            if (_criticalCount >= ConfirmSamples)
            {
                SetLevel(BatteryLevel.Critical);
            }
            else if (_lowCount >= ConfirmSamples)
            {
                if (Level != BatteryLevel.Critical) SetLevel(BatteryLevel.Low);
            }
            else if (perCell > RecoverPerCell)
            {
                // Critical latches until a rested reading proves fresh cells (replaced batteries).
                SetLevel(BatteryLevel.Ok);
            }
        }

        void SetLevel(BatteryLevel level)
        {
            if (Level == level) return;
            Level = level;
            _drive.BatteryLevel = level;
            if (level == BatteryLevel.Critical) _drive.Stop();
            System.Diagnostics.Debug.WriteLine("Battery level -> " + BatteryLevelNames.Name(level) + " (" + Volts.ToString("F2") + " V)");
        }
    }
}
