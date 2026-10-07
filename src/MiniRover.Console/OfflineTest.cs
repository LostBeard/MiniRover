using System.Diagnostics;
using System.Text.Json.Nodes;
using static MiniRover.ConsoleApp.WebTest;

namespace MiniRover.ConsoleApp;

/// <summary>
/// The app works without a network (PWA): open it once, then with the web server gone and every service worker
/// stopped (so the .NET service worker must start cold, offline), reload and expect the app (its navigation; a fresh
/// profile has no cars, so the app opens on the setup wizard).
/// No car is needed.
/// </summary>
static class OfflineTest
{
    /// <summary>
    /// The hosted copy (e.g. GitHub Pages): open it online so the worker caches it, close Chrome, start it again on the
    /// same profile with the site's host name mapped to nowhere (so the page and the worker both lose it), and open it.
    /// </summary>
    static async Task<int> RunHostedAsync(string appUrl, int cdpPort)
    {
        if (!appUrl.EndsWith("/")) appUrl += "/";
        string host = new Uri(appUrl).Host;
        if (!DriveTest.PortFree(cdpPort)) { Console.Error.WriteLine($"port {cdpPort} is in use; pick another"); return 2; }
        string profile = Path.Combine(Path.GetTempPath(), "minirover-offlinetest-hosted-chrome");
        if (Directory.Exists(profile)) Directory.Delete(profile, true);

        Process Launch(params string[] extra)
        {
            var psi = new ProcessStartInfo { FileName = @"C:\Program Files\Google\Chrome\Application\chrome.exe", UseShellExecute = false };
            foreach (var a in new[] { $"--remote-debugging-port={cdpPort}", $"--user-data-dir={profile}", "--no-first-run", "--no-default-browser-check", "--window-size=1100,800" }) psi.ArgumentList.Add(a);
            foreach (var a in extra) psi.ArgumentList.Add(a);
            psi.ArgumentList.Add("about:blank");
            return Process.Start(psi)!;
        }

        Process chrome = Launch();
        try
        {
            await using (var cdp = await Cdp.ConnectAsync(cdpPort))
            {
                await cdp.SendAsync("Runtime.enable");
                await cdp.SendAsync("Page.enable");
                await cdp.SendAsync("Page.navigate", new JsonObject { ["url"] = appUrl });
                await WaitForAsync(cdp, "[data-test=nav-setup]", TimeSpan.FromSeconds(90), "the app (online)");
                Console.WriteLine("PASS online: " + appUrl + " opened");
                var sw = Stopwatch.StartNew();
                int cached = 0;
                bool controlled = false;
                while (sw.ElapsedMilliseconds < 180000 && !(controlled && cached > 100))
                {
                    await Task.Delay(1000);
                    controlled = (await EvalAsync(cdp, "navigator.serviceWorker.controller != null")).GetValue<bool>();
                    cached = (await EvalAsync(cdp, "(async()=>{let n=0;for(const k of await caches.keys()){if(k.startsWith('minirover-offline-'))n+=(await (await caches.open(k)).keys()).length;}return n;})()")).GetValue<int>();
                }
                if (!controlled || cached <= 100) throw new Exception($"FAIL the service worker did not take over (controlled {controlled}, cached files {cached})");
                Console.WriteLine($"PASS service worker in control, {cached} files cached, after {sw.ElapsedMilliseconds} ms");
                try { await cdp.SendAsync("Browser.close"); } catch { /* the connection closes with the browser */ }
            }
            chrome.WaitForExit(15000);
            if (!chrome.HasExited) chrome.Kill(true);

            // Offline for this site: its host name resolves to nothing, for the page and the service worker alike.
            chrome = Launch($"--host-resolver-rules=MAP {host} ~NOTFOUND");
            await using (var cdp = await Cdp.ConnectAsync(cdpPort))
            {
                await cdp.SendAsync("Runtime.enable");
                await cdp.SendAsync("Page.enable");
                var sw = Stopwatch.StartNew();
                await cdp.SendAsync("Page.navigate", new JsonObject { ["url"] = appUrl });
                await WaitForAsync(cdp, "[data-test=nav-setup]", TimeSpan.FromSeconds(90), "the app (offline)");
                long ms = sw.ElapsedMilliseconds;
                // Prove the site really is unreachable: a never-cached URL must fail.
                string probe = (await EvalAsync(cdp, "fetch('index.html?offline-probe=' + Date.now(), { cache: 'no-store' }).then(r => 'reached the server (' + r.status + ')', e => 'unreachable')")).ToString();
                if (probe != "unreachable") throw new Exception("FAIL the site is still reachable (" + probe + "); the offline check proves nothing");
                Console.WriteLine($"PASS offline: {appUrl} opened with {host} unreachable, in {ms} ms");
            }
            Console.WriteLine("ALL PASS");
            return 0;
        }
        finally
        {
            try { if (!chrome.HasExited) chrome.Kill(true); } catch { }
            try { if (Directory.Exists(profile)) Directory.Delete(profile, true); } catch { }
        }
    }

    /// <summary>A published wwwroot served here, or the URL of a hosted copy.</summary>
    public static Task<int> RunAsync(string webRoot, int httpPort, int cdpPort) =>
        webRoot.StartsWith("https://") || webRoot.StartsWith("http://") ? RunHostedAsync(webRoot, cdpPort) : RunLocalAsync(webRoot, httpPort, cdpPort);

    static async Task<int> RunLocalAsync(string webRoot, int httpPort, int cdpPort)
    {
        if (!File.Exists(Path.Combine(webRoot, "index.html"))) throw new FileNotFoundException("publish the app first", Path.Combine(webRoot, "index.html"));
        if (!File.Exists(Path.Combine(webRoot, "service-worker-assets.js"))) throw new FileNotFoundException("the publish has no asset manifest", Path.Combine(webRoot, "service-worker-assets.js"));
        foreach (int port in new[] { httpPort, cdpPort })
        {
            if (!DriveTest.PortFree(port)) { Console.Error.WriteLine($"port {port} is in use; pick another"); return 2; }
        }
        string appUrl = $"http://localhost:{httpPort}/";
        var server = StaticServer.Start(webRoot, httpPort);
        string profile = Path.Combine(Path.GetTempPath(), "minirover-offlinetest-chrome");
        if (Directory.Exists(profile)) Directory.Delete(profile, true);
        using Process chrome = Process.Start(new ProcessStartInfo
        {
            FileName = @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            ArgumentList = { $"--remote-debugging-port={cdpPort}", $"--user-data-dir={profile}", "--no-first-run",
                             "--no-default-browser-check", "--window-size=1100,800", "about:blank" },
            UseShellExecute = false,
        })!;
        Console.WriteLine($"chrome PID {chrome.Id} on CDP {cdpPort}, app on {appUrl}");
        try
        {
            await using var cdp = await Cdp.ConnectAsync(cdpPort);
            await cdp.SendAsync("Runtime.enable");
            await cdp.SendAsync("Page.enable");
            await cdp.SendAsync("Page.navigate", new JsonObject { ["url"] = appUrl });
            try { await WaitForAsync(cdp, "[data-test=nav-setup]", TimeSpan.FromSeconds(60), "the app (online)"); }
            catch
            {
                Console.WriteLine("page body: " + (await EvalAsync(cdp, "location.href + ' | ' + document.readyState + ' | ' + document.body.innerHTML.length")).ToString());
                using (var lh = new HttpClient()) Console.WriteLine("targets: " + string.Join(" ; ", System.Text.Json.Nodes.JsonNode.Parse(await lh.GetStringAsync($"http://127.0.0.1:{cdpPort}/json/list"))!.AsArray().Select(t => t!["type"] + " " + t["url"])));
                Console.WriteLine("controlled by a service worker: " + (await EvalAsync(cdp, "navigator.serviceWorker.controller != null")).ToString());
                Console.WriteLine("worker state: " + (await EvalAsync(cdp, "navigator.serviceWorker.controller?.state ?? 'none'")).ToString());
                Console.WriteLine("a fetch through the worker: " + (await EvalAsync(cdp, "Promise.race([fetch('icon-192.png').then(r => 'status ' + r.status, e => 'error ' + e), new Promise(r => setTimeout(() => r('no answer in 8 s'), 8000))])")).ToString());
                try
                {
                    await using var swc = await Cdp.ConnectAsync(cdpPort, "service_worker");
                    Console.WriteLine("service worker console (attached late; Log replays buffered entries):");
                    await swc.SendAsync("Log.enable");
                    await swc.SendAsync("Runtime.enable");
                    Console.WriteLine("  assetsManifest: " + (await EvalAsync(swc, "typeof self.assetsManifest")).ToString());
                    await Task.Delay(3000);
                }
                catch (Exception ex) { Console.WriteLine("no service worker target: " + ex.Message); }
                throw;
            }
            Console.WriteLine("PASS online: the app opened");

            // The service worker installs (caching every file), activates and takes control of the page.
            var sw = Stopwatch.StartNew();
            int cached = 0;
            bool controlled = false;
            while (sw.ElapsedMilliseconds < 120000 && !(controlled && cached > 0))
            {
                await Task.Delay(1000);
                controlled = (await EvalAsync(cdp, "navigator.serviceWorker.controller != null")).GetValue<bool>();
                cached = (await EvalAsync(cdp, "(async()=>{let n=0;for(const k of await caches.keys()){if(k.startsWith('minirover-offline-'))n+=(await (await caches.open(k)).keys()).length;}return n;})()")).GetValue<int>();
            }
            if (!controlled || cached == 0) throw new Exception($"FAIL the service worker did not take over (controlled {controlled}, cached files {cached})");
            int manifestCount = File.ReadAllText(Path.Combine(webRoot, "service-worker-assets.js")).Split("\"url\"").Length - 1;
            Console.WriteLine($"PASS service worker in control, {cached} files cached of {manifestCount} in the manifest, after {sw.ElapsedMilliseconds} ms");

            // Offline: no web server at all, and every service worker stopped so the .NET worker must boot cold.
            server.Dispose();
            await cdp.SendAsync("ServiceWorker.enable");
            await cdp.SendAsync("ServiceWorker.stopAllWorkers");
            await Task.Delay(1000);
            bool serverGone;
            try { using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(3) }; await probe.GetAsync(appUrl); serverGone = false; }
            catch { serverGone = true; }
            if (!serverGone) throw new Exception("FAIL the web server still answers; the offline check would prove nothing");

            sw.Restart();
            await cdp.SendAsync("Page.reload", new JsonObject { ["ignoreCache"] = false });
            await WaitForAsync(cdp, "[data-test=nav-setup]", TimeSpan.FromSeconds(60), "the app (offline, cold service worker)");
            Console.WriteLine($"PASS offline: the app opened with no server and a cold service worker in {sw.ElapsedMilliseconds} ms");
            Console.WriteLine("ALL PASS");
            return 0;
        }
        finally
        {
            try { chrome.Kill(true); } catch { }
            try { server.Dispose(); } catch { }
            try { if (Directory.Exists(profile)) Directory.Delete(profile, true); } catch { }
        }
    }
}
