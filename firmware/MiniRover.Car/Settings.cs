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

        /// <summary>Changes on every Set: lets callers cache anything built from the settings (the /status JSON).</summary>
        public int Version { get; private set; }
        readonly object _lock = new object();

        public double PanTrim { get => GetDouble("pan.trim", 0); set => Set("pan.trim", value); }
        public double TiltTrim { get => GetDouble("tilt.trim", 0); set => Set("tilt.trim", value); }

        /// <summary>Pack volts per pin volt, paired with BatteryService.AdcFullScaleVolts (Freenove's fitted
        /// raw-ADC formula uses 3.7). Calibrate against a multimeter (Docs/battery.md).</summary>
        public double BatteryCoefficient { get => GetDouble("battery.coef", 3.7); set => Set("battery.coef", value); }

        /// <summary>Pack volts per CALIBRATED pin volt (the divider ratio), used when the chip has ADC calibration
        /// data (BatteryService). Calibrate against a multimeter or charger (Docs/battery.md).</summary>
        public double BatteryDivider { get => GetDouble("battery.divider", BatteryService.DefaultDivider); set => Set("battery.divider", value); }

        /// <summary>Global speed limit 0..1 applied to every drive command ("kid mode" below 1).</summary>
        public double SpeedLimit { get => GetDouble("drive.limit", 1.0); set => Set("drive.limit", value); }

        public int MotorMinimumDuty { get => (int)GetDouble("motor.minduty", 1600); set => Set("motor.minduty", value); }

        public bool GetMotorInverted(int motor) => GetDouble("motor" + motor + ".invert", 0) != 0;
        public void SetMotorInverted(int motor, bool inverted) => Set("motor" + motor + ".invert", inverted ? 1 : 0);
        public double GetMotorGain(int motor) => GetDouble("motor" + motor + ".gain", 1);
        public void SetMotorGain(int motor, double gain) => Set("motor" + motor + ".gain", gain);

        public int LedBrightness { get => (int)GetDouble("led.brightness", 64); set => Set("led.brightness", value); }

        /// <summary>20-byte signaling room key (hex) shared with paired browsers; empty until first pairing.</summary>
        public string RoomKeyHex { get => GetString("pair.roomkey", ""); set => SetString("pair.roomkey", value); }

        /// <summary>The pairing key, or null when the car is not paired.</summary>
        public byte[] RoomKey
        {
            get
            {
                string hex = RoomKeyHex;
                if (hex == null || hex.Length != 2 * MiniRover.Protocol.CarLink.KeyBytes) return null;
                byte[] b = new byte[hex.Length / 2];
                for (int i = 0; i < b.Length; i++) b[i] = (byte)Convert.ToInt32(hex.Substring(i * 2, 2), 16);
                return b;
            }
        }

        // Camera (Docs/hardware.md). Size = esp32-camera framesize (6 = 320x240), quality 0..63 (lower = better).
        public int CameraSize { get => (int)GetDouble("camera.size", 6); set => Set("camera.size", value); }
        public int CameraQuality { get => (int)GetDouble("camera.quality", 12); set => Set("camera.quality", value); }
        public int CameraMaxFps { get => (int)GetDouble("camera.fps", 15); set => Set("camera.fps", value); }
        // The FNK0053 head holds the sensor upside down: a true 180-degree rotation (flip + mirror) makes it upright and
        // unmirrored. Verified on the real car by panning: at pan 160 (camera turned left) the scene moved right.
        public bool CameraMirror { get => GetDouble("camera.mirror", 1) != 0; set => Set("camera.mirror", value ? 1 : 0); }
        public bool CameraFlip { get => GetDouble("camera.flip", 1) != 0; set => Set("camera.flip", value ? 1 : 0); }

        /// <summary>
        /// Which corner each of the 12 LEDs sits on, one character per LED: 0 front left, 1 front right, 2 rear left,
        /// 3 rear right, '-' unknown. Filled in by the app's light calibration; the order is not documented, so it is
        /// never guessed. Headlights, brake lights and turn signals need it.
        /// </summary>
        public string LedCorners { get => GetString("led.corners", "------------"); set => SetString("led.corners", value); }

        // Camera sensor controls, "sensor.<name>" keys, index = MiniRover.Native.Camera.Ctrl* id. Stored only once a
        // client sets one; an unset control keeps the driver's default (the app is shown the sensor's live value).
        public static readonly string[] SensorNames =
        {
            "", "brightness", "contrast", "saturation", "sharpness", "denoise", "gainceiling", "awb", "awbgain", "wbmode",
            "agc", "agcgain", "aec", "aec2", "aelevel", "aecvalue", "bpc", "wpc", "rawgamma", "lenc", "dcw", "effect",
            "xclk",
        };
        static readonly int[] s_sensorMin = { 0, -2, -2, -2, -2, 0, 0, 0, 0, 0, 0, 0, 0, 0, -2, 0, 0, 0, 0, 0, 0, 0, 8 };
        static readonly int[] s_sensorMax = { 0, 2, 2, 2, 2, 8, 6, 1, 1, 4, 1, 30, 1, 1, 2, 1200, 1, 1, 1, 1, 1, 6, 24 };

        /// <summary>The Camera.Ctrl* id of a "sensor.&lt;name&gt;" key, or 0.</summary>
        public static int SensorControlId(string key)
        {
            if (key == null || !key.StartsWith("sensor.")) return 0;
            string name = key.Substring(7);
            for (int i = 1; i < SensorNames.Length; i++) if (SensorNames[i] == name) return i;
            return 0;
        }

        /// <summary>A sensor control the client set (false = never set: the driver default applies).</summary>
        public bool TryGetSensorControl(int id, out int value)
        {
            value = 0;
            if (id <= 0 || id >= SensorNames.Length) return false;
            string v = GetString("sensor." + SensorNames[id], "");
            if (v.Length == 0)
            {
                // MiniRover's own default where the driver's is wrong for a car indoors. The gain ceiling: esp32-camera
                // leaves the OV2640 at 2x, which starves auto gain in a room. Measured on the car (QVGA, room light):
                // 2x mean luma 31.5 / relative noise 5.97% / JPEG block grid 2.98; 8x 66.4 / 5.45% / 2.28; 32x 70.4 /
                // 5.52% / 2.26 with visibly more colour noise. 8x it is.
                if (id == 6) { value = 2; return true; }
                return false;
            }
            try { value = int.Parse(v); return true; } catch { return false; }
        }

        /// <summary>The local HTTP test API (curl drive/servo/led routes). Off by default: HTTP on the LAN is not paired.</summary>
        public bool HttpApi { get => GetDouble("http.api", 0) != 0; set => Set("http.api", value ? 1 : 0); }

        /// <summary>The current value of a client key as text ("" for an unknown key).</summary>
        public string ValueOf(string key)
        {
            switch (key)
            {
                case "pan.trim": return Num(PanTrim);
                case "tilt.trim": return Num(TiltTrim);
                case "battery.coef": return Num(BatteryCoefficient);
                case "battery.divider": return Num(BatteryDivider);
                case "drive.limit": return Num(SpeedLimit);
                case "motor.minduty": return MotorMinimumDuty.ToString();
                case "led.brightness": return LedBrightness.ToString();
                case "camera.size": return CameraSize.ToString();
                case "camera.quality": return CameraQuality.ToString();
                case "camera.fps": return CameraMaxFps.ToString();
                case "camera.mirror": return CameraMirror ? "1" : "0";
                case "camera.flip": return CameraFlip ? "1" : "0";
                case "http.api": return HttpApi ? "1" : "0";
                case "led.corners": return LedCorners;
            }
            if (key.Length >= 11 && key.StartsWith("motor") && key[5] >= '0' && key[5] <= '3')
            {
                int m = key[5] - '0';
                if (key.Substring(6) == ".invert") return GetMotorInverted(m) ? "1" : "0";
                if (key.Substring(6) == ".gain") return Num(GetMotorGain(m));
            }
            return "";
        }

        /// <summary>Every client key as "key=value" lines (the app's Settings page reads this).</summary>
        public string Describe()
        {
            var sb = new StringBuilder();
            foreach (string key in ClientKeys) sb.Append(key).Append('=').Append(ValueOf(key)).Append('\n');
            return sb.ToString();
        }

        static string Num(double v) => v.ToString("F3");

        /// <summary>The last abnormal reset (crash, brownout, watchdog) and when: diagnostics that survive the
        /// self-healing full restart, which would otherwise hide the cause.</summary>
        public string LastAbnormalReset { get => GetString("diag.reset", ""); set => SetString("diag.reset", value); }

        /// <summary>Why the last WiFi connection attempt failed (shown to the app in setup mode).</summary>
        public string LastWifiError { get => GetString("wifi.error", ""); set => SetString("wifi.error", value); }

        /// <summary>Play mode: the car makes its own WiFi (no internet; the app signals over BLE). Not a client key: it
        /// changes only through WifiService, which also rewrites the network configuration and restarts.</summary>
        public bool PlayMode { get => GetString("wifi.mode", "home") == "play"; set => SetString("wifi.mode", value ? "play" : "home"); }

        /// <summary>Every key a client may change (HTTP, BLE and the app link all go through <see cref="TryApply"/>).</summary>
        public static readonly string[] ClientKeys =
        {
            "pan.trim", "tilt.trim", "battery.coef", "battery.divider", "drive.limit", "motor.minduty", "led.brightness",
            "camera.size", "camera.quality", "camera.fps", "camera.mirror", "camera.flip", "http.api", "led.corners",
            "motor0.invert", "motor1.invert", "motor2.invert", "motor3.invert",
            "motor0.gain", "motor1.gain", "motor2.gain", "motor3.gain",
        };

        /// <summary>
        /// Applies one "key=value" from a client, clamped to safe ranges (a bad value must never make the car unsafe:
        /// a speed limit above 1, a battery coefficient that hides a flat pack). Does not save. False for an unknown key
        /// or a value that is not a number.
        /// </summary>
        public bool TryApply(string kv)
        {
            int eq = kv == null ? -1 : kv.IndexOf('=');
            if (eq <= 0) return false;
            string key = kv.Substring(0, eq).Trim();
            if (key == "led.corners")
            {
                // Text, not a number: 12 characters from "0123-" (see LedCorners).
                string corners = kv.Substring(eq + 1).Trim();
                if (corners.Length != 12) return false;
                for (int i = 0; i < corners.Length; i++) if ("0123-".IndexOf(corners[i]) < 0) return false;
                LedCorners = corners;
                return true;
            }
            int sensor = SensorControlId(key);
            if (sensor > 0)
            {
                // Integer in the control's range; empty = forget it (the driver default applies from the next start).
                string text = kv.Substring(eq + 1).Trim();
                if (text.Length == 0) { SetString(key, ""); return true; }
                int iv;
                try { iv = int.Parse(text); } catch { return false; }
                if (iv < s_sensorMin[sensor]) iv = s_sensorMin[sensor];
                if (iv > s_sensorMax[sensor]) iv = s_sensorMax[sensor];
                SetString(key, iv.ToString());
                return true;
            }
            double v;
            try { v = double.Parse(kv.Substring(eq + 1).Trim()); } catch { return false; }
            switch (key)
            {
                case "pan.trim": PanTrim = Clamp(v, -30, 30); return true;
                case "tilt.trim": TiltTrim = Clamp(v, -30, 30); return true;
                case "battery.coef": BatteryCoefficient = Clamp(v, 2.5, 5.5); return true;
                case "battery.divider": BatteryDivider = Clamp(v, 3.0, 5.5); return true;
                case "drive.limit": SpeedLimit = Clamp(v, 0, 1); return true;
                case "motor.minduty": MotorMinimumDuty = (int)Clamp(v, 0, 4000); return true;
                case "led.brightness": LedBrightness = (int)Clamp(v, 0, 255); return true;
                case "camera.size": CameraSize = (int)Clamp(v, 1, 11); return true;
                case "camera.quality": CameraQuality = (int)Clamp(v, 8, 63); return true; // below 8 a frame can outgrow the camera buffers
                case "camera.fps": CameraMaxFps = (int)Clamp(v, 1, 30); return true;
                case "camera.mirror": CameraMirror = v != 0; return true;
                case "camera.flip": CameraFlip = v != 0; return true;
                case "http.api": HttpApi = v != 0; return true;
            }
            // motorN.invert / motorN.gain, N = 0..3
            if (key.Length >= 11 && key.StartsWith("motor") && key[5] >= '0' && key[5] <= '3')
            {
                int m = key[5] - '0';
                string rest = key.Substring(6);
                if (rest == ".invert") { SetMotorInverted(m, v != 0); return true; }
                if (rest == ".gain") { SetMotorGain(m, Clamp(v, 0, 2)); return true; }
            }
            return false;
        }

        static double Clamp(double v, double lo, double hi) => v < lo ? lo : (v > hi ? hi : v);

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

        string GetString(string key, string fallback)
        {
            lock (_lock)
            {
                return _values[key] as string ?? fallback;
            }
        }

        void SetString(string key, string value)
        {
            // One line per key in the file: keep values single-line.
            if (value != null && (value.IndexOf((char)10) >= 0 || value.IndexOf((char)13) >= 0)) throw new ArgumentException(nameof(value));
            lock (_lock)
            {
                _values[key] = value ?? "";
                Version++;
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
                Version++;
            }
        }
    }
}
