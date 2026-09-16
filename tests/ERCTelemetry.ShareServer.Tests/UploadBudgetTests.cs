using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ERCTelemetry.Core.Share;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace ERCTelemetry.ShareServer.Tests;

/// <summary>Same shared collection as the other ShareServer test classes: they configure
/// the server through process-global env vars, so the classes must never run in parallel
/// (a parallel UploadBudgetTests would drop the others' budget back to its tiny test value).</summary>
[Collection("ShareServer")]
/// <summary>H1 regression: the per-session upload budget is enforced against the bytes
/// actually written, not a declared Content-Length. A tiny configured budget
/// (Share__LimitSessionBytes) exercises the 413 path without multi-GB bodies — and a
/// chunked request with no Content-Length, which the old declared-length pre-check could
/// not count, is the exact shape the fix closes.</summary>
public sealed class UploadBudgetTests : IClassFixture<UploadBudgetFixture>
{
    private readonly UploadBudgetFixture _fixture;

    public UploadBudgetTests(UploadBudgetFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Single_shot_upload_over_the_budget_is_413_and_leaves_no_file()
    {
        const ulong uid = 42_100;
        var owner = await _fixture.CreateSessionAsync(uid);
        var over = new byte[UploadBudgetFixture.Budget + 1024];
        over.AsSpan().Fill(0x41);

        var response = await _fixture.PutClipAsync(uid, owner, over);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.False(File.Exists(Path.Combine(_fixture.RootPath, uid.ToString(), "clip-1.mp4")));
    }

    [Fact]
    public async Task Single_shot_upload_within_the_budget_still_works()
    {
        const ulong uid = 42_101;
        var owner = await _fixture.CreateSessionAsync(uid);
        var small = new byte[UploadBudgetFixture.Budget / 2];
        small.AsSpan().Fill(0x42);

        var put = await _fixture.PutClipAsync(uid, owner, small);
        Assert.Equal(HttpStatusCode.Created, put.StatusCode);

        var video = await _fixture.Client.GetAsync($"/s/{uid}/clips/clip-1.mp4");
        var stored = await video.Content.ReadAsByteArrayAsync();
        Assert.Equal(small.Length, stored.Length);
        Assert.Equal(0x42, stored[^1]);
    }

    [Fact]
    public async Task Chunked_part_without_content_length_is_counted_against_the_budget()
    {
        const ulong uid = 42_102;
        var owner = await _fixture.CreateSessionAsync(uid);

        // A stream that hides its length makes HttpClient send Transfer-Encoding: chunked —
        // the body arrives with no Content-Length, the shape the old declared-length
        // pre-check saw as "0 bytes" and let through.
        var over = new byte[UploadBudgetFixture.Budget + 2048];
        over.AsSpan().Fill(0x43);
        var put = new HttpRequestMessage(HttpMethod.Put, $"/api/sessions/{uid}/clips/clip-1.mp4")
        {
            Content = new StreamContent(new UnknownLengthStream(over)),
        };
        put.Headers.Add(ShareConstants.TokenHeader, UploadBudgetFixture.Token);
        put.Headers.Add(ShareConstants.OwnerHeader, owner);

        var response = await _fixture.Client.SendAsync(put);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.False(File.Exists(Path.Combine(_fixture.RootPath, uid.ToString(), "clip-1.mp4")));
        // No scratch part files either.
        Assert.Empty(Directory.Exists(Path.Combine(_fixture.RootPath, uid.ToString()))
            ? Directory.GetFiles(Path.Combine(_fixture.RootPath, uid.ToString()), "*.part*")
            : []);
    }

    [Fact]
    public async Task Rejected_bytes_are_not_counted_against_the_budget()
    {
        const ulong uid = 42_103;
        var owner = await _fixture.CreateSessionAsync(uid);

        // Half the budget fits…
        var half = new byte[UploadBudgetFixture.Budget / 2];
        half.AsSpan().Fill(0x11);
        Assert.Equal(HttpStatusCode.Created, (await _fixture.PutClipAsync(uid, owner, half)).StatusCode);

        // …the next claim (another whole budget) is rejected…
        var over = new byte[UploadBudgetFixture.Budget];
        over.AsSpan().Fill(0x22);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await _fixture.PutClipAsync(uid, owner, over)).StatusCode);

        // …and the session still has room for exactly half the budget — the rejected bytes
        // were never claimed.
        var retry = new byte[UploadBudgetFixture.Budget / 2];
        retry.AsSpan().Fill(0x33);
        Assert.Equal(HttpStatusCode.Created, (await _fixture.PutClipAsync(uid, owner, retry)).StatusCode);
    }

    /// <summary>A bounded in-memory stream that hides its length (no Length override), so
    /// HttpClient must send the body with Transfer-Encoding: chunked.</summary>
    private sealed class UnknownLengthStream : Stream
    {
        private readonly byte[] _data;
        private int _position;

        public UnknownLengthStream(byte[] data) => _data = data;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= _data.Length)
            {
                return 0;
            }

            var n = Math.Min(count, _data.Length - _position);
            Array.Copy(_data, _position, buffer, offset, n);
            _position += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>WebApplicationFactory with a tiny per-session upload budget so the 413 path is
/// reachable with small bodies. The budget is read from Share__LimitSessionBytes (default:
/// ClipUpload.MaxSessionBytes = 2 GiB in production).</summary>
public sealed class UploadBudgetFixture : IDisposable
{
    public const string Token = "test-token";

    /// <summary>Tiny on purpose — the tests upload payloads around this size instead of
    /// multi-GB bodies.</summary>
    public const long Budget = 4096;

    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _root;

    public UploadBudgetFixture()
    {
        _root = Path.Combine(Path.GetTempPath(), $"erc-budget-test-{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable("Share__Token", Token);
        Environment.SetEnvironmentVariable("Share__RootPath", _root);
        Environment.SetEnvironmentVariable("Share__LimitSessionBytes", Budget.ToString());
        _factory = new WebApplicationFactory<Program>();
        Client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    public HttpClient Client { get; }

    public string RootPath => _root;

    public async Task<string> CreateSessionAsync(ulong uid)
    {
        var post = new HttpRequestMessage(HttpMethod.Post, "/api/sessions")
        {
            Content = JsonContent.Create(new ShareManifest(
                uid, "Spa", "Race", DateTimeOffset.UtcNow, 3720, 1,
                [new ShareResultRow(1, "Player One", "McLaren", 44, 90_123, 3600.5, "Finished", 25f)],
                [new ShareClip("clip-1.mp4", 7, 3, null, "Player One", null, 2, 12.5)])),
        };
        post.Headers.Add(ShareConstants.TokenHeader, Token);
        var response = await Client.SendAsync(post);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("ownerSecret").GetString()!;
    }

    public Task<HttpResponseMessage> PutClipAsync(ulong uid, string owner, byte[] body)
    {
        var put = new HttpRequestMessage(HttpMethod.Put, $"/api/sessions/{uid}/clips/clip-1.mp4")
        {
            Content = new ByteArrayContent(body),
        };
        put.Headers.Add(ShareConstants.TokenHeader, Token);
        put.Headers.Add(ShareConstants.OwnerHeader, owner);
        return Client.SendAsync(put);
    }

    public void Dispose()
    {
        Client.Dispose();
        _factory.Dispose();
        Environment.SetEnvironmentVariable("Share__Token", null);
        Environment.SetEnvironmentVariable("Share__RootPath", null);
        Environment.SetEnvironmentVariable("Share__LimitSessionBytes", null);
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
