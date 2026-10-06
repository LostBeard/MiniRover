using System;
using System.Device.Pwm;
using System.Threading;
using nanoFramework.Hardware.Esp32;

namespace MiniRover.Car.Drivers
{
    /// <summary>
    /// The passive buzzer on GPIO2 (through an NPN transistor; loudest near 2 kHz). A passive buzzer needs a
    /// square wave, so a tone is a 50% duty PWM at the note's frequency. GPIO2 is also the board's blue LED,
    /// which glows while a tone plays.
    /// </summary>
    public sealed class Buzzer : IDisposable
    {
        public const int ResonantHz = 2000;

        readonly PwmChannel _pwm;
        readonly object _lock = new object();

        public Buzzer()
        {
            Configuration.SetPinFunction(BoardPins.Buzzer, DeviceFunction.PWM1);
            _pwm = PwmChannel.CreateFromPin(BoardPins.Buzzer, ResonantHz, 0);
        }

        /// <summary>Starts a continuous tone; 0 Hz stops it.</summary>
        public void Tone(int frequencyHz)
        {
            lock (_lock)
            {
                if (frequencyHz <= 0)
                {
                    _pwm.Stop();
                    return;
                }
                if (frequencyHz < 20) frequencyHz = 20;
                if (frequencyHz > 10000) frequencyHz = 10000;
                _pwm.Frequency = frequencyHz;
                _pwm.DutyCycle = 0.5;
                _pwm.Start();
            }
        }

        public void Off() => Tone(0);

        /// <summary>Plays a tone for a duration, blocking the caller.</summary>
        public void Beep(int frequencyHz, int milliseconds)
        {
            Tone(frequencyHz);
            Thread.Sleep(milliseconds);
            Off();
        }

        /// <summary>Plays a melody of (frequency Hz, milliseconds) pairs; frequency 0 is a rest.</summary>
        public void Play(int[] notes)
        {
            for (int i = 0; i + 1 < notes.Length; i += 2)
            {
                Tone(notes[i]);
                Thread.Sleep(notes[i + 1]);
            }
            Off();
        }

        public void Dispose()
        {
            Off();
            _pwm.Dispose();
        }
    }
}
