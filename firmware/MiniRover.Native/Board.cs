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
    }
}
