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
- based on `ESP32_PSRAM_REV0`, so it runs on every ESP32 chip revision a kit might have.

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

Deploy and watch the debug output with:

```
dotnet run tools/nf-deploy.cs firmware/MiniRover.Car/bin/Debug COM8 30
```

(`NF_DEBUG_LIBRARY` must point at the preview extension's `nanoFramework.Tools.DebugLibrary.Net.dll`.)
