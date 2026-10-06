using System;
using System.Device.Wifi;
using System.Text;
using System.Threading;
using MiniRover.Protocol;
using nanoFramework.Device.Bluetooth;
using nanoFramework.Device.Bluetooth.GenericAttributeProfile;

namespace MiniRover.Car
{
    /// <summary>
    /// BLE side of the setup protocol (src/MiniRover.Protocol/BleSetup.cs): the web app finds the car over Web
    /// Bluetooth, proves the person can see the car (code on the LED eyes), then scans WiFi, hands over the
    /// network and receives the pairing key. Runs only in setup mode, plus a short window after a BLE setup so
    /// the app can confirm the car joined; it never runs alongside driving.
    /// </summary>
    public sealed class BleSetupService
    {
        readonly Car _car;
        readonly WifiService _wifi;
        readonly Settings _settings;
        readonly string _name;
        readonly Random _rng = new Random(); // ESP32: backed by esp_random(), the hardware RNG
        readonly object _lock = new object();

        GattServiceProvider _provider;
        GattLocalCharacteristic _events;
        bool _eventsSubscribed;
        bool _authorised;
        string _code;
        int _attemptsLeft;

        public bool Running { get; private set; }

        public BleSetupService(Car car, WifiService wifi, Settings settings, string name)
        {
            _car = car;
            _wifi = wifi;
            _settings = settings;
            _name = name;
        }

        public void Start()
        {
            BluetoothLEServer server = BluetoothLEServer.Instance;
            server.DeviceName = _name;

            GattServiceProviderResult result = GattServiceProvider.Create(BleSetup.ServiceUuid);
            if (result.Error != BluetoothError.Success)
            {
                throw new InvalidOperationException("BLE service create failed: " + result.Error.ToString());
            }
            _provider = result.ServiceProvider;
            GattLocalService service = _provider.Service;

            GattLocalCharacteristic info = Create(service, BleSetup.InfoUuid, GattCharacteristicProperties.Read, "Info");
            info.ReadRequested += (sender, args) =>
            {
                GattReadRequest request = args.GetRequest();
                request.RespondWithValue(new Buffer(Encoding.UTF8.GetBytes(BuildInfo())));
            };

            GattLocalCharacteristic control = Create(service, BleSetup.ControlUuid,
                GattCharacteristicProperties.Write | GattCharacteristicProperties.WriteWithoutResponse, "Control");
            control.WriteRequested += OnControlWrite;

            _events = Create(service, BleSetup.EventsUuid, GattCharacteristicProperties.Notify, "Events");
            _events.SubscribedClientsChanged += (sender, args) =>
            {
                lock (_lock)
                {
                    _eventsSubscribed = sender.SubscribedClients.Length > 0;
                    if (!_eventsSubscribed)
                    {
                        // The app went away: forget the authorisation and the code so a new visitor starts over.
                        _authorised = false;
                        _code = null;
                        ShowEyes();
                    }
                }
            };

            _provider.StartAdvertising(new GattServiceProviderAdvertisingParameters
            {
                IsConnectable = true,
                IsDiscoverable = true,
            });
            Running = true;
            System.Diagnostics.Debug.WriteLine("BLE setup: advertising as '" + _name + "'");
        }

        public void Stop()
        {
            if (!Running) return;
            Running = false;
            try { _provider.StopAdvertising(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine("BLE stop: " + ex.Message); }
        }

        static GattLocalCharacteristic Create(GattLocalService service, Guid uuid, GattCharacteristicProperties props, string description)
        {
            GattLocalCharacteristicResult r = service.CreateCharacteristic(uuid, new GattLocalCharacteristicParameters
            {
                CharacteristicProperties = props,
                UserDescription = description,
            });
            if (r.Error != BluetoothError.Success)
            {
                throw new InvalidOperationException("BLE characteristic " + description + " failed: " + r.Error.ToString());
            }
            return r.Characteristic;
        }

        void OnControlWrite(GattLocalCharacteristic sender, GattWriteRequestedEventArgs args)
        {
            GattWriteRequest request = args.GetRequest();
            byte[] frame = new byte[request.Value.Length];
            DataReader.FromBuffer(request.Value).ReadBytes(frame);
            if (request.Option == GattWriteOption.WriteWithResponse)
            {
                request.Respond();
            }
            if (frame.Length == 0) return;

            try
            {
                switch (frame[0])
                {
                    case BleSetup.OpRequestCode:
                        ShowNewCode();
                        break;
                    case BleSetup.OpSubmitCode:
                        SubmitCode(frame);
                        break;
                    case BleSetup.OpScan:
                        if (RequireAuth()) new Thread(Scan).Start();
                        break;
                    case BleSetup.OpGetPairing:
                        if (RequireAuth()) SendPairing();
                        break;
                    case BleSetup.OpSetWifi:
                        if (RequireAuth()) SetWifi(frame);
                        break;
                    case BleSetup.OpFinish:
                        Stop();
                        break;
                    default:
                        SendError("unknown opcode 0x" + frame[0].ToString("X2"));
                        break;
                }
            }
            catch (Exception ex)
            {
                SendError(ex.Message);
            }
        }

        void ShowNewCode()
        {
            lock (_lock)
            {
                _code = (_rng.Next(10000)).ToString("D4");
                _attemptsLeft = BleSetup.MaxCodeAttempts;
                _authorised = false;
            }
            // Also on the debug console: a developer without the camera head fitted can still pair.
            System.Diagnostics.Debug.WriteLine("BLE setup code: " + _code);
            if (_car.Matrix != null) _car.Matrix.Show(Digits.Render(_code));
            if (_car.Buzzer != null) _car.Buzzer.Beep(2000, 60);
            Notify(new byte[] { BleSetup.EvCodeShown });
        }

        void SubmitCode(byte[] frame)
        {
            bool ok;
            int left;
            lock (_lock)
            {
                if (_code == null)
                {
                    SendError("request a code first");
                    return;
                }
                string typed = frame.Length == 1 + BleSetup.CodeDigits ? Encoding.UTF8.GetString(frame, 1, BleSetup.CodeDigits) : "";
                ok = typed == _code;
                if (ok)
                {
                    _authorised = true;
                    _code = null;
                }
                else
                {
                    _attemptsLeft--;
                }
                left = _attemptsLeft;
            }

            Notify(new byte[] { BleSetup.EvAuthResult, (byte)(ok ? 1 : 0), (byte)(left < 0 ? 0 : left) });
            if (ok)
            {
                ShowEyes();
            }
            else if (left <= 0)
            {
                ShowNewCode(); // 3 misses: a fresh code, so guessing gains nothing
            }
        }

        bool RequireAuth()
        {
            if (_authorised) return true;
            SendError("enter the code shown on the car first");
            return false;
        }

        void Scan()
        {
            try
            {
                WifiAdapter adapter = WifiAdapter.FindAllAdapters()[0];
                // ScanAsync only STARTS a scan; results arrive with AvailableNetworksChanged. Reading NetworkReport
                // straight after ScanAsync returns an empty list.
                var done = new AutoResetEvent(false);
                AvailableNetworksChangedEventHandler onDone = (s, e) => done.Set();
                adapter.AvailableNetworksChanged += onDone;
                WifiAvailableNetwork[] networks;
                try
                {
                    DateTime started = DateTime.UtcNow;
                    adapter.ScanAsync();
                    // A passive scan is 500 ms per channel, and in AP+STA mode the radio keeps returning to the setup
                    // AP's channel between scan channels, so allow well over the 7 s a 14-channel STA scan takes.
                    bool fired = done.WaitOne(25_000, false);
                    networks = adapter.NetworkReport.AvailableNetworks;
                    System.Diagnostics.Debug.WriteLine("BLE setup scan: event " + (fired ? "fired" : "did NOT fire") + " after " +
                        ((int)(DateTime.UtcNow - started).TotalMilliseconds).ToString() + " ms, " + networks.Length.ToString() + " networks in report");
                    if (!fired && networks.Length == 0)
                    {
                        SendError("scan timed out");
                        return;
                    }
                }
                finally
                {
                    adapter.AvailableNetworksChanged -= onDone;
                }
                int count = 0;
                foreach (WifiAvailableNetwork n in networks)
                {
                    if (n.Ssid == null || n.Ssid.Length == 0) continue; // hidden networks
                    // nanoFramework's scan result carries no security type, so report "secured" for every network;
                    // the app always offers an (optional) password field.
                    Notify(BleSetup.EncodeScanResult((int)n.NetworkRssiInDecibelMilliwatts, true, Encoding.UTF8.GetBytes(n.Ssid)));
                    count++;
                    Thread.Sleep(20); // let each notification out
                }
                Notify(new byte[] { BleSetup.EvScanDone, (byte)(count > 255 ? 255 : count) });
            }
            catch (Exception ex)
            {
                SendError("scan failed: " + ex.Message);
            }
        }

        void SendPairing()
        {
            Notify(BleSetup.EncodePairingInfo(EnsureRoomKey(), Encoding.UTF8.GetBytes(_name)));
        }

        byte[] EnsureRoomKey()
        {
            string hex = _settings.RoomKeyHex;
            if (hex.Length != BleSetup.RoomKeyBytes * 2)
            {
                byte[] key = new byte[BleSetup.RoomKeyBytes];
                _rng.NextBytes(key);
                var sb = new StringBuilder();
                foreach (byte b in key) sb.Append(b.ToString("x2"));
                hex = sb.ToString();
                _settings.RoomKeyHex = hex;
                _settings.Save();
            }
            byte[] result = new byte[BleSetup.RoomKeyBytes];
            for (int i = 0; i < result.Length; i++)
            {
                result[i] = (byte)Convert.ToInt32(hex.Substring(i * 2, 2), 16);
            }
            return result;
        }

        void SetWifi(byte[] frame)
        {
            byte[] ssid, password;
            if (!BleSetup.TryDecodeSetWifi(frame, out ssid, out password))
            {
                SendError("bad network frame");
                return;
            }
            EnsureRoomKey(); // the app may have skipped GetPairing; the key must exist before it reconnects
            _settings.AnnounceAfterSetup = true;
            _settings.LastWifiError = "";
            _settings.Save();
            Notify(new byte[] { BleSetup.EvWifiSaved });
            Thread.Sleep(400); // let the notification leave before the radio goes down
            _wifi.SaveNetworkAndReboot(Encoding.UTF8.GetString(ssid, 0, ssid.Length), Encoding.UTF8.GetString(password, 0, password.Length));
        }

        string BuildInfo()
        {
            var sb = new StringBuilder();
            sb.Append("proto=").Append(BleSetup.Version.ToString()).Append('\n');
            sb.Append("name=").Append(_name).Append('\n');
            sb.Append("fw=").Append(Program.FirmwareVersion).Append('\n');
            sb.Append("state=").Append(_wifi.InSetupMode ? "setup" : (_wifi.Connected ? "connected" : "offline")).Append('\n');
            sb.Append("ip=").Append(_wifi.IpAddress).Append('\n');
            sb.Append("ssid=").Append(_wifi.StationSsid).Append('\n');
            sb.Append("wifiError=").Append(_settings.LastWifiError).Append('\n');
            sb.Append("paired=").Append(_settings.RoomKeyHex.Length > 0 ? "1" : "0").Append('\n');
            sb.Append("battery=").Append(_car.Battery != null ? _car.Battery.Volts.ToString("F2") : "").Append('\n');
            sb.Append("faults=").Append(_car.Faults).Append('\n');
            return sb.ToString();
        }

        void ShowEyes()
        {
            try
            {
                if (_car.Matrix != null) _car.Matrix.Show(_wifi.InSetupMode ? Eyes.Setup : Eyes.Open);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Matrix: " + ex.Message);
            }
        }

        void SendError(string message)
        {
            byte[] text = Encoding.UTF8.GetBytes(message);
            int n = text.Length > 160 ? 160 : text.Length;
            byte[] b = new byte[1 + n];
            b[0] = BleSetup.EvError;
            Array.Copy(text, 0, b, 1, n);
            Notify(b);
        }

        void Notify(byte[] payload)
        {
            if (!_eventsSubscribed) return;
            try
            {
                _events.NotifyValue(new Buffer(payload));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("BLE notify failed: " + ex.Message);
            }
        }
    }
}
