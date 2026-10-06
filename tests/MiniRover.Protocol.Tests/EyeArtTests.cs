using System.Reflection;
using MiniRover.Protocol;
using Xunit;

namespace MiniRover.Protocol.Tests;

/// <summary>Eye artwork, font and the face message. The car draws with this code, so these pin what it shows.</summary>
public class EyeArtTests
{
    static byte[] Eye(byte[] frame, int matrix) => frame[(matrix * 8)..(matrix * 8 + 8)];

    [Fact]
    public void Open_eyes_are_a_disc_with_a_centred_2x2_pupil_on_both_matrices()
    {
        byte[] expected = { 0x3C, 0x7E, 0xFF, 0b11100111, 0b11100111, 0xFF, 0x7E, 0x3C };
        byte[] f = EyeArt.Open();
        Assert.Equal(EyeArt.FrameBytes, f.Length);
        Assert.Equal(expected, Eye(f, 0));
        Assert.Equal(expected, Eye(f, 1));
    }

    [Fact]
    public void Looking_moves_the_pupil_in_screen_columns_and_is_clamped()
    {
        // lookX +2: pupil in columns 5-6 (bits 2 and 1), rows 3-4.
        byte[] right = EyeArt.Alive(2, 0, false);
        Assert.Equal(0b11111001, right[3]);
        Assert.Equal(0b11111001, right[4]);
        Assert.Equal(0xFF, right[2]);
        // lookY -1: rows 2-3; lookX -2: columns 1-2.
        byte[] upLeft = EyeArt.Alive(-2, -1, false);
        Assert.Equal(0b10011111, upLeft[2]);
        Assert.Equal(0b10011111, upLeft[3]);
        Assert.Equal(0xFF, upLeft[4]);
        // Out-of-range looks clamp to the edge instead of drawing outside the eye.
        Assert.Equal(EyeArt.Alive(EyeArt.LookRange, 1, false), EyeArt.Alive(99, 99, false));
        Assert.Equal(EyeArt.Closed(), EyeArt.Alive(1, 1, true));
    }

    [Fact]
    public void Every_pupil_position_stays_inside_the_eye()
    {
        byte[] disc = EyeArt.Alive(0, 0, false);
        for (int x = -EyeArt.LookRange; x <= EyeArt.LookRange; x++)
        {
            for (int y = -1; y <= 1; y++)
            {
                byte[] f = EyeArt.Alive(x, y, false);
                int dark = 0;
                for (int r = 0; r < 8; r++)
                {
                    // A pupil pixel is a pixel of the disc that is off. The disc (pupil filled in) is the outline.
                    int discRow = disc[r] | (r == 3 || r == 4 ? 0b00011000 : 0);
                    Assert.Equal(0, f[r] & ~discRow); // nothing lit outside the disc
                    for (int c = 0; c < 8; c++) if ((discRow & (0x80 >> c)) != 0 && (f[r] & (0x80 >> c)) == 0) dark++;
                }
                Assert.Equal(4, dark);
            }
        }
    }

    [Fact]
    public void Sad_and_angry_brows_mirror_on_the_right_eye()
    {
        foreach (int mood in new[] { EyeArt.MoodSad, EyeArt.MoodAngry })
        {
            byte[] f = EyeArt.Mood(mood);
            for (int r = 0; r < 8; r++)
            {
                for (int c = 0; c < 8; c++)
                {
                    Assert.Equal(EyeArt.GetPixel(f, c, r), EyeArt.GetPixel(f, 15 - c, r));
                }
            }
            Assert.NotEqual(Eye(f, 0), Eye(f, 1)); // the brows really slant
        }
    }

    [Fact]
    public void Every_mood_has_its_own_picture_and_unknown_moods_are_open_eyes()
    {
        var seen = new HashSet<string>();
        for (int m = 0; m < EyeArt.MoodCount; m++) Assert.True(seen.Add(Convert.ToHexString(EyeArt.Mood(m))), "mood " + m);
        Assert.Equal(EyeArt.Open(), EyeArt.Mood(200));
        Assert.Equal(EyeArt.Open(), EyeArt.Mood(EyeArt.MoodOpen));
    }

    [Fact]
    public void Font_table_has_five_rows_for_every_character_and_no_duplicates()
    {
        var t = typeof(EyeArt);
        string chars = (string)t.GetField("GlyphChars", BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue()!;
        byte[] font = (byte[])t.GetField("s_font", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        Assert.Equal(chars.Length * 5, font.Length);
        Assert.Equal(chars.Length, chars.Distinct().Count());
        Assert.All(font, b => Assert.True(b <= 0b111));
        var shapes = new HashSet<string>();
        for (int g = 0; g < chars.Length; g++)
        {
            Assert.True(shapes.Add(Convert.ToHexString(font, g * 5, 5)), "glyph '" + chars[g] + "' looks like another");
        }
        Assert.Equal(EyeArt.GlyphIndex('A'), EyeArt.GlyphIndex('a'));
        Assert.Equal(-1, EyeArt.GlyphIndex(' '));
        Assert.Equal(-1, EyeArt.GlyphIndex('~'));
    }

    [Fact]
    public void Text_draws_glyphs_left_to_right_from_row_one_and_scrolls()
    {
        // "HI": H in columns 0-2, I in 4-6. H top row 101, I top row 111.
        byte[] f = EyeArt.Text("HI", 0);
        Assert.True(EyeArt.GetPixel(f, 0, 1));
        Assert.False(EyeArt.GetPixel(f, 1, 1));
        Assert.True(EyeArt.GetPixel(f, 2, 1));
        Assert.True(EyeArt.GetPixel(f, 4, 1) && EyeArt.GetPixel(f, 5, 1) && EyeArt.GetPixel(f, 6, 1));
        for (int c = 0; c < 16; c++) { Assert.False(EyeArt.GetPixel(f, c, 0)); Assert.False(EyeArt.GetPixel(f, c, 6)); }
        // Scrolling by one column moves every pixel one column left.
        byte[] s = EyeArt.Text("HI", 1);
        for (int c = 0; c < 15; c++) for (int r = 0; r < 8; r++) Assert.Equal(EyeArt.GetPixel(f, c + 1, r), EyeArt.GetPixel(s, c, r));
        // The start and the end of a pass are blank; text crosses from the left matrix into the right one.
        Assert.All(EyeArt.Text("HI", -EyeArt.Columns), b => Assert.Equal(0, b));
        Assert.All(EyeArt.Text("HI", EyeArt.TextColumns("HI")), b => Assert.Equal(0, b));
        Assert.True(EyeArt.GetPixel(EyeArt.Text("HI", -8), 8, 1)); // H's top-left pixel in the right matrix
    }

    [Fact]
    public void Text_width_counts_glyphs_spaces_and_gaps()
    {
        Assert.Equal(8, EyeArt.TextColumns("HI"));
        Assert.Equal(11, EyeArt.TextColumns("A B"));
        Assert.Equal(0, EyeArt.TextColumns(""));
        Assert.Equal(0, EyeArt.TextColumns(null!));
    }

    [Fact]
    public void Face_message_round_trips_and_keeps_text_printable_and_bounded()
    {
        byte[] m = CarLink.EncodeFace(CarLink.FaceText, 2, "Hi Aubs! *");
        Assert.Equal(CarLink.MsgFace, m[0]);
        Assert.True(CarLink.TryDecodeFace(m, 0, m.Length, out int mode, out int arg, out string text));
        Assert.Equal(CarLink.FaceText, mode);
        Assert.Equal(2, arg);
        Assert.Equal("Hi Aubs! *", text);

        byte[] odd = CarLink.EncodeFace(CarLink.FaceText, 0, "café\n");
        Assert.True(CarLink.TryDecodeFace(odd, 0, odd.Length, out _, out _, out string t2));
        Assert.Equal("caf??", t2);

        byte[] longText = CarLink.EncodeFace(CarLink.FaceText, 0, new string('A', 200));
        Assert.Equal(3 + CarLink.MaxFaceText, longText.Length);

        byte[] mood = CarLink.EncodeFace(CarLink.FaceMood, EyeArt.MoodHeart, null!);
        Assert.Equal(3, mood.Length);
        Assert.True(CarLink.TryDecodeFace(mood, 0, mood.Length, out int m2, out int a2, out string t3));
        Assert.Equal((CarLink.FaceMood, EyeArt.MoodHeart, ""), (m2, a2, t3));

        // Inside a bigger buffer, at an offset (how the car's receive loop calls it).
        byte[] buf = new byte[5 + m.Length];
        Array.Copy(m, 0, buf, 5, m.Length);
        Assert.True(CarLink.TryDecodeFace(buf, 5, m.Length, out _, out _, out string t4));
        Assert.Equal("Hi Aubs! *", t4);

        Assert.False(CarLink.TryDecodeFace(new byte[] { CarLink.MsgFace, 0 }, 0, 2, out _, out _, out _));
        Assert.False(CarLink.TryDecodeFace(m, 0, m.Length + 1, out _, out _, out _));
    }
}
