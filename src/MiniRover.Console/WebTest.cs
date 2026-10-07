using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MiniRover.ConsoleApp;

/// <summary>
/// Hardware-in-the-loop browser test of the web app's BLE setup wizard, against a REAL car over REAL Bluetooth:
/// serves the published app, launches its own Chrome (own profile + port, killed by PID, never the user's browser),
/// drives the wizard over CDP with real user gestures, answers Chrome's Bluetooth device chooser through the CDP
/// DeviceAccess domain, reads the car's setup code from its debug output, and asserts on named DOM hooks.
/// It does NOT send a WiFi network (that needs the owner's real credentials).
/// </summary>
public static class WebTest
{
    public static async Task<int> RunAsync(string webRoot, string debugPort, int httpPort, int cdpPort, string shotDir)
    {
        if (!File.Exists(Path.Combine(webRoot, "index.html"))) throw new FileNotFoundException("publish the app first", Path.Combine(webRoot, "index.html"));
        Directory.CreateDirectory(shotDir);

        using var debug = NfDebugListener.Attach(debugPort);
        var code = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        debug.Line += line =>
        {
            Match m = Regex.Match(line, @"BLE setup code: (\d{4})");
            if (m.Success) code.TrySetResult(m.Groups[1].Value);
        };

        using var server = StaticServer.Start(webRoot, httpPort);
        string profile = Path.Combine(Path.GetTempPath(), "minirover-webtest-chrome");
        if (Directory.Exists(profile)) Directory.Delete(profile, true);

        using Process chrome = Process.Start(new ProcessStartInfo
        {
            FileName = @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            ArgumentList = { $"--remote-debugging-port={cdpPort}", $"--user-data-dir={profile}", "--no-first-run",
                             "--no-default-browser-check", "--window-size=1100,900", $"http://localhost:{httpPort}/" },
            UseShellExecute = false,
        })!;
        Console.WriteLine($"chrome PID {chrome.Id} on CDP {cdpPort}, app on http://localhost:{httpPort}/");
        try
        {
            await using var cdp = await Cdp.ConnectAsync(cdpPort);
            await cdp.SendAsync("Runtime.enable");
            await cdp.SendAsync("DeviceAccess.enable");

            await WaitForAsync(cdp, "[data-test=setup-find]", TimeSpan.FromSeconds(60), "the setup page with a Find button");
            Console.WriteLine("PASS app rendered the setup wizard");
            await cdp.ScreenshotAsync(Path.Combine(shotDir, "1-start.png"));

            var prompt = cdp.WaitForEvent("DeviceAccess.deviceRequestPrompted");
            await ClickAsync(cdp, "[data-test=setup-find]");
            JsonNode prompted = await WithTimeout(prompt, TimeSpan.FromSeconds(10), "Chrome's Bluetooth chooser");
            string promptId = prompted["id"]!.GetValue<string>();

            // Chrome fills the chooser as devices are discovered; re-read until our car appears.
            string? deviceId = null;
            DateTime until = DateTime.UtcNow.AddSeconds(30);
            JsonNode last = prompted;
            while (deviceId == null && DateTime.UtcNow < until)
            {
                foreach (JsonNode? d in last["devices"]!.AsArray())
                {
                    string name = d!["name"]?.GetValue<string>() ?? "";
                    if (name.StartsWith("MiniRover", StringComparison.Ordinal)) { deviceId = d["id"]!.GetValue<string>(); Console.WriteLine($"chooser offered {name}"); }
                }
                if (deviceId == null) last = await WithTimeout(cdp.WaitForEvent("DeviceAccess.deviceRequestPrompted"), TimeSpan.FromSeconds(30), "the car in the chooser");
            }
            if (deviceId == null) throw new Exception("FAIL the car never appeared in Chrome's Bluetooth chooser");
            await cdp.SendAsync("DeviceAccess.selectPrompt", new JsonObject { ["id"] = promptId, ["deviceId"] = deviceId });

            await WaitForAsync(cdp, "[data-test=setup-step][data-step=Code]", TimeSpan.FromSeconds(30), "the code step");
            string theCode = await WithTimeout(code.Task, TimeSpan.FromSeconds(10), "the car printing its code");
            Console.WriteLine($"PASS connected over Web Bluetooth; car shows code {theCode}");
            await cdp.ScreenshotAsync(Path.Combine(shotDir, "2-code.png"));

            await TypeAsync(cdp, "[data-test=setup-code]", theCode);
            await ClickAsync(cdp, "[data-test=setup-code-submit]");
            await WaitForAsync(cdp, "[data-test=setup-step][data-step=Network]", TimeSpan.FromSeconds(15), "the network step (code accepted)");
            Console.WriteLine("PASS code accepted, pairing key received");

            // Scan takes ~14 s on the car; the list replaces the scanning notice.
            await WaitForAsync(cdp, "[data-test=setup-networks]", TimeSpan.FromSeconds(45), "the network list");
            int networks = (await EvalAsync(cdp, Deep("[data-test=setup-networks]") + "?.querySelectorAll('button').length ?? 0")).GetValue<int>();
            if (networks <= 0) throw new Exception("FAIL the car's scan listed no networks");
            Console.WriteLine($"PASS scan listed {networks} networks");
            await cdp.ScreenshotAsync(Path.Combine(shotDir, "3-networks.png"));

            string saved = (await EvalAsync(cdp, "localStorage.getItem('minirover.cars.v1') ?? ''")).GetValue<string>();
            var cars = JsonNode.Parse(saved.Length > 0 ? saved : "[]")!.AsArray();
            JsonNode? car = cars.FirstOrDefault(c => (c!["Name"]?.GetValue<string>() ?? "").StartsWith("MiniRover"));
            string key = car?["RoomKeyHex"]?.GetValue<string>() ?? "";
            if (key.Length != 40) throw new Exception("FAIL the paired car was not saved with a 20-byte room key: " + saved);
            Console.WriteLine($"PASS car saved in localStorage: {car!["Name"]} (room key {key[..6]}...)");

            string error = (await EvalAsync(cdp, Deep("[data-test=setup-error]") + "?.textContent ?? ''")).GetValue<string>();
            if (error.Length > 0) throw new Exception("FAIL the wizard shows an error: " + error);
            Console.WriteLine("ALL PASS");
            return 0;
        }
        finally
        {
            try { if (!chrome.HasExited) chrome.Kill(entireProcessTree: true); } catch { }
            try { Directory.Delete(profile, true); } catch { }
        }
    }

    /// <summary>querySelector that also searches open shadow roots (the RazorRenderer may mount inside one).</summary>
    internal static string Deep(string selector) =>
        "(function f(root,s){const h=root.querySelector(s);if(h)return h;for(const e of root.querySelectorAll('*')){if(e.shadowRoot){const r=f(e.shadowRoot,s);if(r)return r;}}return null;})(document," + JsonSerializer.Serialize(selector) + ")";

    internal static async Task<JsonNode> EvalAsync(Cdp cdp, string expression, bool userGesture = false)
    {
        JsonNode r = await cdp.SendAsync("Runtime.evaluate", new JsonObject { ["expression"] = expression, ["returnByValue"] = true, ["awaitPromise"] = true, ["userGesture"] = userGesture });
        if (r["exceptionDetails"] != null) throw new Exception("page script failed: " + r["exceptionDetails"]!.ToJsonString());
        return r["result"]!["value"] ?? JsonValue.Create("")!;
    }

    internal static async Task WaitForAsync(Cdp cdp, string selector, TimeSpan timeout, string what)
    {
        DateTime end = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < end)
        {
            if ((await EvalAsync(cdp, Deep(selector) + " !== null")).GetValue<bool>()) return;
            await Task.Delay(250);
        }
        string err = (await EvalAsync(cdp, Deep("[data-test=setup-error]") + "?.textContent ?? ''")).GetValue<string>();
        throw new Exception($"FAIL timed out waiting for {what} ({selector})" + (err.Length > 0 ? " - page error: " + err : ""));
    }

    internal static Task ClickAsync(Cdp cdp, string selector) => EvalAsync(cdp, Deep(selector) + ".click(), true", userGesture: true);

    internal static Task TypeAsync(Cdp cdp, string selector, string text) =>
        EvalAsync(cdp, "(()=>{const e=" + Deep(selector) + ";e.focus();e.value=" + JsonSerializer.Serialize(text) + ";e.dispatchEvent(new Event('input',{bubbles:true}));return true;})()");

    internal static async Task<T> WithTimeout<T>(Task<T> task, TimeSpan timeout, string what)
    {
        if (await Task.WhenAny(task, Task.Delay(timeout)) != task) throw new TimeoutException($"FAIL timed out waiting for {what}");
        return await task;
    }
}

/// <summary>Minimal Chrome DevTools Protocol client over one page target.</summary>
sealed class Cdp : IAsyncDisposable
{
    readonly ClientWebSocket _ws = new();
    readonly Dictionary<int, TaskCompletionSource<JsonNode>> _pending = new();
    readonly List<(string Method, TaskCompletionSource<JsonNode> Tcs)> _waiters = new();
    readonly CancellationTokenSource _cts = new();
    int _id;

    public static async Task<Cdp> ConnectAsync(int port, string targetType = "page")
    {
        using var http = new HttpClient();
        string? wsUrl = null;
        for (int i = 0; i < (targetType == "page" ? 60 : 6) && wsUrl == null; i++)
        {
            try
            {
                var targets = JsonNode.Parse(await http.GetStringAsync($"http://127.0.0.1:{port}/json/list"))!.AsArray();
                wsUrl = targets.FirstOrDefault(t => t!["type"]?.GetValue<string>() == targetType)?["webSocketDebuggerUrl"]?.GetValue<string>();
            }
            catch (HttpRequestException) { }
            if (wsUrl == null) await Task.Delay(500);
        }
        if (wsUrl == null) throw new Exception("Chrome's CDP endpoint did not come up");
        var cdp = new Cdp();
        await cdp._ws.ConnectAsync(new Uri(wsUrl), CancellationToken.None);
        _ = cdp.ReceiveLoop();
        return cdp;
    }

    public Task<JsonNode> WaitForEvent(string method)
    {
        var tcs = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_waiters) _waiters.Add((method, tcs));
        return tcs.Task;
    }

    public async Task<JsonNode> SendAsync(string method, JsonObject? parameters = null)
    {
        int id = Interlocked.Increment(ref _id);
        var tcs = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pending) _pending[id] = tcs;
        var msg = new JsonObject { ["id"] = id, ["method"] = method, ["params"] = parameters ?? new JsonObject() };
        await _ws.SendAsync(Encoding.UTF8.GetBytes(msg.ToJsonString()), WebSocketMessageType.Text, true, CancellationToken.None);
        if (await Task.WhenAny(tcs.Task, Task.Delay(60_000)) != tcs.Task) throw new TimeoutException("CDP " + method + " timed out");
        return await tcs.Task;
    }

    public async Task ScreenshotAsync(string path)
    {
        JsonNode r = await SendAsync("Page.captureScreenshot", new JsonObject { ["format"] = "png" });
        await File.WriteAllBytesAsync(path, Convert.FromBase64String(r["data"]!.GetValue<string>()));
    }

    async Task ReceiveLoop()
    {
        var buf = new byte[1 << 20];
        var sb = new StringBuilder();
        while (_ws.State == WebSocketState.Open && !_cts.IsCancellationRequested)
        {
            WebSocketReceiveResult r;
            try { r = await _ws.ReceiveAsync(buf, _cts.Token); } catch { return; }
            sb.Append(Encoding.UTF8.GetString(buf, 0, r.Count));
            if (!r.EndOfMessage) continue;
            JsonNode msg = JsonNode.Parse(sb.ToString())!;
            sb.Clear();
            if (msg["id"] is JsonNode idNode)
            {
                TaskCompletionSource<JsonNode>? tcs;
                lock (_pending) { _pending.Remove(idNode.GetValue<int>(), out tcs); }
                if (msg["error"] != null) tcs?.TrySetException(new Exception("CDP error: " + msg["error"]!.ToJsonString()));
                else tcs?.TrySetResult(msg["result"] ?? new JsonObject());
            }
            else if (msg["method"]?.GetValue<string>() is string method)
            {
                if (method == "Log.entryAdded")
                {
                    var en = msg["params"]!["entry"]!;
                    Console.WriteLine($"[log {en["level"]}] {en["text"]} {en["url"]}");
                }
                else if (method == "Runtime.exceptionThrown")
                {
                    var d = msg["params"]!["exceptionDetails"]!;
                    Console.WriteLine("[page exception] " + (d["exception"]?["description"]?.ToString() ?? d["text"]?.ToString()));
                }
                else if (method == "Runtime.consoleAPICalled")
                {
                    var args = msg["params"]!["args"]!.AsArray().Select(a => a!["value"]?.ToString() ?? a!["description"]?.ToString() ?? "");
                    Console.WriteLine("[page] " + string.Join(" ", args));
                }
                lock (_waiters)
                {
                    for (int i = _waiters.Count - 1; i >= 0; i--)
                    {
                        if (_waiters[i].Method == method) { _waiters[i].Tcs.TrySetResult(msg["params"]!); _waiters.RemoveAt(i); }
                    }
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { }
        _ws.Dispose();
    }
}

/// <summary>Static file server for the published app (correct MIME types for .wasm etc.).</summary>
sealed class StaticServer : IDisposable
{
    readonly HttpListener _listener = new();

    public static StaticServer Start(string root, int port)
    {
        var s = new StaticServer();
        s._listener.Prefixes.Add($"http://localhost:{port}/");
        s._listener.Start();
        _ = Task.Run(async () =>
        {
            while (s._listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await s._listener.GetContextAsync(); } catch { return; }
                try
                {
                    string rel = Uri.UnescapeDataString(ctx.Request.Url!.AbsolutePath.TrimStart('/'));
                    if (rel.Length == 0) rel = "index.html";
                    string full = Path.GetFullPath(Path.Combine(root, rel));
                    if (!full.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
                    {
                        ctx.Response.StatusCode = 404;
                    }
                    else
                    {
                        ctx.Response.ContentType = Path.GetExtension(full).ToLowerInvariant() switch
                        {
                            ".html" => "text/html; charset=utf-8",
                            ".js" or ".mjs" => "text/javascript",
                            ".css" => "text/css",
                            ".json" => "application/json",
                            ".wasm" => "application/wasm",
                            ".png" => "image/png",
                            ".svg" => "image/svg+xml",
                            _ => "application/octet-stream",
                        };
                        ctx.Response.AddHeader("Cache-Control", "no-store");
                        byte[] bytes = await File.ReadAllBytesAsync(full);
                        await ctx.Response.OutputStream.WriteAsync(bytes);
                    }
                }
                catch { ctx.Response.StatusCode = 500; }
                finally { try { ctx.Response.Close(); } catch { } }
            }
        });
        return s;
    }

    public void Dispose()
    {
        try { _listener.Stop(); _listener.Close(); } catch { }
    }
}
