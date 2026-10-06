using System.Security.Cryptography;
using MiniRover.Protocol;
using SpawnDev.RTC;
using SpawnDev.RTC.Signaling;

namespace MiniRover.Client;

/// <summary>
/// The app side of the car link (protocol: <see cref="CarLink"/>). The car offers into the paired room on the
/// tracker; this side joins the room without offers of its own, answers the car's offer, then runs the mutual key
/// handshake on "ctrl". Commands are refused by the car until the handshake completes, and this side refuses a car
/// that cannot prove it holds the same key.
///
/// Video frames arrive on <see cref="VideoChannel"/>. Browser consumers should read them with
/// <c>OnArrayBufferMessage</c> so the JPEG bytes stay in JavaScript memory; desktop consumers use <c>OnBinaryMessage</c>.
/// </summary>
public sealed class CarConnection : IAsyncDisposable
{
    readonly byte[] _key;
    readonly string _trackerUrl;
    readonly RTCPeerConnectionConfig _config;
    readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    TrackerSignalingClient? _tracker;
    RtcPeerConnectionRoomHandler? _handler;
    RoomKey _room;
    IRTCDataChannel? _ctrl;
    string? _carPeerId;
    byte[]? _clientNonce;
    int _driveSeq;
    bool _disposed;

    public CarConnection(byte[] pairingKey, string? trackerUrl = null, string? iceServers = null)
    {
        if (pairingKey == null || pairingKey.Length != CarLink.KeyBytes)
            throw new ArgumentException($"A pairing key is {CarLink.KeyBytes} bytes.", nameof(pairingKey));
        _key = pairingKey;
        _trackerUrl = trackerUrl ?? CarLink.DefaultTrackerUrl;
        _config = new RTCPeerConnectionConfig
        {
            IceServers = (iceServers ?? CarLink.DefaultIceServers)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(u => new RTCIceServerConfig(u)).ToArray(),
        };
    }

    public static CarConnection FromKeyHex(string keyHex, string? trackerUrl = null) => new(Convert.FromHexString(keyHex), trackerUrl);

    /// <summary>The car's name from its Hello.</summary>
    public string? CarName { get; private set; }
    public int CarProtocolVersion { get; private set; }
    public bool IsConnected { get; private set; }
    public string Status { get; private set; } = "idle";

    /// <summary>The car's "video" channel (JPEG frames), once the car opened it.</summary>
    public IRTCDataChannel? VideoChannel { get; private set; }

    public CarLink.Telemetry? LastTelemetry { get; private set; }

    public event Action<string>? OnStatus;
    public event Action<CarLink.Telemetry>? OnTelemetry;
    public event Action<IRTCDataChannel>? OnVideoChannel;
    public event Action<string>? OnText;
    /// <summary>The link dropped after it was up. Create a new connection to reconnect.</summary>
    public event Action? OnDisconnected;

    /// <summary>Joins the room and waits until the car has connected and both sides proved the key.</summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var peerId = new byte[20];
        "-MRC001-"u8.CopyTo(peerId);
        RandomNumberGenerator.Fill(peerId.AsSpan(8));
        for (int i = 8; i < peerId.Length; i++) peerId[i] = (byte)('a' + peerId[i] % 26); // printable, like the car's

        _room = RoomKey.FromBytes(CarLink.DeriveRoomId(_key));
        _handler = new RtcPeerConnectionRoomHandler(_config);
        _handler.OnDataChannel += OnDataChannel;
        _handler.OnPeerDisconnected += OnPeerDisconnected;

        _tracker = new TrackerSignalingClient(_trackerUrl, peerId);
        _tracker.Subscribe(_room, _handler);
        SetStatus("waiting for the car");
        // No offers of our own: the car is the offerer. NumWant 0 keeps the tracker from asking us for any.
        await _tracker.AnnounceAsync(_room, new AnnounceOptions { Event = "started", NumWant = 0 }, ct).ConfigureAwait(false);

        using (ct.Register(() => _ready.TrySetCanceled(ct)))
        {
            await _ready.Task.ConfigureAwait(false);
        }
    }

    void OnDataChannel(IRTCDataChannel channel, string remotePeerId)
    {
        if (channel.Label == CarLink.VideoChannel)
        {
            VideoChannel = channel;
            OnVideoChannel?.Invoke(channel);
            return;
        }
        if (channel.Label != CarLink.ControlChannel || _ctrl != null) return;
        _ctrl = channel;
        _carPeerId = remotePeerId;
        channel.OnBinaryMessage += OnControlMessage;
        channel.OnClose += () => Drop("control channel closed");
        SetStatus("connected, authenticating");
    }

    void OnControlMessage(byte[] m)
    {
        if (m == null || m.Length == 0 || _ctrl == null) return;
        try
        {
            switch (m[0])
            {
                case CarLink.MsgHello:
                    if (m.Length < 3 + CarLink.NonceBytes) return;
                    CarProtocolVersion = m[1];
                    byte[] carNonce = m[2..(2 + CarLink.NonceBytes)];
                    int nameLen = Math.Min(m[2 + CarLink.NonceBytes], m.Length - 3 - CarLink.NonceBytes);
                    CarName = System.Text.Encoding.UTF8.GetString(m, 3 + CarLink.NonceBytes, nameLen);
                    _clientNonce = RandomNumberGenerator.GetBytes(CarLink.NonceBytes);
                    _ctrl.Send(CarLink.EncodeAuth(CarLink.ClientProof(_key, carNonce), _clientNonce));
                    break;
                case CarLink.MsgAuthResult:
                    bool ok = m.Length >= 2 + CarLink.ProofBytes && m[1] == 1 && _clientNonce != null &&
                              CarLink.ProofEquals(m, 2, CarLink.CarProof(_key, _clientNonce));
                    if (!ok)
                    {
                        // Either the car rejected our key, or the peer in this room is not the car we paired with.
                        Fail(m.Length >= 2 && m[1] == 1 ? "the car could not prove it holds the pairing key" : "the car rejected the pairing key");
                        return;
                    }
                    IsConnected = true;
                    SetStatus("connected");
                    _ready.TrySetResult(true);
                    break;
                case CarLink.MsgTelemetry:
                    if (!IsConnected) return;
                    var t = new CarLink.Telemetry();
                    if (!CarLink.TryDecodeTelemetry(m, m.Length, t)) return;
                    LastTelemetry = t;
                    OnTelemetry?.Invoke(t);
                    break;
                case CarLink.MsgText:
                    OnText?.Invoke(System.Text.Encoding.UTF8.GetString(m, 1, m.Length - 1));
                    break;
            }
        }
        catch (Exception ex)
        {
            Fail("bad message from the car: " + ex.Message);
        }
    }

    /// <summary>
    /// Wheel speeds in percent (-100..100). The car holds them for <paramref name="holdMs"/> and stops by itself if no
    /// newer command arrives (its deadman), so callers resend at a steady rate while driving.
    /// </summary>
    public void Drive(int leftPercent, int rightPercent, int holdMs = 300)
        => Send(CarLink.EncodeDrive(_driveSeq++ & 0xFFFF, leftPercent, rightPercent, holdMs));

    public void Stop() => Send([CarLink.MsgStop]);

    /// <summary>Camera angles in degrees (90 = centered).</summary>
    public void Servo(double panDegrees, double tiltDegrees)
    {
        short pan = (short)Math.Round(panDegrees * 10), tilt = (short)Math.Round(tiltDegrees * 10);
        Send([CarLink.MsgServo, (byte)pan, (byte)(pan >> 8), (byte)tilt, (byte)(tilt >> 8)]);
    }

    /// <summary>Starts or stops the camera stream on <see cref="VideoChannel"/>. 0 keeps the car's saved size / quality / fps.
    /// Sizes: 6 = 320x240, 8 = 400x296, 10 = 640x480 (see the car's MiniRover.Native.Camera); quality 4..63, lower = better.</summary>
    public void Video(bool enable, int frameSize = 0, int jpegQuality = 0, int maxFps = 0)
        => Send(CarLink.EncodeVideo(enable, frameSize, jpegQuality, maxFps));

    /// <summary>Changes a car setting ("camera.fps=10"); the car clamps it to a safe range, saves it and answers with a Text.</summary>
    public void Setting(string keyValue) => Send(CarLink.EncodeSetting(keyValue));

    public void Leds(byte r, byte g, byte b) => Send([CarLink.MsgLeds, r, g, b]);

    public void Buzzer(int hz, int ms) => Send([CarLink.MsgBuzzer, (byte)hz, (byte)(hz >> 8), (byte)ms, (byte)(ms >> 8)]);

    /// <summary>A 16-byte eye frame: 8 rows per eye, bit 7 = leftmost pixel.</summary>
    public void Eyes(byte[] frame)
    {
        if (frame.Length != 16) throw new ArgumentException("An eye frame is 16 bytes.", nameof(frame));
        Send([CarLink.MsgEyes, .. frame]);
    }

    void Send(byte[] message)
    {
        if (!IsConnected || _ctrl == null) throw new InvalidOperationException("Not connected to the car.");
        _ctrl.Send(message);
    }

    void OnPeerDisconnected(string remotePeerId)
    {
        if (remotePeerId == _carPeerId) Drop("the car disconnected");
    }

    void Fail(string reason)
    {
        SetStatus(reason);
        _ready.TrySetException(new InvalidOperationException(reason));
        _ = DisposeAsync();
    }

    void Drop(string reason)
    {
        bool wasConnected = IsConnected;
        IsConnected = false;
        SetStatus(reason);
        _ready.TrySetException(new InvalidOperationException(reason));
        if (wasConnected) OnDisconnected?.Invoke();
    }

    void SetStatus(string s)
    {
        Status = s;
        OnStatus?.Invoke(s);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        IsConnected = false;
        if (_ctrl != null) _ctrl.OnBinaryMessage -= OnControlMessage;
        if (_handler != null)
        {
            _handler.OnDataChannel -= OnDataChannel;
            _handler.OnPeerDisconnected -= OnPeerDisconnected;
            // Closes the peer connection, not only the channels, so the car sees the drop at once.
            try { _handler.Dispose(); } catch { }
        }
        if (_tracker != null)
        {
            try { _tracker.Unsubscribe(_room); } catch { }
            try { await _tracker.AnnounceAsync(_room, new AnnounceOptions { Event = "stopped" }).ConfigureAwait(false); } catch { }
            try { await _tracker.DisposeAsync().ConfigureAwait(false); } catch { }
        }
        _ready.TrySetCanceled();
    }
}
