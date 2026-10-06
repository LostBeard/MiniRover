using System;
using System.Net.Sockets;
using System.Text;
using MiniRover.Car.Drivers;
using nanoFramework.Runtime.Native;

namespace MiniRover.Car
{
    /// <summary>
    /// HTTP routes. In setup mode only the WiFi setup page is served. In normal mode this is the local
    /// test/diagnostic API (curl-able, used by hardware-in-the-loop tests); everyday driving goes over WebRTC.
    /// Every motion route is bounded by the drive watchdog, so a dropped request cannot leave the car moving.
    /// </summary>
    public sealed class WebApi
    {
        readonly Car _car;
        readonly WifiService _wifi;
        readonly HttpServer _http;

        /// <summary>The app link (null in setup mode).</summary>
        public RtcLinkService Link;

        public WebApi(Car car, WifiService wifi, HttpServer http)
        {
            _car = car;
            _wifi = wifi;
            _http = http;
        }

        public void Handle(HttpRequest r, Socket c)
        {
            if (r.Method == "OPTIONS")
            {
                HttpServer.SendText(c, 204, "text/plain", "");
                return;
            }

            if (_wifi.InSetupMode)
            {
                HandleSetup(r, c);
                return;
            }

            switch (r.Path)
            {
                case "/":
                case "/status":
                    HttpServer.SendText(c, 200, "application/json", StatusJson());
                    return;
                case "/stop":
                    // Always open: anyone standing next to the car may stop it.
                    if (_car.Drive != null) _car.Drive.Stop();
                    Ok(c);
                    return;
            }

            // Everything else moves the car or changes it, and HTTP on the LAN has no pairing: off unless the owner
            // turned it on over a paired channel (BLE with the code on the eyes, or the authenticated app link).
            if (!_car.Settings.HttpApi)
            {
                HttpServer.SendText(c, 403, "text/plain",
                    "The car's local test API is off. Turn it on from a paired app or `minirover hw COMx \"set http.api=1\"`.");
                return;
            }

            switch (r.Path)
            {
                case "/servo/center":
                    Need(_car.Servos, "servos");
                    _car.Servos.CenterBoth();
                    Ok(c);
                    return;
                case "/servo":
                    Need(_car.Servos, "servos");
                    if (r.Get("pan") != null) _car.Servos.SetPan(r.GetDouble("pan", PanTilt.Center));
                    if (r.Get("tilt") != null) _car.Servos.SetTilt(r.GetDouble("tilt", PanTilt.Center));
                    if (r.Get("relax") == "1") _car.Servos.Relax();
                    Ok(c);
                    return;
                case "/drive":
                    Need(_car.Drive, "drive");
                    _car.Drive.Drive(r.GetDouble("left", 0), r.GetDouble("right", 0), (int)r.GetDouble("ms", DriveService.DefaultHoldMs));
                    Ok(c);
                    return;
                case "/motor":
                    Need(_car.Drive, "drive");
                    _car.Drive.DriveMotor((int)r.GetDouble("m", 0), r.GetDouble("speed", 0), (int)r.GetDouble("ms", DriveService.DefaultHoldMs));
                    Ok(c);
                    return;
                case "/leds":
                    Need(_car.Leds, "leds");
                    if (r.Get("brightness") != null) _car.Leds.Brightness = (int)r.GetDouble("brightness", 64);
                    byte red = (byte)r.GetDouble("r", 0), green = (byte)r.GetDouble("g", 0), blue = (byte)r.GetDouble("b", 0);
                    if (r.Get("i") != null) _car.Leds.SetPixel((int)r.GetDouble("i", 0), red, green, blue);
                    else _car.Leds.Fill(red, green, blue);
                    _car.Leds.Show();
                    Ok(c);
                    return;
                case "/matrix":
                    Need(_car.Matrix, "matrix");
                    if (r.Get("brightness") != null) _car.Matrix.SetBrightness((int)r.GetDouble("brightness", 8));
                    string hex = r.Get("hex");
                    _car.Matrix.Show(hex != null ? ParseHex(hex) : EyesByName(r.Get("eyes", "open")));
                    Ok(c);
                    return;
                case "/buzzer":
                    Need(_car.Buzzer, "buzzer");
                    int ms = (int)r.GetDouble("ms", 200);
                    if (ms > 2000) ms = 2000;
                    _car.Buzzer.Beep((int)r.GetDouble("hz", Buzzer.ResonantHz), ms);
                    Ok(c);
                    return;
                case "/sonar":
                    Need(_car.Sonar, "sonar");
                    double cm;
                    bool got = _car.Sonar.TryReadCentimeters(out cm);
                    HttpServer.SendText(c, 200, "application/json", "{\"ok\":" + B(got) + ",\"cm\":" + cm.ToString("F1") + "}");
                    return;
                case "/settings":
                    ApplySettings(r);
                    HttpServer.SendText(c, 200, "application/json", StatusJson());
                    return;
                case "/wifi/setup":
                    Ok(c);
                    _wifi.EnterSetupMode(); // reboots
                    return;
                case "/reboot":
                    Ok(c);
                    Power.RebootDevice();
                    return;
            }
            HttpServer.SendText(c, 404, "text/plain", "not found: " + r.Path);
        }

        void HandleSetup(HttpRequest r, Socket c)
        {
            if (r.Method == "POST" && r.Path == "/setup")
            {
                string ssid = r.Get("ssid", "").Trim();
                if (ssid.Length == 0)
                {
                    HttpServer.SendText(c, 400, "text/html", Page("<p>Please enter your WiFi network name.</p><p><a href=\"/\">Back</a></p>"));
                    return;
                }
                HttpServer.SendText(c, 200, "text/html", Page(
                    "<p>Saved. The car is restarting and will join <b>" + HttpServer.HtmlEncode(ssid) + "</b>.</p>" +
                    "<p>Reconnect this device to your normal WiFi. If the car cannot join, it comes back to this setup network.</p>"));
                _wifi.SaveNetworkAndReboot(ssid, r.Get("password", ""));
                return;
            }

            string current = _wifi.StationSsid.Length > 0
                ? "<p>Saved network: <b>" + HttpServer.HtmlEncode(_wifi.StationSsid) + "</b> (could not connect, or setup was requested).</p>"
                : "";
            HttpServer.SendText(c, 200, "text/html", Page(
                current +
                "<form method=\"post\" action=\"/setup\">" +
                "<label>WiFi network name<br><input name=\"ssid\" autocomplete=\"off\" required></label>" +
                "<label>Password<br><input name=\"password\" type=\"password\"></label>" +
                "<button type=\"submit\">Save and restart</button></form>" +
                "<p class=\"n\">2.4 GHz networks only (the ESP32 has no 5 GHz radio).</p>"));
        }

        static string Page(string body)
        {
            return "<!doctype html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">" +
                   "<title>MiniRover setup</title><style>" +
                   "body{font-family:system-ui,sans-serif;max-width:28rem;margin:2rem auto;padding:0 1rem;background:#111;color:#eee}" +
                   "h1{font-size:1.4rem}label{display:block;margin:1rem 0}input{width:100%;padding:.6rem;font-size:1rem;box-sizing:border-box}" +
                   "button{padding:.7rem 1.2rem;font-size:1rem}.n{color:#aaa;font-size:.9rem}a{color:#8cf}" +
                   "</style></head><body><h1>MiniRover WiFi setup</h1>" + body + "</body></html>";
        }

        void ApplySettings(HttpRequest r)
        {
            Settings s = _car.Settings;
            bool any = false;
            foreach (string key in Settings.ClientKeys)
            {
                string v = r.Get(key);
                if (v != null && s.TryApply(key + "=" + v)) any = true;
            }
            if (!any) return;
            s.Save();
            _car.ApplySettings();
        }

        string StatusJson()
        {
            var sb = new StringBuilder();
            // Monotonic: the wall clock jumps when WiFi syncs the time (wall-clock uptime read ~56 years on the car).
            sb.Append("{\"uptimeS\":").Append((Environment.TickCount64 / 1000).ToString());
            sb.Append(",\"ip\":\"").Append(_wifi.IpAddress).Append('"');
            int rssi = 0;
            try { rssi = MiniRover.Native.Board.WifiRssi(); } catch { }
            sb.Append(",\"rssi\":").Append(rssi.ToString());
            sb.Append(",\"memoryKb\":{\"internal\":").Append((MiniRover.Native.Board.FreeMemory(MiniRover.Native.Board.MemInternalFree) / 1024).ToString())
              .Append(",\"internalBlock\":").Append((MiniRover.Native.Board.FreeMemory(MiniRover.Native.Board.MemInternalLargest) / 1024).ToString())
              .Append(",\"internalMinimum\":").Append((MiniRover.Native.Board.FreeMemory(MiniRover.Native.Board.MemInternalMinimumEver) / 1024).ToString())
              .Append(",\"psram\":").Append((MiniRover.Native.Board.FreeMemory(MiniRover.Native.Board.MemPsramFree) / 1024).ToString())
              .Append(",\"psramBlock\":").Append((MiniRover.Native.Board.FreeMemory(MiniRover.Native.Board.MemPsramLargest) / 1024).ToString()).Append('}');
            sb.Append(",\"camera\":{\"sensor\":").Append(_car.CameraSensor.ToString());
            if (_car.CameraSensor > 0)
            {
                sb.Append(",\"fpsTenths\":").Append(MiniRover.Native.Camera.GetStat(MiniRover.Native.Camera.StatFpsTenths).ToString())
                  .Append(",\"framesSent\":").Append(MiniRover.Native.Camera.GetStat(MiniRover.Native.Camera.StatFramesSent).ToString())
                  .Append(",\"lastFrameBytes\":").Append(MiniRover.Native.Camera.GetStat(MiniRover.Native.Camera.StatLastFrameBytes).ToString())
                  .Append(",\"captureErrors\":").Append(MiniRover.Native.Camera.GetStat(MiniRover.Native.Camera.StatCaptureErrors).ToString())
                  .Append(",\"encodeMs\":").Append(MiniRover.Native.Camera.GetStat(MiniRover.Native.Camera.StatEncodeMs).ToString());
            }
            sb.Append('}');
            sb.Append(",\"managedBytesInUse\":").Append(System.GC.GetTotalMemory(false).ToString());
            string faults = _car.Faults;
            if (_http.StartError.Length > 0) faults += (faults.Length > 0 ? "; " : "") + _http.StartError;
sb.Append(",\"faults\":\"").Append(JsonEscape(faults)).Append('"');
            if (Link != null)
            {
                sb.Append(",\"link\":{\"status\":\"").Append(JsonEscape(Link.Status)).Append("\",\"authenticated\":").Append(B(Link.Authenticated))
                  .Append(",\"sessions\":").Append(Link.Sessions.ToString())
                  .Append(",\"controlMessages\":").Append(Link.ControlMessages.ToString())
                  .Append(",\"driveMessages\":").Append(Link.DriveMessages.ToString())
                  .Append(",\"telemetrySent\":").Append(Link.TelemetrySent.ToString())
                  .Append(",\"sendFailures\":").Append(Link.SendFailures.ToString())
                  .Append(",\"txQueuedBytes\":").Append(Link.TxQueuedBytes.ToString())
                  .Append(",\"sctpRetransmits\":").Append(Link.Stat(SpawnDev.nanoFramework.WebRTC.PeerConnection.StatSctpRetransmits).ToString())
                  .Append(",\"sctpAbandoned\":").Append(Link.Stat(SpawnDev.nanoFramework.WebRTC.PeerConnection.StatSctpAbandoned).ToString())
                  .Append(",\"sctpForwardTsn\":").Append(Link.Stat(SpawnDev.nanoFramework.WebRTC.PeerConnection.StatSctpForwardTsn).ToString())
                  .Append(",\"peerForwardTsn\":").Append(Link.Stat(SpawnDev.nanoFramework.WebRTC.PeerConnection.StatSctpPeerForwardTsn).ToString())
                  .Append(",\"sctpUnprotected\":").Append(Link.Stat(SpawnDev.nanoFramework.WebRTC.PeerConnection.StatSctpUnprotected).ToString())
                  .Append(",\"testDropped\":").Append(Link.Stat(SpawnDev.nanoFramework.WebRTC.PeerConnection.StatTestDropped).ToString())
                  .Append(",\"udpSendErrors\":").Append(SpawnDev.nanoFramework.WebRTC.PeerConnection.GetStat(-1, SpawnDev.nanoFramework.WebRTC.PeerConnection.StatUdpSendErrors).ToString())
                  .Append(",\"udpSendRetries\":").Append(SpawnDev.nanoFramework.WebRTC.PeerConnection.GetStat(-1, SpawnDev.nanoFramework.WebRTC.PeerConnection.StatUdpSendRetries).ToString())
                  .Append(",\"videoFramesDropped\":").Append(Link.VideoFramesDropped.ToString()).Append('}');
            }

            BatteryService b = _car.Battery;
            if (b != null)
            {
                sb.Append(",\"battery\":{\"volts\":").Append(b.Volts.ToString("F2"))
                  .Append(",\"lastVolts\":").Append(b.LastVolts.ToString("F2"))
                  .Append(",\"raw\":").Append(b.LastRaw.ToString())
                  .Append(",\"underLoad\":").Append(B(b.LastSampleUnderLoad))
                  .Append(",\"noBattery\":").Append(B(b.NoBattery))
                  .Append(",\"level\":\"").Append(BatteryLevelNames.Name(b.Level)).Append("\"}");
            }
            if (_car.Line != null) sb.Append(",\"line\":").Append(SafeInt(() => _car.Line.Read()).ToString());
            if (_car.Light != null) sb.Append(",\"light\":").Append(SafeInt(() => _car.Light.ReadRaw()).ToString());
            if (_car.Servos != null)
            {
                sb.Append(",\"pan\":").Append(_car.Servos.Pan.ToString("F1"))
                  .Append(",\"tilt\":").Append(_car.Servos.Tilt.ToString("F1"));
            }
            if (_car.Drive != null)
            {
                sb.Append(",\"moving\":").Append(B(_car.Drive.Moving))
                  .Append(",\"deadmanStops\":").Append(_car.Drive.DeadmanStops.ToString());
            }
            if (_car.LastIrMs != 0)
            {
                sb.Append(",\"ir\":{\"code\":\"0x").Append(_car.LastIrCode.ToString("X8"))
                  .Append("\",\"ageS\":").Append(((Environment.TickCount64 - _car.LastIrMs) / 1000).ToString()).Append('}');
            }
            Settings s = _car.Settings;
            sb.Append(",\"settings\":{\"pan.trim\":").Append(s.PanTrim.ToString("F1"))
              .Append(",\"tilt.trim\":").Append(s.TiltTrim.ToString("F1"))
              .Append(",\"battery.coef\":").Append(s.BatteryCoefficient.ToString("F3"))
              .Append(",\"drive.limit\":").Append(s.SpeedLimit.ToString("F2"))
              .Append(",\"motor.minduty\":").Append(s.MotorMinimumDuty.ToString())
              .Append(",\"led.brightness\":").Append(s.LedBrightness.ToString()).Append('}');
            sb.Append('}');
            return sb.ToString();
        }

        delegate int IntFunc();

        static int SafeInt(IntFunc f)
        {
            try { return f(); }
            catch { return -1; }
        }

        static string B(bool v) => v ? "true" : "false";

        static string JsonEscape(string s)
        {
            var sb = new StringBuilder();
            foreach (char ch in s)
            {
                if (ch == '"' || ch == '\\') sb.Append('\\');
                if (ch >= ' ') sb.Append(ch);
            }
            return sb.ToString();
        }

        static void Ok(Socket c) => HttpServer.SendText(c, 200, "application/json", "{\"ok\":true}");

        static void Need(object part, string name)
        {
            if (part == null) throw new InvalidOperationException(name + " not available (see /status faults)");
        }

        static byte[] EyesByName(string name)
        {
            switch (name)
            {
                case "closed": return Eyes.Closed;
                case "happy": return Eyes.Happy;
                case "setup": return Eyes.Setup;
                case "dead": return Eyes.Dead;
                default: return Eyes.Open;
            }
        }

        static byte[] ParseHex(string hex)
        {
            if (hex.Length != LedMatrix.FrameBytes * 2) throw new ArgumentException("hex must be 32 hex digits");
            var frame = new byte[LedMatrix.FrameBytes];
            for (int i = 0; i < frame.Length; i++)
            {
                frame[i] = (byte)Convert.ToInt32(hex.Substring(i * 2, 2), 16);
            }
            return frame;
        }
    }
}
