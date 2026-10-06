# Servo centering and calibration

The camera sits on a pan/tilt head driven by two servos:

| Servo | PCA9685 channel | Moves |
|---|---|---|
| Servo 1 | 0 | Pan (left/right) |
| Servo 2 | 1 | Tilt (up/down) |

A servo's output spline can be attached at any tooth, so if you attach the head while the servo sits at a random angle, "straight ahead" in software will point somewhere else and part of the travel will be lost. **Center both servos before you mount the camera head.**

## Step 1 - drive both servos to 90 degrees

### With MiniRover firmware (once the firmware is released)

1. Flash MiniRover (see the flashing guide, coming with the first firmware release).
2. Power the car **from the batteries** with the power switch on (USB alone cannot drive the servos properly).
3. Use **Settings -> Servo calibration -> Center** in the app, or type `servo center` in the serial console. Both servos move to 90 degrees and hold there.

### With Freenove's own sketch (works today)

Freenove's tutorial does this as its first step:

1. Install the Arduino IDE and the ESP32 board package as described in Chapter 0 of Freenove's `Tutorial.pdf`.
2. Open `Sketches/00.0_Servo_90/00.0_Servo_90.ino` from the [Freenove repository](https://github.com/Freenove/Freenove_4WD_Car_Kit_for_ESP32).
3. Select the **ESP32 Wrover Module** board and the car's COM port, then upload.
4. With batteries in and the power switch on, both servos go to 90 degrees.

Flashing MiniRover later replaces this sketch; nothing else is needed.

## Step 2 - mount the head

With the servos held at 90 degrees:

- Fit the pan servo horn so the head faces **straight forward** along the car.
- Fit the tilt servo horn so the camera looks **level** (horizontal).
- Plug Servo 1 and Servo 2 into their own headers; Freenove's assembly guide warns not to swap them.
- Get as close as the spline teeth allow. Software trim removes the last few degrees.

## Step 3 - fine trim in software

Spline teeth are a few degrees apart, so the head is rarely perfect. MiniRover stores a trim per servo in the car's flash:

1. **Settings -> Servo calibration**.
2. Nudge pan until the video center lines up with something straight ahead of the car.
3. Nudge tilt until the horizon is level in the video.
4. **Save to car**. The trim survives reboots and firmware updates.

The app also stores safe travel limits for each servo, so the head never drives into the chassis. Freenove's own firmware limits tilt to 80-180 degrees for the same reason.
