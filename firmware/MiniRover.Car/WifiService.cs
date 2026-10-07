using System;
using System.Device.Wifi;
using System.Net.NetworkInformation;
using System.Threading;
using nanoFramework.Networking;
using nanoFramework.Runtime.Native;

namespace MiniRover.Car
{
    /// <summary>
    /// WiFi for every kit owner, with no tools: if the car has no WiFi network saved (or cannot reach the saved
    /// one) it reboots into SETUP mode and starts an open access point "MiniRover-XXXX" serving a setup page at
    /// http://192.168.4.1. Saving a network there stores it in nanoFramework's network configuration block
    /// (the same place nanoff and Visual Studio write it) and reboots into normal STATION mode.
    ///
    /// PLAY mode (a paired car away from its home network): the car makes its own WPA2 network, named like the setup
    /// one, with the password <see cref="MiniRover.Protocol.CarLink.PlayPassword"/> that every paired app derives from
    /// the pairing key. The app drives over WebRTC on that network and signals over BLE. A paired car that cannot
    /// reach its home network goes to play mode instead of setup mode; an unpaired one still goes to setup mode.
    /// </summary>
    public sealed class WifiService
    {
        public const string SetupAddress = "192.168.4.1";
        const int ConnectTimeoutMs = 30_000;

        public bool InSetupMode { get; private set; }
        public bool InPlayMode { get; private set; }
        public bool Connected { get; private set; }
        public string IpAddress { get; private set; } = "";
        public string StationSsid { get; private set; } = "";
        public string SetupSsid { get; private set; } = "";

        readonly Settings _settings;

        public WifiService(Settings settings)
        {
            _settings = settings;
            SetupSsid = BuildSetupSsid();
        }

        /// <summary>Brings WiFi up. Returns once connected, or once the setup access point is running.
        /// May reboot the device (entering setup mode).</summary>
        public void Start()
        {
            WirelessAPConfiguration ap = WirelessAPConfiguration.GetAllWirelessAPConfigurations()[0];
            Wireless80211Configuration sta = Wireless80211Configuration.GetAllWireless80211Configurations()[0];
            SetupSsid = BuildSetupSsid();
            StationSsid = sta.Ssid ?? "";

            if ((ap.Options & WirelessAPConfiguration.ConfigurationOptions.Enable) != 0)
            {
                bool play = _settings != null && _settings.PlayMode && _settings.RoomKey != null;
                // Booted into setup mode (the AP auto-started). The radio must be AP+STA to scan for the app's
                // network list; a car put into setup mode by older firmware lacks the station flag, so fix it once.
                if ((sta.Options & Wireless80211Configuration.ConfigurationOptions.Enable) == 0)
                {
                    System.Diagnostics.Debug.WriteLine("WiFi: enabling the station interface for scanning (one reboot)");
                    if (play) EnterPlayMode(); else EnterSetupMode();
                    return;
                }
                InPlayMode = play;
                InSetupMode = !play;
                NetworkInterface apIf = FindInterface(NetworkInterfaceType.WirelessAP);
                if (apIf != null && apIf.IPv4Address != SetupAddress)
                {
                    apIf.EnableStaticIPv4(SetupAddress, "255.255.255.0", SetupAddress);
                }
                IpAddress = SetupAddress;
                System.Diagnostics.Debug.WriteLine(InPlayMode
                    ? "WiFi: PLAY mode, the car's own network '" + ap.Ssid + "' at " + SetupAddress
                    : "WiFi: SETUP mode, join '" + ap.Ssid + "' and open http://" + SetupAddress);
                return;
            }

            if (StationSsid.Length == 0)
            {
                System.Diagnostics.Debug.WriteLine("WiFi: no network saved");
                EnterPlayOrSetupMode();
                return;
            }

            System.Diagnostics.Debug.WriteLine("WiFi: connecting to '" + StationSsid + "'");
            var cts = new CancellationTokenSource(ConnectTimeoutMs);
            bool ok = WifiNetworkHelper.ConnectDhcp(StationSsid, sta.Password, WifiReconnectionKind.Automatic, false, 0, cts.Token);
            if (!ok)
            {
                // Keep the saved network (the router may just be off) but let the owner fix it. The reason is shown
                // to the app over BLE in setup mode.
                string reason = WifiNetworkHelper.Status.ToString();
                if (WifiNetworkHelper.HelperException != null) reason += ": " + WifiNetworkHelper.HelperException.Message;
                System.Diagnostics.Debug.WriteLine("WiFi: could not connect (" + reason + ")");
                if (_settings != null)
                {
                    _settings.LastWifiError = "could not join '" + StationSsid + "' (" + reason + ")";
                    _settings.Save();
                }
                EnterPlayOrSetupMode();
                return;
            }

            Connected = true;
            NetworkInterface staIf = FindInterface(NetworkInterfaceType.Wireless80211);
            IpAddress = staIf != null ? staIf.IPv4Address : "";
            System.Diagnostics.Debug.WriteLine("WiFi: connected, http://" + IpAddress);
        }

        /// <summary>No home network: a paired car makes its play network (its owner can still drive it), an unpaired car
        /// starts the open setup network.</summary>
        void EnterPlayOrSetupMode()
        {
            if (_settings != null && _settings.RoomKey != null) EnterPlayMode();
            else EnterSetupMode();
        }

        /// <summary>Switches to play mode (the car's own WPA2 network) and restarts. Paired cars only.</summary>
        public void EnterPlayMode()
        {
            byte[] key = _settings != null ? _settings.RoomKey : null;
            if (key == null) throw new InvalidOperationException("play mode needs a paired car");
            System.Diagnostics.Debug.WriteLine("WiFi: switching to PLAY mode");
            _settings.PlayMode = true;
            _settings.Save();
            WirelessAPConfiguration ap = WirelessAPConfiguration.GetAllWirelessAPConfigurations()[0];
            ap.Ssid = BuildSetupSsid();
            ap.Password = MiniRover.Protocol.CarLink.PlayPassword(key);
            ap.Authentication = AuthenticationType.WPA2;
            ap.Encryption = EncryptionType.WPA2;
            ap.MaxConnections = 4;
            ap.Options = WirelessAPConfiguration.ConfigurationOptions.Enable | WirelessAPConfiguration.ConfigurationOptions.AutoStart;
            ap.SaveConfiguration();
            // Station enabled but not auto-connecting, as in setup mode: AP+STA can scan (the app can still set a new
            // home network over BLE), and nothing hops channels under the car's own network.
            Wireless80211Configuration sta = Wireless80211Configuration.GetAllWireless80211Configurations()[0];
            sta.Options = Wireless80211Configuration.ConfigurationOptions.Enable;
            sta.SaveConfiguration();
            Thread.Sleep(200);
            Power.RebootDevice();
        }

        /// <summary>Back to the saved home network (station mode) and restarts. False when no home network is saved.</summary>
        public bool EnterHomeMode()
        {
            Wireless80211Configuration sta = Wireless80211Configuration.GetAllWireless80211Configurations()[0];
            if (sta.Ssid == null || sta.Ssid.Length == 0) return false;
            System.Diagnostics.Debug.WriteLine("WiFi: switching to the home network '" + sta.Ssid + "'");
            if (_settings != null)
            {
                _settings.PlayMode = false;
                _settings.Save();
            }
            sta.Options = Wireless80211Configuration.ConfigurationOptions.Enable | Wireless80211Configuration.ConfigurationOptions.AutoConnect;
            sta.SaveConfiguration();
            WirelessAPConfiguration ap = WirelessAPConfiguration.GetAllWirelessAPConfigurations()[0];
            ap.Options = WirelessAPConfiguration.ConfigurationOptions.None;
            ap.SaveConfiguration();
            Thread.Sleep(200);
            Power.RebootDevice();
            return true;
        }

        /// <summary>Starts the setup access point on the next boot and reboots now.</summary>
        public void EnterSetupMode()
        {
            if (_settings != null && _settings.PlayMode)
            {
                _settings.PlayMode = false;
                _settings.Save();
            }
            WirelessAPConfiguration ap = WirelessAPConfiguration.GetAllWirelessAPConfigurations()[0];
            ap.Ssid = BuildSetupSsid();
            ap.Password = "";
            ap.Authentication = AuthenticationType.Open;
            ap.Encryption = EncryptionType.None;
            ap.MaxConnections = 2;
            ap.Options = WirelessAPConfiguration.ConfigurationOptions.Enable | WirelessAPConfiguration.ConfigurationOptions.AutoStart;
            ap.SaveConfiguration();

            // Keep the station interface ENABLED (but not auto-connecting) so the radio runs AP+STA: an AP-only ESP32
            // cannot scan, and the app's network list comes from a scan (nanoFramework picks APSTA only when the
            // station config has the Enable flag - NF_ESP32_Wireless.cpp). No AutoConnect: retrying a network that
            // just failed would hop channels under the setup access point.
            Wireless80211Configuration sta = Wireless80211Configuration.GetAllWireless80211Configurations()[0];
            sta.Options = Wireless80211Configuration.ConfigurationOptions.Enable;
            sta.SaveConfiguration();
            Thread.Sleep(200);
            Power.RebootDevice();
        }

        /// <summary>Saves the owner's WiFi network, turns the setup access point off and reboots.</summary>
        public void SaveNetworkAndReboot(string ssid, string password)
        {
            if (_settings != null && _settings.PlayMode)
            {
                _settings.PlayMode = false;
                _settings.Save();
            }
            Wireless80211Configuration sta = Wireless80211Configuration.GetAllWireless80211Configurations()[0];
            sta.Ssid = ssid;
            sta.Password = password ?? "";
            sta.Authentication = password == null || password.Length == 0 ? AuthenticationType.Open : AuthenticationType.WPA2;
            sta.Encryption = password == null || password.Length == 0 ? EncryptionType.None : EncryptionType.WPA2;
            sta.Options = Wireless80211Configuration.ConfigurationOptions.Enable | Wireless80211Configuration.ConfigurationOptions.AutoConnect;
            sta.SaveConfiguration();

            WirelessAPConfiguration ap = WirelessAPConfiguration.GetAllWirelessAPConfigurations()[0];
            ap.Options = WirelessAPConfiguration.ConfigurationOptions.None;
            ap.SaveConfiguration();

            Thread.Sleep(200);
            Power.RebootDevice();
        }

        static NetworkInterface FindInterface(NetworkInterfaceType type)
        {
            foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.NetworkInterfaceType == type) return ni;
            }
            return null;
        }

        static string BuildSetupSsid()
        {
            // Last two MAC bytes keep two cars in one room apart.
            NetworkInterface ni = FindInterface(NetworkInterfaceType.Wireless80211);
            byte[] mac = ni != null ? ni.PhysicalAddress : null;
            if (mac == null || mac.Length < 2) return "MiniRover";
            return "MiniRover-" + mac[mac.Length - 2].ToString("X2") + mac[mac.Length - 1].ToString("X2");
        }
    }
}
