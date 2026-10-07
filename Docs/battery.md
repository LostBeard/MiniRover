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

- converts the raw 12-bit reading to millivolts at the pin with the **chip's factory calibration** (the reference voltage stored in its eFuse, ESP-IDF line fitting): every ESP32's ADC gain and offset differ. On the first car the calibrated line has a lower slope plus a 142 mV offset compared with Freenove's fixed formula `raw / 4096 * 3.9 * 3.7`, so near empty it reads about 0.09 V higher than the formula did. A chip without calibration data falls back to the formula;
- multiplies by the **divider ratio** (`battery.divider`, default 4.025: anchored where the formula was set up, a full pack at raw 2369 = 8.35 V). The kit's resistors vary, so calibrate it once in **Settings -> Battery** by entering the pack voltage measured with a multimeter.

The car only reads the battery when the pack is switched on. With the power switch off and USB connected, the board still runs (lights on the shield stay lit from USB) but the battery reads 0 V and the car refuses to drive or move the servos.

## Voltage to percent

Freenove's PC client maps 7.0 V to 0% and 8.4 V to 100% in a straight line. Li-ion discharge is not linear: the voltage drops quickly at first, sits on a long plateau, then falls off a cliff. A straight line reads too low in the middle and too high near the end. MiniRover uses a per-cell discharge curve (open-circuit voltage to state of charge) applied to half the pack voltage.

## Voltage sag under load

Measured on the car running on battery only (2026-10-07): at rest 7.80 V; the camera stream and all 12 LEDs at full white moved it by 0.02 V or less; the motors pulled it down hard - 0.5 V for a moment when they start, about 0.13 V while they keep running at 60 % - and it recovered within one 2 s sample when they stopped.

So the motors are the only load that matters, and MiniRover compensates for them (`BatterySag`, shared by the firmware and the unit tests):

- every sample carries the motor load (mean PWM duty of the four motors);
- each step from resting to driving teaches the car how many volts one unit of load costs on this pack, today (it changes with charge and temperature, so it keeps learning);
- while driving, the learned sag is added back, so the percentage keeps following the real charge on a long drive instead of sinking toward the sagged readings (measured: raw readings 7.62-7.70 V while driving, the gauge stayed at 7.73-7.77 V against 7.77 V rested);
- warnings and the motor cut-off still use rested readings only.

### Charger percentages

A charger that shows a percentage while it charges works it out from the voltage it measures *while current flows*, which reads 0.1-0.2 V per cell high. A nearly empty cell can show 40-50 % moments after it goes in. Compare the car with a cell's voltage at rest (out of the car for a while, before charging starts).

## Protection

- **Low** (default about 3.4 V per cell, rested): the app warns and the car lowers its speed limit.
- **Critical** (default about 3.2 V per cell, rested): the car stops the motors and refuses to drive until the batteries are replaced. Lights and telemetry stay on so you can see why.

Both thresholds are adjustable in settings, within safe limits.
