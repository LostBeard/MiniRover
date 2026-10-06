# MiniRover - Plan

The living roadmap. Each phase ends tested on a real car, committed and pushed. Tick boxes as work lands; record measured numbers (flash use, heap, fps, latency) next to the item that produced them.

## Goal

Open-source software for the **Freenove FNK0053 4WD Car Kit** that any kit owner can use:

- nanoFramework (C#) firmware on the car's ESP32-WROVER;
- a .NET WebAssembly browser app that drives the car by touch, gamepad, keyboard and later VR, with live video, all sensors, lights, buzzer and battery tracking;
- machine-learning modes (face tracking, follow-me, auto-drive) running in the browser on SpawnDev.ILGPU.ML;
- a showcase for .NET, .NET WebAssembly, nanoFramework and the SpawnDev libraries.

Hardware details: [Docs/hardware.md](Docs/hardware.md). System overview: [Docs/architecture.md](Docs/architecture.md).

## Key decisions

1. **Browser app** uses `SpawnJSAppBuilder` + SpawnDev.SpawnJS.RazorRenderer + RazorUI (no Blazor JavaScript runtime). All JavaScript interop goes through SpawnDev.SpawnJS wrappers.
2. **Video** is camera JPEG frames over a dedicated WebRTC data channel (unordered, no retransmits).
   - The classic ESP32 has no video encoder.
   - Capture and send happen in a native firmware task (the pattern from libpeer's ESP32 example), so frames never enter the managed heap.
   - The browser decodes with WebCodecs and keeps pixels in JavaScript and GPU memory.
3. **Car WebRTC** is libpeer inside a custom nanoFramework firmware build, through the shared `SpawnDev.nanoFramework.WebRTC` library. That library is extracted from [SpawnWear](https://github.com/LostBeard/SpawnWear), which already runs libpeer data channels on an ESP32-S3.
   - The car is the DTLS client with an ECDSA certificate.
   - Signaling uses the WebTorrent tracker protocol.
4. **Car logic** is C# on nanoFramework, using the existing nanoFramework IoT device bindings where they exist (PCA9685, PCF8574, HT16K33, WS28xx, HC-SR04). Native code is limited to the camera and WebRTC.
5. **Safety lives in the firmware**, not the client: deadman stop (300 ms without a control frame), low-battery speed limit, critical-battery cutoff, sonar emergency stop, configurable speed limit.
6. **Setup for anyone:**
   - First boot without WiFi credentials starts an access point `MiniRover-XXXX` with a setup page.
   - Pairing is a link containing the room key; the room key is also shown on the LED matrix and the serial console.
   - Prebuilt firmware images ship as GitHub Release assets.
   - The browser app is hosted on GitHub Pages.
7. **Offline / LAN mode:** `MiniRover.Server` serves the app and runs its own tracker + STUN (SpawnDev.RTC.Server).
8. **One protocol source** is compiled into both the nanoFramework firmware and the .NET client, with cross-runtime parity tests.

## Repository layout

```
firmware/
  MiniRover.Car/            nanoFramework app: services (drive, servo, lights, matrix, buzzer, sensors, battery, link, setup)
  MiniRover.Car.Drivers/    board drivers (separate assembly: nanoFramework has a per-assembly size cap)
  native/MiniRover.Camera/  C++ interop: camera init, sensor detect, JPEG -> data channel task
  nf-preset/                firmware build preset, sdkconfig, 4 MB partition table
src/
  MiniRover.Protocol/       shared message definitions (net10.0)
  MiniRover.Protocol.Nano/  the same source files built for nanoFramework
  MiniRover.Client/         connection, telemetry, battery model, input mixing (net10.0)
  MiniRover.App/            browser app (.NET WebAssembly)
  MiniRover.Console/        desktop command-line client (SpawnDev.RTC desktop), also the hardware test driver
  MiniRover.Server/         LAN host: static app + tracker + STUN
tests/
  MiniRover.Protocol.Tests/ cross-runtime protocol parity
  MiniRover.Client.Tests/   battery model, input mixer, controllers
  MiniRover.App.Tests/      browser tests
  MiniRover.Hil.Tests/      hardware-in-the-loop tests against a real car (opt-in)
```

## Phases

### Phase 0 - Scaffold
- [x] Repository, MIT license, README, docs (hardware, servo calibration, battery, architecture), this plan
- [ ] GitHub repository + first push

### Phase 0b - Shared nanoFramework WebRTC library
- [ ] New repo `SpawnDev.nanoFramework.WebRTC`, contents moved from SpawnWear:
  - managed `PeerConnection` wrapper (libpeer) and `Ed25519`/`X25519` (Monocypher), with their native C++
  - tracker signaling + WebSocket client, generalized (peer id, room, ICE servers configurable)
- [ ] TLS server certificate verified against a bundled root CA (SpawnWear currently skips verification)
- [ ] Per-message size limits configurable and PSRAM-backed (SpawnWear: 512 B TX / 1 KB RX; JPEG frames need tens of KB)
- [ ] SpawnWear rebuilt and verified on the shared library

### Phase 1 - Car bring-up on stock nanoFramework firmware
- [ ] Board drivers on the shared I2C bus: PCA9685, PCF8574, HT16K33
- [ ] **`servo center`** (both servos to 90 degrees) so the camera head can be mounted
- [ ] Motors: per-motor direction, trim, skid steering mixer
- [ ] WS2812 LEDs + GPIO32 battery sampling time-shared with them
- [ ] Buzzer (tones, melodies), light sensors, line sensors, IR remote (NEC), ultrasonic
- [ ] Settings persisted in flash (servo trim, motor trim, battery coefficient)
- [ ] Serial console for every function; each verified on the real car

### Phase 2 - Custom firmware with WebRTC and camera
- [ ] Firmware preset for the classic ESP32-WROVER: libpeer + srtp + esp32-camera + shared WebRTC interop + MiniRover.Camera; no Bluetooth
- [ ] Partition table that fits in 4 MB flash (measure: firmware image size, free heap, free PSRAM)
- [ ] DTLS handshake with Chrome and with the desktop client
- [ ] libpeer: multiple data channels, unordered/no-retransmit channel, large messages (fix in libpeer if missing)
- [ ] Native JPEG task; measure fps and latency vs resolution and quality for OV2640 and GC0308

### Phase 3 - Protocol and link
- [ ] Shared protocol (drive intent, servo, lights, matrix, buzzer, modes, settings, telemetry, camera control)
- [ ] Tracker signaling on the car, pairing link, Ed25519 challenge
- [ ] First-boot access point + setup page (WiFi credentials, room key, pairing link)
- [ ] Deadman stop, measured
- [ ] `MiniRover.Console` drives the real car; hardware-in-the-loop tests

### Phase 4 - Browser app
- [ ] Connect / pair / multiple cars / auto-reconnect
- [ ] Drive page: full-screen video, HUD (battery, link RTT, WiFi signal, fps, distance, line sensors, speed limit)
- [ ] Touch dual sticks (pointer events), gamepad (polled per frame, standard mapping, rumble), keyboard
- [ ] Lights, LED-matrix eyes and text, buzzer
- [ ] Sensors page with live charts
- [ ] Battery model: Li-ion discharge curve, sag-aware smoothing, time remaining, warnings
- [ ] Settings: servo calibration wizard, motor trim, camera, speed limit, input curves, signaling server
- [ ] Car-side modes: line following, light seeking, obstacle avoidance
- [ ] Browser tests against the published release build

### Phase 5 - Hosting and releases
- [ ] `MiniRover.Server` (LAN / offline)
- [ ] GitHub Pages deployment of the browser app
- [ ] Firmware images as GitHub Release assets + flashing guide

### Phase 6 - Machine-learning modes
- [ ] Face tracking: face detection -> camera pan/tilt controller (servos only, wheels stay still)
- [ ] Follow me: pose or person detection -> steering + distance keeping, sonar stop
- [ ] Follow an object: pick a detected class (ball, pet, ...)
- [ ] Gesture driving: arm poses -> drive commands
- [ ] Depth-aware auto-drive: monocular depth + sonar -> free-space steering
- [ ] Extras: super-resolution display, depth-based 3D views, snapshots

### Phase 7 - VR
- [ ] WebXR session: video on a virtual screen, **head pose drives camera pan/tilt**, controller sticks drive
- [ ] Optional depth-based stereo

### Phase 8 - Polish
- [ ] Docs with photos and GIFs, troubleshooting guide, CHANGELOG, first tagged release

## Ideas for later

- Voice commands and a "voice" for the car
- Record and replay a route
- Time-lapse and photo capture
- Hardware mods for anyone who wants them (I2C add-ons such as an IMU, since GPIOs are all used)

## Verification standard

- Protocol: encode on one runtime, decode on the other, identical bytes.
- Firmware: hardware-in-the-loop over a real WebRTC link, wheels off the ground. Each motor and servo moves as commanded, the deadman stop time is measured, sensor values change when the sensors are covered, and video frames decode with fps logged.
- Browser app: tests run against the published release build. They connect to a real car, count decoded video frames, and check that touch and gamepad input produce the expected control frames.
- "Working" means observed working, with the evidence named.
