using System;
using System.Threading;

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
            if (car.Faults.Length > 0)
            {
                System.Diagnostics.Debug.WriteLine("Faults: " + car.Faults);
            }

            // Every startup step is guarded: an exception escaping Main kills this thread (the HTTP bind did, on the
            // real car) and leaves a half-started car. A failed step becomes a reported fault instead.
            Step(car, "greeting", () =>
            {
                if (car.Matrix != null) car.Matrix.Show(car.HasBattery ? Eyes.Open : Eyes.Closed);
                if (car.Leds != null && car.HasBattery)
                {
                    // Dim white headlights so a powered car is obvious. Not on USB power alone (brownout).
                    car.Leds.Fill(40, 40, 40);
                    car.Leds.Show();
                }
                if (car.Buzzer != null) car.Buzzer.Play(new int[] { 1568, 70, 0, 30, 2093, 90 });
            });

            // Camera before WiFi/BLE: its DMA buffers need contiguous internal RAM, which only gets scarcer later.
            Step(car, "camera", car.InitCamera);

            var wifi = new WifiService(settings);
            Step(car, "wifi", () =>
            {
                wifi.Start();
                if (wifi.InSetupMode && car.Matrix != null) car.Matrix.Show(Eyes.Setup);
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
            if (announce)
            {
                Step(car, "ble window", () =>
                {
                    new Thread(() =>
                    {
                        Thread.Sleep(Protocol.BleSetup.ConnectedAdvertiseSeconds * 1000);
                        ble.Stop();
                        System.Diagnostics.Debug.WriteLine("BLE setup window closed");
                        DisableModemSleep();
                    }).Start();
                });
            }

            // The app's WebRTC link: only on a real network (setup mode has no internet and no pairing yet).
            RtcLinkService link = null;
            if (wifi.Connected)
            {
                link = new RtcLinkService(car, settings, wifi.SetupSsid);
                Step(car, "link", link.Start);
            }

            var http = new HttpServer(80, null);
            var api = new WebApi(car, wifi, http) { Link = link };
            http.Handle = api.Handle;
            http.Start();

            // Show critical battery on the eyes; everything else runs on its own threads.
            BatteryLevel shown = BatteryLevel.Ok;
            while (true)
            {
                Thread.Sleep(1000);
                if (car.Battery != null && car.Matrix != null && !wifi.InSetupMode && car.Battery.Level != shown)
                {
                    shown = car.Battery.Level;
                    try { car.Matrix.Show(shown == BatteryLevel.Critical ? Eyes.Dead : Eyes.Open); }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine("Matrix: " + ex.Message); }
                }
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

        delegate void StepAction();

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
