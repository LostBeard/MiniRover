namespace MiniRover.Protocol
{
    /// <summary>
    /// MiniRover's own artwork and renderers for the two 8x8 LED matrices ("eyes"), shared by the car (which draws
    /// them) and the app (which can preview them). Original drawings: never paste Freenove's tables here
    /// (CC BY-NC-SA, see CLAUDE.md).
    ///
    /// A frame is 16 bytes: bytes 0..7 are the rows (top to bottom) of the matrix on the viewer's LEFT when facing
    /// the car, 8..15 the right one. In a row byte bit 7 is the leftmost pixel, so a 0b literal reads as it looks
    /// (verified on the car: with low bit left, the pairing code "2270" read as a mirrored "5507").
    /// Screen columns 0..15 run left to right across both matrices as the viewer sees them.
    /// Plain arrays only: this file also compiles for nanoFramework.
    /// </summary>
    public static class EyeArt
    {
        public const int FrameBytes = 16;
        public const int Columns = 16;

        // Moods (MsgFace with FaceMood): the numbers are on the wire.
        public const int MoodOpen = 0;
        public const int MoodHappy = 1;
        public const int MoodHeart = 2;
        public const int MoodSad = 3;
        public const int MoodAngry = 4;
        public const int MoodSurprised = 5;
        public const int MoodSleepy = 6;
        public const int MoodCount = 7;

        /// <summary>The pupil can move this many pixels left/right of centre (and 1 up/down).</summary>
        public const int LookRange = 2;

        // The white of the eye: a round disc. The pupil is cut out of it.
        static readonly byte[] s_disc = { 0x3C, 0x7E, 0xFF, 0xFF, 0xFF, 0xFF, 0x7E, 0x3C };

        static readonly byte[] s_closed = { 0, 0, 0, 0b01111110, 0b11111111, 0, 0, 0 };

        static readonly byte[] s_happy = { 0, 0, 0b00111100, 0b01111110, 0b11000011, 0b10000001, 0, 0 };

        static readonly byte[] s_heart =
        {
            0b01100110,
            0b11111111,
            0b11111111,
            0b11111111,
            0b01111110,
            0b00111100,
            0b00011000,
            0b00000000,
        };

        // Sad and angry have brows that slant towards or away from the nose, so the right eye is the mirror image.
        // These are the viewer's LEFT eye: its nose side is on the right.
        static readonly byte[] s_sadLeft =
        {
            0b00000011,
            0b00001110,
            0b00111000,
            0b00000000,
            0b00111100,
            0b01111110,
            0b01111110,
            0b00111100,
        };

        static readonly byte[] s_angryLeft =
        {
            0b11000000,
            0b01110000,
            0b00011100,
            0b00000000,
            0b00111100,
            0b01111110,
            0b01111110,
            0b00111100,
        };

        static readonly byte[] s_surprised =
        {
            0b00111100,
            0b01000010,
            0b10000001,
            0b10011001,
            0b10011001,
            0b10000001,
            0b01000010,
            0b00111100,
        };

        static readonly byte[] s_sleepy = { 0, 0, 0, 0b11111111, 0b11100111, 0b01111110, 0b00111100, 0 };

        static readonly byte[] s_setup =
        {
            0b11111111,
            0b10000001,
            0b10111101,
            0b10100101,
            0b10100101,
            0b10111101,
            0b10000001,
            0b11111111,
        };

        static readonly byte[] s_dead =
        {
            0b10000001,
            0b01000010,
            0b00100100,
            0b00011000,
            0b00011000,
            0b00100100,
            0b01000010,
            0b10000001,
        };

        static byte[] Pair(byte[] eye, bool mirrorRight)
        {
            var f = new byte[FrameBytes];
            for (int r = 0; r < 8; r++)
            {
                f[r] = eye[r];
                f[8 + r] = mirrorRight ? Reverse(eye[r]) : eye[r];
            }
            return f;
        }

        static byte Reverse(byte b)
        {
            int v = 0;
            for (int i = 0; i < 8; i++) if ((b & (1 << i)) != 0) v |= 0x80 >> i;
            return (byte)v;
        }

        /// <summary>Eyes looking straight ahead.</summary>
        public static byte[] Open() => Alive(0, 0, false);

        /// <summary>Closed (a blink frame).</summary>
        public static byte[] Closed() => Pair(s_closed, false);

        /// <summary>Concentric "setup mode" target, shown while the WiFi setup access point is up.</summary>
        public static byte[] Setup() => Pair(s_setup, false);

        /// <summary>X eyes: critical battery, the car will not drive.</summary>
        public static byte[] Dead() => Pair(s_dead, false);

        /// <summary>A mood frame (Mood* constants); an unknown number gives open eyes.</summary>
        public static byte[] Mood(int mood)
        {
            switch (mood)
            {
                case MoodHappy: return Pair(s_happy, false);
                case MoodHeart: return Pair(s_heart, false);
                case MoodSad: return Pair(s_sadLeft, true);
                case MoodAngry: return Pair(s_angryLeft, true);
                case MoodSurprised: return Pair(s_surprised, false);
                case MoodSleepy: return Pair(s_sleepy, false);
                default: return Open();
            }
        }

        /// <summary>
        /// Round eyes with a 2x2 pupil. <paramref name="lookX"/> moves the pupils right as the viewer sees them
        /// (-<see cref="LookRange"/>..<see cref="LookRange"/>), <paramref name="lookY"/> down (-1..1). Both eyes look
        /// the same way. <paramref name="blink"/> gives the closed frame.
        /// </summary>
        public static byte[] Alive(int lookX, int lookY, bool blink)
        {
            if (blink) return Closed();
            lookX = lookX < -LookRange ? -LookRange : (lookX > LookRange ? LookRange : lookX);
            lookY = lookY < -1 ? -1 : (lookY > 1 ? 1 : lookY);
            var eye = new byte[8];
            for (int r = 0; r < 8; r++) eye[r] = s_disc[r];
            // Centre pupil: columns 3-4, rows 3-4. Column c is bit (7 - c).
            int c0 = 3 + lookX, r0 = 3 + lookY;
            for (int r = r0; r < r0 + 2; r++)
            {
                for (int c = c0; c < c0 + 2; c++)
                {
                    eye[r] = (byte)(eye[r] & ~(1 << (7 - c)));
                }
            }
            return Pair(eye, false);
        }

        // ---- 3x5 font ----
        // Five rows per glyph, 3 bits each: bit 2 = left column. Uppercase only (lowercase is drawn as uppercase).
        // '*' draws a small heart.
        const string GlyphChars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789!?.,-+:'*/=()";
        static readonly byte[] s_font =
        {
            0b010, 0b101, 0b111, 0b101, 0b101, // A
            0b110, 0b101, 0b110, 0b101, 0b110, // B
            0b011, 0b100, 0b100, 0b100, 0b011, // C
            0b110, 0b101, 0b101, 0b101, 0b110, // D
            0b111, 0b100, 0b110, 0b100, 0b111, // E
            0b111, 0b100, 0b110, 0b100, 0b100, // F
            0b011, 0b100, 0b101, 0b101, 0b011, // G
            0b101, 0b101, 0b111, 0b101, 0b101, // H
            0b111, 0b010, 0b010, 0b010, 0b111, // I
            0b001, 0b001, 0b001, 0b101, 0b010, // J
            0b101, 0b101, 0b110, 0b101, 0b101, // K
            0b100, 0b100, 0b100, 0b100, 0b111, // L
            0b101, 0b111, 0b111, 0b101, 0b101, // M
            0b110, 0b101, 0b101, 0b101, 0b101, // N
            0b010, 0b101, 0b101, 0b101, 0b010, // O
            0b110, 0b101, 0b110, 0b100, 0b100, // P
            0b010, 0b101, 0b101, 0b110, 0b011, // Q
            0b110, 0b101, 0b110, 0b101, 0b101, // R
            0b011, 0b100, 0b010, 0b001, 0b110, // S
            0b111, 0b010, 0b010, 0b010, 0b010, // T
            0b101, 0b101, 0b101, 0b101, 0b111, // U
            0b101, 0b101, 0b101, 0b101, 0b010, // V
            0b101, 0b101, 0b111, 0b111, 0b101, // W
            0b101, 0b101, 0b010, 0b101, 0b101, // X
            0b101, 0b101, 0b010, 0b010, 0b010, // Y
            0b111, 0b001, 0b010, 0b100, 0b111, // Z
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
            0b010, 0b010, 0b010, 0b000, 0b010, // !
            0b110, 0b001, 0b010, 0b000, 0b010, // ?
            0b000, 0b000, 0b000, 0b000, 0b010, // .
            0b000, 0b000, 0b000, 0b010, 0b100, // ,
            0b000, 0b000, 0b111, 0b000, 0b000, // -
            0b000, 0b010, 0b111, 0b010, 0b000, // +
            0b000, 0b010, 0b000, 0b010, 0b000, // :
            0b010, 0b010, 0b000, 0b000, 0b000, // '
            0b101, 0b111, 0b111, 0b010, 0b000, // * (heart)
            0b001, 0b001, 0b010, 0b100, 0b100, // /
            0b000, 0b111, 0b000, 0b111, 0b000, // =
            0b001, 0b010, 0b010, 0b010, 0b001, // (
            0b100, 0b010, 0b010, 0b010, 0b100, // )
        };

        /// <summary>Glyph rows for a character (5 rows, 3 bits, bit 2 = left), or -1 for a space or a character
        /// the font does not have (drawn as a narrow gap).</summary>
        public static int GlyphIndex(char ch)
        {
            if (ch >= 'a' && ch <= 'z') ch = (char)(ch - 'a' + 'A');
            return GlyphChars.IndexOf(ch);
        }

        /// <summary>One row (0..4) of a glyph from <see cref="GlyphIndex"/>.</summary>
        public static int GlyphRow(int glyph, int row) => glyph < 0 ? 0 : s_font[glyph * 5 + row];

        const int GlyphWidth = 3, Gap = 1, SpaceWidth = 2, TextTop = 1;

        static int CharColumns(char ch) => (GlyphIndex(ch) < 0 ? SpaceWidth : GlyphWidth) + Gap;

        /// <summary>Width of a text in columns, gaps included.</summary>
        public static int TextColumns(string text)
        {
            int w = 0;
            if (text != null) for (int i = 0; i < text.Length; i++) w += CharColumns(text[i]);
            return w;
        }

        /// <summary>
        /// Draws <paramref name="text"/> scrolled so that text column <paramref name="offset"/> is at screen column 0
        /// (a negative offset starts the text further right: -16 puts it just off the right edge). One full pass
        /// runs offset from -<see cref="Columns"/> to <see cref="TextColumns"/>.
        /// </summary>
        public static byte[] Text(string text, int offset)
        {
            var f = new byte[FrameBytes];
            if (text == null) return f;
            int x = -offset; // screen column of the current character's left edge
            for (int i = 0; i < text.Length && x < Columns; i++)
            {
                char ch = text[i];
                int g = GlyphIndex(ch);
                if (g >= 0 && x + GlyphWidth > 0)
                {
                    for (int row = 0; row < 5; row++)
                    {
                        int bits = s_font[g * 5 + row];
                        for (int c = 0; c < GlyphWidth; c++)
                        {
                            if ((bits & (4 >> c)) != 0) SetPixel(f, x + c, TextTop + row);
                        }
                    }
                }
                x += CharColumns(ch);
            }
            return f;
        }

        /// <summary>Lights screen pixel (column 0..15 left to right as seen, row 0..7); outside is ignored.</summary>
        public static void SetPixel(byte[] frame, int column, int row)
        {
            if (column < 0 || column >= Columns || row < 0 || row > 7) return;
            int matrix = column / 8;
            frame[matrix * 8 + row] |= (byte)(0x80 >> (column % 8));
        }

        /// <summary>True when screen pixel (column, row) is lit.</summary>
        public static bool GetPixel(byte[] frame, int column, int row)
        {
            if (column < 0 || column >= Columns || row < 0 || row > 7) return false;
            return (frame[(column / 8) * 8 + row] & (0x80 >> (column % 8))) != 0;
        }
    }
}
