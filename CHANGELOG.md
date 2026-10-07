# Changelog

All notable changes to MiniRover are recorded here.

## Unreleased

- Video: 320x240 from about 6 to 20 fps on the same WiFi, and a link round trip of 31-49 ms instead of ~100 ms. The
  camera's 20 MHz clock jammed the car's own WiFi (now 24 MHz, setting `sensor.xclk`); Bluetooth is fully off after
  the setup window on the home network; the WebRTC sender no longer waits out 10 ms ticks; ChaCha20-Poly1305 encryption;
  a faster firmware build (ESP32 revision 3+, -O2, 80 MHz PSRAM). The firmware now needs ESP32 revision 3 or later.
  Details and how to measure (`minirover udptest`, `/link/times`): Docs/video.md.
- Car interpreter load: the eyes draw only when they change, still light patterns sleep, the link loop polls at
  50 Hz, and `/status` caches the settings (0.74 -> 0.46 s per request).

- Play mode: drive with no internet. The car makes its own WiFi; the app starts each connection over Bluetooth. See Docs/play-mode.md.
- Project started: README, MIT license, hardware documentation (pin map, I2C map, power), servo centering guide, battery notes, architecture overview and roadmap (PLANS.md).
