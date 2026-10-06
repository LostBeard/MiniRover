using System;
using System.Threading;

namespace MiniRover.Car
{
    public static class Program
    {
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

            if (car.Matrix != null) car.Matrix.Show(car.HasBattery ? Eyes.Open : Eyes.Closed);
            if (car.Leds != null && car.HasBattery)
            {
                // Dim white headlights so a powered car is obvious. Not on USB power alone (brownout).
                car.Leds.Fill(40, 40, 40);
                car.Leds.Show();
            }
            if (car.Buzzer != null) car.Buzzer.Play(new int[] { 1568, 70, 0, 30, 2093, 90 });

            var wifi = new WifiService();
            wifi.Start();
            if (wifi.InSetupMode && car.Matrix != null) car.Matrix.Show(Eyes.Setup);

            var api = new WebApi(car, wifi);
            new HttpServer(80, api.Handle).Start();

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
    }
}
