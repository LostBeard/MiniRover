using System;
using System.Net.Sockets;
using System.Text;
using MiniRover.Car.Drivers;
using MiniRover.Protocol;
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
                    if (r.Get("mode") != null && _car.Lights != null)
                    {
                        _car.Lights.Set((int)r.GetDouble("mode", 1), red, green, blue, (int)r.GetDouble("param", 0));
                    }
                    else if (r.Get("i") != null)
                    {
                        // One LED for wiring tests: stop the patterns so they do not paint over it.
                        if (_car.Lights != null) _car.Lights.Set(LightsService.ModeIdentify, 0, 0, 0, 100 + (int)r.GetDouble("i", 0));
                    }
                    else if (_car.Lights != null) _car.Lights.Set(LightsService.ModeSolid, red, green, blue, 0);
                    Ok(c);
                    return;
                case "/matrix":
                    Need(_car.Face, "matrix");
                    if (r.Get("brightness") != null) _car.Matrix.SetBrightness((int)r.GetDouble("brightness", 8));
                    string hex = r.Get("hex");
                    string text = r.Get("text");
                    if (hex != null) _car.Face.ShowCustom(ParseHex(hex));
                    else if (text != null) _car.Face.Set(CarLink.FaceText, (int)r.GetDouble("passes", 1), text);
                    else
                    {
                        int mood = MoodByName(r.Get("eyes", "alive"));
                        _car.Face.Set(mood < 0 ? CarLink.FaceAlive : CarLink.FaceMood, mood, null);
                    }
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
            try
            {
                // WiFi as the radio sees it (play mode / setup mode diagnostics): mode, the access point's address in
                // esp-netif and in lwIP, its DHCP server, stations on it, the station's address.
                sb.Append(",\"net\":{\"mode\":").Append(MiniRover.Native.Board.FreeMemory(MiniRover.Native.Board.NetWifiMode).ToString())
                  .Append(",\"play\":").Append(B(_wifi.InPlayMode))
                  .Append(",\"apStations\":").Append(MiniRover.Native.Board.FreeMemory(MiniRover.Native.Board.NetApStations).ToString())
                  .Append(",\"apIp\":\"").Append(Ip(MiniRover.Native.Board.FreeMemory(MiniRover.Native.Board.NetApAddress)))
                  .Append("\",\"apLwipIp\":\"").Append(Ip(MiniRover.Native.Board.FreeMemory(MiniRover.Native.Board.NetApLwipAddress)))
                  .Append("\",\"apDhcpServer\":").Append(MiniRover.Native.Board.FreeMemory(MiniRover.Native.Board.NetApDhcpServer).ToString())
                  .Append(",\"staIp\":\"").Append(Ip(MiniRover.Native.Board.FreeMemory(MiniRover.Native.Board.NetStaAddress))).Append("\"}");
            }
            catch (Exception ex)
            {
                sb.Append(",\"netError\":\"").Append(JsonEscape(ex.Message)).Append('"');
            }
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
                  .Append(",\"videoFramesDropped\":").Append(Link.VideoFramesDropped.ToString())
                  .Append(",\"ice\":\"").Append(JsonEscape(Link.LastIce)).Append("\"}");
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
            if (_car.Lights != null) sb.Append(",\"lights\":{\"mode\":").Append(_car.Lights.Mode.ToString()).Append('}');
            if (_car.Face != null)
            {
                // "shown" is read back from the matrix chip: what the eyes really show, not what was asked for.
                string shown;
                try
                {
                    byte[] f = _car.Matrix.ReadFrame();
                    var hx = new StringBuilder(32);
                    for (int i = 0; i < f.Length; i++) hx.Append(f[i].ToString("x2"));
                    shown = hx.ToString();
                }
                catch (Exception ex) { shown = "error: " + ex.Message; }
                sb.Append(",\"face\":{\"mode\":").Append(_car.Face.Mode.ToString()).Append(",\"shown\":\"").Append(shown).Append("\"}");
            }
            if (_car.CameraSensor > 0)
            {
                // Live sensor control values (what the sensor runs now, set or default).
                sb.Append(",\"sensor\":{");
                bool first = true;
                for (int id = 1; id < Settings.SensorNames.Length; id++)
                {
                    int v = MiniRover.Native.Camera.GetControl(id);
                    if (v == int.MinValue) continue;
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append('"').Append(Settings.SensorNames[id]).Append("\":").Append(v.ToString());
                }
                sb.Append('}');
            }
            sb.Append(",\"bootVolts\":").Append(_car.BootVolts.ToString("F2"));
            sb.Append(",\"resetReason\":\"").Append(Program.ResetReasonName(MiniRover.Native.Board.ResetReason())).Append('"');
            sb.Append(",\"lastAbnormalReset\":\"").Append(JsonEscape(_car.Settings.LastAbnormalReset)).Append('"');
            sb.Append(",\"i2cRecovery\":\"").Append(JsonEscape(MiniRover.Car.Drivers.SharedI2c.RecoveryNote)).Append('"');
            // Every client setting, as the strings the app link reports (Settings.ValueOf), so tools can compare.
            sb.Append(",\"settings\":{");
            for (int i = 0; i < Settings.ClientKeys.Length; i++)
            {
                string key = Settings.ClientKeys[i];
                sb.Append(i == 0 ? "\"" : ",\"").Append(key).Append("\":\"").Append(s.ValueOf(key)).Append('"');
            }
            sb.Append('}');
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

        /// <summary>IPv4 in network byte order (as lwIP keeps it) to dotted text.</summary>
        static string Ip(int a) => (a & 0xFF) + "." + ((a >> 8) & 0xFF) + "." + ((a >> 16) & 0xFF) + "." + ((a >> 24) & 0xFF);

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

        /// <summary>An EyeArt mood by name, or -1 for "alive" (and anything unknown).</summary>
        static int MoodByName(string name)
        {
            switch (name)
            {
                case "open": return EyeArt.MoodOpen;
                case "happy": return EyeArt.MoodHappy;
                case "heart": return EyeArt.MoodHeart;
                case "sad": return EyeArt.MoodSad;
                case "angry": return EyeArt.MoodAngry;
                case "surprised": return EyeArt.MoodSurprised;
                case "sleepy": return EyeArt.MoodSleepy;
                default: return -1;
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
