# Hardware - Freenove 4WD Car Kit (FNK0053)

Everything here comes from Freenove's own repository ([Freenove_4WD_Car_Kit_for_ESP32](https://github.com/Freenove/Freenove_4WD_Car_Kit_for_ESP32)): `Tutorial.pdf`, `Datasheet/ESP32_Schematic.pdf` and the example sketches. Items marked **(confirm)** will be verified on a real car during firmware bring-up, and this page updated.

## Controller

| | |
|---|---|
| Board | Freenove ESP32-WROVER CAM (`ESP_CAM_PRO_v1.4`) |
| Chip | **Classic ESP32** (dual-core Xtensa LX6, 240 MHz). Not an ESP32-S3. |
| Flash / PSRAM | 4 MB / 8 MB |
| USB | Micro-USB through a CH340C USB-serial chip (install the CH340 driver on Windows if no COM port appears) |
| Antenna | PCB antenna or IPEX connector, depending on the module |

Freenove ships only one FNK0053 board; there is no S3 version of this kit.

## Bus and address map

All I2C devices share one bus: **SDA = GPIO13, SCL = GPIO14**.

| I2C address | Chip | Job |
|---|---|---|
| `0x5F` | PCA9685 16-channel PWM | 2 servos + 4 motor driver inputs |
| `0x20` | PCF8574 I/O expander | 3 line-tracking sensors |
| `0x71` | VK16K33 (HT16K33 compatible) | Two 8x8 LED matrices on the camera head (only present when the head is plugged in) |

## PCA9685 channels

| Channel | Use |
|---|---|
| 0 | Servo 1 - camera **pan**: below 90 degrees turns the camera to the car's right, above 90 to its left |
| 1 | Servo 2 - camera **tilt**: above 90 degrees looks up (Freenove limits tilt to 80..180) |
| 2-7 | Free |
| 8, 9 | Motor M2 (IN1 = 9, IN2 = 8) |
| 10, 11 | Motor M4 (IN1 = 10, IN2 = 11) |
| 12, 13 | Motor M3 (IN1 = 12, IN2 = 13) |
| 14, 15 | Motor M1 (IN1 = 15, IN2 = 14) |

- IN1 high + IN2 low = forward, IN1 low + IN2 high = backward.
- **M1 = front-left, M2 = rear-left, M3 = front-right, M4 = rear-right** (front = the line-tracking end). Verified on a real car.
- **Direction depends on assembly.** On the first car tested, all four motors ran backwards with Freenove's IN1 = forward convention (the motor leads were plugged the other way round). MiniRover stores a per-motor direction flag on the car, set once during setup (`motorN.invert`), instead of changing the firmware.
- The PCA9685 has one PWM frequency for all 16 channels. Servos need about 50 Hz, so the motors run at 50 Hz PWM too. (One of Freenove's libraries switches the chip to 1 kHz for motors and back to 50 Hz for servos; MiniRover keeps one frequency so servos and motors can be driven at the same time.)
- Motors can turn the wrong way if a motor is wired reversed. MiniRover has per-motor direction invert and trim in its settings.

## Direct GPIO

| GPIO | Use | Notes |
|---|---|---|
| 0 | IR receiver (NEC protocol) | Also the BOOT strapping pin |
| 1 / 3 | Serial TX / RX | USB serial console |
| 2 | Passive buzzer (through an NPN transistor) | Shared with the board's blue LED. Loudest near 2 kHz. |
| 12 / 15 | Ultrasonic HC-SR04 Trig / Echo | Only when the ultrasonic head is fitted instead of the camera head |
| 13 / 14 | I2C SDA / SCL | |
| 32 | **12x WS2812 RGB LEDs** (GRB order) **and battery voltage ADC** | Shared pin, see below |
| 33 | Photoresistor pair (ADC) | One divider: both sensors equally lit reads mid-scale. Lower/higher means the light is to one side. |
| 4, 5, 18, 19, 21, 22, 23, 25, 26, 27, 34, 35, 36, 39 | Camera | See below |

### GPIO32 is shared by the LEDs and the battery sense

Freenove wired the WS2812 data line and the battery voltage divider to the same pin ("Because the battery voltage is not read frequently, this GPIO is also used to control the WS2812"). The firmware has to:

1. Finish any LED update.
2. Switch GPIO32 to an ADC input and take the battery sample.
3. Switch it back to the LED driver.

The divider is about 1/4, so a full 8.4 V pack reads about 2.1 V at the pin. The exact coefficient differs between Freenove sketch versions (3, 3.4 x 3.3/4096 scaling, 4). MiniRover calibrates it: see [battery.md](battery.md).

### Camera (DVP)

| Signal | GPIO |
|---|---|
| XCLK | 21 |
| SIOD / SIOC (SCCB) | 26 / 27 |
| D7..D0 (Y9..Y2) | 35, 34, 39, 36, 19, 18, 5, 4 |
| VSYNC / HREF / PCLK | 25 / 23 / 22 |
| PWDN / RESET | not connected |

Freenove ships two camera types, **OV2640** or **GC0308**, and the board cannot tell you which you have until the firmware reads the sensor ID over SCCB. MiniRover detects it at boot and reports it (`/status` -> `camera.sensor`: `38` = 0x26 OV2640, `155` = 0x9B GC0308).

- **OV2640** encodes JPEG itself. Measured on the first car: 320x240 at quality 12 is about 6 KB per frame and streams at 14.6 fps to a browser.
- **GC0308** has **no JPEG encoder**: it outputs YUV, and the firmware JPEG-encodes each frame in software (esp32-camera's `frame2jpg`), which costs CPU and lowers the frame rate. Its largest size is 640x480. Not yet measured on a real GC0308.

**Orientation:** the camera head holds the sensor upside down. MiniRover's default is a 180-degree rotation (`camera.flip=1`, `camera.mirror=1`), verified on an OV2640 by panning: with the camera turned left (pan above 90), the scene moves right in the picture, as it should for an unmirrored image. If your picture is mirrored or upside down, change those two settings.

**Resources the camera takes** (so nothing else uses them): XCLK on LEDC low-speed timer 3 / channel 7, SCCB on I2C port 1, I2S0 for the parallel data, and about 40 KB of internal RAM for DMA buffers and its task.

## Power

- **2x 18650 Li-ion cells** in series (2S): 8.4 V full, about 7.4 V nominal. Not included in the kit. Freenove's [battery list](https://github.com/Freenove/Freenove_Battery_List/blob/main/18650_Flat-Top_Unprotected.md) recommends flat-top cells that can supply more than 3 A.
- **USB does not charge the cells.** Use an external 18650 charger.
- **The power button only switches the pack on with USB unplugged** (observed on the first car tested): unplug USB, press the button, plug USB back in. With the pack off, USB still powers the logic (shield LEDs lit), the battery reads 0 V, and MiniRover keeps the motors and servos off.
- Freenove warns that assembling or running the car without proper batteries can damage the servos.

## What the kit does not have

- **No microphone, no speaker** - no audio streaming. The buzzer is the only sound source.
- **No IMU** (no accelerometer or gyro).
- **No wheel encoders** - speed is open-loop PWM.
- **Essentially no free GPIOs.** Adding hardware (for example an I2S microphone) means giving up an existing function. I2C devices can still be added on the existing SDA/SCL bus.
