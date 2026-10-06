using System.Text;
using MiniRover.Protocol;
using Xunit;

namespace MiniRover.Protocol.Tests;

/// <summary>
/// The car (nanoFramework) and every client compile the SAME BleSetup.cs, so these tests pin the wire format
/// both sides share. Hostile inputs: the decoder runs on the car and must reject anything inconsistent rather than
/// read past a buffer or accept a truncated password.
/// </summary>
public class BleSetupTests
{
    [Theory]
    [InlineData("home", "hunter22")]
    [InlineData("x", "")]                                                   // open network, 1-char SSID
    [InlineData("Café ☕ Net", "pässwörd with spaces")]                      // multi-byte UTF-8 both fields
    [InlineData("12345678901234567890123456789012", "1234567890123456789012345678901234567890123456789012345678901234")] // 32 / 64 byte limits
    public void SetWifi_round_trips_exact_bytes(string ssid, string password)
    {
        byte[] s = Encoding.UTF8.GetBytes(ssid), p = Encoding.UTF8.GetBytes(password);
        byte[] frame = BleSetup.EncodeSetWifi(s, p);

        Assert.Equal(BleSetup.OpSetWifi, frame[0]);
        Assert.Equal(3 + s.Length + p.Length, frame.Length);
        Assert.True(BleSetup.TryDecodeSetWifi(frame, out byte[] ds, out byte[] dp));
        Assert.Equal(s, ds);
        Assert.Equal(p, dp);
    }

    [Fact]
    public void SetWifi_wire_layout_is_length_prefixed()
    {
        byte[] frame = BleSetup.EncodeSetWifi("ab"u8.ToArray(), "xyz"u8.ToArray());
        Assert.Equal(new byte[] { 0x04, 2, (byte)'a', (byte)'b', 3, (byte)'x', (byte)'y', (byte)'z' }, frame);
    }

    [Fact]
    public void SetWifi_encoder_rejects_out_of_range_fields()
    {
        Assert.Throws<ArgumentException>(() => BleSetup.EncodeSetWifi(Array.Empty<byte>(), "p"u8.ToArray()));
        Assert.Throws<ArgumentException>(() => BleSetup.EncodeSetWifi(new byte[33], "p"u8.ToArray()));
        Assert.Throws<ArgumentException>(() => BleSetup.EncodeSetWifi("s"u8.ToArray(), new byte[65]));
    }

    public static IEnumerable<object[]> MalformedFrames()
    {
        byte[] good = BleSetup.EncodeSetWifi("net"u8.ToArray(), "secret"u8.ToArray());
        yield return new object[] { Array.Empty<byte>() };
        yield return new object[] { new byte[] { BleSetup.OpSetWifi } };
        yield return new object[] { good[..^1] };                                   // password truncated by one byte
        yield return new object[] { good.Concat(new byte[] { 0 }).ToArray() };      // trailing garbage
        yield return new object[] { new byte[] { BleSetup.OpSetWifi, 0, 0 } };      // empty SSID
        yield return new object[] { new byte[] { BleSetup.OpSetWifi, 200, 0 } };    // SSID length past the frame
        yield return new object[] { new byte[] { BleSetup.OpSetWifi, 33 }.Concat(new byte[34]).ToArray() }; // SSID > 32
        byte[] wrongOp = (byte[])good.Clone(); wrongOp[0] = BleSetup.OpScan;
        yield return new object[] { wrongOp };
        byte[] longPass = new byte[3 + 1 + 65]; longPass[0] = BleSetup.OpSetWifi; longPass[1] = 1; longPass[3] = 65;
        yield return new object[] { longPass };                                     // password > 64
    }

    [Theory]
    [MemberData(nameof(MalformedFrames))]
    public void SetWifi_decoder_rejects_malformed_frames(byte[] frame)
    {
        Assert.False(BleSetup.TryDecodeSetWifi(frame, out byte[] s, out byte[] p));
        Assert.Null(s);
        Assert.Null(p);
    }

    [Theory]
    [InlineData(-25, "home")]
    [InlineData(-128, "x")]
    [InlineData(-200, "clamped low")]   // clamps to -128
    [InlineData(10, "clamped high")]    // positive values are legal sbyte
    public void ScanResult_layout(int rssi, string ssid)
    {
        byte[] s = Encoding.UTF8.GetBytes(ssid);
        byte[] ev = BleSetup.EncodeScanResult(rssi, true, s);
        Assert.Equal(BleSetup.EvScanResult, ev[0]);
        Assert.Equal(Math.Clamp(rssi, -128, 127), (sbyte)ev[1]);
        Assert.Equal(1, ev[2]);
        Assert.Equal(s.Length, ev[3]);
        Assert.Equal(s, ev[4..]);
    }

    [Fact]
    public void ScanResult_truncates_ssid_to_32_bytes()
    {
        byte[] ev = BleSetup.EncodeScanResult(-40, false, new byte[40]);
        Assert.Equal(32, ev[3]);
        Assert.Equal(4 + 32, ev.Length);
    }

    [Fact]
    public void PairingInfo_layout()
    {
        byte[] key = Enumerable.Range(1, BleSetup.RoomKeyBytes).Select(i => (byte)i).ToArray();
        byte[] ev = BleSetup.EncodePairingInfo(key, "MiniRover-A408"u8.ToArray());
        Assert.Equal(BleSetup.EvPairingInfo, ev[0]);
        Assert.Equal(key, ev[1..21]);
        Assert.Equal(14, ev[21]);
        Assert.Equal("MiniRover-A408", Encoding.UTF8.GetString(ev, 22, ev[21]));
        Assert.Throws<ArgumentException>(() => BleSetup.EncodePairingInfo(new byte[19], "x"u8.ToArray()));
    }

    [Fact]
    public void Uuids_share_one_base_and_are_distinct()
    {
        Guid[] all = { BleSetup.ServiceUuid, BleSetup.InfoUuid, BleSetup.ControlUuid, BleSetup.EventsUuid };
        Assert.Equal(all.Length, all.Distinct().Count());
        foreach (Guid g in all) Assert.StartsWith("1d7bd351-574a-44a3-be60-f66b840e43", g.ToString());
    }
}
