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
    /// </summary>
    public static class BleSetup
    {
        public static readonly Guid ServiceUuid = new Guid("1d7bd351-574a-44a3-be60-f66b840e4301");
        public static readonly Guid InfoUuid = new Guid("1d7bd351-574a-44a3-be60-f66b840e4302");
        public static readonly Guid ControlUuid = new Guid("1d7bd351-574a-44a3-be60-f66b840e4303");
        public static readonly Guid EventsUuid = new Guid("1d7bd351-574a-44a3-be60-f66b840e4304");

        /// <summary>Protocol version reported in Info as "proto=".</summary>
        public const int Version = 1;

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
        public const byte EvError = 0x8F;        // [UTF-8 message]

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
