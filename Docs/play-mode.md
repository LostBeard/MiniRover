# Play mode: driving with no internet

At home the car joins your WiFi and the app reaches it through the internet (a WebRTC link, signaled through a tracker
server). Away from home (a park, a car trip) there is no WiFi to join, so the car makes its own.

## How it works

- **The car's own WiFi.** In play mode the car is a WiFi access point. Its name is the car's name (`MiniRover-XXXX`).
  The WPA2 password comes from the pairing key, so every phone or computer paired with the car can show it, and nobody
  without the key can join. The car gives out addresses (`192.168.4.x`); it is `192.168.4.1`.
- **Bluetooth starts each connection.** With no internet there is no tracker. A browser page served over https cannot
  reach the car over plain http either (mixed content). So the start of the connection crosses Bluetooth instead: the
  app proves it holds the pairing key, the car sends its WebRTC offer, and the app sends its answer, all over BLE.
- **Then everything runs on the car's WiFi.** Video, driving, telemetry and settings use the same encrypted WebRTC link
  as at home, with the same pairing-key handshake. Bluetooth is let go once the link is up and is used again only to
  reconnect.

## Using it

1. **Switch the car to play mode** in either of two ways:
   - while connected at home: the drive page's Settings, WiFi, **Play mode**;
   - anywhere: My cars, **No internet?**, **Play mode (Bluetooth)**.

   The car restarts. A paired car that cannot find its home WiFi when it starts goes to play mode by itself. An
   unpaired car goes to setup mode instead.
2. **Join the car's WiFi** on the phone or computer: My cars, **No internet?** shows the network name and password.
   The device has no internet while it is on this network.
3. **Drive without internet** (same panel). Pick the car in the Bluetooth window. The first connection takes a few
   seconds.

**Back home:** **Home WiFi (Bluetooth)** in the same panel, or **Back to home WiFi** in the drive page's Settings. The
car restarts and joins your network again.

Browsers: Chrome or Edge (Web Bluetooth). Safari and iOS are not supported.

## From a computer (developers)

```
minirover playinfo                    # the play WiFi name and password of the last paired car
minirover playmode on|off             # switch over BLE (the car restarts)
minirover link --ble "telemetry 3000" # a session in play mode: BLE signaling, WebRTC on the car's WiFi
minirover drivetest <wwwroot> --play  # the browser test in play mode (this PC must be on the car's WiFi)
```

## Firmware notes

- Upstream nanoFramework builds the ESP32 without ESP-IDF's DHCP server (`CONFIG_LWIP_DHCPS=n`), so a device could join
  the car's network but never got an address. The MiniRover target turns it on and starts it when a station joins.
- nanoFramework treated a WiFi station that was only *enabled* as *auto-connecting* (a flag test bug), so a car in
  play mode also joined the home network when it was in range. Fixed in the MiniRover nanoFramework fork.
- Browsers hide their LAN address behind an mDNS name (`xxxx.local`). The car does not resolve those (that blocked
  the car's program for up to 15 s); it learns the browser's address from the browser's own connectivity checks
  (peer-reflexive candidates, libpeer fork).
- Play mode keeps BLE running beside the WebRTC session. Internal RAM is the tight resource there: the WebRTC task's
  stack lives in PSRAM so the WiFi driver keeps enough internal memory.
