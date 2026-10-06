using System;
using System.Security.Cryptography;
using System.Text;

namespace MiniRover.Protocol
{
    /// <summary>
    /// The car <-> app link over WebRTC data channels. Compiled into BOTH the nanoFramework firmware and the .NET
    /// clients, so the wire format cannot drift: constants and byte[] code only (no Span, LINQ or generics).
    ///
    /// Signaling: the car announces an offer on a WebTorrent tracker in a room whose 20-byte info_hash is
    /// <see cref="DeriveRoomId"/>(pairing key). The tracker sees only the derived id, never the key.
    ///
    /// Channels (opened by the car): "ctrl" reliable + ordered (commands, telemetry), "video" unordered with no
    /// retransmits (JPEG frames: a late frame is useless).
    ///
    /// Handshake on ctrl, before the car accepts any command:
    ///   car    -> Hello      [type][proto][carNonce 16][nameLen][name UTF-8]
    ///   client -> Auth       [type][clientProof 32][clientNonce 16]   clientProof = HMAC(key, "client" + carNonce)
    ///   car    -> AuthResult [type][ok][carProof 32]                    carProof   = HMAC(key, "car" + clientNonce)
    /// Both sides prove they hold the pairing key against the other side's fresh nonce; nothing secret crosses the
    /// wire, and a recorded handshake cannot be replayed.
    /// </summary>
    public static class CarLink
    {
        public const int Version = 1;

        public const string ControlChannel = "ctrl";
        public const string VideoChannel = "video";

        public const int KeyBytes = 20;
        public const int NonceBytes = 16;
        public const int ProofBytes = 32;

        /// <summary>Default WebTorrent tracker (SpawnDev hub); configurable on both ends.</summary>
        public const string DefaultTrackerUrl = "wss://hub.spawndev.com:44365/announce";
        /// <summary>Default STUN servers, space separated.</summary>
        public const string DefaultIceServers = "stun:hub.spawndev.com:3478 stun:stun.l.google.com:19302";

        // car -> client
        public const byte MsgHello = 0x01;
        public const byte MsgAuthResult = 0x03;
        public const byte MsgTelemetry = 0x10;
        public const byte MsgAck = 0x11;
        public const byte MsgText = 0x12;
        public const byte MsgPong = 0x14;       // [token u32] echoed from MsgPing
        public const byte MsgSettings = 0x13;   // [UTF-8 "key=value" lines]: every setting + read-only "info.*" keys

        // client -> car
        public const byte MsgAuth = 0x02;
        public const byte MsgDrive = 0x20;      // [seq u16][left i8 %][right i8 %][holdMs u16]
        public const byte MsgServo = 0x21;      // [pan i16 deg*10][tilt i16 deg*10]
        public const byte MsgLeds = 0x22;       // [r][g][b]
        public const byte MsgBuzzer = 0x23;     // [hz u16][ms u16]
        public const byte MsgEyes = 0x24;       // [16-byte matrix frame]
        public const byte MsgVideo = 0x25;      // [enable u8][frame size u8][jpeg quality u8][max fps u8]
        public const byte MsgSetting = 0x26;    // [UTF-8 "key=value"]
        public const byte MsgStop = 0x27;       // stop all motors now
        public const byte MsgSettingsRequest = 0x28; // the car answers with MsgSettings
        public const byte MsgPing = 0x2A;       // [token u32]: the car answers MsgPong with the same token (link round trip)
        public const byte MsgMotorTest = 0x29;  // [motor u8 0..3][percent i8][holdMs u16]: one wheel (setup / calibration)

        public const int TelemetryBytes = 22;
        public const int DriveBytes = 7;

        /// <summary>Tracker room id for a pairing key.</summary>
        public static byte[] DeriveRoomId(byte[] key)
        {
            byte[] mac = Hmac(key, Encoding.UTF8.GetBytes("minirover/room/v1"));
            byte[] room = new byte[20];
            Array.Copy(mac, 0, room, 0, 20);
            return room;
        }

        public static byte[] ClientProof(byte[] key, byte[] carNonce) => Hmac(key, Concat(Encoding.UTF8.GetBytes("client"), carNonce));

        public static byte[] CarProof(byte[] key, byte[] clientNonce) => Hmac(key, Concat(Encoding.UTF8.GetBytes("car"), clientNonce));

        // HashData, never `using (new HMACSHA256(key))`: nanoFramework's HMACSHA256 keeps the CALLER's key array (no
        // copy) and Dispose() clears it, so the first HMAC wiped the car's pairing key and every later proof was
        // computed with an all-zero key (found on the real car; nanoFramework.System.Security.Cryptography 2.0.0-preview.12).
        static byte[] Hmac(byte[] key, byte[] data) => HMACSHA256.HashData(key, data);

        /// <summary>Constant-time comparison (a byte-by-byte early exit would leak how much of a proof was right).</summary>
        public static bool ProofEquals(byte[] a, int aOffset, byte[] b)
        {
            if (a == null || b == null || a.Length - aOffset < b.Length) return false;
            int diff = 0;
            for (int i = 0; i < b.Length; i++) diff |= a[aOffset + i] ^ b[i];
            return diff == 0;
        }

        static byte[] Concat(byte[] a, byte[] b)
        {
            byte[] r = new byte[a.Length + b.Length];
            Array.Copy(a, 0, r, 0, a.Length);
            Array.Copy(b, 0, r, a.Length, b.Length);
            return r;
        }

        // ---- Hello / Auth / AuthResult ----

        public static byte[] EncodeHello(byte[] carNonce, string name)
        {
            byte[] n = Encoding.UTF8.GetBytes(name ?? "");
            int len = n.Length > 32 ? 32 : n.Length;
            byte[] b = new byte[3 + NonceBytes + len];
            b[0] = MsgHello;
            b[1] = (byte)Version;
            Array.Copy(carNonce, 0, b, 2, NonceBytes);
            b[2 + NonceBytes] = (byte)len;
            Array.Copy(n, 0, b, 3 + NonceBytes, len);
            return b;
        }

        public static byte[] EncodeAuth(byte[] clientProof, byte[] clientNonce)
        {
            byte[] b = new byte[1 + ProofBytes + NonceBytes];
            b[0] = MsgAuth;
            Array.Copy(clientProof, 0, b, 1, ProofBytes);
            Array.Copy(clientNonce, 0, b, 1 + ProofBytes, NonceBytes);
            return b;
        }

        public static byte[] EncodeAuthResult(bool ok, byte[] carProof)
        {
            byte[] b = new byte[2 + ProofBytes];
            b[0] = MsgAuthResult;
            b[1] = (byte)(ok ? 1 : 0);
            if (ok) Array.Copy(carProof, 0, b, 2, ProofBytes);
            return b;
        }

        // ---- Drive ----

        public static byte[] EncodeDrive(int seq, int leftPercent, int rightPercent, int holdMs)
        {
            byte[] b = new byte[DriveBytes];
            b[0] = MsgDrive;
            b[1] = (byte)seq;
            b[2] = (byte)(seq >> 8);
            b[3] = (byte)(sbyte)Clamp(leftPercent, -100, 100);
            b[4] = (byte)(sbyte)Clamp(rightPercent, -100, 100);
            int hold = Clamp(holdMs, 0, 65535);
            b[5] = (byte)hold;
            b[6] = (byte)(hold >> 8);
            return b;
        }

        public static bool TryDecodeDrive(byte[] b, int length, out int seq, out int leftPercent, out int rightPercent, out int holdMs)
            => TryDecodeDrive(b, 0, length, out seq, out leftPercent, out rightPercent, out holdMs);

        /// <summary>Decodes a Drive message starting at <paramref name="offset"/> (no copy: the car decodes in its receive buffer).</summary>
        public static bool TryDecodeDrive(byte[] b, int offset, int length, out int seq, out int leftPercent, out int rightPercent, out int holdMs)
        {
            seq = leftPercent = rightPercent = holdMs = 0;
            if (b == null || offset < 0 || length < DriveBytes || b.Length - offset < DriveBytes || b[offset] != MsgDrive) return false;
            seq = b[offset + 1] | (b[offset + 2] << 8);
            leftPercent = Clamp((sbyte)b[offset + 3], -100, 100);
            rightPercent = Clamp((sbyte)b[offset + 4], -100, 100);
            holdMs = b[offset + 5] | (b[offset + 6] << 8);
            return true;
        }

        /// <summary>True if sequence number <paramref name="seq"/> is newer than <paramref name="last"/> (16-bit wrap).</summary>
        public static bool IsNewer(int seq, int last)
        {
            int d = (seq - last) & 0xFFFF;
            return d != 0 && d < 0x8000;
        }

        // ---- Text / Setting / Video ----

        /// <summary>[type][UTF-8 text], capped so a message always fits one SCTP chunk.</summary>
        public static byte[] EncodeText(string text) => EncodeUtf8(MsgText, text);

        /// <summary>A "key=value" setting for the car (it clamps values to safe ranges and answers with a Text).</summary>
        public static byte[] EncodeSetting(string keyValue) => EncodeUtf8(MsgSetting, keyValue);

        /// <summary>Video on/off. 0 for size, quality or fps keeps the car's saved setting.</summary>
        public static byte[] EncodeVideo(bool enable, int frameSize, int jpegQuality, int maxFps)
        {
            return new byte[]
            {
                MsgVideo, (byte)(enable ? 1 : 0),
                (byte)Clamp(frameSize, 0, 255), (byte)Clamp(jpegQuality, 0, 63), (byte)Clamp(maxFps, 0, 30),
            };
        }

        public const int MaxTextBytes = 512;
        public const int MaxSettingsBytes = 2048;

        /// <summary>The car's settings reply: "key=value" lines.</summary>
        public static byte[] EncodeSettings(string lines)
        {
            byte[] t = Encoding.UTF8.GetBytes(lines ?? "");
            int len = t.Length > MaxSettingsBytes ? MaxSettingsBytes : t.Length;
            byte[] b = new byte[1 + len];
            b[0] = MsgSettings;
            Array.Copy(t, 0, b, 1, len);
            return b;
        }

        public static byte[] EncodePing(uint token) => EncodeToken(MsgPing, token);
        public static byte[] EncodePong(uint token) => EncodeToken(MsgPong, token);

        /// <summary>Reads the token of a Ping or Pong (type checked by the caller).</summary>
        public static bool TryDecodeToken(byte[] b, int offset, int length, out uint token)
        {
            token = 0;
            if (b == null || offset < 0 || length < 5 || b.Length - offset < 5) return false;
            token = (uint)(b[offset + 1] | (b[offset + 2] << 8) | (b[offset + 3] << 16) | (b[offset + 4] << 24));
            return true;
        }

        static byte[] EncodeToken(byte type, uint token)
        {
            return new byte[] { type, (byte)token, (byte)(token >> 8), (byte)(token >> 16), (byte)(token >> 24) };
        }

        public static byte[] EncodeMotorTest(int motor, int percent, int holdMs)
        {
            int hold = Clamp(holdMs, 0, 65535);
            return new byte[] { MsgMotorTest, (byte)Clamp(motor, 0, 3), (byte)(sbyte)Clamp(percent, -100, 100), (byte)hold, (byte)(hold >> 8) };
        }

        public static bool TryDecodeMotorTest(byte[] b, int offset, int length, out int motor, out int percent, out int holdMs)
        {
            motor = percent = holdMs = 0;
            if (b == null || offset < 0 || length < 5 || b.Length - offset < 5 || b[offset] != MsgMotorTest) return false;
            motor = b[offset + 1];
            if (motor > 3) return false;
            percent = Clamp((sbyte)b[offset + 2], -100, 100);
            holdMs = b[offset + 3] | (b[offset + 4] << 8);
            return true;
        }

        static byte[] EncodeUtf8(byte type, string text)
        {
            byte[] t = Encoding.UTF8.GetBytes(text ?? "");
            int len = t.Length > MaxTextBytes ? MaxTextBytes : t.Length;
            byte[] b = new byte[1 + len];
            b[0] = type;
            Array.Copy(t, 0, b, 1, len);
            return b;
        }

        // ---- Telemetry ----

        /// <summary>Car state snapshot, sent ~5 times a second on ctrl.</summary>
        public sealed class Telemetry
        {
            public int Seq;
            public int BatteryMillivolts;
            public int BatteryLevel;      // 0 ok, 1 low, 2 critical
            public bool Moving;
            public bool NoBattery;
            public bool Authenticated;
            public int Light;             // raw 12-bit
            public int Line;              // 3 bits: 1 left, 2 middle, 4 right
            public int SonarCm;           // -1 = no reading
            public int PanTenths;
            public int TiltTenths;
            public int Rssi;              // dBm, 0 = unknown
            public int VideoFps;
            public int FreeHeapKb;
        }

        public static byte[] EncodeTelemetry(Telemetry t)
        {
            byte[] b = new byte[TelemetryBytes];
            b[0] = MsgTelemetry;
            PutU16(b, 1, t.Seq);
            PutU16(b, 3, Clamp(t.BatteryMillivolts, 0, 65535));
            b[5] = (byte)t.BatteryLevel;
            b[6] = (byte)((t.Moving ? 1 : 0) | (t.NoBattery ? 2 : 0) | (t.Authenticated ? 4 : 0));
            PutU16(b, 7, Clamp(t.Light, 0, 65535));
            b[9] = (byte)t.Line;
            PutU16(b, 10, t.SonarCm < 0 ? 0xFFFF : Clamp(t.SonarCm, 0, 0xFFFE));
            PutU16(b, 12, t.PanTenths & 0xFFFF);
            PutU16(b, 14, t.TiltTenths & 0xFFFF);
            b[16] = (byte)(sbyte)Clamp(t.Rssi, -128, 127);
            b[17] = (byte)Clamp(t.VideoFps, 0, 255);
            PutU16(b, 18, Clamp(t.FreeHeapKb, 0, 65535));
            // 20..21 reserved
            return b;
        }

        public static bool TryDecodeTelemetry(byte[] b, int length, Telemetry t)
        {
            if (b == null || length < TelemetryBytes || b[0] != MsgTelemetry) return false;
            t.Seq = GetU16(b, 1);
            t.BatteryMillivolts = GetU16(b, 3);
            t.BatteryLevel = b[5];
            t.Moving = (b[6] & 1) != 0;
            t.NoBattery = (b[6] & 2) != 0;
            t.Authenticated = (b[6] & 4) != 0;
            t.Light = GetU16(b, 7);
            t.Line = b[9];
            int sonar = GetU16(b, 10);
            t.SonarCm = sonar == 0xFFFF ? -1 : sonar;
            t.PanTenths = (short)GetU16(b, 12);
            t.TiltTenths = (short)GetU16(b, 14);
            t.Rssi = (sbyte)b[16];
            t.VideoFps = b[17];
            t.FreeHeapKb = GetU16(b, 18);
            return true;
        }

        // ---- helpers ----

        static void PutU16(byte[] b, int i, int v)
        {
            b[i] = (byte)v;
            b[i + 1] = (byte)(v >> 8);
        }

        static int GetU16(byte[] b, int i) => b[i] | (b[i + 1] << 8);

        static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
    }
}
