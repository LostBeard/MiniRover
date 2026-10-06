namespace MiniRover.Car
{
    /// <summary>
    /// MiniRover's own LED-matrix artwork (16 bytes: 8 rows per matrix, bit n = column n).
    /// Original drawings - do not paste Freenove's tables here (CC BY-NC-SA, see CLAUDE.md).
    /// </summary>
    public static class Eyes
    {
        static byte[] Pair(byte[] eye) => new byte[]
        {
            eye[0], eye[1], eye[2], eye[3], eye[4], eye[5], eye[6], eye[7],
            eye[0], eye[1], eye[2], eye[3], eye[4], eye[5], eye[6], eye[7],
        };

        /// <summary>Round open eye with a 2x2 pupil in the middle.</summary>
        public static readonly byte[] Open = Pair(new byte[]
        {
            0b00111100,
            0b01111110,
            0b11111111,
            0b11100111,
            0b11100111,
            0b11111111,
            0b01111110,
            0b00111100,
        });

        /// <summary>Closed (a blink frame).</summary>
        public static readonly byte[] Closed = Pair(new byte[]
        {
            0, 0, 0,
            0b01111110,
            0b11111111,
            0, 0, 0,
        });

        /// <summary>Happy upturned arcs.</summary>
        public static readonly byte[] Happy = Pair(new byte[]
        {
            0, 0,
            0b00111100,
            0b01111110,
            0b11000011,
            0b10000001,
            0, 0,
        });

        /// <summary>Concentric "setup mode" target, shown while the WiFi setup access point is up.</summary>
        public static readonly byte[] Setup = Pair(new byte[]
        {
            0b11111111,
            0b10000001,
            0b10111101,
            0b10100101,
            0b10100101,
            0b10111101,
            0b10000001,
            0b11111111,
        });

        /// <summary>X eyes: critical battery, the car will not drive.</summary>
        public static readonly byte[] Dead = Pair(new byte[]
        {
            0b10000001,
            0b01000010,
            0b00100100,
            0b00011000,
            0b00011000,
            0b00100100,
            0b01000010,
            0b10000001,
        });
    }
}
