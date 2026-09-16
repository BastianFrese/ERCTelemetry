using System.Text.Json;
using ERCTelemetry.Core.Analysis;
using ERCTelemetry.Core.Llm;
using Xunit;

namespace ERCTelemetry.Core.Tests.Llm;

/// <summary>LlmRaceSummarizer tests: returns the client's answer, falls back to null on
/// failure, and feeds the Layer-1 paragraphs into the prompt.</summary>
public sealed class LlmRaceSummarizerTests
{
    private static RaceSummary Summary() => new(
        Title: "Rennzusammenfassung — Bahrain",
        Paragraphs:
        [
            "Rennen auf Bahrain über 57 Runden, 20 Fahrer am Start.",
            "Sieg für Erdi vor Max.",
        ]);

    [Fact]
    public async Task SummarizeAsync_returns_client_response()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Ok("Erdi dominiert in Bahrain."));
        var client = new OllamaClient("https://ollama.com", "key", "model", handler);
        var summarizer = new LlmRaceSummarizer(client);

        var result = await summarizer.SummarizeAsync(Summary(), CancellationToken.None);

        Assert.Equal("Erdi dominiert in Bahrain.", result);
    }

    [Fact]
    public async Task SummarizeAsync_returns_null_when_client_fails()
    {
        var handler = new FakeHttpHandler(_ => throw new HttpRequestException("boom"));
        var client = new OllamaClient("https://ollama.com", "key", "model", handler);
        var summarizer = new LlmRaceSummarizer(client);

        var result = await summarizer.SummarizeAsync(Summary(), CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task SummarizeAsync_prompt_contains_paragraphs()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Ok("ok"));
        var client = new OllamaClient("https://ollama.com", "key", "model", handler);
        var summarizer = new LlmRaceSummarizer(client);

        await summarizer.SummarizeAsync(Summary(), CancellationToken.None);

        var body = JsonDocument.Parse(handler.LastRequestBody!);
        var user = body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString();
        Assert.Contains("Bahrain", user);
        Assert.Contains("Sieg für Erdi vor Max.", user);
    }
}
