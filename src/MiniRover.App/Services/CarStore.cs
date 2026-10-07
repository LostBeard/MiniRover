using SpawnDev.SpawnJS;
using SpawnDev.SpawnJS.JSObjects;

namespace MiniRover.App.Services;

/// <summary>A car this browser has paired with.</summary>
public sealed class SavedCar
{
    public string Name { get; set; } = "";
    /// <summary>20-byte signaling room key, hex. Whoever holds it can reach the car, so it never leaves this browser.</summary>
    public string RoomKeyHex { get; set; } = "";
    public string LastIp { get; set; } = "";
    public string Firmware { get; set; } = "";
    public DateTime PairedUtc { get; set; }

    // Driving feel, per car and per browser (the car's own top speed is a car setting).
    public double SteerGain { get; set; } = 0.8;
    public double Expo { get; set; } = 0.35;
    // Picture clean-up on the GPU (MiniRover.Video), per car and per browser. Off by default: it needs WebGPU.
    public bool VideoEnhance { get; set; }
    public double VideoDenoise { get; set; } = 0.5;
    public double VideoSharpen { get; set; } = 0.3;
    public bool VideoAutoLevels { get; set; } = true;
    public bool VideoWhiteBalance { get; set; } = true;

    /// <summary>Signaling server, empty = the default (SpawnDev hub). A LAN server (MiniRover.Server) goes here.</summary>
    public string TrackerUrl { get; set; } = "";
}

/// <summary>Which car to drive, and how: through the internet (Ble null), or in play mode with the Bluetooth device
/// the person picked (WebRTC signaling over BLE, the car's own WiFi).</summary>
public sealed record DriveRequest(SavedCar Car, CarSetupBle? Ble);

/// <summary>Paired cars, kept in this browser's localStorage.</summary>
public sealed class CarStore
{
    const string Key = "minirover.cars.v1";
    readonly SpawnJSRuntime _js;

    public CarStore(SpawnJSRuntime js) => _js = js;

    public List<SavedCar> GetAll()
    {
        try
        {
            using var window = _js.Get<Window>("window");
            using var storage = window.LocalStorage;
            return storage.ItemExists(Key) ? storage.GetJSON<List<SavedCar>>(Key) ?? new() : new();
        }
        catch (Exception ex)
        {
            Console.WriteLine("MiniRover: could not read saved cars: " + ex.Message);
            return new();
        }
    }

    public void Save(SavedCar car)
    {
        var all = GetAll();
        all.RemoveAll(c => c.Name == car.Name);
        all.Add(car);
        using var window = _js.Get<Window>("window");
        using var storage = window.LocalStorage;
        storage.SetJSON(Key, all);
    }

    public void Remove(string name)
    {
        var all = GetAll();
        all.RemoveAll(c => c.Name == name);
        using var window = _js.Get<Window>("window");
        using var storage = window.LocalStorage;
        storage.SetJSON(Key, all);
    }
}
