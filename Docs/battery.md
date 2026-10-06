# Battery

## Pack

Two 18650 Li-ion cells in series (2S):

| State | Pack voltage | Per cell |
|---|---|---|
| Full (just off the charger) | 8.4 V | 4.2 V |
| Nominal | 7.4 V | 3.7 V |
| Low - time to stop | about 6.8 V | 3.4 V |
| Empty - stop now | 6.4 V | 3.2 V |

The car's USB port does **not** charge the cells. Use an external 18650 charger.

## How the car measures it

The pack goes through a resistor divider (about 1/4) into **GPIO32**, the same pin that drives the WS2812 LEDs (see [hardware.md](hardware.md)). The firmware pauses LED updates for each sample. Battery samples are taken every couple of seconds, which is plenty: the voltage changes slowly.

The ESP32 ADC is not precise out of the box, and the divider resistors have tolerance. MiniRover:

- converts the raw 12-bit reading with Freenove's fitted formula `raw / 4096 * 3.9 * 3.7` (on the first car tested, a full pack read raw 2369 = 8.35 V). The ESP32 ADC is non-linear and nanoFramework does not apply the chip's factory calibration yet; reading calibrated millivolts (the reference voltage is stored in the chip's eFuse) is planned for the firmware's native code;
- multiplies by a **divider coefficient** stored on the car (default 3.7). You can calibrate it once in **Settings -> Battery** by entering the pack voltage measured with a multimeter.

The car only reads the battery when the pack is switched on. With the power switch off and USB connected, the board still runs (lights on the shield stay lit from USB) but the battery reads 0 V and the car refuses to drive or move the servos.

## Voltage to percent

Freenove's PC client maps 7.0 V to 0% and 8.4 V to 100% in a straight line. Li-ion discharge is not linear: the voltage drops quickly at first, sits on a long plateau, then falls off a cliff. A straight line reads too low in the middle and too high near the end. MiniRover uses a per-cell discharge curve (open-circuit voltage to state of charge) applied to half the pack voltage.

## Voltage sag under load

When the motors pull current, the pack voltage drops for a moment and recovers when they stop. Reading the battery while driving hard would make the percentage jump around and trigger false low-battery warnings. MiniRover:

- tags every sample with whether the motors were running;
- weights idle samples much more heavily in a smoothed estimate;
- never lets a single sagging sample trigger a warning.

## Protection

- **Low** (default about 3.4 V per cell, rested): the app warns and the car lowers its speed limit.
- **Critical** (default about 3.2 V per cell, rested): the car stops the motors and refuses to drive until the batteries are replaced. Lights and telemetry stay on so you can see why.

Both thresholds are adjustable in settings, within safe limits.
