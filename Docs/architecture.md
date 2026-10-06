# Architecture

```
 Browser (any device)                                         Car (ESP32-WROVER)
 .NET WebAssembly app                                         nanoFramework firmware (C#)
 ┌───────────────────────────────┐                           ┌──────────────────────────────────┐
 │ Input: touch / gamepad /      │   WebRTC data channels    │ Drive, Servo, Lights, Matrix,    │
 │        keyboard / WebXR       │ ───── "ctrl" (reliable) ─▶│ Buzzer, Sensors, Battery,        │
 │ HUD + telemetry + settings    │ ◀──── telemetry ───────── │ Safety (deadman, low battery)    │
 │ Video decode (WebCodecs)      │ ◀──── "video" (unordered) │ Native camera task (C):          │
 │ ML on WebGPU / WebGL / Wasm   │       JPEG frames         │   esp32-camera -> libpeer        │
 └───────────────────────────────┘                           └──────────────────────────────────┘
              │                                                              │
              └────────── signaling only: WebTorrent-style tracker ──────────┘
                          (public hub, or MiniRover.Server on your LAN)
```

## Signaling and connection

- Browser and car meet in a **room** on a WebSocket tracker (the WebTorrent tracker protocol, as used by SpawnDev.RTC). The room key is a random 20-byte value created by the car during setup and shared through the pairing link.
- The tracker only passes the WebRTC offer/answer. After that, everything is peer to peer.
- After the data channel opens, the car and the browser prove they share the pairing secret with an Ed25519 challenge before the car accepts drive commands.
- STUN is used to find a path through home routers. On the same LAN, direct host candidates are enough.

## Video

The classic ESP32 has no video encoder, and its camera hardware outputs JPEG. A native FreeRTOS task in the firmware grabs each JPEG frame from the camera driver and sends it straight down a dedicated WebRTC data channel, so frame bytes never pass through the C# heap. The C# side only sets resolution, quality, frame rate and on/off.

In the browser, each frame arrives as a JavaScript ArrayBuffer. It is decoded with WebCodecs into a `VideoFrame`, drawn to the screen, and when an ML mode is on, copied directly into GPU memory for the model. Pixel data stays in JavaScript and GPU memory and is never copied into the .NET heap.

The video channel is unordered with no retransmits: a late frame is useless, and a lost frame should never delay the next one or a control command.

## Control

- The client merges all input sources into one drive intent (throttle, steer, camera pan/tilt, buttons), applies dead zones, curves and the speed limit, and sends it about 30 times a second on the reliable control channel.
- Each control frame has a sequence number. The car drops stale frames.
- **Deadman:** if no control frame arrives for 300 ms, the car stops the motors on its own. A dropped WiFi link, a closed browser tab or a crashed app all end the same way: stopped.

## Shared protocol

The message format is written once in C# and compiled into both the nanoFramework firmware and the .NET client, so the two sides cannot drift apart. Tests encode on one runtime and decode on the other.

## Offline / LAN mode

`MiniRover.Server` is a small ASP.NET Core app you can run on a laptop. It serves the browser app and runs its own tracker and STUN server, so the whole system works with no internet connection, including when the car hosts its own WiFi access point.
