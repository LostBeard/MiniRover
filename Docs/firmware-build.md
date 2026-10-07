# Building the firmware

MiniRover runs on **.NET nanoFramework 2.0** with a custom firmware image (`nanoCLR`) for the car's ESP32-WROVER. Most kit owners never need to build it: prebuilt images will ship with each GitHub release. This page is for developers.

## Why a custom image (not the stock nanoFramework one)

The stock nanoFramework ESP32 images run the debugger/deploy link (the "wire protocol") at 921600 baud. The FNK0053's **CH340C USB-serial chip cannot sustain that**: it overruns and silently drops bytes from the ESP32 to the PC. Short messages get through, longer ones do not, so Visual Studio and `nanoff` report that no device was found and nothing can be deployed. Measured on a real kit:

| Test | 921600 baud | 460800 baud |
|---|---|---|
| esptool read of 1.29 MB of flash | "Corrupt data" | clean (MD5 verified) |
| Device-info reply (200 bytes) | 6-23 bytes lost per reply, Windows reports Overrun | complete |

The `MINIROVER_ESP32` target fixes this and prepares the board for WebRTC:

- wire protocol at **460800 baud**, with **CRC32** on so any corruption is detected and retried;
- IPv6, DTLS and PSRAM settings needed by the WebRTC stack (libpeer);
- 240 MHz CPU;
- WebRTC data channels compiled in: the `SpawnDev.nanoFramework.WebRTC` interop assembly and its patched libpeer, which the build uses instead of the ESP-IDF registry copy;
- the camera: esp32-camera + esp_jpeg (vendored in `firmware/native/components`, see the README there) and MiniRover's own interop assembly `MiniRover.Native` (`firmware/native/MiniRover.Native`: camera task, WiFi signal and power save, memory numbers);
- BLE (NimBLE) for setup from the web app;
- a memory budget that lets WebRTC, BLE and the camera run together: 1 MB of PSRAM reserved for native code, NimBLE and mid-size allocations in PSRAM, FreeRTOS functions in flash to make IRAM room for the camera's interrupt code (each measured, see the comments in the target's `sdkconfig.default_minirover.esp32`);
- speed: built for **ESP32 chip revision 3 and later** (no PSRAM cache workaround), compiled with `-O2`, PSRAM at
  80 MHz, ChaCha20-Poly1305 for WebRTC encryption. Video went from 12 to 20 fps at 320x240 with these (see
  [video.md](video.md)). Freenove's WROVER-E kits are revision 3; an older board with a WROVER-B module (revision 1)
  will not boot this image. `esptool chip-id` prints the revision ("revision v3.1").

### Changing an interop assembly

`MiniRover.Native` (and `SpawnDev.nanoFramework.WebRTC`) are interop assemblies: the firmware holds a native method table with a checksum that must match the managed assembly. After adding, removing or changing any `extern` method, build the nfproj, then copy **all** generated table files from its `Stubs/` folder (`*_Native.cpp`, `*_Native.h`, every `*_mshl.cpp` and class `.h`) into the native folder, implement the new method, and rebuild the firmware. A firmware and an app built from different checksums refuse to run together. Every method gets a table entry, so adding even a plain managed method changes the checksum (measured); constants do not.

## Get the sources

```
git clone --recursive https://github.com/LostBeard/MiniRover.git
```

`--recursive` brings in `firmware/external/SpawnDev.nanoFramework.WebRTC` (WebRTC data channels for nanoFramework) and its libpeer. In an existing clone: `git submodule update --init --recursive`.

## Prerequisites (Windows)

- [ESP-IDF v5.5.5](https://docs.espressif.com/projects/esp-idf/en/v5.5.5/esp32/get-started/windows-setup.html) at `C:\Espressif\frameworks\esp-idf-v5.5.5`
- Python 3.13 (ESP-IDF 5.5's `export.bat` uses the `idf5.5_py3.13_env` environment)
- CMake 3.31+ and Ninja (installed by the ESP-IDF tools installer)
- The nanoFramework interpreter fork, cloned **next to** this repository:

```
git clone --branch minirover/esp32-wrover https://github.com/LostBeard/nf-interpreter.git
cd nf-interpreter
git submodule update --init targets-community
```

Copy `config/user-tools-repos.TEMPLATE.json` to `config/user-tools-repos.json` and set `ESP32_IDF_PATH`. Copy `config/user-prefs.TEMPLATE.json` to `config/user-prefs.json`.

## Build

From `cmd` or PowerShell (not Git Bash):

```
firmware\build-firmware.bat
```

Set `NF_DIR` if the interpreter clone is somewhere else. The script prints the output files:

| File | Flash offset |
|---|---|
| `build\bootloader\bootloader.bin` | `0x1000` |
| `build\partition_table\partition-table.bin` | `0x8000` |
| `build\nanoCLR.bin` | `0x10000` |

If you change the target's defconfig or sdkconfig, delete `nf-interpreter\sdkconfig` before building, or the old settings win.

## Building the car application

The C# projects under `firmware/` are nanoFramework 2.0 projects. Build them with the preview nanoFramework extension for Visual Studio 2026, or from the command line with MSBuild and the extension's MSBuild files. Packages restore from nuget.org with `nuget restore firmware\MiniRover.Car\packages.config -PackagesDirectory firmware\packages`.

### Deploying the app (fast, no debugger)

The build writes `firmware\MiniRover.Car\bin\Debug\MiniRover.Car.bin`, the app's complete deployment image. Write it
straight to the car's `deploy` partition:

```
powershell -ExecutionPolicy Bypass -File firmware\deploy-app.ps1 -Port COM8
```

This takes about 4 seconds (a debugger deploy takes minutes at the kit's 460800 baud) and works even when the
debugger cannot attach. The car restarts. Settings, the pairing key and WiFi credentials are in other partitions and
are kept. It uses esptool from PATH or the copy that comes with `nanoff`.

### Deploying with the debugger, and watching the log

```
dotnet run tools/nf-deploy.cs firmware/MiniRover.Car/bin/Debug COM8 30
minirover monitor COM8 60
```

(`NF_DEBUG_LIBRARY` must point at the preview extension's `nanoFramework.Tools.DebugLibrary.Net.dll`.) `nf-deploy`
restarts the CLR held for the debugger before it writes, so the old app's native tasks are not running.

If the debugger stops finding the device ("found no device" / "Couldn't connect") while the car otherwise works,
unplugging and replugging the USB cable is the first thing to try; `deploy-app.ps1` still works meanwhile.
