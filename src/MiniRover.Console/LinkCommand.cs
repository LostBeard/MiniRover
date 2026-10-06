using Microsoft.Extensions.Logging;
using System.Diagnostics;
using MiniRover.Client;
using MiniRover.Protocol;

namespace MiniRover.ConsoleApp;

/// <summary>
/// Pairing keys this computer holds, one "name keyhex" line per car in %LOCALAPPDATA%\MiniRover\cars.txt (never in
/// the repo). Written by <c>setup --pairing</c>.
/// </summary>
static class CarKeys
{
    static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MiniRover", "cars.txt");

    public static void Save(string name, byte[] key)
    {
        var lines = Load().Where(c => c.Name != name).Select(c => c.Name + " " + c.KeyHex).ToList();
        lines.Add(name + " " + Convert.ToHexString(key).ToLowerInvariant());
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllLines(FilePath, lines);
        Console.WriteLine($"pairing key for '{name}' saved to {FilePath}");
    }

    public static List<(string Name, string KeyHex)> Load()
    {
        if (!File.Exists(FilePath)) return [];
        return File.ReadAllLines(FilePath)
            .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(p => p.Length == 2)
            .Select(p => (p[0], p[1])).ToList();
    }
}

/// <summary>
/// minirover link [--car name] [--key hex] [--tracker url] ["command; command; ..."]
///   drive &lt;left%&gt; &lt;right%&gt; &lt;ms&gt;   resend every 100 ms for ms, then stop sending (the car's deadman stops it)
///   once &lt;left%&gt; &lt;right%&gt; &lt;hold&gt;  send ONE drive frame and time how long until telemetry says stopped
///   servo &lt;pan&gt; &lt;tilt&gt; | leds &lt;r&gt; &lt;g&gt; &lt;b&gt; | beep &lt;hz&gt; &lt;ms&gt; | stop | wait &lt;ms&gt; | telemetry &lt;ms&gt;
/// With no commands: connect, print telemetry for 5 s, disconnect.
/// </summary>
static class LinkCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        string? carName = null, keyHex = null, tracker = null;
        var script = new List<string>();
        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--car": carName = args[++i]; break;
                case "--key": keyHex = args[++i]; break;
                case "--tracker": tracker = args[++i]; break;
                case "--trace":
                    // SipSorcery's own log (ICE, DTLS, SCTP) on the console, to see the desktop side of a link problem.
                    SIPSorcery.LogFactory.Set(Microsoft.Extensions.Logging.LoggerFactory.Create(b =>
                        b.AddSimpleConsole(o => o.TimestampFormat = "HH:mm:ss.fff ").SetMinimumLevel(LogLevel.Debug)));
                    break;
                default: script.Add(args[i]); break;
            }
        }
        if (keyHex == null)
        {
            var cars = CarKeys.Load();
            var car = carName == null ? cars.LastOrDefault() : cars.FirstOrDefault(c => c.Name == carName);
            if (car.KeyHex == null)
            {
                Console.Error.WriteLine("No pairing key. Pair first: switch the car off and on, then run `minirover setup --pairing`.");
                return 2;
            }
            keyHex = car.KeyHex;
            carName = car.Name;
        }

        await using var link = CarConnection.FromKeyHex(keyHex, tracker);
        var sw = Stopwatch.StartNew();
        link.OnStatus += s => Console.WriteLine($"[{sw.ElapsedMilliseconds,6} ms] {s}");
        int telemetryCount = 0;
        CarLink.Telemetry? last = null;
        bool print = false;
        long lastTelemetryMs = -1, maxTelemetryGapMs = 0;
        link.OnTelemetry += t =>
        {
            long nowMs = sw.ElapsedMilliseconds;
            if (lastTelemetryMs >= 0) maxTelemetryGapMs = Math.Max(maxTelemetryGapMs, nowMs - lastTelemetryMs);
            lastTelemetryMs = nowMs;
            telemetryCount++;
            last = t;
            if (print) Console.WriteLine($"  telemetry #{t.Seq}: {t.BatteryMillivolts / 1000.0:F2} V level {t.BatteryLevel} moving={t.Moving} " +
                                         $"light={t.Light} line={t.Line} pan={t.PanTenths / 10.0} tilt={t.TiltTenths / 10.0} heap={t.FreeHeapKb} KB wifi={t.Rssi} dBm");
        };
        int videoFrames = 0;
        long videoBytes = 0;
        byte[]? lastFrame = null;
        link.OnVideoChannel += ch => ch.OnBinaryMessage += f => { videoFrames++; videoBytes += f.Length; lastFrame = f; };
        link.OnText += t => Console.WriteLine("  car says: " + t);

        Console.WriteLine($"connecting to '{carName ?? "car"}' via {tracker ?? CarLink.DefaultTrackerUrl} (switch the car on if it is off)");
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120)))
        {
            await link.ConnectAsync(cts.Token);
        }
        Console.WriteLine($"connected to {link.CarName} (protocol {link.CarProtocolVersion}) in {sw.ElapsedMilliseconds} ms");

        if (script.Count == 0) script.Add("telemetry 5000");
        foreach (string raw in string.Join(' ', script).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] p = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            Console.WriteLine("> " + raw);
            switch (p[0])
            {
                case "telemetry":
                {
                    int before = telemetryCount;
                    print = true;
                    await Task.Delay(int.Parse(p[1]));
                    print = false;
                    Console.WriteLine($"  {telemetryCount - before} telemetry frames in {p[1]} ms");
                    break;
                }
                case "drive":
                {
                    int l = int.Parse(p[1]), r = int.Parse(p[2]), ms = int.Parse(p[3]);
                    var end = Stopwatch.StartNew();
                    while (end.ElapsedMilliseconds < ms)
                    {
                        link.Drive(l, r, 300);
                        await Task.Delay(100);
                    }
                    break;
                }
                case "once":
                {
                    // Deadman over the real link: one frame, then silence. The car must stop by itself after `hold`.
                    int hold = int.Parse(p[3]);
                    var t0 = Stopwatch.StartNew();
                    link.Drive(int.Parse(p[1]), int.Parse(p[2]), hold);
                    bool sawMoving = false;
                    long stoppedAt = -1;
                    while (t0.ElapsedMilliseconds < hold + 3000)
                    {
                        await Task.Delay(10);
                        if (last == null) continue;
                        if (last.Moving) sawMoving = true;
                        else if (sawMoving) { stoppedAt = t0.ElapsedMilliseconds; break; }
                    }
                    Console.WriteLine(stoppedAt > 0
                        ? $"  moving seen, stopped by the car after {stoppedAt} ms (hold {hold} ms, telemetry every ~200 ms)"
                        : $"  FAIL: {(sawMoving ? "never stopped" : "never seen moving")}");
                    break;
                }
                case "video":
                {
                    // video <ms> [size] [quality] [fps]: stream, count frames, save the last one as a JPEG to check it decodes.
                    int ms = int.Parse(p[1]);
                    int size = p.Length > 2 ? int.Parse(p[2]) : 0, q = p.Length > 3 ? int.Parse(p[3]) : 0, fps = p.Length > 4 ? int.Parse(p[4]) : 0;
                    maxTelemetryGapMs = 0;
                    int t0 = telemetryCount;
                    int f0 = videoFrames;
                    long b0 = videoBytes;
                    link.Video(true, size, q, fps);
                    var vw = Stopwatch.StartNew();
                    await Task.Delay(ms);
                    link.Video(false);
                    int n = videoFrames - f0;
                    Console.WriteLine($"  {n} frames in {vw.ElapsedMilliseconds} ms = {n * 1000.0 / vw.ElapsedMilliseconds:F1} fps, avg {(n > 0 ? (videoBytes - b0) / n : 0)} bytes, car reports {last?.VideoFps} fps");
                    Console.WriteLine($"  telemetry during video: {telemetryCount - t0} messages, longest gap {maxTelemetryGapMs} ms");
                    if (lastFrame != null)
                    {
                        bool jpeg = lastFrame.Length > 4 && lastFrame[0] == 0xFF && lastFrame[1] == 0xD8 && lastFrame[^2] == 0xFF && lastFrame[^1] == 0xD9;
                        string file = Path.Combine(Path.GetTempPath(), "minirover-frame.jpg");
                        File.WriteAllBytes(file, lastFrame);
                        Console.WriteLine($"  last frame {lastFrame.Length} bytes, JPEG start/end markers {(jpeg ? "OK" : "MISSING")}, saved {file}");
                    }
                    break;
                }
                case "set": link.Setting(p[1]); break;
                case "load":
                {
                    // load <ms> <left%> <right%>: drive commands at 20 Hz (like the browser) while counting telemetry.
                    int ms = int.Parse(p[1]), l = int.Parse(p[2]), r = int.Parse(p[3]);
                    int t0 = telemetryCount;
                    maxTelemetryGapMs = 0;
                    lastTelemetryMs = sw.ElapsedMilliseconds;
                    var lw = Stopwatch.StartNew();
                    int moving = 0;
                    while (lw.ElapsedMilliseconds < ms)
                    {
                        link.Drive(l, r, 300);
                        if (last?.Moving == true) moving++;
                        await Task.Delay(50);
                    }
                    link.Stop();
                    Console.WriteLine($"  {telemetryCount - t0} telemetry in {ms} ms while driving at 20 Hz, longest gap {maxTelemetryGapMs} ms, 'moving' seen in {moving} of {ms / 50} samples");
                    break;
                }
                case "lights": link.Lights(int.Parse(p[1]), 40, 40, 40, p.Length > 2 ? int.Parse(p[2]) : 0); break;
                // face alive | face mood <n> | face text <passes> <words...>
                case "face" when p.Length > 1 && p[1] == "alive": link.Face(CarLink.FaceAlive); break;
                case "face" when p.Length > 2 && p[1] == "mood": link.Face(CarLink.FaceMood, int.Parse(p[2])); break;
                case "face" when p.Length > 3 && p[1] == "text": link.Face(CarLink.FaceText, int.Parse(p[2]), string.Join(' ', p[3..])); break;
                case "videoon": link.Video(true); break;
                case "videooff": link.Video(false); break;
                case "stop": link.Stop(); break;
                case "servo": link.Servo(double.Parse(p[1]), double.Parse(p[2])); break;
                case "leds": link.Leds(byte.Parse(p[1]), byte.Parse(p[2]), byte.Parse(p[3])); break;
                case "beep": link.Buzzer(int.Parse(p[1]), int.Parse(p[2])); break;
                case "wait": await Task.Delay(int.Parse(p[1])); break;
                default: Console.WriteLine("  unknown command"); break;
            }
        }
        Console.WriteLine($"done: {telemetryCount} telemetry frames, {videoFrames} video frames, last battery {(last?.BatteryMillivolts ?? 0) / 1000.0:F2} V");
        return 0;
    }
}
