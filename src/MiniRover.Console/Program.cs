using System.Text.RegularExpressions;
using MiniRover.ConsoleApp;

// minirover setup [--name <prefix>] [--code <4 digits> | --code-from-debug <COMx>] [--scan] [--pairing] [--wifi <ssid> <password>]
// With no --code options the code shown on the car's LED eyes is asked for on the console.

// minirover link [--car name] [--key hex] ["drive 40 40 1000; servo 90 120; ..."]   (see LinkCommand.cs)
if (args.Length >= 1 && args[0] == "link") return await LinkCommand.RunAsync(args);

// minirover reboot <COMx>   restart the car's program (CLR) over USB, as if it had restarted
if (args.Length >= 2 && args[0] == "reboot")
{
    using var dbgReboot = NfDebugListener.Attach(args[1]);
    Console.WriteLine(dbgReboot.RebootClr() ? "car program restarting" : "reboot not supported by this debugger library");
    return 0;
}

// minirover monitor <COMx> [seconds]   print the car's debug output (Debug.WriteLine, exceptions)
if (args.Length >= 2 && args[0] == "monitor")
{
    using var mon = NfDebugListener.Attach(args[1]);
    var clock = System.Diagnostics.Stopwatch.StartNew();
    mon.Line += line => Console.WriteLine($"[{clock.Elapsed.TotalSeconds,7:F1}] {line}");
    await Task.Delay(TimeSpan.FromSeconds(args.Length > 2 ? int.Parse(args[2]) : 60));
    return 0;
}

// minirover drivetest <publish wwwroot> [httpPort] [cdpPort] [screenshotDir] [--reboot COMx] [--loss permille] [--car http://ip]   (THE WHEELS MUST BE OFF THE GROUND)
if (args.Length >= 2 && args[0] == "drivetest")
{
    return await DriveTest.RunAsync(args[1],
        args.Length > 2 ? int.Parse(args[2]) : 8641,
        args.Length > 3 ? int.Parse(args[3]) : 9241,
        args.Length > 4 && !args[4].StartsWith("--") ? args[4] : Path.Combine(Path.GetTempPath(), "minirover-drivetest-shots"),
        args.SkipWhile(a => a != "--reboot").Skip(1).FirstOrDefault(),
        int.TryParse(args.SkipWhile(a => a != "--loss").Skip(1).FirstOrDefault(), out int lossArg) ? lossArg : 0,
        args.SkipWhile(a => a != "--car").Skip(1).FirstOrDefault());
}

// minirover webtest <publish wwwroot> <COMx> [httpPort] [cdpPort] [screenshotDir]
if (args.Length >= 3 && args[0] == "webtest")
{
    return await WebTest.RunAsync(args[1], args[2],
        args.Length > 3 ? int.Parse(args[3]) : 8640,
        args.Length > 4 ? int.Parse(args[4]) : 9240,
        args.Length > 5 ? args[5] : Path.Combine(Path.GetTempPath(), "minirover-webtest-shots"));
}

// minirover hw <COMx> <command>; <command>; ...   (code read from the car's debug output)
//   sensors | servo center | servo pan|tilt <deg> | motor <0-3|all> <percent> <ms> | leds <r> <g> <b> | beep <hz> <ms> | set <key=value> | wait <ms>
if (args.Length >= 3 && args[0] == "hw")
{
    using var dbg = NfDebugListener.Attach(args[1]);
    var hwCode = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    dbg.Line += line =>
    {
        if (line.StartsWith("BLE") || line.Contains("Battery") || line.Contains("Exception")) Console.WriteLine("[car] " + line);
        Match m = Regex.Match(line, @"BLE setup code: (\d{4})");
        if (m.Success) hwCode.TrySetResult(m.Groups[1].Value);
    };
    await using var hw = new BleSetupClient();
    await hw.ConnectAsync("MiniRover", TimeSpan.FromSeconds(30));
    await hw.RequestCodeAsync();
    if (await Task.WhenAny(hwCode.Task, Task.Delay(10_000)) != hwCode.Task) throw new TimeoutException("no setup code on the debug channel");
    var (authOk, _) = await hw.SubmitCodeAsync(await hwCode.Task);
    if (!authOk) throw new InvalidOperationException("code rejected");

    foreach (string raw in string.Join(' ', args.Skip(2)).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        string[] p = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Console.WriteLine("> " + raw);
        try
        {
            switch (p[0])
            {
                case "sensors": Console.Write(await hw.ReadSensorsAsync()); break;
                case "servo" when p[1] == "center": await hw.HardwareAsync([MiniRover.Protocol.BleSetup.OpServo, 2, 0, 0]); break;
                case "servo":
                    int tenths = (int)Math.Round(double.Parse(p[2]) * 10);
                    await hw.HardwareAsync([MiniRover.Protocol.BleSetup.OpServo, (byte)(p[1] == "pan" ? 0 : 1), (byte)(tenths >> 8), (byte)tenths]);
                    break;
                case "motor":
                    int ms = int.Parse(p[3]);
                    await hw.HardwareAsync([MiniRover.Protocol.BleSetup.OpMotor, p[1] == "all" ? (byte)0xFF : byte.Parse(p[1]), (byte)(sbyte)int.Parse(p[2]), (byte)(ms >> 8), (byte)ms]);
                    break;
                case "leds": await hw.HardwareAsync([MiniRover.Protocol.BleSetup.OpLeds, byte.Parse(p[1]), byte.Parse(p[2]), byte.Parse(p[3])]); break;
                case "beep":
                    int hz = int.Parse(p[1]), bms = int.Parse(p[2]);
                    await hw.HardwareAsync([MiniRover.Protocol.BleSetup.OpBuzzer, (byte)(hz >> 8), (byte)hz, (byte)(bms >> 8), (byte)bms]);
                    break;
                case "set": await hw.HardwareAsync([MiniRover.Protocol.BleSetup.OpSetting, .. System.Text.Encoding.UTF8.GetBytes(p[1])]); break;
                case "wait": await Task.Delay(int.Parse(p[1])); break;
                case "showcode":
                    // Shows a fresh code on the LED eyes (this ends the authorisation, so keep it last).
                    await hw.RequestCodeAsync();
                    break;
                default: Console.WriteLine("  unknown command"); break;
            }
            Console.WriteLine("  ok");
        }
        catch (Exception ex)
        {
            Console.WriteLine("  FAILED: " + ex.Message);
        }
    }
    return 0;
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
        Console.WriteLine($"pairing: name={name}");
        CarKeys.Save(name, key);
    }
    if (ssid != null)
    {
        await client.SetWifiAsync(ssid, password!);
        Console.WriteLine($"network '{ssid}' saved; the car is restarting to join it");
    }
}

debug?.Dispose();
return 0;
