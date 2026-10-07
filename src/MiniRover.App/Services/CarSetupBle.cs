using System.Text;
using System.Threading.Channels;
using MiniRover.Client;
using MiniRover.Protocol;
using SpawnDev.SpawnJS;
using SpawnDev.SpawnJS.JSObjects;

namespace MiniRover.App.Services;

/// <summary>
/// Web Bluetooth client for the car's setup service (protocol: src/MiniRover.Protocol/BleSetup.cs, the same file
/// the firmware compiles). Holds on to the BluetoothDevice across the car's reboot after SetWifi, so the app can
/// reconnect without a second device picker and confirm the car joined the network.
/// </summary>
public sealed class CarSetupBle : IAsyncDisposable, ICarBle
{
    readonly SpawnJSRuntime _js;
    readonly Channel<byte[]> _events = Channel.CreateUnbounded<byte[]>();

    BluetoothDevice? _device;
    BluetoothRemoteGATTServer? _server;
    BluetoothRemoteGATTService? _service;
    BluetoothRemoteGATTCharacteristic? _info, _control, _eventsChar;
    Action<Event>? _onValue;

    public CarSetupBle(SpawnJSRuntime js) => _js = js;

    public static bool IsSupported(SpawnJSRuntime js)
    {
        using var navigator = js.Get<Navigator>("navigator");
        using var bt = navigator.Bluetooth;
        return bt != null;
    }

    public string Name => _device?.Name ?? "";
    public bool HasDevice => _device != null;

    /// <summary>Drops the BLE link but keeps the device, so <see cref="ConnectAsync"/> can reconnect without a picker.</summary>
    public void Disconnect()
    {
        try { if (_server?.Connected == true) _server.Disconnect(); } catch { }
        ReleaseGatt();
    }
    public bool Connected => _server?.Connected == true;

    /// <summary>Shows the browser's device picker (must be called from a click), then connects.</summary>
    public async Task PickAndConnectAsync()
    {
        using var navigator = _js.Get<Navigator>("navigator");
        using var bluetooth = navigator.Bluetooth ?? throw new NotSupportedException(
            "This browser has no Web Bluetooth. Use Chrome or Edge (Windows, macOS, ChromeOS, Android), or the car's setup WiFi network.");
        string service = BleSetup.ServiceUuid.ToString();
        _device = await bluetooth.RequestDevice(new BluetoothDeviceOptions
        {
            Filters = new[]
            {
                new BluetoothDeviceFilter { Services = new[] { service } },
                // Name fallback in case an advertisement omits the 128-bit service UUID.
                new BluetoothDeviceFilter { NamePrefix = "MiniRover" },
            },
            OptionalServices = new[] { service },
        });
        await ConnectAsync();
    }

    /// <summary>(Re)connects to the device picked earlier, e.g. after the car rebooted onto the network.</summary>
    public async Task ConnectAsync()
    {
        if (_device == null) throw new InvalidOperationException("pick a car first");
        ReleaseGatt();
        using var gatt = _device.GATT ?? throw new InvalidOperationException("device has no GATT server");
        _server = await gatt.Connect();
        _service = await _server.GetPrimaryService(BleSetup.ServiceUuid.ToString());
        _info = await _service.GetCharacteristic(BleSetup.InfoUuid.ToString());
        _control = await _service.GetCharacteristic(BleSetup.ControlUuid.ToString());
        _eventsChar = await _service.GetCharacteristic(BleSetup.EventsUuid.ToString());

        while (_events.Reader.TryRead(out _)) { } // drop anything left from a previous connection
        _onValue = e =>
        {
            // JS callback: an exception escaping here would exit the WASM runtime. Never let one out.
            try
            {
                using var c = e.TargetAs<BluetoothRemoteGATTCharacteristic>();
                byte[]? bytes = c.ValueBytes;
                if (bytes is { Length: > 0 }) _events.Writer.TryWrite(bytes);
            }
            catch (Exception ex)
            {
                Console.WriteLine("MiniRover: BLE event read failed: " + ex.Message);
            }
        };
        _eventsChar.OnCharacteristicValueChanged += _onValue;
        await _eventsChar.StartNotifications();
    }

    public async Task<Dictionary<string, string>> ReadInfoAsync()
    {
        byte[] bytes = await _info!.ReadValueBytes();
        var map = new Dictionary<string, string>();
        foreach (string line in Encoding.UTF8.GetString(bytes).Split('\n'))
        {
            int eq = line.IndexOf('=');
            if (eq > 0) map[line[..eq]] = line[(eq + 1)..];
        }
        return map;
    }

    public Task SendAsync(byte[] frame) => _control!.WriteValueWithResponse(frame);

    public async Task<byte[]> WaitForAsync(byte opcode, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            while (true)
            {
                byte[] ev = await _events.Reader.ReadAsync(cts.Token);
                if (ev[0] == BleSetup.EvError) throw new CarSetupException(Encoding.UTF8.GetString(ev, 1, ev.Length - 1));
                if (ev[0] == opcode) return ev;
            }
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException("The car did not answer in time.");
        }
    }

    public async Task RequestCodeAsync()
    {
        await SendAsync(new[] { BleSetup.OpRequestCode });
        await WaitForAsync(BleSetup.EvCodeShown, TimeSpan.FromSeconds(5));
    }

    public async Task<(bool Ok, int AttemptsLeft)> SubmitCodeAsync(string code)
    {
        code = code.Trim();
        if (code.Length != BleSetup.CodeDigits || !code.All(char.IsAsciiDigit)) return (false, -1);
        byte[] frame = new byte[1 + BleSetup.CodeDigits];
        frame[0] = BleSetup.OpSubmitCode;
        Encoding.ASCII.GetBytes(code, 0, BleSetup.CodeDigits, frame, 1);
        await SendAsync(frame);
        byte[] ev = await WaitForAsync(BleSetup.EvAuthResult, TimeSpan.FromSeconds(5));
        return (ev[1] == 1, ev[2]);
    }

    /// <summary>Networks the car can see, strongest first, one entry per name.</summary>
    public async Task<List<WifiNetwork>> ScanAsync()
    {
        await SendAsync(new[] { BleSetup.OpScan });
        var best = new Dictionary<string, WifiNetwork>();
        // The car's scan takes ~14 s in setup mode (measured); allow more.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        try
        {
            while (true)
            {
                byte[] ev = await _events.Reader.ReadAsync(cts.Token);
                if (ev[0] == BleSetup.EvError) throw new CarSetupException(Encoding.UTF8.GetString(ev, 1, ev.Length - 1));
                if (ev[0] == BleSetup.EvScanDone) break;
                if (ev[0] == BleSetup.EvScanResult && ev.Length >= 4)
                {
                    var n = new WifiNetwork(Encoding.UTF8.GetString(ev, 4, ev[3]), (sbyte)ev[1]);
                    if (!best.TryGetValue(n.Ssid, out var seen) || n.Rssi > seen.Rssi) best[n.Ssid] = n;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException("The car's WiFi scan did not finish.");
        }
        return best.Values.OrderByDescending(n => n.Rssi).ToList();
    }

    public async Task<(byte[] RoomKey, string Name)> GetPairingAsync()
    {
        await SendAsync(new[] { BleSetup.OpGetPairing });
        byte[] ev = await WaitForAsync(BleSetup.EvPairingInfo, TimeSpan.FromSeconds(5));
        int n = ev[1 + BleSetup.RoomKeyBytes];
        return (ev[1..(1 + BleSetup.RoomKeyBytes)], Encoding.UTF8.GetString(ev, 2 + BleSetup.RoomKeyBytes, n));
    }

    public async Task SetWifiAsync(string ssid, string password)
    {
        await SendAsync(BleSetup.EncodeSetWifi(Encoding.UTF8.GetBytes(ssid), Encoding.UTF8.GetBytes(password ?? "")));
        await WaitForAsync(BleSetup.EvWifiSaved, TimeSpan.FromSeconds(5));
    }

    public async Task FinishAsync()
    {
        try { await SendAsync(new[] { BleSetup.OpFinish }); } catch { /* the car may already have closed the link */ }
    }

    void ReleaseGatt()
    {
        if (_eventsChar != null && _onValue != null) _eventsChar.OnCharacteristicValueChanged -= _onValue;
        _info?.Dispose(); _control?.Dispose(); _eventsChar?.Dispose(); _service?.Dispose(); _server?.Dispose();
        _info = _control = _eventsChar = null; _service = null; _server = null; _onValue = null;
    }

    public ValueTask DisposeAsync()
    {
        try { if (_server?.Connected == true) _server.Disconnect(); } catch { }
        ReleaseGatt();
        _device?.Dispose();
        _device = null;
        return ValueTask.CompletedTask;
    }
}

public sealed record WifiNetwork(string Ssid, int Rssi);

/// <summary>An error message reported by the car itself.</summary>
public sealed class CarSetupException(string message) : CarBleException(message);
