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
        int _telemetrySeq;

        public string Status { get; private set; } = "idle";
        public bool Authenticated { get; private set; }
        public int Sessions { get; private set; }

        /// <summary>Handle and video stream id for the native camera task (MiniRover.Native), -1 when not streaming.</summary>
        public int VideoHandle => Authenticated ? _handle : -1;
        public int VideoStreamId => Authenticated ? _videoSid : -1;

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
                    System.Diagnostics.Debug.WriteLine("Link: " + Status);
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
                int n;
                while ((n = PeerConnection.TryReceive(_handle, _rx)) > 0)
                {
                    int sid = _rx[0] | (_rx[1] << 8);
                    if (sid != _ctrlSid) continue; // the app never sends on video
                    Handle(n - PeerConnection.ReceiveHeaderBytes);
                }
                if (Authenticated && Environment.TickCount64 >= nextTelemetry)
                {
                    nextTelemetry = Environment.TickCount64 + TelemetryPeriodMs;
                    Send(CarLink.EncodeTelemetry(BuildTelemetry()));
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
                Send(CarLink.EncodeAuthResult(ok, proof));
                Status = ok ? "connected" : "app failed authentication";
                System.Diagnostics.Debug.WriteLine("Link: authentication " + (ok ? "OK" : "FAILED (" + len + " bytes)"));
                return;
            }
            if (!Authenticated) return; // nothing else counts until the app has proved it holds the key

            switch (type)
            {
                case CarLink.MsgDrive:
                    byte[] frame = new byte[len];
                    Array.Copy(_rx, o, frame, 0, len);
                    int seq, l, r, hold;
                    if (_car.Drive != null && CarLink.TryDecodeDrive(frame, len, out seq, out l, out r, out hold) &&
                        (_lastDriveSeq < 0 || CarLink.IsNewer(seq, _lastDriveSeq)))
                    {
                        _lastDriveSeq = seq; // stale or duplicated frames never move the car backwards in time
                        _car.Drive.Drive(l / 100.0, r / 100.0, hold);
                    }
                    break;
                case CarLink.MsgStop:
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
                    if (len >= 4 && _car.Leds != null)
                    {
                        _car.Leds.Fill(_rx[o + 1], _rx[o + 2], _rx[o + 3]);
                        _car.Leds.Show();
                    }
                    break;
                case CarLink.MsgBuzzer:
                    if (len >= 5 && _car.Buzzer != null)
                    {
                        int hz = _rx[o + 1] | (_rx[o + 2] << 8);
                        int ms = _rx[o + 3] | (_rx[o + 4] << 8);
                        new Thread(() => _car.Buzzer.Beep(hz, ms > 2000 ? 2000 : ms)).Start();
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
            t.FreeHeapKb = PeerConnection.GetStat(_handle, PeerConnection.StatFreeInternalBytes) / 1024;
            return t;
        }

        void Send(byte[] message)
        {
            if (_handle >= 0 && _ctrlSid >= 0) PeerConnection.Send(_handle, _ctrlSid, message, message.Length);
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
            if (_car.Drive != null && wasAuthenticated) _car.Drive.Stop();
            if (_handle >= 0)
            {
                try { PeerConnection.Close(_handle); } catch { }
            }
            _handle = -1;
            _ctrlSid = _videoSid = -1;
            _lastDriveSeq = -1;
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
