using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ERCTelemetry.Core.Share;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace ERCTelemetry.ShareServer.Tests;

/// <summary>End-to-end share-server flow via WebApplicationFactory: upload manifest +
/// clip, public page + video, token rejection, path traversal, owner-gated mutations
/// (clip upload + delete), delete. Shares the "ShareServer" collection with the other
/// classes so the per-class fixtures' process env vars cannot race across parallel runs.</summary>
[Collection("ShareServer")]
public sealed class ShareServerTests : IClassFixture<ShareServerFixture>
{
    private readonly ShareServerFixture _fixture;

    public ShareServerTests(ShareServerFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Owner_secret_is_returned_in_post_response()
    {
        var client = _fixture.Client;
        var response = await client.SendAsync(CreateManifestPost(client, 42_060));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        // 24 random bytes → 48 hex chars; a short/guessable value would be a red flag.
        Assert.Equal(48, doc.RootElement.GetProperty("ownerSecret").GetString()?.Length);
    }

    [Fact]
    public async Task Upload_flow_serves_page_and_video()
    {
        var client = _fixture.Client;

        // POST manifest → relative page path + owner secret.
        var post = new HttpRequestMessage(HttpMethod.Post, "/api/sessions")
        {
            Content = JsonContent.Create(SampleManifest()),
        };
        post.Headers.Add(ShareConstants.TokenHeader, ShareServerFixture.Token);
        var postResponse = await client.SendAsync(post);
        Assert.Equal(HttpStatusCode.Created, postResponse.StatusCode);
        var postJson = await postResponse.Content.ReadAsStringAsync();
        using var postDoc = JsonDocument.Parse(postJson);
        Assert.Equal("/s/42000", postDoc.RootElement.GetProperty("url").GetString());
        var ownerSecret = postDoc.RootElement.GetProperty("ownerSecret").GetString()!;

        // PUT clip → MP4 bytes on disk, gated on the owner secret.
        var put = new HttpRequestMessage(HttpMethod.Put, "/api/sessions/42000/clips/clip-1.mp4")
        {
            Content = new ByteArrayContent("fake-mp4-bytes"u8.ToArray())
            {
                Headers = { ContentType = new MediaTypeHeaderValue("video/mp4") },
            },
        };
        put.Headers.Add(ShareConstants.TokenHeader, ShareServerFixture.Token);
        put.Headers.Add(ShareConstants.OwnerHeader, ownerSecret);
        var putResponse = await client.SendAsync(put);
        Assert.Equal(HttpStatusCode.Created, putResponse.StatusCode);

        // GET page → HTML with track, driver and the root-relative video src.
        var page = await client.GetAsync("/s/42000");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("Spa", html);
        Assert.Contains("Player One", html);
        Assert.Contains("/s/42000/clips/clip-1.mp4", html);

        // GET video → the uploaded bytes with the MP4 content type.
        var video = await client.GetAsync("/s/42000/clips/clip-1.mp4");
        Assert.Equal(HttpStatusCode.OK, video.StatusCode);
        Assert.Equal("video/mp4", video.Content.Headers.ContentType?.MediaType);
        Assert.Equal("fake-mp4-bytes", await video.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Wrong_token_is_rejected()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/sessions")
        {
            Content = JsonContent.Create(SampleManifest()),
        };
        request.Headers.Add(ShareConstants.TokenHeader, "wrong-token");

        var response = await _fixture.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Missing_token_is_rejected()
    {
        var response = await _fixture.Client.PostAsJsonAsync("/api/sessions", SampleManifest());
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Config_endpoint_returns_the_current_token_without_auth()
    {
        // Public by design: the app bootstraps the token for Enduser before the first
        // upload, so /api/config must answer without the X-Share-Token header.
        var response = await _fixture.Client.GetAsync("/api/config");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(ShareServerFixture.Token, doc.RootElement.GetProperty("shareToken").GetString());
    }

    [Fact]
    public async Task Path_traversal_in_file_name_is_rejected()
    {
        var request = new HttpRequestMessage(
            HttpMethod.Put,
            "/api/sessions/42000/clips/..%2F..%2Fevil.mp4")
        {
            Content = new ByteArrayContent("x"u8.ToArray()),
        };
        request.Headers.Add(ShareConstants.TokenHeader, ShareServerFixture.Token);

        var response = await _fixture.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Second_post_of_same_session_is_conflict()
    {
        var client = _fixture.Client;
        Assert.Equal(HttpStatusCode.Created, (await client.SendAsync(CreateManifestPost(client, 42_070))).StatusCode);

        // A session belongs to its creator — a second share of the same uid must not
        // silently overwrite the existing manifest.
        var second = await client.SendAsync(CreateManifestPost(client, 42_070));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Clip_put_without_owner_header_is_forbidden()
    {
        const ulong uid = 42_071;
        var client = _fixture.Client;
        await CreateTestSessionAsync(client, uid);

        var put = new HttpRequestMessage(HttpMethod.Put, $"/api/sessions/{uid}/clips/clip-1.mp4")
        {
            Content = new ByteArrayContent("x"u8.ToArray()),
        };
        put.Headers.Add(ShareConstants.TokenHeader, ShareServerFixture.Token);

        // A valid uploader (share token) but not the session owner.
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(put)).StatusCode);
    }

    [Fact]
    public async Task Clip_put_with_wrong_owner_header_is_forbidden()
    {
        const ulong uid = 42_072;
        var client = _fixture.Client;
        await CreateTestSessionAsync(client, uid);

        var put = new HttpRequestMessage(HttpMethod.Put, $"/api/sessions/{uid}/clips/clip-1.mp4")
        {
            Content = new ByteArrayContent("x"u8.ToArray()),
        };
        put.Headers.Add(ShareConstants.TokenHeader, ShareServerFixture.Token);
        put.Headers.Add(ShareConstants.OwnerHeader, "deadbeef");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(put)).StatusCode);
    }

    [Fact]
    public async Task Delete_without_owner_header_is_forbidden()
    {
        const ulong uid = 42_073;
        var client = _fixture.Client;
        await CreateTestSessionAsync(client, uid);

        var delete = new HttpRequestMessage(HttpMethod.Delete, $"/api/sessions/{uid}");
        delete.Headers.Add(ShareConstants.TokenHeader, ShareServerFixture.Token);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(delete)).StatusCode);
    }

    [Fact]
    public async Task Delete_removes_the_session()
    {
        const ulong uid = 42_040; // distinct from the 42_000 the upload-flow test leaves behind
        var client = _fixture.Client;
        var owner = await CreateTestSessionAsync(client, uid);

        var delete = new HttpRequestMessage(HttpMethod.Delete, $"/api/sessions/{uid}");
        delete.Headers.Add(ShareConstants.TokenHeader, ShareServerFixture.Token);
        delete.Headers.Add(ShareConstants.OwnerHeader, owner);
        var deleteResponse = await client.SendAsync(delete);
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        var page = await client.GetAsync($"/s/{uid}");
        Assert.Equal(HttpStatusCode.NotFound, page.StatusCode);
    }

    [Fact]
    public async Task Repost_of_a_session_whose_files_were_swept_creates_it_afresh()
    {
        // LOW-4: the retention sweep deletes a session's files without touching the
        // in-memory ownership/byte accounting. Re-posting the same game-session uid must
        // therefore re-create the session (201) instead of answering 409 forever.
        const ulong uid = 42_041;
        var client = _fixture.Client;
        var owner = await CreateTestSessionAsync(client, uid);

        // Simulate the sweep having cleaned the files off disk (manifest included).
        Assert.True(Directory.Exists(Path.Combine(_fixture.RootPath, uid.ToString())));
        Directory.Delete(Path.Combine(_fixture.RootPath, uid.ToString()), recursive: true);

        var repost = CreateManifestPost(client, uid);
        var repostResponse = await client.SendAsync(repost);

        Assert.Equal(HttpStatusCode.Created, repostResponse.StatusCode);
        // The stale owner secret was also dropped — the fresh one is usable for uploads.
        var newOwner = ReadOwnerSecret(await repostResponse.Content.ReadAsStringAsync());
        Assert.NotEqual(owner, newOwner);

        var put = new HttpRequestMessage(HttpMethod.Put, $"/api/sessions/{uid}/clips/clip-1.mp4")
        {
            Content = new ByteArrayContent([0x42, 0x43]),
        };
        put.Headers.Add(ShareConstants.TokenHeader, ShareServerFixture.Token);
        put.Headers.Add(ShareConstants.OwnerHeader, newOwner);
        Assert.Equal(HttpStatusCode.Created, (await client.SendAsync(put)).StatusCode);
    }

    [Fact]
    public async Task Chunked_upload_assembles_mp4_from_parts()
    {
        const ulong uid = 42_001;
        var client = _fixture.Client;
        var owner = await CreateTestSessionAsync(client, uid);

        // Two parts of 2 MB each, uploaded in order with part/parts query parameters.
        var part0 = new byte[2 * 1024 * 1024];
        part0.AsSpan().Fill(0x11);
        var part1 = new byte[2 * 1024 * 1024];
        part1.AsSpan().Fill(0x22);
        Assert.Equal(HttpStatusCode.Created, (await client.SendAsync(PutChunk(uid, owner, 0, 2, part0))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await client.SendAsync(PutChunk(uid, owner, 1, 2, part1))).StatusCode);

        // Parts are assembled in order and the scratch files are cleaned up.
        var video = await client.GetAsync($"/s/{uid}/clips/clip-1.mp4");
        Assert.Equal(HttpStatusCode.OK, video.StatusCode);
        var bytes = await video.Content.ReadAsByteArrayAsync();
        Assert.Equal(part0.Length + part1.Length, bytes.Length);
        Assert.Equal(0x11, bytes[0]);
        Assert.Equal(0x11, bytes[part0.Length - 1]);
        Assert.Equal(0x22, bytes[part0.Length]);
        Assert.Equal(0x22, bytes[^1]);
        Assert.Empty(Directory.GetFiles(Path.Combine(_fixture.RootPath, uid.ToString()), "*.part*"));
    }

    [Fact]
    public async Task Chunked_upload_with_a_single_part_matches_a_direct_upload()
    {
        const ulong uid = 42_002;
        var client = _fixture.Client;
        var owner = await CreateTestSessionAsync(client, uid);

        var put = PutChunk(uid, owner, 0, 1, "solo-part"u8.ToArray());
        Assert.Equal(HttpStatusCode.Created, (await client.SendAsync(put)).StatusCode);

        var video = await client.GetAsync($"/s/{uid}/clips/clip-1.mp4");
        Assert.Equal(HttpStatusCode.OK, video.StatusCode);
        Assert.Equal("solo-part", await video.Content.ReadAsStringAsync());
        Assert.Empty(Directory.GetFiles(Path.Combine(_fixture.RootPath, uid.ToString()), "*.part*"));
    }

    [Theory]
    [InlineData(42_030, "?part=2&parts=2")]    // part index out of range
    [InlineData(42_031, "?part=0&parts=0")]    // zero parts
    [InlineData(42_032, "?part=0&parts=5000")] // beyond the configured part-count cap
    [InlineData(42_033, "?part=0")]            // missing parts — not a chunk pair
    [InlineData(42_034, "?parts=2")]           // missing part
    [InlineData(42_035, "?part=x&parts=2")]    // non-numeric part
    public async Task Chunked_upload_rejects_invalid_part_range(ulong uid, string query)
    {
        var client = _fixture.Client;
        var owner = await CreateTestSessionAsync(client, uid);

        var put = new HttpRequestMessage(HttpMethod.Put, $"/api/sessions/{uid}/clips/clip-1.mp4{query}")
        {
            Content = new ByteArrayContent("x"u8.ToArray()),
        };
        put.Headers.Add(ShareConstants.TokenHeader, ShareServerFixture.Token);
        put.Headers.Add(ShareConstants.OwnerHeader, owner);

        var response = await client.SendAsync(put);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        // LOW-NOTE: the message is fixed (Results.BadRequest(string) JSON-quotes it) and the
        // attacker-controlled query values (part/parts) are NOT echoed into the body —
        // reflected-input defense. Asserting "does not contain" keeps this robust against
        // serialization detail changes while guarding the actual property.
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Invalid part range", body);
        Assert.DoesNotContain(query, body);
    }

    [Fact]
    public async Task Chunked_upload_missing_part_is_rejected_when_last_part_arrives()
    {
        const ulong uid = 42_004;
        var client = _fixture.Client;
        var owner = await CreateTestSessionAsync(client, uid);

        // The last part arrives first — the earlier part file has not been uploaded yet.
        var put = PutChunk(uid, owner, 1, 2, "last-part"u8.ToArray());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(put)).StatusCode);

        // No final file is produced from an incomplete set.
        Assert.False(File.Exists(Path.Combine(_fixture.RootPath, uid.ToString(), "clip-1.mp4")));
    }

    private static async Task<string> CreateTestSessionAsync(HttpClient client, ulong uid)
    {
        var post = new HttpRequestMessage(HttpMethod.Post, "/api/sessions")
        {
            Content = JsonContent.Create(SampleManifest(uid)),
        };
        post.Headers.Add(ShareConstants.TokenHeader, ShareServerFixture.Token);
        var response = await client.SendAsync(post);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return ReadOwnerSecret(await response.Content.ReadAsStringAsync());
    }

    private static HttpRequestMessage CreateManifestPost(HttpClient client, ulong uid)
    {
        var post = new HttpRequestMessage(HttpMethod.Post, "/api/sessions")
        {
            Content = JsonContent.Create(SampleManifest(uid)),
        };
        post.Headers.Add(ShareConstants.TokenHeader, ShareServerFixture.Token);
        return post;
    }

    private static HttpRequestMessage PutChunk(ulong uid, string ownerSecret, int part, int parts, byte[] body)
    {
        var put = new HttpRequestMessage(
            HttpMethod.Put,
            $"/api/sessions/{uid}/clips/clip-1.mp4?{ClipUpload.PartQuery}={part}&{ClipUpload.PartsQuery}={parts}")
        {
            Content = new ByteArrayContent(body),
        };
        put.Headers.Add(ShareConstants.TokenHeader, ShareServerFixture.Token);
        put.Headers.Add(ShareConstants.OwnerHeader, ownerSecret);
        return put;
    }

    private static string ReadOwnerSecret(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("ownerSecret").GetString()!;
    }

    private static ShareManifest SampleManifest(ulong uid = 42_000) => new(
        uid, "Spa", "Race",
        DateTimeOffset.Parse("2026-09-07T18:30:00Z", CultureInfo.InvariantCulture),
        3720, 3,
        [new ShareResultRow(1, "Player One", "McLaren", 44, 90_123, 3600.5, "Finished", 25f)],
        [new ShareClip("clip-1.mp4", 7, 3, null, "Player One", null, 2, 12.5)]);
}

/// <summary>One WebApplicationFactory per test class: temp share root + a known token.
/// Env vars are a config source AFTER appsettings.json, so they override the empty
/// defaults there (WebHostBuilder config callbacks would be shadowed by the JSON).</summary>
public sealed class ShareServerFixture : IDisposable
{
    public const string Token = "test-token";

    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _root;

    public ShareServerFixture()
    {
        _root = Path.Combine(Path.GetTempPath(), $"erc-share-test-{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable("Share__Token", Token);
        Environment.SetEnvironmentVariable("Share__RootPath", _root);
        // Deliberately NOT setting Share__DiscordClientSecret / TwitchClientSecret here:
        // the committed-appsettings removal (HIGH 1) must not leave the server dependent on
        // a secret that was in source control. The startup warning covers missing secrets.
        _factory = new WebApplicationFactory<Program>();
        // No auto-redirect: the Twitch callback tests assert the 302 + Location header
        // itself, and a following client would chase the redirect to a dead target.
        Client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    public HttpClient Client { get; }

    /// <summary>The temp root the test server writes to — lets tests assert on-disk state
    /// (e.g. that chunk scratch files are removed after assembly).</summary>
    public string RootPath => _root;

    public void Dispose()
    {
        Client.Dispose();
        _factory.Dispose();
        Environment.SetEnvironmentVariable("Share__Token", null);
        Environment.SetEnvironmentVariable("Share__RootPath", null);
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
