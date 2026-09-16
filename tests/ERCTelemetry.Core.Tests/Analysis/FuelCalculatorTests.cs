using Xunit;
using ERCTelemetry.Core.Analysis;

namespace ERCTelemetry.Core.Tests.Analysis;

public sealed class FuelCalculatorTests
{
    [Fact]
    public void Shortfall_needs_a_lap_counter()
    {
        // Arrange
        const int lapsToGo = 0;

        // Act + Assert
        Assert.Null(FuelCalculator.Shortfall(lapsToGo, 30f));
    }

    [Fact]
    public void Shortfall_rounds_up_partial_laps_and_gets_half_lap_grace()
    {
        // Arrange — 0.8 lap short → minus the 0.5 grace → 0.3 → rounds up to 1

        // Act + Assert
        Assert.Equal(1, FuelCalculator.Shortfall(10, 9.2f));
        Assert.Equal(0, FuelCalculator.Shortfall(10, 9.6f)); // inside the half-lap grace
        Assert.Equal(2, FuelCalculator.Shortfall(34, 32.0f)); // ceil(34-32-0.5) = ceil(1.5) = 2
    }

    [Fact]
    public void Litres_needs_a_shortfall()
    {
        // Arrange
        const int shortfall = -2; // spare fuel

        // Act + Assert
        Assert.Equal(0f, FuelCalculator.Litres(shortfall, 2.2f));
    }

    [Fact]
    public void Litres_multiplies_shortfall_by_fuel_per_lap()
    {
        // Arrange
        const int shortfall = 3;

        // Act + Assert
        Assert.Equal(6.6f, FuelCalculator.Litres(shortfall, 2.2f), 3);
    }

    [Fact]
    public void Litres_is_zero_with_unknown_burn()
    {
        // Arrange
        const int shortfall = 2;

        // Act + Assert
        Assert.Equal(0f, FuelCalculator.Litres(shortfall, 0f));
    }

    [Fact]
    public void Compute_is_null_without_lap_counters()
    {
        // Arrange/Act + Assert
        Assert.Null(FuelCalculator.Compute(0, 0, 30f, 2.2f));
        Assert.Null(FuelCalculator.Compute(44, 0, 30f, 2.2f));
    }

    [Fact]
    public void Compute_reports_shortfall_and_litres_to_fuel()
    {
        // Arrange — last lap cannot even be coasted home
        const byte totalLaps = 44;
        const byte currentLap = 43; // 1 lap to go

        // Act
        var advice = FuelCalculator.Compute(totalLaps, currentLap, 0.4f, 2.2f);

        // Assert
        Assert.NotNull(advice);
        Assert.Equal(1, advice!.LapsToGo);
        Assert.Equal(1, advice.ShortfallLaps); // ceil(1 - 0.4 - 0.5) = ceil(0.1) = 1
        Assert.Equal(2.2f, advice.LitresToFuel);
    }

    [Fact]
    public void Compute_reports_spare_fuel_as_negative_shortfall()
    {
        // Arrange
        const byte totalLaps = 20;
        const byte currentLap = 5; // 15 laps to go
        const float fuelLaps = 17.4f;

        // Act
        var advice = FuelCalculator.Compute(totalLaps, currentLap, fuelLaps, 2.2f);

        // Assert
        Assert.NotNull(advice);
        Assert.Equal(15, advice!.LapsToGo);
        Assert.Equal(-2, advice.ShortfallLaps); // ceil(15 - 17.4 - 0.5) = ceil(-2.9) = -2
        Assert.Equal(0f, advice.LitresToFuel);
    }

    [Fact]
    public void Compute_reports_shortfall_when_run_dry_before_the_finish()
    {
        // Arrange
        const byte totalLaps = 44;
        const byte currentLap = 10; // 34 laps to go
        const float fuelLaps = 32.0f;

        // Act
        var advice = FuelCalculator.Compute(totalLaps, currentLap, fuelLaps, 2.2f);

        // Assert
        Assert.NotNull(advice);
        Assert.Equal(34, advice!.LapsToGo);
        Assert.Equal(2, advice.ShortfallLaps); // ceil(34 - 32.0 - 0.5) = ceil(1.5) = 2
        Assert.Equal(4.4f, advice.LitresToFuel); // 2 × 2.2
    }
}