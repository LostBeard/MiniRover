namespace MiniRover.Car
{
    /// <summary>
    /// 3x5 digit font for showing short numbers (the BLE setup code) on the two 8x8 matrices: two digits per
    /// matrix, so a 4-digit code fills both eyes. Original artwork.
    /// </summary>
    public static class Digits
    {
        // Each digit is 5 rows of 3 bits; bit 2 = left column, bit 0 = right column.
        static readonly byte[] s_font =
        {
            0b111, 0b101, 0b101, 0b101, 0b111, // 0
            0b010, 0b110, 0b010, 0b010, 0b111, // 1
            0b111, 0b001, 0b111, 0b100, 0b111, // 2
            0b111, 0b001, 0b011, 0b001, 0b111, // 3
            0b101, 0b101, 0b111, 0b001, 0b001, // 4
            0b111, 0b100, 0b111, 0b001, 0b111, // 5
            0b111, 0b100, 0b111, 0b101, 0b111, // 6
            0b111, 0b001, 0b010, 0b010, 0b010, // 7
            0b111, 0b101, 0b111, 0b101, 0b111, // 8
            0b111, 0b101, 0b111, 0b001, 0b111, // 9
        };

        /// <summary>Renders up to 4 digits ("0".."9") into a 16-byte matrix frame.</summary>
        public static byte[] Render(string digits)
        {
            var frame = new byte[16];
            for (int i = 0; i < 4 && i < digits.Length; i++)
            {
                int d = digits[i] - '0';
                if (d < 0 || d > 9) continue;
                int matrix = i / 2;              // digits 0,1 -> first matrix; 2,3 -> second
                int colOffset = (i % 2) * 4;     // 3 columns + 1 space per digit
                for (int row = 0; row < 5; row++)
                {
                    int bits = s_font[d * 5 + row];
                    for (int c = 0; c < 3; c++)
                    {
                        if ((bits & (4 >> c)) != 0)
                        {
                            // Frame rows 0..7 per matrix, bit n = column n; rows 1..5 centre the 5-row glyph.
                            frame[matrix * 8 + 1 + row] |= (byte)(1 << (colOffset + c));
                        }
                    }
                }
            }
            return frame;
        }
    }
}
