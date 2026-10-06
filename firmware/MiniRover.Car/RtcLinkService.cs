using System;
using System.Text;
using System.Threading;
using MiniRover.Protocol;
using SpawnDev.nanoFramework.WebRTC;

namespace MiniRover.Car
{
    /// <summary>
    /// The car's WebRTC link to the app (protocol: src/MiniRover.Protocol/CarLink.cs). One session at a time, on its
    /// own thread: offer into the paired room on the tracker, take the first answer, open "ctrl" + "video", run the
    /// mutual key handshake, then accept commands and stream telemetry. Any failure or disconnect stops the motors and
    /// starts a fresh session (the deadman stops them anyway if commands stop arriving).
    /// </summary>
    public sealed class RtcLinkService
    {
        const int AnnounceTries = 12;          // x 5 s, then a fresh peer connection (fresh ICE candidates)
        const int AnswerWaitMs = 5000;
        const int ConnectTimeoutMs = 30000;    // the ESP32 answers ICE checks late (SpawnWear: ~20 s worst case)
        const int ChannelOpenTimeoutMs = 10000;
        const int TelemetryPeriodMs = 200;
        const int MaxMessagesPerPass = 16;

        readonly Car _car;
        readonly Settings _settings;
        readonly string _name;
        readonly Random _rng = new Random(); // hardware RNG on ESP32
        readonly byte[] _rx = new byte[PeerConnection.ReceiveHeaderBytes + 2048];

        int _handle = -1;
        int _ctrlSid = -1;
        int _videoSid = -1;
        byte[] _key;
        byte[] _carNonce;
        int _lastDriveSeq = -1;
        bool _pendingDrive;
        int _pendingLeft, _pendingRight, _pendingHold;
        int _telemetrySeq;

        public string Status { get; private set; } = "idle";

        public delegate void LinkEvent();
        /// <summary>Called (on the link thread) each time an app proves the pairing key.</summary>
        public LinkEvent OnAppConnected;
        /// <summary>Control messages received from the app, and how many were drive commands (this session).</summary>
        public int ControlMessages { get; private set; }
        public int DriveMessages { get; private set; }
        /// <summary>Telemetry messages queued this session, and sends refused because the transmit queue was full.</summary>
        public int TelemetrySent { get; private set; }
        public int SendFailures { get; private set; }
        public bool Authenticated { get; private set; }
        public int Sessions { get; private set; }

        /// <summary>Handle and video stream id for the native camera task (MiniRover.Native), -1 when not streaming.</summary>
        public int VideoHandle => Authenticated ? _handle : -1;
        public int VideoStreamId => Authenticated ? _videoSid : -1;

        /// <summary>Camera frames the link skipped because the previous one was still being sent (latest wins).</summary>
        public int Stat(int id) => _handle >= 0 ? PeerConnection.GetStat(_handle, id) : 0;

        public int TxQueuedBytes => _handle >= 0 ? PeerConnection.GetStat(_handle, PeerConnection.StatTxQueuedBytes) : 0;

        public int VideoFramesDropped => _handle >= 0 ? PeerConnection.GetStat(_handle, PeerConnection.StatFramesDropped) : 0;

        public RtcLinkService(Car car, Settings settings, string name)
        {
            _car = car;
            _settings = settings;
            _name = name;
        }

        public void Start()
        {
            new Thread(Run).Start();
        }

        void Run()
        {
            while (true)
            {
                try
                {
                    Session();
                }
                catch (Exception ex)
                {
                    Status = "error: " + ex.Message;
                    System.Diagnostics.Debug.WriteLine("Link: " + Status + " [" + Program.MemoryText() + "]");
                }
                finally
                {
                    EndSession();
                }
                Thread.Sleep(3000);
            }
        }

        void Session()
        {
            _key = HexToBytes(_settings.RoomKeyHex);
            if (_key == null || _key.Length != CarLink.KeyBytes)
            {
                Status = "not paired";
                Thread.Sleep(10000);
                return;
            }
            Sessions++;
            byte[] room = CarLink.DeriveRoomId(_key);
            byte[] peerId = RandomId("-MR0001-");
            byte[] offerId = RandomId("o");

            _handle = PeerConnection.Create(CarLink.DefaultIceServers);
            if (_handle < 0) throw new Exception("no peer connection slot / out of memory");
            PeerConnection.CreateOffer(_handle);
            string offer = WaitForLocalSdp(10000);
            if (offer == null) throw new Exception("offer SDP not generated");

            string answer = null;
            using (var tracker = new TrackerSignaling(CarLink.DefaultTrackerUrl, RootCertificates.IsrgRootX1))
            {
                Status = "connecting to tracker";
                if (!tracker.Connect()) throw new Exception("tracker: " + tracker.LastError);
                for (int i = 0; i < AnnounceTries && answer == null; i++)
                {
                    Status = "waiting for the app";
                    if (!tracker.AnnounceOffer(room, peerId, offerId, offer)) throw new Exception("announce failed: " + tracker.LastError);
                    string answerer;
                    answer = tracker.WaitForAnswer(offerId, AnswerWaitMs, out answerer);
                    if (!tracker.IsOpen) throw new Exception("tracker closed: " + tracker.LastError);
                }
            } // tracker socket closed BEFORE DTLS: peak memory (SpawnWear)
            if (answer == null) return; // nobody came: fresh peer connection next session

            Status = "connecting";
            PeerConnection.SetRemoteDescription(_handle, answer);
            long deadline = Environment.TickCount64 + ConnectTimeoutMs;
            while (PeerConnection.GetState(_handle) != PeerConnection.StateCompleted)
            {
                int st = PeerConnection.GetState(_handle);
                if (st == PeerConnection.StateFailed || st == PeerConnection.StateClosed || Environment.TickCount64 > deadline)
                {
                    throw new Exception("WebRTC connect failed (state " + st + ")");
                }
                Thread.Sleep(50);
            }

            // ICE "completed" comes before the SCTP association; Open waits for it.
            _ctrlSid = DataChannel.Open(_handle, CarLink.ControlChannel, PeerConnection.ChannelReliable, 0, ChannelOpenTimeoutMs);
            _videoSid = DataChannel.Open(_handle, CarLink.VideoChannel, PeerConnection.ChannelPartialRetransmitUnordered, 0, ChannelOpenTimeoutMs);
            if (_ctrlSid < 0 || _videoSid < 0) throw new Exception("data channels not opened");

            _carNonce = new byte[CarLink.NonceBytes];
            _rng.NextBytes(_carNonce);
            Send(CarLink.EncodeHello(_carNonce, _name));
            Status = "connected, waiting for app authentication";
            System.Diagnostics.Debug.WriteLine("Link: connected (ctrl sid " + _ctrlSid + ", video sid " + _videoSid + ")");

            long nextTelemetry = 0;
            while (PeerConnection.GetState(_handle) == PeerConnection.StateCompleted)
            {
                // Drain a bounded batch, then always fall through to telemetry: with video running the car handled only
                // ~12 drive messages/s, the app sent 20/s, and an unbounded drain never reached the telemetry send.
                // Drive commands are coalesced: only the newest one in the batch moves the motors.
                int n, batch = 0;
                _pendingDrive = false;
                while (batch < MaxMessagesPerPass && (n = PeerConnection.TryReceive(_handle, _rx)) > 0)
                {
                    batch++;
                    int sid = _rx[0] | (_rx[1] << 8);
                    if (sid != _ctrlSid) continue; // the app never sends on video
                    ControlMessages++;
                    Handle(n - PeerConnection.ReceiveHeaderBytes);
                }
                if (_pendingDrive && _car.Drive != null)
                {
                    _car.Drive.Drive(_pendingLeft / 100.0, _pendingRight / 100.0, _pendingHold);
                }
                if (Authenticated && Environment.TickCount64 >= nextTelemetry)
                {
                    nextTelemetry = Environment.TickCount64 + TelemetryPeriodMs;
                    Send(CarLink.EncodeTelemetry(BuildTelemetry()));
                    TelemetrySent++;
                }
                Thread.Sleep(10);
            }
            Status = "disconnected";
        }

        void Handle(int len)
        {
            const int o = PeerConnection.ReceiveHeaderBytes;
            if (len < 1) return;
            byte type = _rx[o];
            if (type == CarLink.MsgAuth)
            {
                bool ok = len >= 1 + CarLink.ProofBytes + CarLink.NonceBytes &&
                          CarLink.ProofEquals(_rx, o + 1, CarLink.ClientProof(_key, _carNonce));
                byte[] proof = null;
                if (ok)
                {
                    byte[] clientNonce = new byte[CarLink.NonceBytes];
                    Array.Copy(_rx, o + 1 + CarLink.ProofBytes, clientNonce, 0, CarLink.NonceBytes);
                    proof = CarLink.CarProof(_key, clientNonce);
                }
                Authenticated = ok;
                if (ok && OnAppConnected != null)
                {
                    try { OnAppConnected(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine("Link: OnAppConnected: " + ex.Message); }
                }
                Send(CarLink.EncodeAuthResult(ok, proof));
                Status = ok ? "connected" : "app failed authentication";
                System.Diagnostics.Debug.WriteLine("Link: authentication " + (ok ? "OK" : "FAILED (" + len + " bytes)"));
                return;
            }
            if (!Authenticated) return; // nothing else counts until the app has proved it holds the key

            switch (type)
            {
                case CarLink.MsgDrive:
                    DriveMessages++;
                    int seq, l, r, hold;
                    if (CarLink.TryDecodeDrive(_rx, o, len, out seq, out l, out r, out hold) &&
                        (_lastDriveSeq < 0 || CarLink.IsNewer(seq, _lastDriveSeq)))
                    {
                        _lastDriveSeq = seq; // stale or duplicated frames never move the car backwards in time
                        _pendingDrive = true; // applied once after the batch (the newest wins)
                        _pendingLeft = l;
                        _pendingRight = r;
                        _pendingHold = hold;
                    }
                    break;
                case CarLink.MsgStop:
                    _pendingDrive = false; // a Stop after a Drive in the same batch wins
                    if (_car.Drive != null) _car.Drive.Stop();
                    break;
                case CarLink.MsgServo:
                    if (len >= 5 && _car.Servos != null && _car.HasBattery)
                    {
                        _car.Servos.SetPan((short)(_rx[o + 1] | (_rx[o + 2] << 8)) / 10.0);
                        _car.Servos.SetTilt((short)(_rx[o + 3] | (_rx[o + 4] << 8)) / 10.0);
                    }
                    break;
                case CarLink.MsgLeds:
                    if (len >= 4 && _car.Lights != null) _car.Lights.Set(LightsService.ModeSolid, _rx[o + 1], _rx[o + 2], _rx[o + 3], 0);
                    break;
                case CarLink.MsgLights:
                    // [mode][r][g][b][param]
                    if (len >= 6 && _car.Lights != null) _car.Lights.Set(_rx[o + 1], _rx[o + 2], _rx[o + 3], _rx[o + 4], _rx[o + 5]);
                    break;
                case CarLink.MsgBuzzer:
                    if (len >= 5 && _car.Buzzer != null)
                    {
                        int hz = _rx[o + 1] | (_rx[o + 2] << 8);
                        int ms = _rx[o + 3] | (_rx[o + 4] << 8);
                        new Thread(() => _car.Buzzer.Beep(hz, ms > 2000 ? 2000 : ms)).Start();
                    }
                    break;
                case CarLink.MsgVideo:
                    // [enable][frame size][jpeg quality][max fps]; 0 for size/quality/fps keeps the car's setting.
                    if (len >= 5 && _car.CameraSensor > 0)
                    {
                        bool on = _rx[o + 1] != 0;
                        int size = _rx[o + 2] != 0 ? _rx[o + 2] : _car.Settings.CameraSize;
                        int quality = _rx[o + 3] != 0 ? _rx[o + 3] : _car.Settings.CameraQuality;
                        int fps = _rx[o + 4] != 0 ? _rx[o + 4] : _car.Settings.CameraMaxFps;
                        MiniRover.Native.Camera.Configure(size, quality);
                        if (on) MiniRover.Native.Camera.Stream(_handle, _videoSid, fps);
                        else MiniRover.Native.Camera.Stream(-1, -1, 1);
                    }
                    break;
                case CarLink.MsgPing:
                    {
                        uint token;
                        if (CarLink.TryDecodeToken(_rx, o, len, out token)) Send(CarLink.EncodePong(token));
                    }
                    break;
                case CarLink.MsgSettingsRequest:
                    SendSettings();
                    break;
                case CarLink.MsgMotorTest:
                    {
                        int motor, pct, mhold;
                        if (_car.Drive != null && CarLink.TryDecodeMotorTest(_rx, o, len, out motor, out pct, out mhold))
                        {
                            _pendingDrive = false; // a wheel test replaces any drive command in this batch
                            _car.Drive.DriveMotor(motor, pct / 100.0, mhold); // same deadman / hold limits as driving
                        }
                    }
                    break;
                case CarLink.MsgSetting:
                    {
                        string kv = Encoding.UTF8.GetString(_rx, o + 1, len - 1);
                        if (kv.StartsWith("test.loss="))
                        {
                            // Link test hook (not a saved setting): drop this share (per mille) of outgoing datagrams to
                            // prove retransmission / FORWARD-TSN. Only over the authenticated link, this session only.
                            int permille = 0;
                            try { permille = int.Parse(kv.Substring(10)); } catch { }
                            PeerConnection.SetTestLoss(_handle, permille);
                            Send(CarLink.EncodeText("test loss " + permille + " per mille"));
                            break;
                        }
                        bool ok = _car.Settings.TryApply(kv);
                        if (ok)
                        {
                            _car.Settings.Save();
                            _car.ApplySettings();
                        }
                        Send(CarLink.EncodeText((ok ? "saved " : "refused ") + kv));
                        if (ok) SendSettings(); // the app shows what the car actually stored (values are clamped)
                    }
                    break;
                case CarLink.MsgEyes:
                    if (len >= 17 && _car.Matrix != null)
                    {
                        byte[] eyes = new byte[16];
                        Array.Copy(_rx, o + 1, eyes, 0, 16);
                        _car.Matrix.Show(eyes);
                    }
                    break;
            }
        }

        void SendSettings()
        {
            string info = "info.firmware=" + Program.FirmwareVersion + "\n"
                + "info.name=" + _name + "\n"
                + "info.camera=" + _car.CameraSensor + "\n"
                + "info.faults=" + _car.Faults + "\n"; // faults are joined with "; ", never line breaks
            Send(CarLink.EncodeSettings(_car.Settings.Describe() + info));
        }

        CarLink.Telemetry BuildTelemetry()
        {
            var t = new CarLink.Telemetry { Seq = _telemetrySeq++ & 0xFFFF, Authenticated = Authenticated, SonarCm = -1 };
            BatteryService b = _car.Battery;
            if (b != null)
            {
                t.BatteryMillivolts = (int)(b.Volts * 1000);
                t.BatteryLevel = (int)b.Level;
                t.NoBattery = b.NoBattery;
            }
            if (_car.Drive != null) t.Moving = _car.Drive.Moving;
            try { if (_car.Light != null) t.Light = _car.Light.ReadRaw(2); } catch { }
            try { if (_car.Line != null) t.Line = _car.Line.Read(); } catch { }
            if (_car.Servos != null)
            {
                t.PanTenths = (int)(_car.Servos.Pan * 10);
                t.TiltTenths = (int)(_car.Servos.Tilt * 10);
            }
            try { t.Rssi = MiniRover.Native.Board.WifiRssi(); } catch { } // so the driver sees the car going out of range
            if (_car.CameraSensor > 0) t.VideoFps = (MiniRover.Native.Camera.GetStat(MiniRover.Native.Camera.StatFpsTenths) + 5) / 10;
            t.FreeHeapKb = PeerConnection.GetStat(_handle, PeerConnection.StatFreeInternalBytes) / 1024;
            return t;
        }

        void Send(byte[] message)
        {
            if (_handle >= 0 && _ctrlSid >= 0 && PeerConnection.Send(_handle, _ctrlSid, message, message.Length) < 0) SendFailures++;
        }

        string WaitForLocalSdp(int timeoutMs)
        {
            long deadline = Environment.TickCount64 + timeoutMs;
            while (Environment.TickCount64 < deadline)
            {
                int len = PeerConnection.GetLocalSdpLength(_handle);
                if (len > 0)
                {
                    byte[] buf = new byte[len];
                    PeerConnection.GetLocalSdp(_handle, buf);
                    return Encoding.UTF8.GetString(buf, 0, len);
                }
                Thread.Sleep(50);
            }
            return null;
        }

        void EndSession()
        {
            bool wasAuthenticated = Authenticated;
            Authenticated = false;
            // Stop the camera task sending before the connection (and its handle) goes away.
            if (_car.CameraSensor > 0) { try { MiniRover.Native.Camera.Stream(-1, -1, 1); } catch { } }
            if (_car.Drive != null && wasAuthenticated) _car.Drive.Stop();
            if (_handle >= 0)
            {
                try { PeerConnection.Close(_handle); } catch { }
            }
            _handle = -1;
            _ctrlSid = _videoSid = -1;
            _lastDriveSeq = -1;
            ControlMessages = DriveMessages = TelemetrySent = SendFailures = 0;
        }

        /// <summary>A 20-byte tracker id: the prefix then random printable ASCII.</summary>
        byte[] RandomId(string prefix)
        {
            byte[] id = new byte[20];
            byte[] p = Encoding.UTF8.GetBytes(prefix);
            Array.Copy(p, 0, id, 0, p.Length);
            for (int i = p.Length; i < id.Length; i++) id[i] = (byte)('a' + _rng.Next(26));
            return id;
        }

        static byte[] HexToBytes(string hex)
        {
            if (hex == null || hex.Length % 2 != 0) return null;
            byte[] b = new byte[hex.Length / 2];
            for (int i = 0; i < b.Length; i++) b[i] = (byte)Convert.ToInt32(hex.Substring(i * 2, 2), 16);
            return b;
        }
    }
}
