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

            var wifi = new WifiService(settings);
            Step(car, "wifi", () =>
            {
                wifi.Start();
                if (wifi.InSetupMode && car.Matrix != null) car.Matrix.Show(Eyes.Setup);
            });

            // BLE setup: always in setup mode; after a BLE-provisioned reboot, for a short window so the app can
            // reconnect and confirm the car joined the network. Never while driving.
            var ble = new BleSetupService(car, wifi, settings, wifi.SetupSsid);
            bool announce = !wifi.InSetupMode && settings.AnnounceAfterSetup;
            if (wifi.InSetupMode || announce)
            {
                Step(car, "ble", ble.Start);
            }
            if (announce)
            {
                Step(car, "ble window", () =>
                {
                    settings.AnnounceAfterSetup = false;
                    settings.Save();
                    new Thread(() =>
                    {
                        Thread.Sleep(Protocol.BleSetup.ConnectedAdvertiseSeconds * 1000);
                        ble.Stop();
                        System.Diagnostics.Debug.WriteLine("BLE setup window closed");
                    }).Start();
                });
            }

            var http = new HttpServer(80, null);
            var api = new WebApi(car, wifi, http);
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

        delegate void StepAction();

        static void Step(Car car, string name, StepAction action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                car.Faults += (car.Faults.Length > 0 ? "; " : "") + name + ": " + ex.Message;
                System.Diagnostics.Debug.WriteLine("Startup step '" + name + "' FAILED: " + ex.Message);
            }
        }
    }
}
