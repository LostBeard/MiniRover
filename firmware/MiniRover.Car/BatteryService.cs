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
        // Raw 12-bit counts at 12 dB attenuation -> volts at the pin. The ESP32 ADC is non-linear and nanoFramework
        // applies no eFuse calibration, so this is Freenove's fitted formula (adc/4096*3.9 * 3.7): a full 2S pack
        // read raw 2369 -> 8.35 V on the real car, where 3.3 * 3.4 said 6.50 V and would have tripped Low.
        // TODO(native interop): read calibrated millivolts (adc_cali, Vref is in eFuse on these chips) and drop the fit.
        public const double AdcFullScaleVolts = 3.9;
        const double AdcMaxCount = 4095;

        /// <summary>
        /// Pack volts per calibrated pin volt when the chip has ADC calibration data (the eFuse reference: every chip's
        /// ADC gain and offset differ, which the fixed formula ignored). Anchored where the fitted formula was set up: a
        /// full pack, raw 2369 = 8.35 V on the first car = 2074 calibrated mV x 4.025. Measured on that car's chip
        /// (2026-10-07): the calibrated line has a lower slope plus a 142 mV offset, so near empty it reads about
        /// 0.09 V higher than the formula did (raw 1992: 7.02 -> 7.11 V). The kit's resistors still vary: calibrate
        /// once against a multimeter (Settings > Battery).
        /// </summary>
        public const double DefaultDivider = 4.025;

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
        /// <summary>Latest reading as calibrated millivolts at the pin, -1 without calibration data.</summary>
        public int LastPinMillivolts { get; private set; } = -1;
        /// <summary>The setting the reading uses: "battery.divider" (calibrated) or "battery.coef" (fitted formula).</summary>
        public string CalibrationKey => LastPinMillivolts >= 0 ? "battery.divider" : "battery.coef";
        /// <summary>Latest single reading, volts.</summary>
        public double LastVolts { get; private set; }
        /// <summary>Smoothed pack voltage, volts.</summary>
        public double Volts { get; private set; }
        public bool LastSampleUnderLoad { get; private set; }
        /// <summary>Latest reading with the learned motor sag added back (= the reading when rested).</summary>
        public double CompensatedVolts { get; private set; }
        /// <summary>Learned volts of sag per unit of motor load (0 until learned) and how many samples taught it.</summary>
        public double SagPerLoad => _sag.SagPerLoad;
        public int SagSamples => _sag.Learned;
        readonly MiniRover.Protocol.BatterySag _sag = new MiniRover.Protocol.BatterySag();
        public bool NoBattery { get; private set; }
        public BatteryLevel Level { get; private set; } = BatteryLevel.Ok;

        public delegate void PowerHandler();
        /// <summary>Fires when a pack appears after USB-only running (power switch turned on).</summary>
        public event PowerHandler PowerRestored;

        /// <summary>Pack volts for a raw ADC reading: calibrated pin millivolts x divider when the chip has calibration
        /// data (pinMillivolts >= 0), else the fitted formula x coefficient.</summary>
        public static double PackVolts(int raw, Settings settings, out int pinMillivolts)
        {
            pinMillivolts = -1;
            try { pinMillivolts = MiniRover.Native.Board.AdcMillivolts(raw); } catch { }
            return pinMillivolts >= 0
                ? pinMillivolts / 1000.0 * settings.BatteryDivider
                : raw / AdcMaxCount * AdcFullScaleVolts * settings.BatteryCoefficient;
        }

        /// <summary>The fitted formula and the calibrated pin voltage side by side over the pack's range (boot log).</summary>
        public static string DescribeCalibration()
        {
            var sb = new System.Text.StringBuilder("ADC raw -> fitted pack V / calibrated pin mV:");
            for (int raw = 1800; raw <= 2450; raw += 50)
            {
                int mv;
                try { mv = MiniRover.Native.Board.AdcMillivolts(raw); } catch { mv = -2; }
                sb.Append(' ').Append(raw.ToString()).Append('=').Append((raw / AdcMaxCount * AdcFullScaleVolts * 3.7).ToString("F2"))
                  .Append('/').Append(mv.ToString());
            }
            return sb.ToString();
        }

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
            double load = underLoad ? _drive.Load : 0;
            int raw = _pin.ReadBatteryRaw();
            int pinMv;
            double volts = PackVolts(raw, _settings, out pinMv);
            LastPinMillivolts = pinMv;

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

            // Driving: add back the motor sag learned from this pack's own rest-to-drive steps (BatterySag), so the
            // charge keeps tracking on a long drive. Until the sag is learned, driving samples barely move the estimate.
            double rested = _sag.Update(volts, load, Environment.TickCount64);
            CompensatedVolts = rested;
            double alpha = !underLoad || _sag.Ready ? 0.3 : 0.03;
            double target = underLoad && _sag.Ready ? rested : volts;
            Volts = Volts <= 0 ? target : Volts + alpha * (target - Volts);

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
