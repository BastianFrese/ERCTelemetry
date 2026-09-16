using System.Net;
using System.Text.Json;
using ERCTelemetry.Core.Llm;
using Xunit;

namespace ERCTelemetry.Core.Tests.Llm;

/// <summary>OllamaClient wire tests: success path, request shape (endpoint, model,
/// messages, bearer token) and the null-on-failure fallback contract.</summary>
public sealed class OllamaClientTests
{
    [Fact]
    public async Task CompleteAsync_returns_content_on_success()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Ok("Druck von hinten!"));
        var client = new OllamaClient("https://ollama.com", "key", "deepseek-v4-flash:cloud", handler);

        var result = await client.CompleteAsync("system", "user", CancellationToken.None);

        Assert.Equal("Druck von hinten!", result);
    }

    [Fact]
    public async Task CompleteAsync_posts_to_chat_endpoint_with_model_and_messages()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Ok("ok"));
        var client = new OllamaClient("https://ollama.com", null, "deepseek-v4-flash:cloud", handler);

        await client.CompleteAsync("Du bist ein Kommentator.", "Runde 1", CancellationToken.None);

        Assert.Equal("https://ollama.com/api/chat", handler.LastRequest!.RequestUri!.ToString());
        var body = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal("deepseek-v4-flash:cloud", body.RootElement.GetProperty("model").GetString());
        var messages = body.RootElement.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(2, messages.Count);
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("Du bist ein Kommentator.", messages[0].GetProperty("content").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        Assert.Equal("Runde 1", messages[1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task CompleteAsync_sends_bearer_token_when_key_set()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Ok("ok"));
        var client = new OllamaClient("https://ollama.com", "secret-key", "model", handler);

        await client.CompleteAsync("s", "u", CancellationToken.None);

        Assert.Equal("Bearer secret-key", handler.LastRequest!.Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task CompleteAsync_returns_null_on_http_error()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Error(HttpStatusCode.Unauthorized));
        var client = new OllamaClient("https://ollama.com", "key", "model", handler);

        var result = await client.CompleteAsync("s", "u", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task CompleteAsync_returns_null_on_malformed_response()
    {
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("not json"),
        });
        var client = new OllamaClient("https://ollama.com", "key", "model", handler);

        var result = await client.CompleteAsync("s", "u", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task CompleteAsync_returns_null_on_network_error()
    {
        var handler = new FakeHttpHandler(_ => throw new HttpRequestException("boom"));
        var client = new OllamaClient("https://ollama.com", "key", "model", handler);

        var result = await client.CompleteAsync("s", "u", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task CompleteAsync_returns_null_on_empty_answer()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Ok("   "));
        var client = new OllamaClient("https://ollama.com", "key", "model", handler);

        var result = await client.CompleteAsync("s", "u", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetModelsAsync_returns_model_names()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(
            """{"models":[{"model":"gemma3:cloud"},{"model":"deepseek-v4-flash:cloud"}]}"""));
        var client = new OllamaClient("https://ollama.com", "key", "model", handler);

        var result = await client.GetModelsAsync(CancellationToken.None);

        Assert.Equal(["gemma3:cloud", "deepseek-v4-flash:cloud"], result);
    }

    [Fact]
    public async Task GetModelsAsync_gets_tags_endpoint_with_bearer_token()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(
            """{"models":[{"model":"gemma3:cloud"}]}"""));
        var client = new OllamaClient("https://ollama.com", "secret-key", "model", handler);

        await client.GetModelsAsync(CancellationToken.None);

        Assert.Equal("https://ollama.com/api/tags", handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal("Bearer secret-key", handler.LastRequest!.Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task GetModelsAsync_returns_null_on_http_error()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Error(HttpStatusCode.Unauthorized));
        var client = new OllamaClient("https://ollama.com", "key", "model", handler);

        var result = await client.GetModelsAsync(CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetModelsAsync_returns_null_on_malformed_response()
    {
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("not json"),
        });
        var client = new OllamaClient("https://ollama.com", "key", "model", handler);

        var result = await client.GetModelsAsync(CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetModelsAsync_returns_null_on_network_error()
    {
        var handler = new FakeHttpHandler(_ => throw new HttpRequestException("boom"));
        var client = new OllamaClient("https://ollama.com", "key", "model", handler);

        var result = await client.GetModelsAsync(CancellationToken.None);

        Assert.Null(result);
    }
}
