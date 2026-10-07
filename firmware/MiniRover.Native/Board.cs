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
        /// Restarts the whole chip, RTC domain included (the RTC watchdog's reset-RTC stage). A crash reset keeps
        /// some state, and the car then found its I2C devices and camera not answering until a reset through the EN
        /// pin; this is the nearest software equivalent. The next boot reports <see cref="ResetOtherWatchdog"/>.
        /// Does not return.
        /// </summary>
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern void FullReset();
    }
}
