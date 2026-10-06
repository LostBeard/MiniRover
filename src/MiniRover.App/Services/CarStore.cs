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
}

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
