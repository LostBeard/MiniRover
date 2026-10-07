using MiniRover.Protocol;
using Xunit;

namespace MiniRover.Protocol.Tests;

/// <summary>
/// The battery gauge's motor-sag compensation (BatterySag, compiled into the firmware too). Numbers from the car on
/// battery power (2026-10-07): 7.80 V rested, 7.29 V at 60 % drive (mean duty 0.756 with the 1600 minimum duty).
/// </summary>
public class BatterySagTests
{
    const double Rest = 7.80, Driving = 7.29, Load = 0.756;

    [Fact]
    public void Rested_readings_pass_through_and_nothing_is_learned()
    {
        var sag = new BatterySag();
        for (int i = 0; i < 10; i++) Assert.Equal(Rest - i * 0.001, sag.Update(Rest - i * 0.001, 0, i * 2000));
        Assert.Equal(0, sag.Learned);
        Assert.False(sag.Ready);
    }

    [Fact]
    public void Learns_the_sag_from_a_rest_to_drive_step_and_adds_it_back()
    {
        var sag = new BatterySag();
        sag.Update(Rest, 0, 0);
        double first = sag.Update(Driving, Load, 2000);
        Assert.Equal(Rest, first, 3);                       // the step itself teaches k = 0.51 / 0.756
        Assert.Equal((Rest - Driving) / Load, sag.SagPerLoad, 6);
        sag.Update(Driving, Load, 4000);
        Assert.True(sag.Ready);
        // A long drive later: the pack has lost 0.2 V and the same load still sags 0.51 V, so the rested
        // estimate follows the real charge instead of reading 0.51 V low.
        double later = sag.Update(Driving - 0.2, Load, 600_000);
        Assert.Equal(Rest - 0.2, later, 2);
    }

    [Fact]
    public void Compensation_scales_with_the_load()
    {
        var sag = new BatterySag();
        sag.Update(Rest, 0, 0);
        sag.Update(Driving, Load, 2000);
        double k = sag.SagPerLoad;
        double half = 0.45;
        Assert.Equal(Rest, sag.Update(Rest - k * half, half, 4000), 3);
    }

    [Fact]
    public void A_stale_rest_reading_does_not_teach()
    {
        var sag = new BatterySag();
        sag.Update(Rest, 0, 0);
        sag.Update(Driving, Load, BatterySag.RestValidMs + 1); // too long after the rest: the charge has moved on
        Assert.Equal(0, sag.Learned);
    }

    [Fact]
    public void Small_loads_and_impossible_steps_do_not_teach()
    {
        var sag = new BatterySag();
        sag.Update(Rest, 0, 0);
        sag.Update(Rest - 0.02, BatterySag.MinimumLearnLoad / 2, 2000); // inside the ADC noise: not a measurement
        Assert.Equal(0, sag.Learned);
        sag.Update(Rest + 0.1, Load, 4000);                            // voltage ROSE under load: a pack swap, not sag
        Assert.Equal(0, sag.Learned);
        sag.Update(Rest - 3.0, Load * 0.5, 6000);                      // 6 V per unit load: a stall spike / glitch
        Assert.Equal(0, sag.Learned);
    }

    [Fact]
    public void One_outlier_moves_the_learned_sag_only_partly()
    {
        var sag = new BatterySag();
        sag.Update(Rest, 0, 0);
        sag.Update(Driving, Load, 2000);
        double k = sag.SagPerLoad;
        sag.Update(Rest - 2 * (Rest - Driving), Load, 4000); // twice the usual sag once (a wheel briefly blocked)
        Assert.InRange(sag.SagPerLoad, k * 1.1, k * 1.5);
    }
}
