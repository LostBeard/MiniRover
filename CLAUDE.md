# MiniRover - notes for AI coding agents

Read [PLANS.md](PLANS.md) first; it is the roadmap and status. Hardware facts are in [Docs/hardware.md](Docs/hardware.md). Do not guess pins or addresses; every one there is sourced.

## Ground rules for this repo

- **Public, generic project.** Code, docs and defaults must work for any FNK0053 owner. No personal network names, room keys, credentials or machine paths in committed files.
- **Do not copy Freenove code or artwork.** Freenove's repository is CC BY-NC-SA 3.0, which is incompatible with MIT. Hardware facts (pins, addresses, wiring, register behaviour) are fine to use and cite; sketches, library code, LED-matrix tables and images are not. Write MiniRover's own.
- **Never commit secrets.** WiFi credentials and pairing keys live on the car (setup page) or in git-ignored `*.local.*` files.
- **Safety is firmware-side.** The deadman stop, battery cutoff and speed limit must not depend on the client behaving.
- **Video bytes never enter a managed heap.** The camera task in native firmware code sends frames directly; in the browser, frames stay in JavaScript and GPU memory. No `ToArray()` / `byte[]` staging of frames.
- **All browser JavaScript goes through SpawnDev.SpawnJS wrappers.** If a wrapper is missing, add it to SpawnDev.SpawnJS rather than writing raw JS here.
- **One protocol source.** Wire-format changes go in `src/MiniRover.Protocol` and must keep the cross-runtime parity tests green.
- **Test on real hardware.** Hardware-in-the-loop tests run against a real car with the wheels off the ground. Do not report a feature as working from a build alone.

## Known hardware traps

- GPIO32 is both the WS2812 data line and the battery ADC. Sample the battery only between LED updates.
- The PCA9685 has one PWM frequency for servos and motors: keep it at 50 Hz.
- The camera can be an OV2640 or a GC0308. Detect it; never assume.
- GPIO0 (IR receiver) and GPIO2 (buzzer) are boot strapping pins.

## Style

- Hyphens, not em dashes, in code comments and docs.
- Comment the WHY on non-obvious lines, especially hardware workarounds.
