using MiniRover.App;
using MiniRover.App.Services;
using SpawnDev.SpawnJS;
using SpawnDev.SpawnJS.RazorRenderer;
using SpawnDev.SpawnJS.RazorUI;
using SpawnDev.SpawnJS.WebWorkers;

// First console line on purpose: a stale cached build explains more browser failures than any theory.
Console.WriteLine("MiniRover app " + typeof(App).Assembly.GetName().Version + " starting");

var builder = SpawnJSAppBuilder.CreateDefault(args, out var JS);

builder.RootComponents.Add<App>();
builder.RootComponents.AddSharedStyleSheet("css/app.css");

builder.Services.AddWebWorkerService();
builder.Services.AddRazorRenderer();
builder.Services.AddRazorUI();
builder.Services.AddSingleton(sp => new HttpClient { BaseAddress = new Uri(JS.AppBaseUri) });
builder.Services.AddSingleton<CarStore>();

await builder.Build().RunAsync();
