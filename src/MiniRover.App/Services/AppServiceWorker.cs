using SpawnDev.SpawnJS;
using SpawnDev.SpawnJS.JSObjects;
using SpawnDev.SpawnJS.WebWorkers;

namespace MiniRover.App.Services;

/// <summary>
/// The app's service worker: the same .NET app, running in the ServiceWorkerGlobalScope (SpawnDev.SpawnJS.WebWorkers),
/// so MiniRover opens without internet - at a park, or on the car's own WiFi.
///
/// Install caches every published file (Blazor's service worker asset manifest, one cache per build). Page loads go
/// to the network first so an online visit always gets the newest build, and fall back to the cached page offline;
/// every other file of this site comes from the cache. Other sites (the signaling tracker, ...) are never cached.
/// </summary>
public sealed class AppServiceWorker : ServiceWorkerEventHandler
{
    const string CachePrefix = "minirover-offline-";

    public AppServiceWorker(SpawnJSRuntime js) : base(js) { }

    string CacheName => CachePrefix + (AssetsManifest?.Version ?? "unversioned");

    protected override async Task ServiceWorker_OnInstallAsync(ExtendableEvent e)
    {
        var manifest = AssetsManifest;
        if (manifest == null)
        {
            // No manifest (a development build without <ServiceWorkerAssetsManifest>): files are cached as they are used.
            JS.Log("MiniRover service worker: no asset manifest, nothing pre-cached");
        }
        else
        {
            using var cache = await Cache.OpenCache(CacheName);
            var urls = manifest.Assets.Select(a => a.Url).ToList();
            // The SpawnJS bundle (main.classic.js) is built after the SDK writes the manifest, so the list misses it,
            // yet the page loads it and it is this worker's own script: add the worker's script URL (no query).
            using (var location = ServiceWorkerThis!.Location)
            {
                string self = location.Href;
                int q = self.IndexOf('?');
                urls.Add(q >= 0 ? self[..q] : self);
            }
            urls = urls.Distinct().ToList();
            // All or nothing: if one file fails the install fails, and the previous worker keeps serving its build.
            await cache.AddAll(urls);
            JS.Log($"MiniRover service worker: cached {urls.Count} files for offline use ({CacheName})");
        }
        _ = ServiceWorkerThis!.SkipWaiting();
    }

    protected override async Task ServiceWorker_OnActivateAsync(ExtendableEvent e)
    {
        // Older builds' caches go; this build's stays.
        foreach (var name in await Cache.CacheNames())
        {
            if (name.StartsWith(CachePrefix, StringComparison.Ordinal) && name != CacheName)
            {
                using var storage = new CacheStorage();
                await storage.Delete(name);
            }
        }
        await ServiceWorkerThis!.Clients.Claim();
    }

    protected override async Task<Response> ServiceWorker_OnFetchAsync(FetchEvent e)
    {
        using var request = e.Request;
        string url = request.Url;
        string origin = ServiceWorkerThis!.Location.Origin;
        if (request.Method != "GET" || !url.StartsWith(origin + "/", StringComparison.Ordinal))
        {
            return await JS.Fetch(request); // other sites and non-GET requests: straight through
        }
        using var cache = await Cache.OpenCache(CacheName);
        if (request.Mode == "navigate")
        {
            try
            {
                return await JS.Fetch(request);
            }
            catch
            {
                // Offline: the cached app page.
                return await cache.Match("index.html") ?? await cache.Match("./") ?? OfflineResponse();
            }
        }
        var cached = await cache.Match(request);
        if (cached != null) return cached;
        var response = await JS.Fetch(request);
        if (response.Ok && response.Type == "basic")
        {
            // Files the manifest did not list (none in a published build): keep a copy for next time.
            await cache.Put(request, response.Clone());
        }
        return response;
    }

    static Response OfflineResponse() => new Response("MiniRover is offline and this page was never cached.", new ResponseOptions
    {
        Status = 503,
        StatusText = "Offline",
        Headers = new Dictionary<string, string> { { "Content-Type", "text/plain" } },
    });
}
