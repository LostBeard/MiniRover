# Video: what limits it, and how to measure

The camera sends JPEG frames over a WebRTC data channel. A native FreeRTOS task on the car takes each frame from the
camera driver and hands it to SpawnDev.nanoFramework.WebRTC's "latest frame wins" slot. The WebRTC task splits it
into SCTP chunks, encrypts them with DTLS and sends UDP datagrams of about 1200 bytes. When the next frame is ready
before the previous one has gone out, the older one is dropped, so the picture never lags behind.

Three things have limited the frame rate on a real car (Freenove FNK0053, ESP32-D0WD-V3, OV2640, 5 ft from a home
router, signal -24 dBm). All numbers below were measured on 2026-10-07.

## 1. The camera's clock jams the car's WiFi

The camera needs a clock from the ESP32 (XCLK). esp32-camera's usual 20 MHz clock interferes with the car's own
radio. Plain UDP from the car to a PC (no WebRTC), 6 to 10 s per run:

| Camera | UDP from the car | Lost on the air | Ping |
|---|---|---|---|
| not started | 13.2 Mbit/s | 0% | 5 ms, steady |
| running, XCLK 20 MHz | 1.0-6 Mbit/s | 0-6% | 6-380 ms |
| running, XCLK 24 MHz | 8.6-13 Mbit/s | 0% | 5-9 ms |

The 20 MHz results vary a lot from run to run, so compare settings alternately (20, 24, 20, 24). The car now runs the
camera at 24 MHz. The `sensor.xclk` setting (8..24 MHz) changes it if your board behaves differently.

Changing the router's channel does not help: the interference comes from the car itself.

## 2. Bluetooth shares the radio

The ESP32 has one 2.4 GHz radio. While its Bluetooth controller is enabled, even idle, WiFi gets about half the
airtime. On the home network the car switches Bluetooth fully off when the setup window after power-on closes, which
roughly doubled the video frame rate (320x240: 5.6 to 12.2 fps). Play mode keeps Bluetooth on because each connection
starts over it.

## 3. The car's CPU

With the radio fixed, the car's processor was the limit: while streaming, both cores were about 95% busy. The WebRTC
task used about 80% of one core, roughly 6 ms of CPU per 1200-byte datagram, most of it in the DTLS record (encryption
plus the UDP send). What helped, each measured on the car:

| Change | Effect |
|---|---|
| The WebRTC task wakes the moment a frame is ready, and polls the network without waiting once connected (both used to wait out a 10 ms FreeRTOS tick) | connection loop pass 20 ms -> 2 ms, 320x240 10.5 -> 12 fps |
| ESP32 revision 3+ build (no PSRAM cache workaround) and `-O2` | AES-GCM per datagram 0.9 -> 0.52 ms, 12 -> 14 fps |
| ChaCha20-Poly1305 encryption instead of AES-GCM (the ESP32 has no GCM hardware; GCM's table lookups miss the cache) | record encryption 2.9 -> 1.7 ms, 13 -> 16 fps |
| PSRAM at 80 MHz instead of 40 | 16 -> 19 fps; the interpreter's idle load 26% -> 18% of a core |
| The eyes redraw only when they change; still light patterns sleep; the link loop polls at 50 Hz | interpreter idle load 37% -> 26% of a core |

Video now (one PC, 5 ft from the router, JPEG quality 12, no frame-rate cap):

| Size | Frames per second | Average frame |
|---|---|---|
| 320x240 | 20 | 8 KB |
| 480x320 | 15 | 11.6 KB |
| 640x480 | 9.6 | 22 KB |

The app asks for 15 fps; at 320x240 the car holds that with almost no dropped frames, in both home and play mode.
Link round trip: 49 ms on the home network, 31 ms in play mode (was about 100 ms).

## Measuring on your car

The local HTTP test API must be on (`http.api=1`, see [car-api.md](car-api.md)).

- `minirover udptest http://<car-ip> 10`: WiFi throughput without WebRTC. The car sends UDP to your PC for 10 s, and
  the command prints the rate, the share lost on the air, and how often the car's WiFi buffers were full.
- `minirover link "video 20000 0 0 30"`: streams for 20 s and counts frames.
- `/link/times`: where the WebRTC task's time went in the current session (frames sent and dropped,
  per-frame send time, per-datagram DTLS write and UDP send time, the negotiated cipher suite).
- `/status` `net`: channel, negotiated PHY mode, bandwidth, signal.

Measure on a quiet PC and alternate the settings you compare: results drift with whatever else is on the air.
