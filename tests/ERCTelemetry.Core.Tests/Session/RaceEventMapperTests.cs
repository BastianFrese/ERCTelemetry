using ERCTelemetry.Core.Session;
using F1Game.UDP.Enums;
using F1Game.UDP.Events;
using Xunit;

namespace ERCTelemetry.Core.Tests.Session;

/// <summary>RaceEventMapper coverage for the race-report-relevant event types, including
/// the collision detail members (SecondCarIndex / DetailValue) added for the report.</summary>
public class RaceEventMapperTests
{
    private static readonly DriverEntry?[] NoDrivers = new DriverEntry?[0];

    [Fact]
    public void Collision_with_environment_carries_no_second_car()
    {
        var entry = RaceEventMapper.Map(new EventDetails(new CollisionEvent
        {
            Vehicle1Index = 3,
            Vehicle2Index = 255, // environment sentinel
            Severity = 250,
        }), NoDrivers);

        Assert.NotNull(entry);
        Assert.Equal("Collision", entry.Type);
        Assert.Equal((byte)3, entry.CarIndex);
        Assert.Null(entry.SecondCarIndex);
        Assert.Equal(250, entry.DetailValue);
        Assert.Contains("environment", entry.Text);
    }

    [Fact]
    public void Collision_with_another_car_maps_both_indices()
    {
        var entry = RaceEventMapper.Map(new EventDetails(new CollisionEvent
        {
            Vehicle1Index = 3,
            Vehicle2Index = 7,
            Severity = 0,
        }), NoDrivers)!;

        Assert.Equal((byte)7, entry.SecondCarIndex);
        Assert.Equal(0, entry.DetailValue);
        Assert.DoesNotContain("environment", entry.Text);
    }

    [Fact]
    public void Collision_text_labels_the_severity()
    {
        // Severity is a byte 0=gering, 1=mittel, 2=hoch — the text must say so,
        // not render a bogus percentage (the old severity/499.5f bug).
        var gering = RaceEventMapper.Map(new EventDetails(new CollisionEvent
        {
            Vehicle1Index = 3,
            Vehicle2Index = 7,
            Severity = 0,
        }), NoDrivers)!;
        Assert.Contains("impact gering", gering.Text);

        var mittel = RaceEventMapper.Map(new EventDetails(new CollisionEvent
        {
            Vehicle1Index = 3,
            Vehicle2Index = 7,
            Severity = 1,
        }), NoDrivers)!;
        Assert.Contains("impact mittel", mittel.Text);

        var hoch = RaceEventMapper.Map(new EventDetails(new CollisionEvent
        {
            Vehicle1Index = 3,
            Vehicle2Index = 7,
            Severity = 2,
        }), NoDrivers)!;
        Assert.Contains("impact hoch", hoch.Text);
    }

    [Fact]
    public void LightsOut_maps_to_its_own_type_without_a_car()
    {
        // LGOT is a type-only event (no payload struct) — the mapper must detect it via
        // the EventType alone, not drop it like the countdown StartLights events.
        var lightsOut = RaceEventMapper.Map(
            new EventDetails { EventType = EventType.LightsOut }, NoDrivers);

        Assert.Equal("LightsOut", lightsOut!.Type);
        Assert.Null(lightsOut.CarIndex);
        Assert.Contains("Lights out", lightsOut.Text);
    }

    [Fact]
    public void StartLights_and_drs_disabled_have_no_car()
    {
        var lights = RaceEventMapper.Map(new EventDetails(new StartLightsEvent { NumLights = 5 }), NoDrivers);
        Assert.Equal("StartLights", lights!.Type);
        Assert.Null(lights.CarIndex);
        Assert.Contains("5", lights.Text);

        var drs = RaceEventMapper.Map(new EventDetails(new DrsDisabledEvent
        {
            Reason = DrsDisabledReason.MinLapNotReached,
        }), NoDrivers);
        Assert.Equal("DrsDisabled", drs!.Type);
        Assert.Null(drs.CarIndex);
    }

    [Fact]
    public void Served_penalties_name_the_driver_and_stop_time()
    {
        var served = RaceEventMapper.Map(
            new EventDetails(new DriveThroughPenaltyServedEvent { VehicleIdx = 4 }), NoDrivers);
        Assert.Equal("DriveThroughServed", served!.Type);
        Assert.Equal((byte)4, served.CarIndex);

        var stopGo = RaceEventMapper.Map(
            new EventDetails(new StopGoPenaltyServedEvent { VehicleIdx = 4, StopTime = 10 }), NoDrivers);
        Assert.Equal("StopGoServed", stopGo!.Type);
        Assert.Contains("10s", stopGo.Text);
    }

    [Fact]
    public void Flashback_reports_the_session_time()
    {
        var entry = RaceEventMapper.Map(
            new EventDetails(new FlashbackEvent { FlashbackSessionTime = 5 * 60 + 30 }), NoDrivers);
        Assert.Equal("Flashback", entry!.Type);
        Assert.Contains("05:30", entry.Text);
    }

    [Fact]
    public void Buttons_are_not_mapped()
    {
        Assert.Null(RaceEventMapper.Map(new EventDetails(new ButtonsEvent { ButtonStatus = 0 }), NoDrivers));
    }

    [Fact]
    public void Warning_penalties_map_to_their_own_type()
    {
        var warning = RaceEventMapper.Map(new EventDetails(new PenaltyEvent
        {
            VehicleIdx = 2,
            PenaltyType = PenaltyType.Warning,
            InfringementType = InfringementType.IllegalTimeGain,
            LapNum = 4,
        }), NoDrivers)!;

        Assert.Equal("Warning", warning.Type);
        Assert.Equal(4, warning.LapNumber);
        Assert.Equal((byte)2, warning.CarIndex);

        // A "multiple warnings" notice is also a Verwarnung, not a Strafe.
        var multiple = RaceEventMapper.Map(new EventDetails(new PenaltyEvent
        {
            VehicleIdx = 2,
            PenaltyType = PenaltyType.TimePenalty,
            InfringementType = InfringementType.MultipleWarnings,
            LapNum = 5,
        }), NoDrivers)!;

        Assert.Equal("Warning", multiple.Type);
    }

    [Fact]
    public void Time_penalties_map_as_penalties()
    {
        var timePenalty = RaceEventMapper.Map(new EventDetails(new PenaltyEvent
        {
            VehicleIdx = 2,
            PenaltyType = PenaltyType.TimePenalty,
            InfringementType = InfringementType.PitLaneSpeeding,
            Time = 5,
            LapNum = 6,
        }), NoDrivers)!;

        Assert.Equal("Penalty", timePenalty.Type);
        Assert.Equal(6, timePenalty.LapNumber);
        Assert.Contains("5s", timePenalty.Text);
    }
}