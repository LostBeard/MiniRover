using System.Collections.Concurrent;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using MiniRover.Protocol;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace MiniRover.ConsoleApp;

/// <summary>
/// Desktop (Windows) client for the car's BLE setup service. The browser app does the same over Web Bluetooth;
/// this one exists so setup can be driven and tested from a PC with real Bluetooth hardware.
/// </summary>
public sealed class BleSetupClient : IAsyncDisposable
{
    readonly BlockingCollection<byte[]> _events = new();
    BluetoothLEDevice? _device;
    GattCharacteristic? _info, _control, _eventsChar;

    public string DeviceName { get; private set; } = "";

    /// <summary>Scans until a car advertising the setup service (or a name starting with <paramref name="namePrefix"/>) appears.</summary>
    public async Task ConnectAsync(string namePrefix, TimeSpan timeout)
    {
        var found = new TaskCompletionSource<(ulong Address, string Name)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        watcher.Received += (_, e) =>
        {
            string name = e.Advertisement.LocalName ?? "";
            bool hasService = e.Advertisement.ServiceUuids.Contains(BleSetup.ServiceUuid);
            if (hasService || (name.Length > 0 && name.StartsWith(namePrefix, StringComparison.OrdinalIgnoreCase)))
            {
                found.TrySetResult((e.BluetoothAddress, name));
            }
        };
        watcher.Start();
        try
        {
            var winner = await Task.WhenAny(found.Task, Task.Delay(timeout));
            if (winner != found.Task) throw new TimeoutException($"no car advertising '{namePrefix}' or the setup service within {timeout.TotalSeconds:0} s");
        }
        finally
        {
            watcher.Stop();
        }

        var (address, advName) = await found.Task;
        _device = await BluetoothLEDevice.FromBluetoothAddressAsync(address) ?? throw new InvalidOperationException("could not open the BLE device");
        DeviceName = string.IsNullOrEmpty(_device.Name) ? advName : _device.Name;

        GattDeviceServicesResult services = await _device.GetGattServicesForUuidAsync(BleSetup.ServiceUuid, BluetoothCacheMode.Uncached);
        if (services.Status != GattCommunicationStatus.Success || services.Services.Count == 0)
            throw new InvalidOperationException($"setup service not found on {DeviceName} ({services.Status})");
        GattDeviceService service = services.Services[0];

        _info = await GetCharacteristic(service, BleSetup.InfoUuid);
        _control = await GetCharacteristic(service, BleSetup.ControlUuid);
        _eventsChar = await GetCharacteristic(service, BleSetup.EventsUuid);

        _eventsChar.ValueChanged += (_, e) => _events.Add(e.CharacteristicValue.ToArray());
        GattCommunicationStatus sub = await _eventsChar.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify);
        if (sub != GattCommunicationStatus.Success) throw new InvalidOperationException($"subscribe to events failed: {sub}");
    }

    static async Task<GattCharacteristic> GetCharacteristic(GattDeviceService service, Guid uuid)
    {
        GattCharacteristicsResult r = await service.GetCharacteristicsForUuidAsync(uuid, BluetoothCacheMode.Uncached);
        if (r.Status != GattCommunicationStatus.Success || r.Characteristics.Count == 0)
            throw new InvalidOperationException($"characteristic {uuid} not found ({r.Status})");
        return r.Characteristics[0];
    }

    public async Task<Dictionary<string, string>> ReadInfoAsync()
    {
        GattReadResult r = await _info!.ReadValueAsync(BluetoothCacheMode.Uncached);
        if (r.Status != GattCommunicationStatus.Success) throw new InvalidOperationException($"info read failed: {r.Status}");
        var map = new Dictionary<string, string>();
        foreach (string line in Encoding.UTF8.GetString(r.Value.ToArray()).Split('\n'))
        {
            int eq = line.IndexOf('=');
            if (eq > 0) map[line[..eq]] = line[(eq + 1)..];
        }
        return map;
    }

    public async Task SendAsync(byte[] frame)
    {
        GattCommunicationStatus s = await _control!.WriteValueAsync(frame.AsBuffer(), GattWriteOption.WriteWithResponse);
        if (s != GattCommunicationStatus.Success) throw new InvalidOperationException($"control write failed: {s}");
    }

    /// <summary>Waits for the next event with the given opcode; an Error event throws with the car's message.</summary>
    public byte[] WaitFor(byte opcode, TimeSpan timeout)
    {
        DateTime end = DateTime.UtcNow + timeout;
        while (true)
        {
            TimeSpan left = end - DateTime.UtcNow;
            if (left <= TimeSpan.Zero || !_events.TryTake(out byte[]? ev, left))
                throw new TimeoutException($"no event 0x{opcode:X2} within {timeout.TotalSeconds:0} s");
            if (ev.Length == 0) continue;
            if (ev[0] == BleSetup.EvError) throw new InvalidOperationException("car: " + Encoding.UTF8.GetString(ev, 1, ev.Length - 1));
            if (ev[0] == opcode) return ev;
        }
    }

    public async Task RequestCodeAsync()
    {
        await SendAsync([BleSetup.OpRequestCode]);
        WaitFor(BleSetup.EvCodeShown, TimeSpan.FromSeconds(5));
    }

    /// <summary>Returns (ok, attempts left).</summary>
    public async Task<(bool Ok, int AttemptsLeft)> SubmitCodeAsync(string code)
    {
        byte[] frame = new byte[1 + BleSetup.CodeDigits];
        frame[0] = BleSetup.OpSubmitCode;
        Encoding.ASCII.GetBytes(code, 0, BleSetup.CodeDigits, frame, 1);
        await SendAsync(frame);
        byte[] ev = WaitFor(BleSetup.EvAuthResult, TimeSpan.FromSeconds(5));
        return (ev[1] == 1, ev[2]);
    }

    public async Task<List<(string Ssid, int Rssi)>> ScanAsync()
    {
        await SendAsync([BleSetup.OpScan]);
        var list = new List<(string, int)>();
        DateTime end = DateTime.UtcNow + TimeSpan.FromSeconds(40);
        while (true)
        {
            if (!_events.TryTake(out byte[]? ev, end - DateTime.UtcNow > TimeSpan.Zero ? end - DateTime.UtcNow : TimeSpan.Zero))
                throw new TimeoutException("scan did not finish within 40 s");
            if (ev.Length == 0) continue;
            if (ev[0] == BleSetup.EvError) throw new InvalidOperationException("car: " + Encoding.UTF8.GetString(ev, 1, ev.Length - 1));
            if (ev[0] == BleSetup.EvScanDone) return list;
            if (ev[0] == BleSetup.EvScanResult && ev.Length >= 4)
                list.Add((Encoding.UTF8.GetString(ev, 4, ev[3]), (sbyte)ev[1]));
        }
    }

    public async Task<(byte[] RoomKey, string Name)> GetPairingAsync()
    {
        await SendAsync([BleSetup.OpGetPairing]);
        byte[] ev = WaitFor(BleSetup.EvPairingInfo, TimeSpan.FromSeconds(5));
        byte[] key = ev[1..(1 + BleSetup.RoomKeyBytes)];
        int n = ev[1 + BleSetup.RoomKeyBytes];
        return (key, Encoding.UTF8.GetString(ev, 2 + BleSetup.RoomKeyBytes, n));
    }

    /// <summary>Sends a hardware check command and waits for the car's acknowledgement.</summary>
    public async Task HardwareAsync(byte[] frame)
    {
        await SendAsync(frame);
        byte[] ok = WaitFor(BleSetup.EvOk, TimeSpan.FromSeconds(5));
        if (ok.Length < 2 || ok[1] != frame[0]) throw new InvalidOperationException($"unexpected ack for 0x{frame[0]:X2}");
    }

    public async Task<string> ReadSensorsAsync()
    {
        await SendAsync([BleSetup.OpReadSensors]);
        byte[] ev = WaitFor(BleSetup.EvSensors, TimeSpan.FromSeconds(5));
        return Encoding.UTF8.GetString(ev, 1, ev.Length - 1);
    }

    public async Task SetWifiAsync(string ssid, string password)
    {
        await SendAsync(BleSetup.EncodeSetWifi(Encoding.UTF8.GetBytes(ssid), Encoding.UTF8.GetBytes(password)));
        WaitFor(BleSetup.EvWifiSaved, TimeSpan.FromSeconds(5));
    }

    public async ValueTask DisposeAsync()
    {
        if (_eventsChar != null)
        {
            try { await _eventsChar.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.None); } catch { }
        }
        _device?.Dispose();
    }
}
