using System.Security.Cryptography;
using System.Text;
using MiniRover.Protocol;
using Xunit;

namespace MiniRover.Protocol.Tests;

/// <summary>
/// Play mode: the WebRTC offer and answer cross BLE in parts, the BLE key proof, and the car's WiFi password. The car
/// and the apps compile the same code, so these pin what both sides do. SDPs here are real-sized (a Chrome answer with
/// candidates is ~600-900 bytes, several parts) and the decoder is fed hostile part sequences, since it runs on the car.
/// </summary>
public class PlayModeTests
{
    static byte[] Sdp(int bytes)
    {
        var sb = new StringBuilder();
        for (int i = 0; sb.Length < bytes; i++) sb.Append("a=candidate:").Append(i).Append(" 1 udp 2122260223 ").Append(Guid.NewGuid()).Append(".local 5").Append(i % 10).Append(" typ host\r\n");
        return Encoding.UTF8.GetBytes(sb.ToString(0, bytes));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(BleSetup.SdpPartBytes - 1)]
    [InlineData(BleSetup.SdpPartBytes)]       // exactly one full part: the last flag must be on that part, no empty part after
    [InlineData(BleSetup.SdpPartBytes + 1)]
    [InlineData(887)]                          // a typical answer
    [InlineData(BleSetup.MaxSdpBytes)]
    public void Sdp_parts_round_trip(int size)
    {
        byte[] sdp = Sdp(size);
        var buffer = new byte[BleSetup.MaxSdpBytes];
        int length = 0, next = 0, parts = 0, result = 0;
        for (int i = 0; ; i++)
        {
            byte[] part = BleSetup.EncodeSdpPart(BleSetup.OpRtcAnswer, sdp, i);
            if (part == null) break;
            parts++;
            Assert.Equal(BleSetup.OpRtcAnswer, part[0]);
            Assert.True(part.Length - 3 <= BleSetup.SdpPartBytes);
            Assert.Equal(0, result); // nothing may follow the last part
            result = BleSetup.AddSdpPart(part, buffer, ref length, ref next);
            Assert.NotEqual(-1, result);
        }
        Assert.Equal(1, result);
        Assert.Equal((size + BleSetup.SdpPartBytes - 1) / BleSetup.SdpPartBytes, parts);
        Assert.Equal(sdp, buffer[..length]);
    }

    [Fact]
    public void Sdp_part_out_of_order_or_repeated_is_rejected()
    {
        byte[] sdp = Sdp(500);
        byte[] p0 = BleSetup.EncodeSdpPart(BleSetup.OpRtcAnswer, sdp, 0), p1 = BleSetup.EncodeSdpPart(BleSetup.OpRtcAnswer, sdp, 1);
        var buffer = new byte[BleSetup.MaxSdpBytes];

        int length = 0, next = 0;
        Assert.Equal(-1, BleSetup.AddSdpPart(p1, buffer, ref length, ref next)); // skipped part 0

        length = 0; next = 0;
        Assert.Equal(0, BleSetup.AddSdpPart(p0, buffer, ref length, ref next));
        Assert.Equal(-1, BleSetup.AddSdpPart(p0, buffer, ref length, ref next)); // part 0 again

        length = 0; next = 0;
        Assert.Equal(-1, BleSetup.AddSdpPart([BleSetup.OpRtcAnswer, 0], buffer, ref length, ref next)); // truncated header
        Assert.Equal(-1, BleSetup.AddSdpPart(null!, buffer, ref length, ref next));
    }

    [Fact]
    public void Sdp_longer_than_the_car_buffer_is_rejected_not_overrun()
    {
        byte[] sdp = Sdp(BleSetup.MaxSdpBytes + 200);
        var buffer = new byte[BleSetup.MaxSdpBytes];
        int length = 0, next = 0, result = 0;
        for (int i = 0; result == 0; i++) result = BleSetup.AddSdpPart(BleSetup.EncodeSdpPart(BleSetup.OpRtcAnswer, sdp, i), buffer, ref length, ref next);
        Assert.Equal(-1, result);
        Assert.True(length <= buffer.Length);
    }

    [Fact]
    public void Key_proof_is_hmac_of_ble_label_and_nonce()
    {
        byte[] key = RandomNumberGenerator.GetBytes(CarLink.KeyBytes), nonce = RandomNumberGenerator.GetBytes(CarLink.NonceBytes);
        byte[] expected = HMACSHA256.HashData(key, (byte[])[.. "ble"u8, .. nonce]);
        Assert.Equal(expected, BleSetup.KeyProof(key, nonce));
        // Distinct from the WebRTC handshake proofs over the same nonce: a proof recorded on one channel is useless on the other.
        Assert.NotEqual(CarLink.ClientProof(key, nonce), BleSetup.KeyProof(key, nonce));
        Assert.NotEqual(BleSetup.KeyProof(key, nonce), BleSetup.KeyProof(key, RandomNumberGenerator.GetBytes(CarLink.NonceBytes)));
    }

    [Fact]
    public void Play_password_is_stable_typeable_and_key_dependent()
    {
        byte[] key = Convert.FromHexString("00112233445566778899aabbccddeeff00112233");
        string pw = CarLink.PlayPassword(key);
        Assert.Equal(CarLink.PlayPasswordLength, pw.Length);
        Assert.InRange(pw.Length, 8, 63); // WPA2 passphrase limits
        Assert.All(pw, c => Assert.Contains(c, "abcdefghjkmnpqrstuvwxyz23456789"));
        Assert.Equal(pw, CarLink.PlayPassword((byte[])key.Clone()));
        // Pinned: the car (nanoFramework) and every app must derive the same text from the same key.
        byte[] mac = HMACSHA256.HashData(key, "minirover/ap/v1"u8.ToArray());
        Assert.Equal(new string([.. mac[..10].Select(b => "abcdefghjkmnpqrstuvwxyz23456789"[b % 31])]), pw);

        var seen = new HashSet<string>();
        for (int i = 0; i < 200; i++) seen.Add(CarLink.PlayPassword(RandomNumberGenerator.GetBytes(CarLink.KeyBytes)));
        Assert.True(seen.Count >= 199);
    }
}
