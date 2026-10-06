using System.Text.RegularExpressions;
using MiniRover.ConsoleApp;

// minirover setup [--name <prefix>] [--code <4 digits> | --code-from-debug <COMx>] [--scan] [--pairing] [--wifi <ssid> <password>]
// With no --code options the code shown on the car's LED eyes is asked for on the console.

// minirover webtest <publish wwwroot> <COMx> [httpPort] [cdpPort] [screenshotDir]
if (args.Length >= 3 && args[0] == "webtest")
{
    return await WebTest.RunAsync(args[1], args[2],
        args.Length > 3 ? int.Parse(args[3]) : 8640,
        args.Length > 4 ? int.Parse(args[4]) : 9240,
        args.Length > 5 ? args[5] : Path.Combine(Path.GetTempPath(), "minirover-webtest-shots"));
}

if (args.Length == 0 || args[0] != "setup")
{
    Console.WriteLine("usage: minirover setup [--name MiniRover] [--code 1234 | --code-from-debug COM8] [--scan] [--pairing] [--wifi <ssid> <password>]");
    return 2;
}

string namePrefix = "MiniRover";
string? code = null, debugPort = null, ssid = null, password = null;
bool scan = false, pairing = false;
for (int i = 1; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--name": namePrefix = args[++i]; break;
        case "--code": code = args[++i]; break;
        case "--code-from-debug": debugPort = args[++i]; break;
        case "--scan": scan = true; break;
        case "--pairing": pairing = true; break;
        case "--wifi": ssid = args[++i]; password = args[++i]; break;
        default: Console.WriteLine("unknown option " + args[i]); return 2;
    }
}

NfDebugListener? debug = null;
var codeFromDebug = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
if (debugPort != null)
{
    debug = NfDebugListener.Attach(debugPort);
    debug.Line += line =>
    {
        Console.WriteLine("[car] " + line);
        Match m = Regex.Match(line, @"BLE setup code: (\d{4})");
        if (m.Success) codeFromDebug.TrySetResult(m.Groups[1].Value);
    };
    Console.WriteLine($"listening to debug output on {debugPort}");
}

await using var client = new BleSetupClient();
Console.WriteLine($"scanning for '{namePrefix}'...");
await client.ConnectAsync(namePrefix, TimeSpan.FromSeconds(30));
Console.WriteLine($"connected to {client.DeviceName}");

foreach (var (k, v) in await client.ReadInfoAsync()) Console.WriteLine($"  {k} = {v}");

if (scan || pairing || ssid != null)
{
    await client.RequestCodeAsync();
    if (debugPort != null)
    {
        var winner = await Task.WhenAny(codeFromDebug.Task, Task.Delay(10_000));
        if (winner != codeFromDebug.Task) throw new TimeoutException("the car did not print a setup code on the debug channel");
        code = await codeFromDebug.Task;
        Console.WriteLine($"code from debug output: {code}");
    }
    else if (code == null)
    {
        Console.Write("code shown on the car: ");
        code = Console.ReadLine()?.Trim() ?? "";
    }

    var (ok, left) = await client.SubmitCodeAsync(code!);
    Console.WriteLine(ok ? "code accepted" : $"code rejected ({left} attempts left)");
    if (!ok) return 1;

    if (scan)
    {
        var nets = await client.ScanAsync();
        Console.WriteLine($"{nets.Count} networks:");
        foreach (var (s, rssi) in nets.OrderByDescending(n => n.Rssi)) Console.WriteLine($"  {rssi,4} dBm  {s}");
    }
    if (pairing)
    {
        var (key, name) = await client.GetPairingAsync();
        Console.WriteLine($"pairing: name={name} roomKey={Convert.ToHexString(key).ToLowerInvariant()}");
    }
    if (ssid != null)
    {
        await client.SetWifiAsync(ssid, password!);
        Console.WriteLine($"network '{ssid}' saved; the car is restarting to join it");
    }
}

debug?.Dispose();
return 0;
