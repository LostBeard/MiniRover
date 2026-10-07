# MiniRover

**Open-source software for the [Freenove 4WD Car Kit (FNK0053)](https://github.com/Freenove/Freenove_4WD_Car_Kit_for_ESP32)** - C# firmware on the car's ESP32 with [.NET nanoFramework](https://www.nanoframework.net/), and a browser app written in C# that runs on .NET WebAssembly.

Drive the car from any modern browser with a gamepad, a touchscreen, a keyboard or (later) a VR headset. Watch live video, read every sensor, control the lights and buzzer, keep an eye on the battery, and switch on machine-learning modes like face tracking and follow-me that run on your own GPU in the browser.

> **Status: early development.** See [PLANS.md](PLANS.md) for the roadmap and what works today.

## Why

The FNK0053 is a great kit. MiniRover gives it a modern, all-.NET software stack:

- **Car firmware in C#** on nanoFramework, with native code only where it has to be (camera capture and WebRTC).
- **Browser client in C#** on .NET WebAssembly. No install, works on Windows, macOS, Linux, ChromeOS and Android.
- **Peer-to-peer WebRTC** between browser and car. Video and control go straight to the car, never through a cloud relay.
- **Machine learning in the browser** on WebGPU / WebGL / WebAssembly with [SpawnDev.ILGPU.ML](https://github.com/LostBeard/SpawnDev.ILGPU.ML).

## Built with

| Piece | Library |
|---|---|
| Car firmware | [.NET nanoFramework](https://github.com/nanoframework) + [libpeer](https://github.com/sepfy/libpeer) (WebRTC) + [esp32-camera](https://github.com/espressif/esp32-camera) |
| Browser app | .NET 10 WebAssembly + [SpawnDev.SpawnJS](https://github.com/LostBeard/SpawnDev.SpawnJS) (JS interop) + [SpawnDev.SpawnJS.RazorRenderer / RazorUI](https://github.com/LostBeard/SpawnDev.SpawnJS.RazorRenderer) |
| Networking | [SpawnDev.RTC](https://github.com/LostBeard/SpawnDev.RTC) (WebRTC + tracker signaling, browser and desktop) |
| Machine learning | [SpawnDev.ILGPU.ML](https://github.com/LostBeard/SpawnDev.ILGPU.ML) on [SpawnDev.ILGPU](https://github.com/LostBeard/SpawnDev.ILGPU) |

## The hardware

The FNK0053 uses a **Freenove ESP32-WROVER CAM** board (classic ESP32, 4 MB flash, 8 MB PSRAM) with:

- 4 DC motors and 2 servos (camera pan/tilt) on a PCA9685 PWM controller
- Camera (OV2640 or GC0308, depending on the kit batch)
- 12 WS2812 RGB LEDs, two 8x8 LED matrices ("eyes"), passive buzzer
- 3 line-tracking sensors, 2 light sensors, IR remote receiver, optional ultrasonic distance sensor
- Battery voltage sensing (2x 18650 Li-ion cells, not included in the kit)

The car has **no microphone or speaker**, so there is no audio streaming; its sounds come from the buzzer. Full pin map and wiring notes: [Docs/hardware.md](Docs/hardware.md).

## Features (planned and in progress)

- **Drive** with touch (dual on-screen sticks), gamepad (with rumble), keyboard, or WebXR
- **Live video** from the car's camera, with pan/tilt control
- **Telemetry**: battery percent and voltage, line sensors, light sensors, distance, WiFi signal
- **Lights**: headlights, brake lights, turn signals, hazards, light shows, matrix "eyes"
- **Buzzer**: horn, tones and tunes
- **Safety in the firmware**: the car stops if it loses contact, protects the battery when it runs low, and supports a speed limit
- **Car-side modes**: line following, light seeking, obstacle avoidance
- **ML modes**: face tracking (camera follows a face), follow-me, follow an object, gesture driving, depth-aware auto-drive
- **Easy setup for every kit owner**: the car opens its own WiFi setup page on first boot, and you pair the browser with a link

## Getting started

Setup docs grow with the project:

1. [Hardware notes and pin map](Docs/hardware.md)
2. [Centering the camera servos before you mount the camera](Docs/servo-calibration.md)
3. [Battery notes](Docs/battery.md)
4. [Architecture](Docs/architecture.md)
5. [Building the firmware](Docs/firmware-build.md) and [flashing the car](Docs/flashing.md)
6. [Play mode: driving with no internet](Docs/play-mode.md) (the car makes its own WiFi; Bluetooth starts each connection)

## Repository layout

```
firmware/   nanoFramework car firmware (C#) + native camera interop + firmware build config
src/        shared protocol, browser app, desktop console, LAN server
tests/      unit, browser and hardware-in-the-loop tests
Docs/       user and developer documentation
```

## Contributing

Issues and pull requests are welcome. If you have the kit and something does not work, please open an issue with your camera type (OV2640 or GC0308, shown on the car's status page) and the firmware version.

## License

[MIT](LICENSE). Freenove, FNK0053 and the Freenove kit hardware belong to Freenove; MiniRover is an independent community project and is not affiliated with Freenove.

The face-tracking model (`src/MiniRover.App/wwwroot/models/blaze-face/model.tflite`) is Google's MediaPipe BlazeFace short-range detector, unmodified, under the Apache License 2.0; see the NOTICE next to it.
