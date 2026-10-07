using System.Text;
using MiniRover.Protocol;

namespace MiniRover.Client;

/// <summary>
/// A BLE connection to the car's setup service (protocol: <see cref="BleSetup"/>): the browser app implements it with
/// Web Bluetooth, the desktop console with Windows' BLE API.
/// </summary>
public interface ICarBle
{
    /// <summary>Writes one frame to the Control characteristic (with response).</summary>
    Task SendAsync(byte[] frame);

    /// <summary>The next event with this opcode (others are skipped). Throws <see cref="CarBleException"/> on the car's
    /// error event and <see cref="TimeoutException"/> when nothing came.</summary>
    Task<byte[]> WaitForAsync(byte opcode, TimeSpan timeout);
}

/// <summary>An error message reported by the car itself over BLE.</summary>
public class CarBleException(string message) : Exception(message);

/// <summary>
/// What a paired app does over BLE: prove it holds the pairing key, switch the car between its home network and play
/// mode, and in play mode carry the WebRTC offer and answer (the car's own WiFi has no internet, so no tracker).
/// </summary>
public sealed class CarBleSignaling(ICarBle ble, byte[] pairingKey)
{
    static readonly TimeSpan Reply = TimeSpan.FromSeconds(5);

    public bool KeyProven { get; private set; }

    /// <summary>Proves the pairing key against a fresh challenge from the car. Needed once per BLE connection.</summary>
    public async Task ProveKeyAsync()
    {
        await ble.SendAsync([BleSetup.OpKeyHello]).ConfigureAwait(false);
        byte[] challenge = await ble.WaitForAsync(BleSetup.EvKeyChallenge, Reply).ConfigureAwait(false);
        if (challenge.Length < 1 + CarLink.NonceBytes) throw new CarBleException("bad challenge from the car");
        byte[] proof = BleSetup.KeyProof(pairingKey, challenge[1..(1 + CarLink.NonceBytes)]);
        await ble.SendAsync([BleSetup.OpKeyProof, .. proof]).ConfigureAwait(false);
        byte[] result = await ble.WaitForAsync(BleSetup.EvAuthResult, Reply).ConfigureAwait(false);
        if (result.Length < 2 || result[1] != 1) throw new CarBleException("the car did not accept this app's pairing key");
        KeyProven = true;
    }

    /// <summary>Switches the car to play mode (its own WiFi) or back to its home network. The car restarts.</summary>
    public async Task SetWifiModeAsync(bool play)
    {
        if (!KeyProven) await ProveKeyAsync().ConfigureAwait(false);
        await ble.SendAsync([BleSetup.OpWifiMode, (byte)(play ? BleSetup.WifiModePlay : BleSetup.WifiModeHome)]).ConfigureAwait(false);
        await WaitForOkAsync(BleSetup.OpWifiMode).ConfigureAwait(false);
    }

    /// <summary>Play mode: asks the car for a new session and returns its offer SDP.</summary>
    public async Task<string> GetOfferAsync()
    {
        if (!KeyProven) await ProveKeyAsync().ConfigureAwait(false);
        await ble.SendAsync([BleSetup.OpRtcOffer]).ConfigureAwait(false);
        var buffer = new byte[BleSetup.MaxSdpBytes];
        int length = 0, next = 0;
        while (true)
        {
            // The car creates its peer connection first (a few hundred ms), then sends the parts back to back.
            byte[] part = await ble.WaitForAsync(BleSetup.EvRtcOfferPart, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            int r = BleSetup.AddSdpPart(part, buffer, ref length, ref next);
            if (r < 0) throw new CarBleException("the car's offer arrived out of order");
            if (r == 1) return Encoding.UTF8.GetString(buffer, 0, length);
        }
    }

    /// <summary>Play mode: sends the answer SDP in parts and waits for the car to take it.</summary>
    public async Task SendAnswerAsync(string sdp)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(sdp);
        if (utf8.Length > BleSetup.MaxSdpBytes) throw new ArgumentException("answer SDP too long for the car", nameof(sdp));
        for (int i = 0; ; i++)
        {
            byte[]? part = BleSetup.EncodeSdpPart(BleSetup.OpRtcAnswer, utf8, i);
            if (part == null) break;
            await ble.SendAsync(part).ConfigureAwait(false);
        }
        await WaitForOkAsync(BleSetup.OpRtcAnswer).ConfigureAwait(false);
    }

    async Task WaitForOkAsync(byte opcode)
    {
        while (true)
        {
            byte[] ok = await ble.WaitForAsync(BleSetup.EvOk, Reply).ConfigureAwait(false);
            if (ok.Length >= 2 && ok[1] == opcode) return;
        }
    }
}
