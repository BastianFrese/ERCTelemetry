using System.Text.Json;
using F1Game.UDP.Enums;
using ERCTelemetry.Core.OverlayProtocol;
using ERCTelemetry.Core.Session;
using Xunit;

namespace ERCTelemetry.Core.Tests.OverlayProtocol;

public class OverlayMessageFactoryTests
{
    private static readonly DateTimeOffset FixedUtc = new(2026, 3, 29, 14, 30, 15, TimeSpan.Zero);

    private static TelemetrySnapshot BuildSnapshot() => new(
        Meta: new SessionMeta(
            SessionUid: 0x1122334455667788,
            SessionType: SessionType.Race,
            Track: Track.Spa,
            TotalLaps: 44,
            TrackLength: 7004,
            IsNetworkGame: true,
            PlayerCarIndex: 3,
            GameMode: GameMode.OnlineCustom,
            Weather: Weather.LightRain,
            TrackTemperature: 27,
            AirTemperature: 19,
            SessionTimeLeft: 0,
            Forecast: new[] { new ForecastSample(10, Weather.LightRain, 65, 26, 21) }),
        Drivers: new[]
        {
            new DriverEntry(3, "Player One", Team.McLaren, 81, false, true, true),
            new DriverEntry(9, "Rival Driver", Team.RedBullRacing, 1, false, false, true),
        },
        Standings: new[]
        {
            new StandingsRow(1, 3, "Player One", Team.McLaren, 81, 12, 91089, 90123, 0, 0,
                PitStatus.None, 0, ResultStatus.Active, ActualCompound.F1C3, 12, true),
            new StandingsRow(2, 9, "Rival Driver", Team.RedBullRacing, 1, 12, 93450, 89876, 2361, 2361,
                PitStatus.Pitting, 2, ResultStatus.Active, ActualCompound.F1Inter, 4, false),
        },
        FinalResults: Array.Empty<FinalResultRow>(),
        RecentEvents: new[]
        {
            new RaceEventEntry(FixedUtc, "fastest-lap", 9, "Rival Driver fastest lap 89876", 7),
        },
        Player: new PlayerFrame(3, "Player One", 271, 6, 0.95f, 0.0f, 11050,
            3_200_000f, ErsDeployMode.Hotlap, 4.2f, 3.1f, ActualCompound.F1C3, 12, true, false),
        Rival: new PlayerFrame(9, "Rival Driver", 264, 5, 1.0f, 0.15f, 10480,
            1_000_000f, ErsDeployMode.Medium, 3.0f, 2.0f, ActualCompound.F1Inter, 4, true, true),
        FrameVersion: 42,
        BuiltAtUtc: FixedUtc);

    [Fact]
    public void Hello_carries_protocol_version()
    {
        Assert.Equal("""{"t":"hello","protocol":1}""", OverlayMessageFactory.CreateHello());
    }

    [Fact]
    public void Hello_carries_scheme_for_german_look()
    {
        Assert.Equal("""{"t":"hello","protocol":1,"scheme":"de"}""",
            OverlayMessageFactory.CreateHello("de"));
    }

    [Fact]
    public void State_carries_forecast_samples()
    {
        using var doc = JsonDocument.Parse(OverlayMessageFactory.CreateState(BuildSnapshot()));
        var forecast = doc.RootElement.GetProperty("session").GetProperty("forecast");

        Assert.Equal(1, forecast.GetArrayLength());
        Assert.Equal(10, forecast[0].GetProperty("timeOffsetMinutes").GetInt32());
        Assert.Equal("LightRain", forecast[0].GetProperty("weather").GetString());
        Assert.Equal(65, forecast[0].GetProperty("rainPercent").GetByte());
        Assert.Equal(26, forecast[0].GetProperty("trackTemperature").GetSByte());
        Assert.Equal(21, forecast[0].GetProperty("airTemperature").GetSByte());
    }

    [Fact]
    public void State_without_scheme_stays_classic_shape()
    {
        using var doc = JsonDocument.Parse(OverlayMessageFactory.CreateState(BuildSnapshot()));

        Assert.False(doc.RootElement.TryGetProperty("scheme", out _));
    }

    [Fact]
    public void State_carries_tyre_heatmap_entries_when_reported()
    {
        var snapshot = BuildSnapshot() with
        {
            Tyres = new[] { new TyreStatus(3, 22.4f, 0f), new TyreStatus(9, 55.1f, 12f) },
        };

        using var doc = JsonDocument.Parse(OverlayMessageFactory.CreateState(snapshot));
        var tyres = doc.RootElement.GetProperty("tyres");

        Assert.Equal(2, tyres.GetArrayLength());
        Assert.Equal(3, tyres[0].GetProperty("carIndex").GetByte());
        Assert.Equal(22.4, tyres[0].GetProperty("wearPercent").GetDouble(), 3);
        Assert.Equal(12, tyres[1].GetProperty("damagePercent").GetDouble(), 3);
    }

    [Fact]
    public void State_sends_empty_tyres_before_first_damage_packet()
    {
        using var doc = JsonDocument.Parse(OverlayMessageFactory.CreateState(BuildSnapshot()));

        Assert.Equal(0, doc.RootElement.GetProperty("tyres").GetArrayLength());
    }

    [Fact]
    public void Map_skips_zero_positions_and_marks_player()
    {
        var snapshot = BuildSnapshot() with
        {
            Positions = new MotionFrame(22, Grid(), Grid(), ZeroGrid(), ZeroGrid()),
        };
        snapshot.Positions!.X[3] = 12.5f;
        snapshot.Positions!.Z[3] = -30.25f;
        snapshot.Positions!.X[9] = -40f;
        snapshot.Positions!.Z[9] = 100f;

        using var doc = JsonDocument.Parse(OverlayMessageFactory.CreateMap(snapshot)!);
        var positions = doc.RootElement.GetProperty("positions");

        Assert.Equal("map", doc.RootElement.GetProperty("t").GetString());
        Assert.Equal(2, positions.GetArrayLength()); // 20 empty slots stay hidden
        Assert.Equal(3, positions[0].GetProperty("carIndex").GetByte());
        Assert.True(positions[0].GetProperty("isPlayer").GetBoolean());
        Assert.Equal(-40f, positions[1].GetProperty("x").GetSingle(), 3);
        Assert.False(positions[1].GetProperty("isPlayer").GetBoolean());
    }

    [Fact]
    public void Map_returns_null_when_positions_missing_or_all_zero()
    {
        Assert.Null(OverlayMessageFactory.CreateMap(BuildSnapshot() with { Positions = null }));

        var empty = BuildSnapshot() with { Positions = new MotionFrame(22, Grid(), Grid(), ZeroGrid(), ZeroGrid()) };
        Assert.Null(OverlayMessageFactory.CreateMap(empty));
    }

    [Fact]
    public void Config_serializes_block_visibility_map()
    {
        var json = OverlayMessageFactory.CreateConfig(new Dictionary<string, bool>
        {
            ["h2h.header"] = true,
            ["h2h.tally"] = false,
        });

        Assert.Equal("""{"t":"config","blocks":{"h2h.header":true,"h2h.tally":false}}""", json);
    }

    [Fact]
    public void State_includes_h2h_tallies_when_present()
    {
        var snapshot = BuildSnapshot() with
        {
            H2h = new[]
            {
                new H2hTally(3, 9, 5, 2, 1, 0),
                new H2hTally(9, 3, 1, 0, 5, 2),
            },
        };

        using var doc = JsonDocument.Parse(OverlayMessageFactory.CreateState(snapshot));
        var h2h = doc.RootElement.GetProperty("h2h");

        Assert.Equal(2, h2h.GetArrayLength());
        Assert.Equal(3, h2h[0].GetProperty("carIndex").GetByte());
        Assert.Equal(9, h2h[0].GetProperty("opponentIndex").GetByte());
        Assert.Equal(5, h2h[0].GetProperty("lapsWon").GetInt32());
        Assert.Equal(2, h2h[0].GetProperty("positionWins").GetInt32());
        Assert.Equal(1, h2h[0].GetProperty("opponentLapsWon").GetInt32());
        Assert.Equal(0, h2h[0].GetProperty("opponentPositionWins").GetInt32());
        Assert.Equal(9, h2h[1].GetProperty("carIndex").GetByte());
        Assert.Equal(3, h2h[1].GetProperty("opponentIndex").GetByte());
        Assert.Equal(1, h2h[1].GetProperty("lapsWon").GetInt32());
        Assert.Equal(5, h2h[1].GetProperty("opponentLapsWon").GetInt32());
    }

    [Fact]
    public void State_omits_h2h_when_null_or_empty()
    {
        using var nullDoc = JsonDocument.Parse(
            OverlayMessageFactory.CreateState(BuildSnapshot() with { H2h = null }));
        Assert.False(nullDoc.RootElement.TryGetProperty("h2h", out _));

        using var emptyDoc = JsonDocument.Parse(
            OverlayMessageFactory.CreateState(BuildSnapshot() with { H2h = Array.Empty<H2hTally>() }));
        Assert.False(emptyDoc.RootElement.TryGetProperty("h2h", out _));
    }

    [Fact]
    public void Standing_maps_best_sector_times()
    {
        var snapshot = BuildSnapshot() with
        {
            Standings =
            [
                new StandingsRow(1, 3, "Player One", Team.McLaren, 81, 12, 91089, 90123, 0, 0,
                    PitStatus.None, 0, ResultStatus.Active, ActualCompound.F1C3, 12, true,
                    BestSector1Ms: 28_510, BestSector2Ms: 30_100, BestSector3Ms: 31_000),
            ],
        };

        using var doc = JsonDocument.Parse(OverlayMessageFactory.CreateState(snapshot));
        var row = doc.RootElement.GetProperty("standings")[0];

        Assert.Equal(28_510u, row.GetProperty("bestSector1TimeMs").GetUInt32());
        Assert.Equal(30_100u, row.GetProperty("bestSector2TimeMs").GetUInt32());
        Assert.Equal(31_000u, row.GetProperty("bestSector3TimeMs").GetUInt32());
    }

    [Fact]
    public void Standing_defaults_best_sectors_to_zero()
    {
        using var doc = JsonDocument.Parse(OverlayMessageFactory.CreateState(BuildSnapshot()));
        var row = doc.RootElement.GetProperty("standings")[0];

        Assert.Equal(0u, row.GetProperty("bestSector1TimeMs").GetUInt32());
    }

    private static float[] Grid() => new float[TelemetryConstants.MaxCars];
    private static float[] ZeroGrid() => new float[TelemetryConstants.MaxCars];


    [Fact]
    public void State_serializes_session_and_standings()
    {
        var json = OverlayMessageFactory.CreateState(BuildSnapshot());

        using var doc = JsonDocument.Parse(json); // must be valid JSON
        var root = doc.RootElement;
        Assert.Equal("state", root.GetProperty("t").GetString());
        Assert.Equal(44, root.GetProperty("session").GetProperty("totalLaps").GetByte());
        Assert.Equal("Spa", root.GetProperty("session").GetProperty("track").GetString());
        Assert.Equal("OnlineCustom", root.GetProperty("session").GetProperty("gameMode").GetString());
        Assert.Equal(2, root.GetProperty("standings").GetArrayLength());
        Assert.Equal(0, root.GetProperty("results").GetArrayLength());

        var first = root.GetProperty("standings")[0];
        Assert.Equal(1, first.GetProperty("position").GetByte());
        Assert.Equal("Player One", first.GetProperty("name").GetString());
        Assert.Equal("McLaren", first.GetProperty("team").GetString());
        Assert.Equal("F1C3", first.GetProperty("tyreCompound").GetString());
        Assert.Equal("Pitting", root.GetProperty("standings")[1].GetProperty("pitStatus").GetString());
        Assert.True(first.GetProperty("isPlayer").GetBoolean());
    }

    [Fact]
    public void Player_serializes_frames_with_ers_percent()
    {
        var json = OverlayMessageFactory.CreatePlayer(BuildSnapshot());

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("player", root.GetProperty("t").GetString());

        var player = root.GetProperty("player");
        Assert.Equal(271, player.GetProperty("speed").GetUInt16());
        Assert.Equal(6, player.GetProperty("gear").GetSByte());
        Assert.Equal("Hotlap", player.GetProperty("ersDeployMode").GetString());
        Assert.Equal(80f, player.GetProperty("ersPercent").GetSingle(), 1); // 3.2 MJ of 4 MJ

        var rival = root.GetProperty("rival");
        Assert.Equal(25f, rival.GetProperty("ersPercent").GetSingle(), 1); // 1.0 MJ of 4 MJ
    }

    [Fact]
    public void Player_message_handles_missing_session_and_frames()
    {
        var empty = new TelemetrySnapshot(null, Array.Empty<DriverEntry>(), Array.Empty<StandingsRow>(),
            Array.Empty<FinalResultRow>(), Array.Empty<RaceEventEntry>(), null, null, 0, FixedUtc);

        Assert.Equal("""{"t":"player","player":null,"rival":null}""",
            OverlayMessageFactory.CreatePlayer(empty));
    }

    [Fact]
    public void Player_select_moves_selected_frame_into_player_slot()
    {
        var json = OverlayMessageFactory.CreatePlayer(BuildSnapshot(), selectedCarIndex: 9);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("Rival Driver", doc.RootElement.GetProperty("player").GetProperty("name").GetString());
        Assert.Equal("Player One", doc.RootElement.GetProperty("rival").GetProperty("name").GetString());
    }

    [Fact]
    public void Player_select_of_the_player_keeps_frames_unchanged()
    {
        var json = OverlayMessageFactory.CreatePlayer(BuildSnapshot(), selectedCarIndex: 3);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("Player One", doc.RootElement.GetProperty("player").GetProperty("name").GetString());
        Assert.Equal("Rival Driver", doc.RootElement.GetProperty("rival").GetProperty("name").GetString());
    }

    [Fact]
    public void Player_select_for_unknown_car_clears_player_slot()
    {
        var json = OverlayMessageFactory.CreatePlayer(BuildSnapshot(), selectedCarIndex: 12);

        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("player").ValueKind == JsonValueKind.Null);
        Assert.Equal("Rival Driver", doc.RootElement.GetProperty("rival").GetProperty("name").GetString());
    }

    [Fact]
    public void Event_maps_race_control_entry()
    {
        var json = OverlayMessageFactory.CreateEvent(
            new RaceControl(new RaceEventEntry(FixedUtc, "fastest-lap", 9, "Rival Driver fastest lap 89876", 7)));

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("event", root.GetProperty("t").GetString());
        Assert.Equal("fastest-lap", root.GetProperty("type").GetString());
        Assert.Equal("Rival Driver fastest lap 89876", root.GetProperty("text").GetString());
        Assert.Equal(9, root.GetProperty("carIndex").GetByte());
        Assert.Equal(7, root.GetProperty("seq").GetInt64());
        Assert.Equal(FixedUtc, root.GetProperty("utc").GetDateTimeOffset());
    }

    [Fact]
    public void Event_maps_session_lifecycle_and_lap_events()
    {
        using var started = JsonDocument.Parse(
            OverlayMessageFactory.CreateEvent(new SessionStarted(BuildSnapshot().Meta!)));
        Assert.Equal("session-started", started.RootElement.GetProperty("type").GetString());
        Assert.Equal("Race at Spa · 44 laps", started.RootElement.GetProperty("text").GetString());

        using var ended = JsonDocument.Parse(
            OverlayMessageFactory.CreateEvent(
                new SessionEnded(1, "final classification", Array.Empty<FinalResultRow>())));
        Assert.Equal("session-ended", ended.RootElement.GetProperty("type").GetString());
        Assert.Equal("final classification", ended.RootElement.GetProperty("text").GetString());

        using var lap = JsonDocument.Parse(
            OverlayMessageFactory.CreateEvent(new LapCompleted(3, "Player One", 12, 91089, 0, 0)));
        Assert.Equal("lap-completed", lap.RootElement.GetProperty("type").GetString());
        Assert.Equal("Player One · lap 12 · 1:31.089", lap.RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public void client_parser_accepts_select_ping_and_rejects_garbage()
    {
        var select = OverlayClientParser.Parse("""{"t":"select","carIndex":9}""");
        Assert.Equal(OverlayProtocolConstants.SelectType, select!.Type);
        Assert.Equal((byte)9, select.CarIndex);

        var clear = OverlayClientParser.Parse("""{"t":"select"}""");
        Assert.Equal(OverlayProtocolConstants.SelectType, clear!.Type);
        Assert.Null(clear.CarIndex);

        var ping = OverlayClientParser.Parse("""{"t":"ping"}""");
        Assert.Equal(OverlayProtocolConstants.PingType, ping!.Type);

        Assert.Null(OverlayClientParser.Parse("not json at all"));
        Assert.Null(OverlayClientParser.Parse("""{"type":"no-discriminator"}"""));
    }

    [Fact]
    public void FormatLapTime_uses_f1_convention()
    {
        Assert.Equal("", OverlayMessageFactory.FormatLapTime(0));
        Assert.Equal("1:31.089", OverlayMessageFactory.FormatLapTime(91089));
        Assert.Equal("10:01.001", OverlayMessageFactory.FormatLapTime(601001));
    }
}