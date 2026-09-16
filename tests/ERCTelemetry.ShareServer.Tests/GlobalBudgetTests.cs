using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ERCTelemetry.Core.Share;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace ERCTelemetry.ShareServer.Tests;

/// <summary>Same shared collection as the other ShareServer test classes: they configure the
/// server through process-global env vars, so the classes must never run in parallel (a
/// parallel GlobalBudgetTests would drag the global quota down for the other classes).</summary>
[Collection("ShareServer")]
/// <summary>MEDIUM-1 regression: the server-global upload budget (Share__LimitGlobalBytes,
/// default ClipUpload.MaxGlobalBytes = 20 GiB) bounds the disk ALL sessions can occupy. The
/// per-session budget alone could not: a holder of the public share token opens unlimited
/// sessions, each with its own room for MaxSessionBytes. The global claim is made before
/// the per-session claim (and refunded if the latter fails), so once the server-wide cap is
/// exhausted, further uploads fail regardless of how much per-session headroom is left.
///
/// Each test builds its OWN fixture/server: the tests deliberately push the global counter
/// to its cap, so sharing one instance would leak that state into the next test.</summary>
public sealed class GlobalBudgetTests
{
    [Fact]
    public async Task A_second_session_is_rejected_once_the_global_quota_is_exhausted()
    {
        using var fixture = new GlobalBudgetFixture();
        const ulong firstUid = 43_001;
        var firstOwner = await fixture.CreateSessionAsync(firstUid);

        // Fill the whole global cap with one session's two uploads.
        var half = new byte[GlobalBudgetFixture.GlobalCap / 2];
        half.AsSpan().Fill(0x11);
        Assert.Equal(HttpStatusCode.Created, (await fixture.PutClipAsync(firstUid, firstOwner, half)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await fixture.PutClipAsync(firstUid, firstOwner, half)).StatusCode);

        // A second session tries to claim too: the global counter is at its cap, so even a
        // tiny body is rejected — the per-session budget alone would have admitted it.
        const ulong secondUid = 43_002;
        var secondOwner = await fixture.CreateSessionAsync(secondUid);
        var small = new byte[1024];
        small.AsSpan().Fill(0x22);

        var put = await fixture.PutClipAsync(secondUid, secondOwner, small);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, put.StatusCode);
        Assert.False(File.Exists(Path.Combine(fixture.RootPath, secondUid.ToString(), "clip-1.mp4")));
    }

    [Fact]
    public async Task Deleting_a_session_releases_its_share_of_the_global_quota()
    {
        using var fixture = new GlobalBudgetFixture();
        const ulong firstUid = 43_003;
        var firstOwner = await fixture.CreateSessionAsync(firstUid);

        var half = new byte[GlobalBudgetFixture.GlobalCap / 2];
        half.AsSpan().Fill(0x33);
        Assert.Equal(HttpStatusCode.Created, (await fixture.PutClipAsync(firstUid, firstOwner, half)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await fixture.PutClipAsync(firstUid, firstOwner, half)).StatusCode);

        // Free the quota by deleting the session (the app's failed-upload cleanup path) —
        // ReleaseSession must return the claimed bytes to the global budget.
        var delete = new HttpRequestMessage(HttpMethod.Delete, $"/api/sessions/{firstUid}");
        delete.Headers.Add(ShareConstants.TokenHeader, GlobalBudgetFixture.Token);
        delete.Headers.Add(ShareConstants.OwnerHeader, firstOwner);
        Assert.Equal(HttpStatusCode.NoContent, (await fixture.Client.SendAsync(delete)).StatusCode);

        // A fresh session can now upload again — the released bytes are usable once more.
        const ulong secondUid = 43_004;
        var secondOwner = await fixture.CreateSessionAsync(secondUid);
        var half2 = new byte[GlobalBudgetFixture.GlobalCap / 2];
        half2.AsSpan().Fill(0x44);
        var put = await fixture.PutClipAsync(secondUid, secondOwner, half2);

        Assert.Equal(HttpStatusCode.Created, put.StatusCode);
        Assert.True(File.Exists(Path.Combine(fixture.RootPath, secondUid.ToString(), "clip-1.mp4")));
    }
}

/// <summary>WebApplicationFactory with a tiny server-GLOBAL upload budget so the cross-session
/// 413 path is reachable with small bodies. The global cap is read from
/// Share__LimitGlobalBytes (default: ClipUpload.MaxGlobalBytes = 20 GiB in production); the
/// per-session budget intentionally stays at its production default (2 GiB), so a 413 in
/// these tests can only come from the global quota.</summary>
public sealed class GlobalBudgetFixture : IDisposable
{
    public const string Token = "test-token";

    /// <summary>Tiny on purpose — the tests upload payloads around this size instead of
    /// multi-GB bodies.</summary>
    public const long GlobalCap = 12_000;

    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _root;

    public GlobalBudgetFixture()
    {
        _root = Path.Combine(Path.GetTempPath(), $"erc-global-budget-test-{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable("Share__Token", Token);
        Environment.SetEnvironmentVariable("Share__RootPath", _root);
        Environment.SetEnvironmentVariable("Share__LimitGlobalBytes", GlobalCap.ToString());
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
        Environment.SetEnvironmentVariable("Share__LimitGlobalBytes", null);
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
