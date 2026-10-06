using System;
using System.Collections;
using System.IO;
using System.Text;

namespace MiniRover.Car
{
    /// <summary>
    /// Car settings persisted on the internal flash drive (I:) as "key=value" lines, so calibration
    /// (servo trim, motor trim, battery coefficient) survives reboots and app redeploys.
    /// WiFi credentials are NOT stored here: the nanoFramework network configuration block owns them.
    /// </summary>
    public sealed class Settings
    {
        const string FilePath = "I:\\minirover.cfg";

        readonly Hashtable _values = new Hashtable();
        readonly object _lock = new object();

        public double PanTrim { get => GetDouble("pan.trim", 0); set => Set("pan.trim", value); }
        public double TiltTrim { get => GetDouble("tilt.trim", 0); set => Set("tilt.trim", value); }

        /// <summary>Pack volts per pin volt. Default 3.4 is Freenove's value for raw (uncalibrated) ADC
        /// readings; calibrate it against a multimeter (Docs/battery.md).</summary>
        public double BatteryCoefficient { get => GetDouble("battery.coef", 3.4); set => Set("battery.coef", value); }

        /// <summary>Global speed limit 0..1 applied to every drive command ("kid mode" below 1).</summary>
        public double SpeedLimit { get => GetDouble("drive.limit", 1.0); set => Set("drive.limit", value); }

        public int MotorMinimumDuty { get => (int)GetDouble("motor.minduty", 1600); set => Set("motor.minduty", value); }

        public bool GetMotorInverted(int motor) => GetDouble("motor" + motor + ".invert", 0) != 0;
        public void SetMotorInverted(int motor, bool inverted) => Set("motor" + motor + ".invert", inverted ? 1 : 0);
        public double GetMotorGain(int motor) => GetDouble("motor" + motor + ".gain", 1);
        public void SetMotorGain(int motor, double gain) => Set("motor" + motor + ".gain", gain);

        public int LedBrightness { get => (int)GetDouble("led.brightness", 64); set => Set("led.brightness", value); }

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                if (File.Exists(FilePath))
                {
                    string text;
                    using (var fs = new FileStream(FilePath, FileMode.Open, FileAccess.Read))
                    {
                        var buf = new byte[(int)fs.Length];
                        fs.Read(buf, 0, buf.Length);
                        text = Encoding.UTF8.GetString(buf, 0, buf.Length);
                    }
                    foreach (string line in text.Split('\n'))
                    {
                        int eq = line.IndexOf('=');
                        if (eq > 0)
                        {
                            s._values[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // A corrupt settings file must not stop the car booting: run on defaults and say so.
                System.Diagnostics.Debug.WriteLine("Settings: load failed, using defaults: " + ex.Message);
            }
            return s;
        }

        public void Save()
        {
            lock (_lock)
            {
                var sb = new StringBuilder();
                foreach (DictionaryEntry e in _values)
                {
                    sb.Append((string)e.Key).Append('=').Append((string)e.Value).Append('\n');
                }
                byte[] bytes = Encoding.UTF8.GetBytes(sb.ToString());
                using (var fs = new FileStream(FilePath, FileMode.Create, FileAccess.Write))
                {
                    fs.Write(bytes, 0, bytes.Length);
                }
            }
        }

        double GetDouble(string key, double fallback)
        {
            lock (_lock)
            {
                string v = _values[key] as string;
                if (v == null) return fallback;
                try { return double.Parse(v); }
                catch { return fallback; }
            }
        }

        void Set(string key, double value)
        {
            lock (_lock)
            {
                _values[key] = value.ToString();
            }
        }
    }
}
