using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using static MiniRover.ConsoleApp.WebTest;

namespace MiniRover.ConsoleApp;

/// <summary>
/// Hardware-in-the-loop browser test of the drive page against a REAL car over the REAL WebRTC link (car -> tracker ->
/// Chrome): seeds the pairing key this computer holds into a throwaway Chrome profile, opens the published app, presses
/// Drive and asserts on named DOM hooks: connected, live battery + WiFi telemetry, then a real touch drag and real key
/// presses must make the car report "moving" and, on release, "stopped". THE WHEELS MUST BE OFF THE GROUND.
/// </summary>
public static class DriveTest
{
    public static async Task<int> RunAsync(string webRoot, int httpPort, int cdpPort, string shotDir, string? rebootPort = null, int lossPermille = 0, string? carHttp = null, bool calibrateLights = false)
    {
        if (!File.Exists(Path.Combine(webRoot, "index.html"))) throw new FileNotFoundException("publish the app first", Path.Combine(webRoot, "index.html"));
        var (name, keyHex) = CarKeys.Load().LastOrDefault();
        if (keyHex == null) { Console.Error.WriteLine("No pairing key: run `minirover setup --pairing` first."); return 2; }
        // A busy port means someone else's server or browser (a teammate's Chrome once got driven by mistake).
        foreach (int port in new[] { httpPort, cdpPort })
        {
            if (!PortFree(port)) { Console.Error.WriteLine($"port {port} is in use; pick another"); return 2; }
        }
        Directory.CreateDirectory(shotDir);

        using var server = StaticServer.Start(webRoot, httpPort);
        string profile = Path.Combine(Path.GetTempPath(), "minirover-drivetest-chrome");
        if (Directory.Exists(profile)) Directory.Delete(profile, true);
        using Process chrome = Process.Start(new ProcessStartInfo
        {
            FileName = @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            ArgumentList = { $"--remote-debugging-port={cdpPort}", $"--user-data-dir={profile}", "--no-first-run",
                             "--no-default-browser-check", "--window-size=1200,950", "about:blank" },
            UseShellExecute = false,
        })!;
        Console.WriteLine($"chrome PID {chrome.Id} on CDP {cdpPort}, app on http://localhost:{httpPort}/");
        try
        {
            await using var cdp = await Cdp.ConnectAsync(cdpPort);
            await cdp.SendAsync("Runtime.enable");
            await cdp.SendAsync("Page.enable");
            await cdp.SendAsync("Emulation.setTouchEmulationEnabled", new JsonObject { ["enabled"] = true, ["maxTouchPoints"] = 2 });
            var car = new JsonArray(new JsonObject { ["Name"] = name, ["RoomKeyHex"] = keyHex, ["LastIp"] = "", ["Firmware"] = "test", ["PairedUtc"] = DateTime.UtcNow.ToString("o") });
            string seed = $"if (location.origin === 'http://localhost:{httpPort}') localStorage.setItem('minirover.cars.v1', {JsonSerializer.Serialize(car.ToJsonString())});";
            await cdp.SendAsync("Page.addScriptToEvaluateOnNewDocument", new JsonObject { ["source"] = seed });
            await cdp.SendAsync("Page.navigate", new JsonObject { ["url"] = $"http://localhost:{httpPort}/" + (lossPermille > 0 ? $"?testloss={lossPermille}" : "") });
            if (lossPermille > 0) Console.WriteLine($"loss test: the car drops {lossPermille / 10.0:F1}% of the datagrams it sends");

            await WaitForAsync(cdp, "[data-test=garage-drive]", TimeSpan.FromSeconds(60), "the garage with a Drive button");
            Console.WriteLine($"PASS garage lists {name}");
            var sw = Stopwatch.StartNew();
            await ClickAsync(cdp, "[data-test=garage-drive]");
            await WaitForAsync(cdp, "[data-test=drive][data-state=connected]", TimeSpan.FromSeconds(95), "the drive page to connect to the car");
            Console.WriteLine($"PASS connected over WebRTC from Chrome in {sw.ElapsedMilliseconds} ms");

            int mv = await WaitForIntAsync(cdp, "[data-test=hud-battery]", "data-volts", v => v > 0, "battery telemetry");
            int rssi = await WaitForIntAsync(cdp, "[data-test=hud-wifi]", "data-rssi", v => v != 0, "WiFi signal telemetry");
            string hud = (await EvalAsync(cdp, Deep("[data-test=drive-hud]") + ".textContent.replace(/\\s+/g,' ').trim()")).GetValue<string>();
            Console.WriteLine($"PASS telemetry: battery {mv / 1000.0:F2} V, WiFi {rssi} dBm; HUD: {hud}");
            // Video: JPEG frames from the car decoded onto the canvas (the data-frames hook counts decoded frames).
            sw.Restart();
            int frames = await WaitForIntAsync(cdp, "[data-test=drive-video]", "data-frames", v => v >= 30, "30 decoded video frames");
            int f0 = frames;
            await Task.Delay(3000);
            int f1 = await WaitForIntAsync(cdp, "[data-test=drive-video]", "data-frames", v => v > f0, "more decoded frames");
            JsonNode size = await EvalAsync(cdp, "(()=>{const c=" + Deep("[data-test=drive-video] canvas") + ";return {w:c.width,h:c.height};})()");
            Console.WriteLine($"PASS video: first 30 frames decoded within {sw.ElapsedMilliseconds} ms, then {(f1 - f0) / 3.0:F1} fps decoded, canvas {size["w"]}x{size["h"]}");
            await cdp.ScreenshotAsync(Path.Combine(shotDir, "drive-1-connected.png"));

            int rtt = await WaitForIntAsync(cdp, "[data-test=hud-rtt]", "data-rtt", v => v > 0, "a link round-trip measurement");
            Console.WriteLine($"PASS link round trip {rtt} ms");
            await ClickAsync(cdp, "[data-test=btn-fullscreen]");
            await Task.Delay(500);
            bool full = (await EvalAsync(cdp, "document.fullscreenElement !== null")).GetValue<bool>();
            if (!full) throw new Exception("FAIL Full screen button did not enter full screen");
            await ClickAsync(cdp, "[data-test=btn-fullscreen]");
            await Task.Delay(500);
            if ((await EvalAsync(cdp, "document.fullscreenElement !== null")).GetValue<bool>()) throw new Exception("FAIL Full screen did not exit");
            Console.WriteLine("PASS full screen on and off");

            // Telemetry must keep flowing while video streams (it once starved: the car fell behind on commands).
            string rateText = (await EvalAsync(cdp, Deep("[data-test=hud-rate]") + ".textContent")).GetValue<string>();
            if (!int.TryParse(rateText.Split('/')[0], out int rate) || rate < 3) throw new Exception($"FAIL telemetry during video is {rateText}, want >= 3/s");
            Console.WriteLine($"PASS telemetry during video: {rateText}");

            // Touch: a real touch drag straight up on the drive stick (full deflection), held, then released.
            JsonNode rect = await EvalAsync(cdp, "(()=>{const r=" + Deep("[data-test=stick-drive]") + ".getBoundingClientRect();return {x:r.x+r.width/2,y:r.y+r.height/2};})()");
            double cx = rect["x"]!.GetValue<double>(), cy = rect["y"]!.GetValue<double>();
            await Touch(cdp, "touchStart", cx, cy);
            for (int i = 1; i <= 8; i++) await Touch(cdp, "touchMove", cx, cy - i * 12);
            sw.Restart();
            await WaitForAttrAsync(cdp, "[data-test=hud-moving]", "data-moving", "1", TimeSpan.FromSeconds(3), "the car to report moving (touch)");
            Console.WriteLine($"PASS touch drag: car reports moving after {sw.ElapsedMilliseconds} ms");
            await cdp.ScreenshotAsync(Path.Combine(shotDir, "drive-2-touch.png"));
            await Task.Delay(700);
            await cdp.SendAsync("Input.dispatchTouchEvent", new JsonObject { ["type"] = "touchEnd", ["touchPoints"] = new JsonArray() });
            sw.Restart();
            await WaitForAttrAsync(cdp, "[data-test=hud-moving]", "data-moving", "0", TimeSpan.FromSeconds(3), "the car to report stopped after release");
            Console.WriteLine($"PASS released: car reports stopped after {sw.ElapsedMilliseconds} ms");

            // Keyboard: hold W.
            await Key(cdp, "keyDown", "w", "KeyW", 87);
            sw.Restart();
            await WaitForAttrAsync(cdp, "[data-test=hud-moving]", "data-moving", "1", TimeSpan.FromSeconds(3), "the car to report moving (key W)");
            Console.WriteLine($"PASS key W: car reports moving after {sw.ElapsedMilliseconds} ms");
            await Task.Delay(500);
            await Key(cdp, "keyUp", "w", "KeyW", 87);
            sw.Restart();
            await WaitForAttrAsync(cdp, "[data-test=hud-moving]", "data-moving", "0", TimeSpan.FromSeconds(3), "the car to report stopped after key up");
            Console.WriteLine($"PASS key up: car reports stopped after {sw.ElapsedMilliseconds} ms");

            if (carHttp != null)
            {
                // Lights: every press of the button selects the next pattern on the car (read back from /status).
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                for (int press = 0; press < 7; press++)
                {
                    await ClickAsync(cdp, "[data-test=btn-lights]");
                    string want = (await EvalAsync(cdp, Deep("[data-test=btn-lights]") + ".getAttribute('data-mode')")).GetValue<string>();
                    string got = "";
                    for (int i = 0; i < 15 && got != want; i++)
                    {
                        await Task.Delay(400);
                        got = JsonNode.Parse(await http.GetStringAsync(carHttp.TrimEnd('/') + "/status"))!["lights"]!["mode"]!.ToString();
                    }
                    if (got != want) throw new Exception($"FAIL lights: the app selected mode {want}, the car runs {got}");
                }
                Console.WriteLine("PASS lights: all 7 patterns selected on the car in turn");
            }

            // Settings: the panel loads the car's values, a wheel test spins a wheel, a change is stored by the car.
            await ClickAsync(cdp, "[data-test=btn-settings]");
            await WaitForAsync(cdp, "[data-test=set-camera-size]", TimeSpan.FromSeconds(10), "the settings panel with the car's values");
            string camSize = (await EvalAsync(cdp, Deep("[data-test=set-camera-size]") + ".value")).GetValue<string>();
            if (carHttp != null)
            {
                // The panel must show what the car actually has (a select once showed its first option instead).
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                string carSize = JsonNode.Parse(await http.GetStringAsync(carHttp.TrimEnd('/') + "/status"))!["settings"]!["camera.size"]!.GetValue<string>();
                if (carSize != camSize) throw new Exception($"FAIL settings panel shows camera size {camSize}, the car has {carSize}");
            }
            Console.WriteLine($"PASS settings loaded (camera size {camSize}{(carHttp != null ? ", matches the car" : "")})");
            sw.Restart();
            await ClickAsync(cdp, "[data-test=wheel-test-0]");
            await WaitForAttrAsync(cdp, "[data-test=hud-moving]", "data-moving", "1", TimeSpan.FromSeconds(3), "the wheel test to move a wheel");
            await WaitForAttrAsync(cdp, "[data-test=hud-moving]", "data-moving", "0", TimeSpan.FromSeconds(3), "the wheel to stop after its test");
            Console.WriteLine($"PASS wheel test: front-left wheel ran and stopped within {sw.ElapsedMilliseconds} ms");
            string other = camSize == "8" ? "6" : "8";
            await EvalAsync(cdp, "(()=>{const s=" + Deep("[data-test=set-camera-size]") + ";s.value='" + other + "';s.dispatchEvent(new Event('change',{bubbles:true}));return true;})()");
            await WaitForTextAsync(cdp, "[data-test=settings-reply]", "saved camera.size=" + other, "the car to confirm the new picture size");
            Console.WriteLine($"PASS setting stored by the car (camera.size {camSize} -> {other})");
            await EvalAsync(cdp, "(()=>{const s=" + Deep("[data-test=set-camera-size]") + ";s.value='" + camSize + "';s.dispatchEvent(new Event('change',{bubbles:true}));return true;})()");
            await WaitForTextAsync(cdp, "[data-test=settings-reply]", "saved camera.size=" + camSize, "the car to restore the picture size");

            if (calibrateLights)
            {
                // Light corners: four groups, answered front-left, front-right, rear-left, rear-right. NOTE: this stores
                // those answers on the car; restore the real layout afterwards (Settings > Lights).
                await ClickAsync(cdp, "[data-test=lights-calibrate]");
                for (int group = 0; group < 4; group++)
                {
                    await WaitForTextAsync(cdp, "[data-test=lights-cal-step]", $"Group {group + 1} of 4", $"calibration step {group + 1}");
                    await ClickAsync(cdp, $"[data-test=corner-{group}]");
                }
                await WaitForTextAsync(cdp, "[data-test=settings-reply]", "saved led.corners=000111222333", "the car to store the light corners");
                Console.WriteLine("PASS light corner calibration stored on the car (000111222333)");
            }
            await ClickAsync(cdp, "[data-test=btn-settings]");

            // Reconnect: restart the car's program over USB (looks like a car restart to the app) and expect the page to
            // come back by itself, video included.
            if (rebootPort != null)
            {
                using (var dbg = NfDebugListener.Attach(rebootPort))
                {
                    if (!dbg.RebootClr()) throw new Exception("FAIL could not restart the car over " + rebootPort);
                }
                sw.Restart();
                await WaitForAsync(cdp, "[data-test=drive][data-state=reconnecting]", TimeSpan.FromSeconds(60), "the page to notice the car went away");
                Console.WriteLine($"PASS car restarted: page shows reconnecting after {sw.ElapsedMilliseconds} ms");
                await WaitForAsync(cdp, "[data-test=drive][data-state=connected][data-reconnects='1']", TimeSpan.FromSeconds(150), "the page to reconnect");
                long back = sw.ElapsedMilliseconds;
                int before = await WaitForIntAsync(cdp, "[data-test=drive-video]", "data-frames", v => v > 0, "video after reconnect");
                await Task.Delay(5000);
                int after = await WaitForIntAsync(cdp, "[data-test=drive-video]", "data-frames", v => v > before, "video frames after reconnect");
                Console.WriteLine($"PASS reconnected {back} ms after the restart; video running again ({(after - before) / 5.0:F1} fps)");
            }

            if (carHttp != null)
            {
                // The car's own counters for this session (retransmissions, abandoned video chunks, FORWARD-TSN).
                try
                {
                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                    var link = JsonNode.Parse(await http.GetStringAsync(carHttp.TrimEnd('/') + "/status"))!["link"]!;
                    Console.WriteLine($"  car link: retransmits {link["sctpRetransmits"]}, abandoned {link["sctpAbandoned"]}, FORWARD-TSN {link["sctpForwardTsn"]}, " +
                                      $"peer supports FORWARD-TSN {link["peerForwardTsn"]}, test-dropped {link["testDropped"]}, unprotected {link["sctpUnprotected"]}");
                }
                catch (Exception ex) { Console.WriteLine("  car status not readable: " + ex.Message); }
            }

            await ClickAsync(cdp, "[data-test=btn-horn]");
            await ClickAsync(cdp, "[data-test=drive-back]");
            await WaitForAsync(cdp, "[data-test=garage-drive]", TimeSpan.FromSeconds(10), "the garage after Back");
            Console.WriteLine("PASS back to the garage (link closed)");
            Console.WriteLine("ALL PASS");
            return 0;
        }
        finally
        {
            try { if (!chrome.HasExited) chrome.Kill(entireProcessTree: true); } catch { }
            try { Directory.Delete(profile, true); } catch { }
        }
    }

    static bool PortFree(int port)
    {
        try { using var l = new TcpListener(IPAddress.Loopback, port); l.Start(); l.Stop(); return true; }
        catch (SocketException) { return false; }
    }

    static Task Touch(Cdp cdp, string type, double x, double y) =>
        cdp.SendAsync("Input.dispatchTouchEvent", new JsonObject
        {
            ["type"] = type,
            ["touchPoints"] = new JsonArray(new JsonObject { ["x"] = x, ["y"] = y, ["id"] = 1 }),
        });

    static Task Key(Cdp cdp, string type, string key, string code, int keyCode)
    {
        var p = new JsonObject { ["type"] = type, ["key"] = key, ["code"] = code, ["windowsVirtualKeyCode"] = keyCode };
        if (type == "keyDown") p["text"] = key; // CDP rejects a null text
        return cdp.SendAsync("Input.dispatchKeyEvent", p);
    }

    static async Task<int> WaitForIntAsync(Cdp cdp, string selector, string attr, Func<int, bool> ok, string what)
    {
        DateTime end = DateTime.UtcNow.AddSeconds(10);
        int v = 0;
        while (DateTime.UtcNow < end)
        {
            string s = (await EvalAsync(cdp, Deep(selector) + $"?.getAttribute('{attr}') ?? ''")).GetValue<string>();
            if (int.TryParse(s, out v) && ok(v)) return v;
            await Task.Delay(200);
        }
        throw new Exception($"FAIL no {what} ({selector} {attr}={v})");
    }

    static async Task WaitForTextAsync(Cdp cdp, string selector, string text, string what)
    {
        DateTime end = DateTime.UtcNow.AddSeconds(8);
        string last = "";
        while (DateTime.UtcNow < end)
        {
            last = (await EvalAsync(cdp, Deep(selector) + "?.textContent ?? ''")).GetValue<string>();
            if (last.Contains(text)) return;
            await Task.Delay(100);
        }
        throw new Exception($"FAIL timed out waiting for {what} (last: '{last}')");
    }

    static async Task WaitForAttrAsync(Cdp cdp, string selector, string attr, string value, TimeSpan timeout, string what)
    {
        DateTime end = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < end)
        {
            if ((await EvalAsync(cdp, Deep(selector) + $"?.getAttribute('{attr}') ?? ''")).GetValue<string>() == value) return;
            await Task.Delay(50);
        }
        throw new Exception($"FAIL timed out waiting for {what}");
    }
}
