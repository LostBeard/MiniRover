namespace MiniRover.Car.Drivers
{
    /// <summary>
    /// Freenove FNK0053 pin and address map. Source: Freenove_4WD_Car_Kit_for_ESP32 Tutorial.pdf,
    /// Datasheet/ESP32_Schematic.pdf and Sketches/06.3_Multi_Functional_Car. See Docs/hardware.md.
    /// </summary>
    public static class BoardPins
    {
        // Shared I2C bus (PCA9685, PCF8574, VK16K33).
        public const int I2cBus = 1;
        public const int I2cSda = 13;
        public const int I2cScl = 14;

        public const int Pca9685Address = 0x5F;
        public const int Pcf8574Address = 0x20;
        public const int MatrixAddress = 0x71;

        // PCA9685 channels.
        public const int ServoPanChannel = 0;
        public const int ServoTiltChannel = 1;
        // Motor M1..M4 as [IN1, IN2] channel pairs. M1/M2 = left side, M3/M4 = right side.
        public const int M1In1 = 15, M1In2 = 14;
        public const int M2In1 = 9, M2In2 = 8;
        public const int M3In1 = 12, M3In2 = 13;
        public const int M4In1 = 10, M4In2 = 11;

        // Direct GPIO.
        public const int IrReceiver = 0;   // NEC remote; also a boot strapping pin
        public const int Buzzer = 2;       // passive buzzer via NPN; also the board's blue LED
        public const int SonarTrigger = 12; // only with the ultrasonic head fitted
        public const int SonarEcho = 15;
        public const int RgbLeds = 32;     // 12x WS2812 AND the battery ADC - see RgbLeds/BatterySense
        public const int LightSensor = 33; // photoresistor pair, one divider

        // ESP32 ADC1 channel numbers for the analog pins (GPIO32 = ADC1_CH4, GPIO33 = ADC1_CH5).
        public const int BatteryAdcChannel = 4;
        public const int LightAdcChannel = 5;

        public const int RgbLedCount = 12;
    }
}
