using System.Runtime.CompilerServices;

namespace MiniRover.Native
{
    /// <summary>
    /// ESP32 functions nanoFramework does not expose, implemented in the MiniRover firmware
    /// (firmware/native/MiniRover.Native). Only works on a MiniRover firmware image.
    /// </summary>
    public static class Board
    {
        /// <summary>Signal strength of the connected WiFi access point in dBm (about -30 next to it, -80 near the edge
        /// of range), or 0 when not connected.</summary>
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern int WifiRssi();

        /// <summary>
        /// WiFi modem sleep on or off. Off costs a little power and removes the up-to-hundreds-of-ms receive stalls that
        /// break 20 Hz driving (a status poll stalled 1.4 s with it on). ESP-IDF requires modem sleep while Bluetooth is
        /// on, so this returns false (and changes nothing) during the BLE setup window.
        /// </summary>
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern bool SetWifiPowerSave(bool enabled);

        public const int MemInternalFree = 0;
        public const int MemInternalLargest = 1;
        public const int MemPsramFree = 2;
        public const int MemPsramLargest = 3;
        public const int MemDmaFree = 4;
        public const int MemInternalMinimumEver = 5;
        // Network diagnostics through FreeMemory (constants only, so the interop checksum stays the same).
        public const int NetApStations = 100;
        public const int NetApAddress = 101;       // IPv4, network order, as esp-netif has it
        public const int NetApLwipAddress = 102;   // the same interface as lwIP has it
        public const int NetApDhcpServer = 103;    // 0 init, 1 started, 2 stopped
        public const int NetStaAddress = 104;
        public const int NetWifiMode = 105;        // 0 off, 1 station, 2 access point, 3 both
        public const int NetWifiPowerSave = 106;   // esp_wifi_get_ps: 0 none, 1 min modem, 2 max modem
        public const int NetBluetoothController = 107; // esp_bt_controller_get_status: 0 idle (off), 1 initialised, 2 enabled
        public const int NetUdpTestBytes = 108;    // UdpTest: bytes handed to the network so far
        public const int NetUdpTestFull = 109;     // UdpTest: sends refused because the WiFi / lwIP buffers were full
        public const int NetUdpTestRunning = 110;  // UdpTest: 1 while the test task runs
        public const int NetStaChannel = 111;      // the joined access point's primary channel
        public const int NetStaSecondChannel = 112; // 0 none (20 MHz), 1 above, 2 below (40 MHz)
        public const int NetStaApPhy = 113;        // the AP's PHY modes, bits: 0 11b, 1 11g, 2 11n, 3 LR, 4 11a, 5 11ac, 6 11ax
        public const int NetStaPhyMode = 114;      // negotiated wifi_phy_mode_t: 0 LR, 1 11b, 2 11g, 3 11a, 4 HT20, 5 HT40, 6 HE20
        public const int NetStaBandwidth = 115;    // 1 HT20, 2 HT40
        public const int NetStaProtocols = 116;    // the station's own protocol bitmap (1 b, 2 g, 4 n, 8 LR)

        /// <summary>Native heap numbers in bytes (Mem* ids). Internal RAM is what runs out first on this board.</summary>
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern int FreeMemory(int kind);

        // ResetReason values (ESP-IDF esp_reset_reason_t).
        public const int ResetUnknown = 0;
        public const int ResetPowerOn = 1;      // power applied, or the EN pin (reset button, USB auto-reset)
        public const int ResetExternal = 2;
        public const int ResetSoftware = 3;     // esp_restart: a deliberate restart
        public const int ResetPanic = 4;        // a crash
        public const int ResetInterruptWatchdog = 5;
        public const int ResetTaskWatchdog = 6;
        public const int ResetOtherWatchdog = 7;
        public const int ResetDeepSleep = 8;
        public const int ResetBrownout = 9;     // the supply dipped (weak batteries under load)
        public const int ResetSdio = 10;

        /// <summary>Why the chip last started (Reset* values).</summary>
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern int ResetReason();

        /// <summary>
        /// An ADC1 reading (12-bit, 12 dB attenuation: how nanoFramework configures its channels) converted to
        /// millivolts at the pin with the chip's factory calibration (the reference voltage in eFuse, ESP-IDF line
        /// fitting). The raw counts are non-linear and the plain formula read a nearly empty pack about 0.5 V low.
        /// -1 when the chip has no calibration data.
        /// </summary>
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern int AdcMillivolts(int raw);

        /// <summary>
        /// Shuts Bluetooth down completely: the NimBLE host and the controller (nanoFramework's own teardown). The
        /// ESP32 has one 2.4 GHz radio; while the Bluetooth controller is enabled it shares the radio's time with WiFi.
        /// Nothing may use BLE afterwards until the next restart. True when Bluetooth is off.
        /// </summary>
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern bool BluetoothOff();

        /// <summary>
        /// WiFi throughput test without WebRTC: a native task sends 1200-byte UDP datagrams to <paramref name="ipv4"/>
        /// : <paramref name="port"/> as fast as the network takes them for <paramref name="durationMs"/> (at most 30 s).
        /// Progress in FreeMemory(NetUdpTest*). False if a test is already running or the address is invalid.
        /// </summary>
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern bool UdpTest(string ipv4, int port, int durationMs);

        /// <summary>
        /// Restarts the whole chip, RTC domain included (the RTC watchdog's reset-RTC stage). A crash reset keeps
        /// some state, and the car then found its I2C devices and camera not answering until a reset through the EN
        /// pin; this is the nearest software equivalent. The next boot reports <see cref="ResetOtherWatchdog"/>.
        /// Does not return.
        /// </summary>
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern void FullReset();
    }
}
