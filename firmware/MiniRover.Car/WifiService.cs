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
    /// </summary>
    public sealed class WifiService
    {
        public const string SetupAddress = "192.168.4.1";
        const int ConnectTimeoutMs = 30_000;

        public bool InSetupMode { get; private set; }
        public bool Connected { get; private set; }
        public string IpAddress { get; private set; } = "";
        public string StationSsid { get; private set; } = "";
        public string SetupSsid { get; private set; } = "";

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
                // Booted into setup mode (the AP auto-started); give it its fixed address.
                InSetupMode = true;
                NetworkInterface apIf = FindInterface(NetworkInterfaceType.WirelessAP);
                if (apIf != null && apIf.IPv4Address != SetupAddress)
                {
                    apIf.EnableStaticIPv4(SetupAddress, "255.255.255.0", SetupAddress);
                }
                IpAddress = SetupAddress;
                System.Diagnostics.Debug.WriteLine("WiFi: SETUP mode, join '" + ap.Ssid + "' and open http://" + SetupAddress);
                return;
            }

            if (StationSsid.Length == 0)
            {
                System.Diagnostics.Debug.WriteLine("WiFi: no network saved, entering setup mode");
                EnterSetupMode();
                return;
            }

            System.Diagnostics.Debug.WriteLine("WiFi: connecting to '" + StationSsid + "'");
            var cts = new CancellationTokenSource(ConnectTimeoutMs);
            bool ok = WifiNetworkHelper.ConnectDhcp(StationSsid, sta.Password, WifiReconnectionKind.Automatic, false, 0, cts.Token);
            if (!ok)
            {
                // Keep the saved network (the router may just be off) but let the owner fix it.
                System.Diagnostics.Debug.WriteLine("WiFi: could not connect (" + WifiNetworkHelper.Status.ToString() + "), entering setup mode");
                EnterSetupMode();
                return;
            }

            Connected = true;
            NetworkInterface staIf = FindInterface(NetworkInterfaceType.Wireless80211);
            IpAddress = staIf != null ? staIf.IPv4Address : "";
            System.Diagnostics.Debug.WriteLine("WiFi: connected, http://" + IpAddress);
        }

        /// <summary>Starts the setup access point on the next boot and reboots now.</summary>
        public void EnterSetupMode()
        {
            WirelessAPConfiguration ap = WirelessAPConfiguration.GetAllWirelessAPConfigurations()[0];
            ap.Ssid = BuildSetupSsid();
            ap.Password = "";
            ap.Authentication = AuthenticationType.Open;
            ap.Encryption = EncryptionType.None;
            ap.MaxConnections = 2;
            ap.Options = WirelessAPConfiguration.ConfigurationOptions.Enable | WirelessAPConfiguration.ConfigurationOptions.AutoStart;
            ap.SaveConfiguration();
            Thread.Sleep(200);
            Power.RebootDevice();
        }

        /// <summary>Saves the owner's WiFi network, turns the setup access point off and reboots.</summary>
        public void SaveNetworkAndReboot(string ssid, string password)
        {
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
