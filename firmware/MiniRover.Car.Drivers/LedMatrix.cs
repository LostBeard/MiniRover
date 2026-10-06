using System;
using System.Device.I2c;

namespace MiniRover.Car.Drivers
{
    /// <summary>
    /// The two 8x8 LED matrices ("eyes") on a VK16K33 (HT16K33-compatible) at 0x71.
    ///
    /// Frames are 16 bytes: bytes 0..7 are the rows (top to bottom) of the matrix on the viewer's LEFT when facing
    /// the car, 8..15 the rows of the right one. In a row byte, bit 7 is the leftmost pixel, so a 0b literal reads
    /// as it looks (verified on hardware). The chip's display RAM is column-major on this board: RAM word k holds
    /// row-byte bit (7 - k), and bit r of the word is frame row r (the wiring Freenove's library documents).
    /// </summary>
    public sealed class LedMatrix : IDisposable
    {
        public const int FrameBytes = 16;

        const byte CmdOscillatorOn = 0x21;
        const byte CmdDisplaySetup = 0x80;
        const byte DisplayOn = 0x01;
        const byte CmdDimming = 0xE0;

        readonly I2cDevice _device;
        readonly byte[] _ram = new byte[1 + FrameBytes];

        public LedMatrix()
        {
            _device = SharedI2c.Open(BoardPins.MatrixAddress);
            SharedI2c.Write(_device, new byte[] { CmdOscillatorOn });
            SharedI2c.Write(_device, new byte[] { CmdDisplaySetup | DisplayOn }); // blink off
            SetBrightness(8);
            Clear();
        }

        /// <summary>0..15.</summary>
        public void SetBrightness(int level)
        {
            if (level < 0) level = 0;
            if (level > 15) level = 15;
            SharedI2c.Write(_device, new byte[] { (byte)(CmdDimming | level) });
        }

        public void Clear() => Show(new byte[FrameBytes]);

        /// <summary>Shows one 16-byte frame (layout in the class summary).</summary>
        public void Show(byte[] frame)
        {
            if (frame == null || frame.Length < FrameBytes) throw new ArgumentException(nameof(frame));

            lock (_ram)
            {
                _ram[0] = 0x00; // display RAM start address
                for (int k = 0; k < 8; k++)
                {
                    int col = 7 - k;
                    int word = 0;
                    for (int row = 0; row < FrameBytes; row++)
                    {
                        if ((frame[row] & (1 << col)) != 0)
                        {
                            word |= 1 << row;
                        }
                    }
                    _ram[1 + 2 * k] = (byte)(word & 0xFF);
                    _ram[2 + 2 * k] = (byte)(word >> 8);
                }
                SharedI2c.Write(_device, _ram);
            }
        }

        public void Dispose()
        {
            try { Clear(); } catch { /* bus gone at shutdown */ }
            _device.Dispose();
        }
    }
}
