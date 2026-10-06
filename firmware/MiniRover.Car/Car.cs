using System;
using System.Device.Adc;
using System.Device.Gpio;
using MiniRover.Car.Drivers;

namespace MiniRover.Car
{
    /// <summary>
    /// Owns every board driver. Each part is brought up independently and records why it failed, so a missing
    /// or unplugged board (a sonar-less head, a loose I2C cable) shows up in the status report instead of
    /// stopping the whole car from booting.
    /// </summary>
    public sealed class Car
    {
        public readonly Settings Settings;
        public Pca9685 Pwm;
        public Motors Motors;
        public PanTilt Servos;
        public LineSensors Line;
        public LedMatrix Matrix;
        public Gpio32 Leds;
        public Buzzer Buzzer;
        public LightSensor Light;
        public Sonar Sonar;
        public IrReceiver Ir;
        public DriveService Drive;
        public BatteryService Battery;

        public string Faults = "";
        /// <summary>A battery pack was detected at boot (false = USB power only).</summary>
        public bool HasBattery;
        /// <summary>Pack voltage measured at boot, before anything drew current (shown even when the motor board failed).</summary>
        public double BootVolts;
        public uint LastIrCode;
        /// <summary>Camera sensor id (MiniRover.Native.Camera.Sensor*), 0 when no camera answered.</summary>
        public int CameraSensor;
        /// <summary>Environment.TickCount64 when the last IR code arrived; 0 = none yet.</summary>
        public long LastIrMs;

        public Car(Settings settings)
        {
            Settings = settings;
        }

        public void Init()
        {
            AdcController adc = null;
            GpioController gpio = new GpioController();

            // Battery check FIRST, before anything draws real current (GPIO32 is still free: no LED channel yet).
            Try("adc", () =>
            {
                adc = new AdcController();
                using (AdcChannel ch = adc.OpenChannel(BoardPins.BatteryAdcChannel))
                {
                    int sum = 0;
                    for (int i = 0; i < 8; i++) sum += ch.ReadValue();
                    double volts = sum / 8 / 4095.0 * BatteryService.AdcFullScaleVolts * Settings.BatteryCoefficient;
                    BootVolts = volts;
                    HasBattery = volts >= BatteryService.NoBatteryVolts;
                    System.Diagnostics.Debug.WriteLine("Boot battery " + volts.ToString("F2") + " V" + (HasBattery ? "" : " - USB power only, servos stay off"));
                }
            });

            Try("pca9685", () =>
            {
                Pwm = new Pca9685(BoardPins.Pca9685Address, 50);
                Motors = new Motors(Pwm) { MinimumDuty = Settings.MotorMinimumDuty };
                for (int m = 0; m < Motors.Count; m++)
                {
                    Motors.SetInverted(m, Settings.GetMotorInverted(m));
                    Motors.SetGain(m, Settings.GetMotorGain(m));
                }
                Servos = new PanTilt(Pwm) { PanTrim = Settings.PanTrim, TiltTrim = Settings.TiltTrim };
                // Straight ahead and level at every boot: flashing the firmware is all it takes to centre
                // the servos before mounting the camera head (Docs/servo-calibration.md). Only with a battery:
                // on USB power alone the servos' start-up current browns the ESP32 out into a reset loop
                // (lights blinking, servos clicking). BatteryService centres them once a pack appears.
                if (HasBattery) Servos.CenterBoth();
            });
            if (SharedI2c.RecoveryNote.Length > 0) System.Diagnostics.Debug.WriteLine(SharedI2c.RecoveryNote);
            Try("leds", () =>
            {
                Leds = new Gpio32(adc) { Brightness = Settings.LedBrightness };
            });
            Try("matrix", () => Matrix = new LedMatrix());
            Try("line", () => Line = new LineSensors());
            Try("light", () => Light = new LightSensor(adc));
            Try("buzzer", () => Buzzer = new Buzzer());
            Try("sonar", () => Sonar = new Sonar(gpio));
            Try("ir", () =>
            {
                Ir = new IrReceiver();
                Ir.CodeReceived += code =>
                {
                    LastIrCode = code;
                    LastIrMs = Environment.TickCount64;
                    System.Diagnostics.Debug.WriteLine("IR: 0x" + code.ToString("X8"));
                };
            });

            if (Motors != null)
            {
                Drive = new DriveService(Motors, Settings);
                Drive.Start();
                if (Leds != null)
                {
                    Battery = new BatteryService(Leds, Drive, Settings);
                    Battery.PowerRestored += () =>
                    {
                        HasBattery = true;
                        if (Servos != null) Servos.CenterBoth();
                    };
                    Try("battery", () => Battery.Start());
                }
            }
            if (Leds != null) Lights = new LightsService(this); // started by Program after the greeting
        }

        /// <summary>Light patterns over <see cref="Leds"/> (null without LEDs). Everything that colours the LEDs goes
        /// through it, so its animation thread never fights another writer.</summary>
        public LightsService Lights;

        void Try(string part, Action init)
        {
            try
            {
                init();
            }
            catch (Exception ex)
            {
                Faults += (Faults.Length > 0 ? "; " : "") + part + ": " + ex.Message;
                System.Diagnostics.Debug.WriteLine("Init " + part + " FAILED: " + ex.Message);
            }
        }

        /// <summary>Starts the camera head if one is fitted (the ultrasonic head has none). Not a fault when absent.</summary>
        public void InitCamera()
        {
            int r = MiniRover.Native.Camera.Init(Settings.CameraSize, Settings.CameraQuality);
            CameraSensor = r > 0 ? r : 0;
            if (CameraSensor > 0) MiniRover.Native.Camera.SetOrientation(Settings.CameraMirror, Settings.CameraFlip);
            System.Diagnostics.Debug.WriteLine(CameraSensor > 0
                ? "Camera: sensor 0x" + CameraSensor.ToString("X2") + (CameraSensor == MiniRover.Native.Camera.SensorGC0308 ? " (GC0308, software JPEG)" : CameraSensor == MiniRover.Native.Camera.SensorOV2640 ? " (OV2640)" : "")
                : "Camera: none found (error " + r + ")");
        }

        public void ApplySettings()
        {
            if (CameraSensor > 0)
            {
                MiniRover.Native.Camera.Configure(Settings.CameraSize, Settings.CameraQuality);
                MiniRover.Native.Camera.SetOrientation(Settings.CameraMirror, Settings.CameraFlip);
            }
            if (Servos != null)
            {
                Servos.PanTrim = Settings.PanTrim;
                Servos.TiltTrim = Settings.TiltTrim;
                Servos.SetPan(Servos.Pan);
                Servos.SetTilt(Servos.Tilt);
            }
            if (Motors != null)
            {
                Motors.MinimumDuty = Settings.MotorMinimumDuty;
                for (int m = 0; m < Motors.Count; m++)
                {
                    Motors.SetInverted(m, Settings.GetMotorInverted(m));
                    Motors.SetGain(m, Settings.GetMotorGain(m));
                }
            }
            if (Leds != null)
            {
                Leds.Brightness = Settings.LedBrightness;
                Leds.Show();
            }
        }
    }
}
