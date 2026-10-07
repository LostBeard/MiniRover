using System;

namespace MiniRover.Protocol
{
    /// <summary>
    /// BLE setup protocol: WiFi provisioning and pairing from the MiniRover web app (Web Bluetooth).
    ///
    /// This file is compiled into BOTH the nanoFramework car firmware and the .NET client, so the wire format
    /// cannot drift. Keep it to constants and byte[] code (no Span, no LINQ, no generics) so it builds on
    /// nanoFramework.
    ///
    /// GATT layout (one service, nanoFramework advertises one service reliably):
    ///   Info    (read)          UTF-8 "key=value\n" lines, no secrets (name, firmware, state, ip, ...).
    ///   Control (write)         [opcode][payload] from the app.
    ///   Events  (notify)        [opcode][payload] from the car.
    ///
    /// Flow:
    ///   1. App connects, subscribes to Events, writes RequestCode. The car shows a 4-digit code on its LED eyes.
    ///   2. App writes SubmitCode with what the person typed. Proves they can see the car; 3 misses = new code.
    ///   3. Authorised: App may Scan, read the pairing key (PairingInfo event) and SetWifi.
    ///   4. SetWifi saves the network and reboots the car into station mode. If it connects, it keeps advertising
    ///      for <see cref="ConnectedAdvertiseSeconds"/> so the app can reconnect and read Info (state=connected, ip).
    ///      If it cannot connect it returns to setup mode and Info carries the failure reason.
    ///
    /// Paired apps (version 2): an app that holds the pairing key proves it instead of typing the code:
    ///   KeyHello -> KeyChallenge [nonce]; KeyProof [HMAC(key, "ble" + nonce)] -> AuthResult. It may then switch the
    ///   car between home WiFi and PLAY mode (WifiMode; the car restarts) and, in play mode, start a WebRTC session:
    ///   RtcOffer -> RtcOfferPart events (the car's offer SDP in chunks), RtcAnswer parts (the app's answer) -> Ok.
    ///   Play mode: the car is its own WiFi access point (name = the car's name, WPA2 password =
    ///   <see cref="CarLink.PlayPassword"/>), there is no internet, so this is the signaling path. BLE stays on.
    /// </summary>
    public static class BleSetup
    {
        public static readonly Guid ServiceUuid = new Guid("1d7bd351-574a-44a3-be60-f66b840e4301");
        public static readonly Guid InfoUuid = new Guid("1d7bd351-574a-44a3-be60-f66b840e4302");
        public static readonly Guid ControlUuid = new Guid("1d7bd351-574a-44a3-be60-f66b840e4303");
        public static readonly Guid EventsUuid = new Guid("1d7bd351-574a-44a3-be60-f66b840e4304");

        /// <summary>Protocol version reported in Info as "proto=".</summary>
        public const int Version = 2;

        public const int CodeDigits = 4;
        public const int MaxCodeAttempts = 3;
        public const int RoomKeyBytes = 20;
        public const int ConnectedAdvertiseSeconds = 120;
        public const int MaxSsidBytes = 32;
        public const int MaxPasswordBytes = 64;

        // App -> car (Control characteristic).
        public const byte OpRequestCode = 0x01;
        public const byte OpSubmitCode = 0x02;   // [4 ASCII digits]
        public const byte OpScan = 0x03;
        public const byte OpSetWifi = 0x04;      // [u8 ssidLen][ssid UTF-8][u8 passLen][password UTF-8]
        public const byte OpGetPairing = 0x05;
        public const byte OpFinish = 0x06;       // stop advertising (normal mode) / leave setup
        public const byte OpKeyHello = 0x07;     // -> EvKeyChallenge
        public const byte OpKeyProof = 0x08;     // [32 HMAC(key, "ble" + nonce)] -> EvAuthResult
        public const byte OpWifiMode = 0x09;     // [u8 WifiModeHome / WifiModePlay] -> EvOk, then the car restarts
        public const byte OpRtcOffer = 0x0A;     // play mode, key proven: the car starts a session -> EvRtcOfferPart...
        public const byte OpRtcAnswer = 0x0B;    // [u8 index][u8 last][UTF-8 part] -> EvOk(OpRtcAnswer) after the last

        // Hardware check / calibration (authorised only; every motion goes through the car's safety checks).
        public const byte OpServo = 0x10;        // [u8 0=pan 1=tilt 2=centre both][u16 BE degrees*10]
        public const byte OpMotor = 0x11;        // [u8 motor 0..3 or 0xFF=all][i8 percent -100..100][u16 BE milliseconds]
        public const byte OpLeds = 0x12;         // [r][g][b] all 12 LEDs
        public const byte OpBuzzer = 0x13;       // [u16 BE hz][u16 BE milliseconds]
        public const byte OpReadSensors = 0x14;  // -> EvSensors
        public const byte OpSetting = 0x15;      // [UTF-8 "key=value"] -> saved on the car, EvOk

        // Car -> app (Events characteristic).
        public const byte EvCodeShown = 0x81;
        public const byte EvAuthResult = 0x82;   // [u8 ok][u8 attemptsLeft]
        public const byte EvScanResult = 0x83;   // [i8 rssi][u8 secured][u8 ssidLen][ssid UTF-8]
        public const byte EvScanDone = 0x84;     // [u8 count]
        public const byte EvWifiSaved = 0x85;    // car is rebooting to join the network
        public const byte EvPairingInfo = 0x86;  // [20 room key][u8 nameLen][name UTF-8]
        public const byte EvSensors = 0x87;      // [UTF-8 "key=value\n" lines]
        public const byte EvOk = 0x88;           // [opcode acknowledged]
        public const byte EvKeyChallenge = 0x89; // [16 nonce]
        public const byte EvRtcOfferPart = 0x8A; // [u8 index][u8 last][UTF-8 part]
        public const byte EvError = 0x8F;        // [UTF-8 message]

        public const int WifiModeHome = 0;
        public const int WifiModePlay = 1;

        /// <summary>SDP bytes per BLE part: under the notification size every link here carried (180 bytes).</summary>
        public const int SdpPartBytes = 160;
        public const int MaxSdpBytes = 4096;

        public static byte[] KeyProof(byte[] key, byte[] nonce) => CarLink.Proof(key, "ble", nonce);

        /// <summary>Part <paramref name="index"/> of an SDP split into <see cref="SdpPartBytes"/> pieces, or null past the end.</summary>
        public static byte[] EncodeSdpPart(byte opcode, byte[] sdpUtf8, int index)
        {
            int start = index * SdpPartBytes;
            if (index < 0 || start >= sdpUtf8.Length) return null;
            int n = sdpUtf8.Length - start > SdpPartBytes ? SdpPartBytes : sdpUtf8.Length - start;
            byte[] b = new byte[3 + n];
            b[0] = opcode;
            b[1] = (byte)index;
            b[2] = (byte)(start + n >= sdpUtf8.Length ? 1 : 0);
            Array.Copy(sdpUtf8, start, b, 3, n);
            return b;
        }

        /// <summary>
        /// Collects SDP parts in order. Returns 1 when the last part arrived (the SDP is complete), 0 for a part that is
        /// not the last, -1 for a bad part (out of order, too long): the caller starts over.
        /// </summary>
        public static int AddSdpPart(byte[] frame, byte[] buffer, ref int length, ref int nextIndex)
        {
            if (frame == null || frame.Length < 3 || frame[1] != (byte)nextIndex) return -1;
            int n = frame.Length - 3;
            if (length + n > buffer.Length) return -1;
            Array.Copy(frame, 3, buffer, length, n);
            length += n;
            nextIndex++;
            return frame[2] != 0 ? 1 : 0;
        }

        public static byte[] EncodeSetWifi(byte[] ssidUtf8, byte[] passwordUtf8)
        {
            if (ssidUtf8 == null || ssidUtf8.Length == 0 || ssidUtf8.Length > MaxSsidBytes) throw new ArgumentException("ssid");
            if (passwordUtf8 == null) passwordUtf8 = new byte[0];
            if (passwordUtf8.Length > MaxPasswordBytes) throw new ArgumentException("password");
            byte[] b = new byte[3 + ssidUtf8.Length + passwordUtf8.Length];
            b[0] = OpSetWifi;
            b[1] = (byte)ssidUtf8.Length;
            Array.Copy(ssidUtf8, 0, b, 2, ssidUtf8.Length);
            b[2 + ssidUtf8.Length] = (byte)passwordUtf8.Length;
            Array.Copy(passwordUtf8, 0, b, 3 + ssidUtf8.Length, passwordUtf8.Length);
            return b;
        }

        /// <summary>Splits a SetWifi frame. Returns false (outputs null) on any length inconsistency.</summary>
        public static bool TryDecodeSetWifi(byte[] frame, out byte[] ssidUtf8, out byte[] passwordUtf8)
        {
            ssidUtf8 = null;
            passwordUtf8 = null;
            if (frame == null || frame.Length < 3 || frame[0] != OpSetWifi) return false;
            int ssidLen = frame[1];
            if (ssidLen == 0 || ssidLen > MaxSsidBytes || frame.Length < 3 + ssidLen) return false;
            int passLen = frame[2 + ssidLen];
            if (passLen > MaxPasswordBytes || frame.Length != 3 + ssidLen + passLen) return false;
            ssidUtf8 = new byte[ssidLen];
            Array.Copy(frame, 2, ssidUtf8, 0, ssidLen);
            passwordUtf8 = new byte[passLen];
            Array.Copy(frame, 3 + ssidLen, passwordUtf8, 0, passLen);
            return true;
        }

        public static byte[] EncodeScanResult(int rssi, bool secured, byte[] ssidUtf8)
        {
            int n = ssidUtf8.Length > MaxSsidBytes ? MaxSsidBytes : ssidUtf8.Length;
            byte[] b = new byte[4 + n];
            b[0] = EvScanResult;
            b[1] = (byte)(sbyte)(rssi < -128 ? -128 : (rssi > 127 ? 127 : rssi));
            b[2] = (byte)(secured ? 1 : 0);
            b[3] = (byte)n;
            Array.Copy(ssidUtf8, 0, b, 4, n);
            return b;
        }

        public static byte[] EncodePairingInfo(byte[] roomKey, byte[] nameUtf8)
        {
            if (roomKey == null || roomKey.Length != RoomKeyBytes) throw new ArgumentException("roomKey");
            int n = nameUtf8.Length > 32 ? 32 : nameUtf8.Length;
            byte[] b = new byte[2 + RoomKeyBytes + n];
            b[0] = EvPairingInfo;
            Array.Copy(roomKey, 0, b, 1, RoomKeyBytes);
            b[1 + RoomKeyBytes] = (byte)n;
            Array.Copy(nameUtf8, 0, b, 2 + RoomKeyBytes, n);
            return b;
        }
    }
}
