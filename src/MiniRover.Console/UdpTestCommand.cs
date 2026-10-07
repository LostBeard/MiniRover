using System.Net;
using System.Net.Sockets;

namespace MiniRover.ConsoleApp;

/// <summary>
/// WiFi throughput from the car to this PC with no WebRTC, DTLS or SCTP in the way: the car's firmware sends 1200-byte
/// UDP datagrams (a sequence number first) as fast as its WiFi driver takes them; this side counts what arrives.
/// Near the video stream's rate: the radio link limits the video. Far above it: the WebRTC send path does.
/// </summary>
static class UdpTestCommand
{
    public static async Task<int> RunAsync(string carUrl, int seconds)
    {
        var car = new Uri(carUrl.Contains("://") ? carUrl : "http://" + carUrl);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var localIp = LocalAddressTowards(car.Host);
        using var udp = new UdpClient(new IPEndPoint(localIp, 0));
        udp.Client.ReceiveBufferSize = 4 << 20;
        int port = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;

        long bytes = 0, packets = 0;
        uint highest = 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        double firstMs = -1, lastMs = 0;
        using var stop = new CancellationTokenSource();
        var receiver = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                UdpReceiveResult r;
                try { r = await udp.ReceiveAsync(stop.Token); } catch (OperationCanceledException) { break; }
                if (firstMs < 0) firstMs = clock.Elapsed.TotalMilliseconds;
                lastMs = clock.Elapsed.TotalMilliseconds;
                bytes += r.Buffer.Length;
                packets++;
                if (r.Buffer.Length >= 4) highest = Math.Max(highest, BitConverter.ToUInt32(r.Buffer, 0));
            }
        });

        var start = await http.GetAsync(new Uri(car, $"/udptest?ip={localIp}&port={port}&ms={seconds * 1000}"));
        Console.WriteLine($"car -> {localIp}:{port} for {seconds} s: {(int)start.StatusCode} {await start.Content.ReadAsStringAsync()}");
        if (!start.IsSuccessStatusCode) return 1;
        await Task.Delay(TimeSpan.FromSeconds(seconds + 1.5));
        stop.Cancel();
        await receiver;

        string status = await http.GetStringAsync(new Uri(car, "/status"));
        var m = System.Text.RegularExpressions.Regex.Match(status, @"""udpTest"":\{""bytes"":(\d+),""full"":(\d+)");
        long sentBytes = m.Success ? long.Parse(m.Groups[1].Value) : -1;
        double spanS = Math.Max(0.001, (lastMs - firstMs) / 1000);
        long sentPackets = highest + 1;
        Console.WriteLine($"received {bytes / 1024} KB in {packets} datagrams over {spanS:F1} s = {bytes / 1024.0 / spanS:F0} KB/s ({bytes * 8 / 1e6 / spanS:F2} Mbit/s)");
        Console.WriteLine($"car handed {sentBytes / 1024} KB to its WiFi driver ({sentBytes / 1024.0 / seconds:F0} KB/s), buffer-full refusals {(m.Success ? m.Groups[2].Value : "?")}");
        Console.WriteLine($"lost on the air: {sentPackets - packets} of {sentPackets} datagrams ({100.0 * (sentPackets - packets) / Math.Max(1, sentPackets):F1}%)");
        return 0;
    }

    /// <summary>This PC's address on the route to the car (a connected UDP socket picks it; nothing is sent).</summary>
    static IPAddress LocalAddressTowards(string host)
    {
        var target = Dns.GetHostAddresses(host).First(a => a.AddressFamily == AddressFamily.InterNetwork);
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        probe.Connect(target, 9);
        return ((IPEndPoint)probe.LocalEndPoint!).Address;
    }
}
