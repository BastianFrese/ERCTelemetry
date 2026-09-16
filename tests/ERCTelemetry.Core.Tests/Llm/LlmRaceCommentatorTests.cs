using System.Text.Json;
using ERCTelemetry.Core.Llm;
using Xunit;

namespace ERCTelemetry.Core.Tests.Llm;

/// <summary>LlmRaceCommentator tests: returns the client's answer, falls back to null on
/// failure, and bundles the session context into the prompt.</summary>
public sealed class LlmRaceCommentatorTests
{
    private static CommentaryContext Context() => new(
        Track: "Bahrain", SessionType: "Race", Lap: 12, TotalLaps: 57, Weather: "Rain",
        PlayerPosition: 3, RecentLines: ["Druck von hinten! Max ist nur +0.4s dahinter."]);

    [Fact]
    public async Task CommentateAsync_returns_client_response()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Ok("Max macht Druck auf P3!"));
        var client = new OllamaClient("https://ollama.com", "key", "model", handler);
        var commentator = new LlmRaceCommentator(client);

        var result = await commentator.CommentateAsync(Context(), CancellationToken.None);

        Assert.Equal("Max macht Druck auf P3!", result);
    }

    [Fact]
    public async Task CommentateAsync_returns_null_when_client_fails()
    {
        var handler = new FakeHttpHandler(_ => throw new HttpRequestException("boom"));
        var client = new OllamaClient("https://ollama.com", "key", "model", handler);
        var commentator = new LlmRaceCommentator(client);

        var result = await commentator.CommentateAsync(Context(), CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task CommentateAsync_prompt_contains_session_context()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Ok("ok"));
        var client = new OllamaClient("https://ollama.com", "key", "model", handler);
        var commentator = new LlmRaceCommentator(client);

        await commentator.CommentateAsync(Context(), CancellationToken.None);

        var body = JsonDocument.Parse(handler.LastRequestBody!);
        var user = body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString();
        Assert.Contains("Bahrain", user);
        Assert.Contains("Runde 12/57", user);
        Assert.Contains("P3", user);
        Assert.Contains("Druck von hinten!", user);
    }
}
