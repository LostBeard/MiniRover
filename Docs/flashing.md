# Flashing the car

> Prebuilt images are not published yet. Until the first release, build the firmware yourself ([firmware-build.md](firmware-build.md)).

## Before you start

- The firmware needs an **ESP32 chip revision 3 or later** (every recent FNK0053 kit). Check with
  `esptool --chip esp32 --port COM8 chip-id`: it prints e.g. "ESP32-D0WD-V3 (revision v3.1)".

- Connect the car's ESP32 board to your PC with a micro-USB **data** cable. A new COM port appears ("USB-SERIAL CH340"). If it does not, install the CH340 driver (Freenove includes it in their repository under `CH340/`).
- **If the board is in the car, put charged batteries in and turn the power switch on.** On USB power alone, anything that drives the servos (including Freenove's own factory sketch) pulls the supply down and the ESP32 resets over and over: the lights blink and the servos click. MiniRover's firmware avoids this by keeping the servos off until it detects a battery, but other firmware may not.

## Flash with esptool

Using the three files from the build (or a release), with your COM port:

```
esptool --chip esp32 --port COM8 --baud 460800 write-flash 0x1000 bootloader.bin 0x8000 partition-table.bin 0x10000 nanoCLR.bin
```

Use 460800 baud, not 921600: the kit's CH340C USB-serial chip drops data at 921600 ([why](firmware-build.md#why-a-custom-image-not-the-stock-nanoframework-one)).

To start completely fresh (this erases the saved WiFi network and calibration), run `esptool --chip esp32 --port COM8 erase-flash` first.

## Going back to Freenove's firmware

Nothing is permanent. Flash any of Freenove's Arduino sketches from the Arduino IDE as their tutorial describes, and the board runs their software again.
