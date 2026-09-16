using System.Text.Json;
using ERCTelemetry.Core.Analysis;
using ERCTelemetry.Core.Llm;
using Xunit;

namespace ERCTelemetry.Core.Tests.Llm;

/// <summary>LlmRaceCoach tests: returns the client's answer, falls back to null on
/// failure, and feeds the structured findings into the prompt.</summary>
public sealed class LlmRaceCoachTests
{
    private static CoachReport Report() => new(
        RivalName: "Max",
        TotalLossSeconds: 0.6,
        Findings:
        [
            new CoachFinding(3, 0.4f, 0.5f, "Du bremst zu früh."),
            new CoachFinding(0, 0.2f, 0, "Behalte diese Linie."),
        ]);

    [Fact]
    public async Task CoachAsync_returns_client_response()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Ok("In Kurve 3 verlierst du am meisten."));
        var client = new OllamaClient("https://ollama.com", "key", "model", handler);
        var coach = new LlmRaceCoach(client);

        var result = await coach.CoachAsync(Report(), CancellationToken.None);

        Assert.Equal("In Kurve 3 verlierst du am meisten.", result);
    }

    [Fact]
    public async Task CoachAsync_returns_null_when_client_fails()
    {
        var handler = new FakeHttpHandler(_ => throw new HttpRequestException("boom"));
        var client = new OllamaClient("https://ollama.com", "key", "model", handler);
        var coach = new LlmRaceCoach(client);

        var result = await coach.CoachAsync(Report(), CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task CoachAsync_prompt_contains_findings()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Ok("ok"));
        var client = new OllamaClient("https://ollama.com", "key", "model", handler);
        var coach = new LlmRaceCoach(client);

        await coach.CoachAsync(Report(), CancellationToken.None);

        var body = JsonDocument.Parse(handler.LastRequestBody!);
        var user = body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString();
        Assert.Contains("Max", user);
        Assert.Contains("Kurve 3", user);
        Assert.Contains("Du bremst zu früh.", user);
        Assert.Contains("Behalte diese Linie.", user);
    }
}
