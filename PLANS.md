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
  MiniRover.Native/         interop assembly (managed side): Camera, Board (WiFi signal, power save, memory)
  native/MiniRover.Native/  its C++: camera task (JPEG -> data channel), RSSI, memory; native/components: esp32-camera, esp_jpeg
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
- [x] GitHub repository + first push (github.com/LostBeard/MiniRover)

### Phase 0b - Shared nanoFramework WebRTC library
- [x] New public repo `SpawnDev.nanoFramework.WebRTC` (github.com/LostBeard/SpawnDev.nanoFramework.WebRTC), a submodule at `firmware/external/`: `PeerConnection` (libpeer interop), WebSocket client, tracker signaling, configurable peer id / room / ICE servers
- [x] TLS server certificate verified against a bundled root CA (ISRG Root X1)
- [x] PSRAM-backed rings: 64 KB TX ring, 16 x 2 KB RX, 96 KB "latest frame wins" video slot fed from native code
- [x] libpeer fork fixes: DCEP channel type, per-channel stream ids, SCTP receive rewrite (reassembly, SACK with gaps, 64 KB window)
- [x] Native slots freed on a CLR soft reboot (a deploy left the old program's connection holding ~190 KB; measured)
- [x] `DataChannel.Open` waits for SCTP: ICE "completed" comes before the SCTP association (six sessions in a row failed on the car)
- [ ] Ed25519 / X25519 (Monocypher) moved from SpawnWear
- [ ] Split the managed signaling code into its own assembly: any new or changed METHOD in the interop assembly changes its checksum and forces a firmware rebuild (measured 0xBF64AE07 -> 0x846AB963 for one managed method; constants do not)
- [x] libpeer send path: bounded retry when network buffers are full + send error/retry counters (StatUdpSendErrors/Retries)
- [x] libpeer SCTP: SACK processing, retransmission for reliable streams, FORWARD-TSN (RFC 3758) for video, HEARTBEAT-ACK. Measured from Chrome with injected loss: 5% -> controls intact, video 9 fps; 15% -> controls intact, video 5.3 fps; 0% -> no spurious retransmits (`drivetest --loss <permille>`)
- [ ] SpawnWear rebuilt and verified on the shared library (Riker's call)
- [x] libpeer: read every waiting datagram per pass (bounded burst): a SipSorcery peer's ICE consent checks were starved behind per-packet SACKs during video and the desktop dropped the car after 8 s
- [x] SpawnDev.SIPSorcery fork (SpawnDev.RTC): FORWARD TSN receive support + advertised in INIT, and an INIT parameter-parsing bug fixed. Desktop <-> car with video and 5% injected loss: 40 s, telemetry continuous (was: dropped after 16-24 s). 5-minute session at 0% loss: 14.6 fps, longest telemetry gap 318 ms
- [ ] Release SpawnDev.SIPSorcery + SpawnDev.RTC with the FORWARD TSN fix (nuget.org needs TJ's go); MiniRover's desktop console uses a local 10.0.10-local.1 build until then

### Phase 1 - Car bring-up
Measured 2026-10-06: the stock nanoFramework image cannot be deployed to over the kit's CH340C (overruns at 921600 baud, see Docs/firmware-build.md), so Phase 1 runs on the MiniRover firmware target (460800 baud + CRC32), which pulls part of Phase 2 forward.
- [x] Firmware target `MINIROVER_ESP32` builds; deploy works
- [x] Car app boots on the bare board: brownout guard (no servos on USB power), faults reported per part, WiFi setup access point up
- [x] RMT receivers (sonar, IR) initialise (fixed a nanoFramework RMT bug for the base ESP32)
- [x] Board drivers on the shared I2C bus: PCA9685, PCF8574, HT16K33
- [x] `servo center`; camera head mounted; pan/tilt directions verified
- [x] Motors: all four verified on the car (positions + direction; per-car `motorN.invert`)
- [x] WS2812 LEDs + GPIO32 battery sampling time-shared with them (battery fit raw/4096 x 3.9 x 3.7)
- [x] Buzzer, light sensor, line sensors, LED-matrix eyes verified on the car
- [ ] IR remote (NEC) and ultrasonic verified on the car (need the remote / the sonar head)
- [x] Settings persisted in flash (servo trim, motor invert/gain, battery coefficient, pairing key)

### Phase 2 - Custom firmware with WebRTC and camera
- [x] Firmware preset for the classic ESP32-WROVER: libpeer + shared WebRTC interop + NimBLE (BLE setup); IPv6, PSRAM lwIP
- [x] Fits the 4 MB flash with the camera: nanoCLR 0x1ac660, 8% of the app partition free
- [x] DTLS + SCTP + two data channels with the desktop client (SipSorcery via SpawnDev.RTC)
- [x] DTLS + data channels with Chrome (the browser app): connect + authenticate in 6.8-8.3 s (`minirover drivetest`)
- [x] Native camera task (esp32-camera, OV2640 / GC0308 detected; GC0308 gets software JPEG): OV2640 320x240 q12 = ~6 KB/frame, 14.6 fps over 90 s to the desktop, 14.7 fps decoded in Chrome
- [ ] Measure fps / latency at other sizes and qualities; glass-to-glass latency; a real GC0308. Measured so far (OV2640, q12): 320x240 13.6-15 fps ~6 KB; 640x480 7.6 fps ~15.6 KB
- [x] Camera size changes while streaming: the driver is re-initialised at the new size under a lock held by the capture task around each frame (esp32-camera sizes its buffers at init; set_framesize while streaming hung the car). Verified QVGA -> CIF -> VGA -> QVGA while streaming
- [x] Car resets while streaming (found, fixed, verified): a settings save (flash write) while streaming VGA froze the chip (interrupt watchdog). esp32-camera's ISR prints an overflow warning whose %s argument lived in flash, and read it with the cache off. Patched in the vendored driver (firmware/native/components/README.md); 10 saves in a row at VGA now pass and video recovers. Not the battery, and not the size change itself (size changes while streaming work: the driver re-initialises under a lock)
- [x] Self-heal after a crash reset: if the motor board does not answer, the car restarts once through the RTC watchdog's reset-RTC stage (a deep-sleep wake did not bring I2C and the camera back; this does, verified on a real crash). The crash cause is kept in `diag.reset` and shown in /status
- [x] I2C bus recovery at boot (9 clocks + STOP) and the boot battery voltage in /status
- [x] `MiniRover.Native` interop (firmware/native): WiFi RSSI of the connected access point, WiFi modem sleep control
- [x] Memory budget with camera + BLE + WebRTC (measured per boot step; see the sdkconfig comments): internal 27 KB during the BLE window (was 2 KB), PSRAM 650+ KB native free (was 3 KB)
- [x] Car command loop under video load: bounded receive batches, newest drive wins, identical motor writes skipped (telemetry had stopped completely while driving with video)
- [ ] Pin the WebRTC pump and camera tasks to core 0 (the CLR has core 1) if the command loop needs more headroom

### Phase 3 - Protocol and link
- [x] Shared protocol `CarLink` (handshake, drive, servo, lights, buzzer, eyes, stop, telemetry), compiled into both runtimes; 37 tests
- [x] Tracker signaling on the car (room id = HMAC of the pairing key), mutual HMAC challenge over "ctrl"
- [x] BLE setup (code on the eyes, WiFi choice, pairing key); a 2-minute BLE window after every boot lets another device pair
- [x] Measured on the real car through hub.spawndev.com: connect + authenticate in 6.6-6.7 s, telemetry 5 Hz, reconnect after a client leaves
- [x] Deadman over WiFi: 300 ms hold -> stopped within 345 ms of sending, 1000 ms hold -> within 1036 ms (HTTP); over WebRTC one drive frame with 300 / 1000 ms hold -> telemetry reports stopped at 519 / 1236 ms (includes up to 200 ms telemetry period)
- [x] `MiniRover.Console link` drives the real car (`MiniRover.Client.CarConnection`, shared with the browser app)
- [x] WiFi modem sleep off once the BLE window closes (ESP-IDF requires it while Bluetooth is on): 60 status polls max 0.43 s, median 0.18 s (one earlier poll had stalled 1.4 s with it on)
- [x] HTTP test API off by default (`http.api`, settable only over paired channels); `/status` and `/stop` stay open; verified on the car (403 / 200). Docs/car-api.md
- [ ] Report upstream: nanoFramework `HMACSHA256(byte[] key)` keeps the caller's array and `Dispose()` zeroes it (wiped the car's pairing key; MiniRover uses `HashData`)

### Phase 4 - Browser app
- [x] App scaffold: SpawnJSAppBuilder + RazorRenderer + RazorUI (dark theme), garage of paired cars (localStorage)
- [x] BLE setup wizard (Web Bluetooth): find car, code check, network scan/choice, pairing key, WiFi hand-off, reconnect after reboot. Verified end to end in Chrome against the real car (`minirover webtest`); WiFi hand-off itself not yet run with a real network
- [x] Connect over WebRTC from the garage (Drive button)
- [x] Auto-reconnect: the drive page notices a car restart in 2.5-4.6 s and is back 7-11 s after it, video included (`drivetest --reboot COM8` restarts the car mid-test)
- [ ] Several cars at once
- [x] Drive page HUD: battery % (Li-ion curve) + volts, WiFi signal bars + dBm with an out-of-range warning, line + light sensors, telemetry rate, car memory
- [x] Drive page video: canvas, browser JPEG decode, newest frame wins, bytes never enter .NET; HUD shows the car's video fps
- [x] Drive page: link round trip in the HUD (44 ms measured on the LAN), full screen (verified), screen wake lock, the car stops when the tab is hidden
- [ ] Drive page: sonar distance (needs the ultrasonic head)
- [x] Touch sticks (pointer capture; drive + camera aim), keyboard (WASD / arrows, keys dropped on window blur), speed levels: touch drag and key W verified moving the real car from Chrome, release stops it (125-314 ms to telemetry "stopped")
- [ ] Gamepad (standard mapping, triggers + sticks, A/B/Y): written, not yet tried with a real pad; rumble
- [x] Lights toggle, horn
- [x] Light patterns animated on the car (LightsService): dim, headlights (+ brake lights and turn signals once calibrated), hazard, police, rainbow, battery gauge, off; app button cycles them, verified on the car via /status. Light corner calibration in Settings (groups or single LEDs), verified stored on the car; the real corner layout still has to be done by someone looking at the car
- [x] LED-matrix eyes animated on the car (FaceService): alive (blinks, looks where the car steers, looks down reversing, glances around when idle), 6 moods, scrolling text in MiniRover's own 3x5 font (`*` = heart); pairing code / setup / flat battery override. Shared EyeArt (protocol project) draws them on the car and previews them in the app's Eyes panel. Verified on the car by reading the matrix chip's RAM back (/status face.shown) for every mode and steering direction, and from Chrome (drivetest)
- [ ] Sensors page with live charts
- [x] Battery: Li-ion curve (%), time remaining from the idle-reading trend (unit-tested), low / empty / switch-off banners (banners not yet seen with a real low pack)
- [x] Settings panel on the drive page (live link): top speed, steering sensitivity + stick curve (per car), camera size/quality/fps/orientation, camera aim (pan/tilt trim, camera held centered), per-wheel test / reversed / balance, battery calibration from a multimeter reading, LED brightness, HTTP test API, signaling server. Verified from Chrome: values match the car, wheel test spins a wheel, a change is stored
- [ ] Guided first-run wizard (wheels, camera aim) after "Add a car"
- [ ] Car-side modes: line following, light seeking, obstacle avoidance
- [ ] Browser tests against the published release build

### Phase 5 - Hosting and releases
- [ ] `MiniRover.Server` (LAN / offline)
- [x] GitHub Pages deployment of the browser app (https://lostbeard.github.io/MiniRover/, verified driving the car)
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
