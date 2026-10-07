# The car's local interfaces

Everyday driving goes over the WebRTC link from the app (see [architecture.md](architecture.md)). The car also has
two local interfaces for setup, testing and tinkering.

## HTTP (on your WiFi network)

The car prints its address at boot (`WiFi: connected, http://<ip>`) and reports it to the app.

Always available:

| Route | What it does |
|---|---|
| `GET /status` | JSON: battery, sensors, WiFi signal (`rssi`), camera (`sensor`, `fpsTenths`, frame stats), memory (`memoryKb`), link state and counters, faults, settings |
| `GET /stop` | Stops the motors. Open to anyone on purpose: whoever is next to the car can always stop it |

**Everything else is off by default.** HTTP on a home network has no pairing, so a route that moves the car would let
anyone on the network drive it. Turn the test API on from a paired channel:

- the app (a paired browser): Settings, or
- `minirover hw COM8 "set http.api=1"` (BLE, needs the code on the car's eyes; during the 2 minutes after power-on), or
- `minirover link "set http.api=1"` (the paired WebRTC link).

Then:

| Route | Parameters |
|---|---|
| `/drive` | `left`, `right` (-1..1), `ms` hold (default 300, max 1000). The car stops by itself when the hold runs out |
| `/motor` | `m` (0..3), `speed` (-1..1), `ms` |
| `/servo` | `pan`, `tilt` (degrees, 90 = center), `relax=1` |
| `/servo/center` | |
| `/leds` | `r`, `g`, `b` (0..255), optional `i` (one LED, 0..11), `brightness` |
| `/matrix` | `eyes` = `alive` (default: blinks, follows the steering), `open`, `happy`, `heart`, `sad`, `angry`, `surprised`, `sleepy`; or `text` (scrolls, `passes`, default 1, 0 = keep going); or `hex` (32 hex digits, one raw frame); `brightness` 0..15 |
| `/buzzer` | `hz`, `ms` (max 2000) |
| `/sonar` | distance (ultrasonic head only) |
| `/settings` | any of the keys below, e.g. `/settings?camera.fps=10` |
| `/wifi/setup` | forget the network and restart in setup mode |
| `/reboot` | |
| `/link/times` | (always open, read-only) the WebRTC session's send timings, see [video.md](video.md) |
| `/udptest` | `ip`, `port`, `ms` (max 30000): WiFi throughput test. The car sends 1200-byte UDP datagrams to `ip:port` as fast as its WiFi takes them; `/status` `net.udpTest` shows progress. `minirover udptest http://<car>` runs it and prints the rate and losses |

Turn it off again with `http.api=0`.

## Settings

Changed through `/settings` (HTTP), the BLE setup service, or the app link. Values are clamped to safe ranges.

| Key | Default | Range / meaning |
|---|---|---|
| `drive.limit` | 1 | top speed 0..1 (the app's speed levels apply on top) |
| `motor0.invert` .. `motor3.invert` | 0 | 1 reverses a motor that was wired the other way round |
| `motor0.gain` .. `motor3.gain` | 1 | 0..2, evens out a motor that runs faster or slower than the others |
| `motor.minduty` | 1600 | 0..4000, the PWM below which the motors only hum |
| `pan.trim`, `tilt.trim` | 0 | -30..30 degrees, so 90 really is straight ahead |
| `battery.coef` | 3.7 | 2.5..5.5, calibrate against a multimeter (see [battery.md](battery.md)) |
| `led.brightness` | 64 | 0..255 |
| `camera.size` | 6 | esp32-camera frame size: 1 = 160x120, 6 = 320x240, 8 = 400x296, 9 = 480x320, 10 = 640x480, 11 = 800x600 (OV2640 only) |
| `camera.quality` | 12 | JPEG quality 8..63, lower is better and bigger |
| `camera.fps` | 15 | 1..30 |
| `sensor.xclk` | 24 | camera clock in MHz, 8..24. The usual 20 MHz jams the car's own WiFi (see [video.md](video.md)) |
| `camera.flip`, `camera.mirror` | 1, 1 | the head holds the sensor upside down; both 1 = upright (see [hardware.md](hardware.md)) |
| `http.api` | 0 | 1 turns on the HTTP test API above |
| `led.corners` | `------------` | which corner each of the 12 LEDs is on (0 front left, 1 front right, 2 rear left, 3 rear right, - unknown); set by the app's light calibration |

## Serial / debug

With USB connected, `minirover monitor COM8 60` prints the car's log (boot steps with memory, link and camera
events). `minirover hw COM8 "..."` drives the hardware over BLE without WiFi, reading the pairing code from the log.
