using System.Security.Cryptography;
using System.Text;
using MiniRover.Protocol;
using Xunit;

namespace MiniRover.Protocol.Tests;

/// <summary>Car link wire format + handshake. The same source runs on the car (nanoFramework), so these pin both ends.</summary>
public class CarLinkTests
{
    static readonly byte[] Key = Enumerable.Range(1, CarLink.KeyBytes).Select(i => (byte)(i * 7)).ToArray();

    [Fact]
    public void Room_id_is_hmac_of_a_fixed_label_and_never_the_key()
    {
        byte[] room = CarLink.DeriveRoomId(Key);
        byte[] expected = new HMACSHA256(Key).ComputeHash(Encoding.UTF8.GetBytes("minirover/room/v1"))[..20];
        Assert.Equal(expected, room);
        Assert.NotEqual(Key, room);
        // a different key gives a different room
        byte[] other = (byte[])Key.Clone();
        other[0] ^= 1;
        Assert.NotEqual(room, CarLink.DeriveRoomId(other));
    }

    [Fact]
    public void Handshake_succeeds_with_the_same_key_and_fails_with_another()
    {
        byte[] carNonce = RandomNumberGenerator.GetBytes(CarLink.NonceBytes);
        byte[] clientNonce = RandomNumberGenerator.GetBytes(CarLink.NonceBytes);

        // client answers the car's Hello
        byte[] auth = CarLink.EncodeAuth(CarLink.ClientProof(Key, carNonce), clientNonce);
        Assert.Equal(CarLink.MsgAuth, auth[0]);

        // car verifies against ITS nonce
        Assert.True(CarLink.ProofEquals(auth, 1, CarLink.ClientProof(Key, carNonce)));
        byte[] wrongKey = (byte[])Key.Clone();
        wrongKey[5] ^= 0x80;
        Assert.False(CarLink.ProofEquals(auth, 1, CarLink.ClientProof(wrongKey, carNonce)));
        // a replayed Auth against a NEW car nonce fails
        Assert.False(CarLink.ProofEquals(auth, 1, CarLink.ClientProof(Key, RandomNumberGenerator.GetBytes(CarLink.NonceBytes))));

        // car proves itself against the client's nonce
        byte[] result = CarLink.EncodeAuthResult(true, CarLink.CarProof(Key, clientNonce));
        Assert.Equal(1, result[1]);
        Assert.True(CarLink.ProofEquals(result, 2, CarLink.CarProof(Key, clientNonce)));
        // the two directions use different labels: a client proof is never a valid car proof
        Assert.False(CarLink.ProofEquals(result, 2, CarLink.ClientProof(Key, clientNonce)));
    }

    [Fact]
    public void ProofEquals_rejects_short_and_null_input()
    {
        byte[] proof = new byte[CarLink.ProofBytes];
        Assert.False(CarLink.ProofEquals(new byte[10], 0, proof));
        Assert.False(CarLink.ProofEquals(null!, 0, proof));
        Assert.False(CarLink.ProofEquals(new byte[CarLink.ProofBytes], 1, proof)); // offset leaves too few bytes
    }

    [Fact]
    public void Hello_layout()
    {
        byte[] nonce = Enumerable.Range(0, CarLink.NonceBytes).Select(i => (byte)i).ToArray();
        byte[] hello = CarLink.EncodeHello(nonce, "MiniRover-A408");
        Assert.Equal(CarLink.MsgHello, hello[0]);
        Assert.Equal(CarLink.Version, hello[1]);
        Assert.Equal(nonce, hello[2..18]);
        Assert.Equal(14, hello[18]);
        Assert.Equal("MiniRover-A408", Encoding.UTF8.GetString(hello, 19, 14));
    }

    [Theory]
    [InlineData(0, 0, 0, 300)]
    [InlineData(65535, 100, -100, 1000)]
    [InlineData(12345, -37, 64, 0)]
    public void Drive_round_trips(int seq, int left, int right, int hold)
    {
        byte[] b = CarLink.EncodeDrive(seq, left, right, hold);
        Assert.True(CarLink.TryDecodeDrive(b, b.Length, out int s, out int l, out int r, out int h));
        Assert.Equal((seq, left, right, hold), (s, l, r, h));
    }

    [Fact]
    public void Drive_clamps_and_rejects()
    {
        byte[] b = CarLink.EncodeDrive(1, 250, -250, 99999);
        Assert.True(CarLink.TryDecodeDrive(b, b.Length, out _, out int l, out int r, out int h));
        Assert.Equal((100, -100, 65535), (l, r, h));
        // a hostile frame with an out-of-range speed byte decodes clamped, never beyond +/-100
        byte[] hostile = { CarLink.MsgDrive, 0, 0, 0x7F, 0x80, 0, 0 };
        Assert.True(CarLink.TryDecodeDrive(hostile, hostile.Length, out _, out l, out r, out _));
        Assert.Equal((100, -100), (l, r));
        Assert.False(CarLink.TryDecodeDrive(b, b.Length - 1, out _, out _, out _, out _));
        b[0] = CarLink.MsgServo;
        Assert.False(CarLink.TryDecodeDrive(b, b.Length, out _, out _, out _, out _));
    }

    [Theory]
    [InlineData(1, 0, true)]
    [InlineData(0, 65535, true)]       // wrap
    [InlineData(100, 99, true)]
    [InlineData(99, 100, false)]       // stale
    [InlineData(5, 5, false)]          // duplicate
    [InlineData(40000, 1, false)]      // more than half the space behind = stale
    public void Sequence_newer(int seq, int last, bool newer) => Assert.Equal(newer, CarLink.IsNewer(seq, last));

    [Fact]
    public void Telemetry_round_trips_including_negative_and_sentinel_values()
    {
        var t = new CarLink.Telemetry
        {
            Seq = 65000, BatteryMillivolts = 8350, BatteryLevel = 1, Moving = true, NoBattery = false, Authenticated = true,
            Light = 1158, Line = 5, SonarCm = -1, PanTenths = -450, TiltTenths = 1200, Rssi = -61, VideoFps = 17, FreeHeapKb = 3900,
        };
        byte[] b = CarLink.EncodeTelemetry(t);
        Assert.Equal(CarLink.TelemetryBytes, b.Length);
        var d = new CarLink.Telemetry();
        Assert.True(CarLink.TryDecodeTelemetry(b, b.Length, d));
        Assert.Equivalent(t, d);

        t.SonarCm = 37;
        CarLink.TryDecodeTelemetry(CarLink.EncodeTelemetry(t), CarLink.TelemetryBytes, d);
        Assert.Equal(37, d.SonarCm);
        Assert.False(CarLink.TryDecodeTelemetry(b, b.Length - 1, d));
    }
}

public class CarLinkMessageTests
{
    [Fact]
    public void Video_layout_and_clamping()
    {
        Assert.Equal(new byte[] { CarLink.MsgVideo, 1, 6, 12, 15 }, CarLink.EncodeVideo(true, 6, 12, 15));
        Assert.Equal(new byte[] { CarLink.MsgVideo, 0, 0, 63, 30 }, CarLink.EncodeVideo(false, -5, 99, 500));
    }

    [Fact]
    public void Setting_and_text_are_utf8_after_the_type_and_capped()
    {
        byte[] s = CarLink.EncodeSetting("camera.fps=10");
        Assert.Equal(CarLink.MsgSetting, s[0]);
        Assert.Equal("camera.fps=10", System.Text.Encoding.UTF8.GetString(s, 1, s.Length - 1));
        Assert.Equal(1 + CarLink.MaxTextBytes, CarLink.EncodeText(new string('x', 5000)).Length);
        Assert.Equal(new byte[] { CarLink.MsgText }, CarLink.EncodeText(null!));
    }
}

public class CarLinkOffsetDecodeTests
{
    [Fact]
    public void Drive_decodes_in_place_after_a_receive_header()
    {
        byte[] frame = CarLink.EncodeDrive(42, -55, 70, 300);
        byte[] rx = new byte[2 + frame.Length + 5];
        Array.Copy(frame, 0, rx, 2, frame.Length);
        Assert.True(CarLink.TryDecodeDrive(rx, 2, frame.Length, out int s, out int l, out int r, out int h));
        Assert.Equal((42, -55, 70, 300), (s, l, r, h));
        // an offset that leaves too few bytes in the buffer is rejected, never read past the end
        Assert.False(CarLink.TryDecodeDrive(rx, rx.Length - 3, frame.Length, out _, out _, out _, out _));
        Assert.False(CarLink.TryDecodeDrive(rx, -1, frame.Length, out _, out _, out _, out _));
    }
}
