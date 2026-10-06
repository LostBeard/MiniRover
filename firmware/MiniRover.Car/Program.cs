using System;
using System.Threading;
using MiniRover.Protocol;

namespace MiniRover.Car
{
    public static class Program
    {
        public const string FirmwareVersion = "0.1.0";

        public static void Main()
        {
            System.Diagnostics.Debug.WriteLine("MiniRover car firmware starting");

            Settings settings = Settings.Load();
            var car = new Car(settings);
            car.Init();

            // After a crash reset (an interrupt watchdog, measured) the car has come up with its I2C devices and
            // camera not answering until a reset through the EN pin. The motor board is always fitted, so if it is
            // missing after such a reset, restart the whole chip including its RTC domain, once: that restart reports
            // the plain "watchdog" reason, which never repeats this, so it cannot loop.
            int reason = MiniRover.Native.Board.ResetReason();
            System.Diagnostics.Debug.WriteLine("Reset reason: " + ResetReasonName(reason));
            bool selfHealBoot = reason == MiniRover.Native.Board.ResetOtherWatchdog;
            if (reason != MiniRover.Native.Board.ResetPowerOn && reason != MiniRover.Native.Board.ResetDeepSleep &&
                reason != MiniRover.Native.Board.ResetSoftware && !selfHealBoot)
            {
                // Kept in the settings file: the self-heal below restarts again and would hide the cause.
                settings.LastAbnormalReset = ResetReasonName(reason) + " at boot " + DateTime.UtcNow.ToString("o")
                    + ", battery " + car.BootVolts.ToString("F2") + " V";
                settings.Save();
            }
            if (car.Pwm == null && reason != MiniRover.Native.Board.ResetPowerOn && reason != MiniRover.Native.Board.ResetDeepSleep &&
                !selfHealBoot)
            {
                System.Diagnostics.Debug.WriteLine("Motor board not answering after a " + ResetReasonName(reason) + " reset: full restart");
                Thread.Sleep(200); // let the message out
                MiniRover.Native.Board.FullReset();
            }
            if (car.Faults.Length > 0)
            {
                System.Diagnostics.Debug.WriteLine("Faults: " + car.Faults);
            }

            // Every startup step is guarded: an exception escaping Main kills this thread (the HTTP bind did, on the
            // real car) and leaves a half-started car. A failed step becomes a reported fault instead.
            Step(car, "greeting", () =>
            {
                if (car.Face != null)
                {
                    // Closed eyes on USB power alone: the car cannot drive.
                    if (!car.HasBattery) car.Face.SetSystem(EyeArt.Closed());
                    car.Face.Start();
                }
                if (car.Lights != null)
                {
                    // Lights start off: lit LEDs drain the batteries for nothing, and the eyes already show the car is
                    // on. The app turns them on when asked.
                    car.Lights.Set(LightsService.ModeOff, 0, 0, 0, 0);
                    car.Lights.Start();
                }
                if (car.Buzzer != null) car.Buzzer.Play(new int[] { 1568, 70, 0, 30, 2093, 90 });
            });

            // Camera before WiFi/BLE: its DMA buffers need contiguous internal RAM, which only gets scarcer later.
            Step(car, "camera", car.InitCamera);

            var wifi = new WifiService(settings);
            Step(car, "wifi", () =>
            {
                wifi.Start();
                if (wifi.InSetupMode && car.Face != null) car.Face.SetSystem(EyeArt.Setup());
            });

            // BLE setup: always in setup mode; otherwise for a short window after every boot, so the app can
            // confirm the car joined the network after setup, and so another device can pair (switch the car off
            // and on, then "Add a car"). Pairing still needs the code shown on the eyes. Never while driving.
            var ble = new BleSetupService(car, wifi, settings, wifi.SetupSsid);
            bool announce = !wifi.InSetupMode;
            if (wifi.InSetupMode || announce)
            {
                Step(car, "ble", ble.Start);
            }
            BleWindow window = null;
            if (announce)
            {
                window = new BleWindow(ble);
                Step(car, "ble window", () =>
                {
                    new Thread(() =>
                    {
                        Thread.Sleep(Protocol.BleSetup.ConnectedAdvertiseSeconds * 1000);
                        window.Close("timeout");
                    }).Start();
                });
            }

            // The app's WebRTC link: only on a real network (setup mode has no internet and no pairing yet).
            RtcLinkService link = null;
            if (wifi.Connected)
            {
                link = new RtcLinkService(car, settings, wifi.SetupSsid);
                // An app that connects needs no BLE: close the window at once and give its memory to the session
                // (measured: 6-7 KB of internal RAM left with a session up inside the window, 30 KB after it).
                if (window != null) link.OnAppConnected = () => window.Close("app connected");
                Step(car, "link", link.Start);
            }

            var http = new HttpServer(80, null);
            var api = new WebApi(car, wifi, http) { Link = link };
            http.Handle = api.Handle;
            http.Start();

            // The eyes' system picture: setup target, X eyes on a flat battery, closed on USB power alone, otherwise
            // the app's choice. Everything else runs on its own threads.
            int shown = -1;
            while (true)
            {
                Thread.Sleep(1000);
                if (car.Face == null) continue;
                int state = wifi.InSetupMode ? 1
                    : (car.Battery != null && car.Battery.Level == BatteryLevel.Critical) ? 2
                    : !car.HasBattery ? 3 : 0;
                if (state == shown) continue;
                shown = state;
                car.Face.SetSystem(state == 1 ? EyeArt.Setup() : state == 2 ? EyeArt.Dead() : state == 3 ? EyeArt.Closed() : null);
            }
        }

        // WiFi modem sleep stalls receives for up to hundreds of ms (a status poll took 1.4 s), too slow for 20 Hz
        // driving. ESP-IDF needs it while Bluetooth is on, so it goes off only once the BLE window has closed.
        static void DisableModemSleep()
        {
            try
            {
                bool ok = MiniRover.Native.Board.SetWifiPowerSave(false);
                System.Diagnostics.Debug.WriteLine("WiFi modem sleep " + (ok ? "off" : "could not be turned off"));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("WiFi modem sleep: " + ex.Message);
            }
        }

        /// <summary>Native heaps, KB: internal free / largest block, PSRAM free / largest block.</summary>
        public static string MemoryText()
        {
            try
            {
                return "internal " + (MiniRover.Native.Board.FreeMemory(MiniRover.Native.Board.MemInternalFree) / 1024) + " KB (block "
                    + (MiniRover.Native.Board.FreeMemory(MiniRover.Native.Board.MemInternalLargest) / 1024) + "), PSRAM "
                    + (MiniRover.Native.Board.FreeMemory(MiniRover.Native.Board.MemPsramFree) / 1024) + " KB (block "
                    + (MiniRover.Native.Board.FreeMemory(MiniRover.Native.Board.MemPsramLargest) / 1024) + ")";
            }
            catch (Exception ex) { return ex.Message; }
        }

        public static string ResetReasonName(int reason)
        {
            switch (reason)
            {
                case MiniRover.Native.Board.ResetPowerOn: return "power-on";
                case MiniRover.Native.Board.ResetExternal: return "external";
                case MiniRover.Native.Board.ResetSoftware: return "software";
                case MiniRover.Native.Board.ResetPanic: return "crash";
                case MiniRover.Native.Board.ResetInterruptWatchdog: return "interrupt watchdog";
                case MiniRover.Native.Board.ResetTaskWatchdog: return "task watchdog";
                case MiniRover.Native.Board.ResetOtherWatchdog: return "RTC watchdog (full restart)";
                case MiniRover.Native.Board.ResetDeepSleep: return "deep-sleep wake";
                case MiniRover.Native.Board.ResetBrownout: return "brownout";
                default: return "unknown (" + reason + ")";
            }
        }

        delegate void StepAction();

        /// <summary>The BLE setup window after boot: closes once, on its timer or when an app connects.</summary>
        sealed class BleWindow
        {
            readonly BleSetupService _ble;
            readonly object _lock = new object();
            bool _closed;

            public BleWindow(BleSetupService ble) => _ble = ble;

            public void Close(string reason)
            {
                lock (_lock)
                {
                    if (_closed) return;
                    _closed = true;
                }
                _ble.Stop();
                System.Diagnostics.Debug.WriteLine("BLE setup window closed (" + reason + ")");
                DisableModemSleep();
            }
        }

        static void Step(Car car, string name, StepAction action)
        {
            try
            {
                action();
                System.Diagnostics.Debug.WriteLine("Memory after " + name + ": " + MemoryText());
            }
            catch (Exception ex)
            {
                car.Faults += (car.Faults.Length > 0 ? "; " : "") + name + ": " + ex.Message;
                System.Diagnostics.Debug.WriteLine("Startup step '" + name + "' FAILED: " + ex.Message);
            }
        }
    }
}
