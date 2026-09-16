using System.Net;
using System.Security.Cryptography;
using System.Text;
using ERCTelemetry.Core.Update;
using Xunit;

namespace ERCTelemetry.Core.Tests;

public class UpdateManifestParserTests
{
    private const string ValidSha256 = "AABB00112233445566778899AABBCCDDEEFF00112233445566778899AABBCCDD";
    private const string ValidJson =
        """{"version":"1.0.1","publishedUtc":"2026-09-03T02:00:00Z","sha256":"AABB00112233445566778899AABBCCDDEEFF00112233445566778899AABBCCDD","fileName":"ERCTelemetry-Setup.exe"}""";

    [Fact]
    public void Parse_ValidManifest_ReturnsAllFields()
    {
        var manifest = UpdateManifestParser.Parse(ValidJson);

        Assert.NotNull(manifest);
        Assert.Equal("1.0.1", manifest.Version);
        Assert.Equal("2026-09-03T02:00:00Z", manifest.PublishedUtc);
        Assert.Equal(64, manifest.Sha256.Length);
        Assert.Equal("ERCTelemetry-Setup.exe", manifest.FileName);
    }

    [Fact]
    public void Parse_MissingFileName_FallsBackToDefault()
    {
        var manifest = UpdateManifestParser.Parse($$"""{"version":"1.1.0","sha256":"{{ValidSha256}}"}""");

        Assert.NotNull(manifest);
        Assert.Equal(UpdateManifestParser.DefaultFileName, manifest.FileName);
        Assert.Equal(string.Empty, manifest.PublishedUtc);
    }

    [Fact]
    public void Parse_UnparseableVersion_ReturnsNull()
    {
        Assert.Null(UpdateManifestParser.Parse("""{"version":"not-a-version"}"""));
    }

    [Theory]
    [InlineData("AA")] // too short
    [InlineData("AABB00112233445566778899AABBCCDDEEFF00112233445566778899AABBCCD")] // 63 chars
    [InlineData("AABB00112233445566778899AABBCCDDEEFF00112233445566778899AABBCCDDG")] // 65 chars
    [InlineData("AABB00112233445566778899AABBCCDDEEFF00112233445566778899AABBCCD!")] // non-hex
    [InlineData("")] // empty
    public void Parse_InvalidSha256_ReturnsNull(string sha256)
    {
        Assert.Null(UpdateManifestParser.Parse($$"""{"version":"1.1.0","sha256":"{{sha256}}"}"""));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{broken")]
    [InlineData("[1,2,3]")]
    [InlineData("{}")]
    public void Parse_MalformedOrEmpty_ReturnsNull(string? json) =>
        Assert.Null(UpdateManifestParser.Parse(json));
}

public class UpdateEvaluatorTests
{
    private static UpdateManifest Manifest(string version) =>
        new(version, "2026-09-03T02:00:00Z", "AA", "ERCTelemetry-Setup.exe");

    [Fact]
    public void Evaluate_NewerPublishedVersion_ReturnsAvailable()
    {
        var decision = UpdateEvaluator.Evaluate(new Version(1, 0, 0), Manifest("1.0.1"));

        Assert.Equal(UpdateDecision.Available, decision);
    }

    [Fact]
    public void Evaluate_ThreePartBeatsTwoPart_ReturnsAvailable()
    {
        var decision = UpdateEvaluator.Evaluate(new Version(1, 0), Manifest("1.0.1"));

        Assert.Equal(UpdateDecision.Available, decision);
    }

    [Fact]
    public void Evaluate_SameVersion_ReturnsSameOrOlder()
    {
        var decision = UpdateEvaluator.Evaluate(new Version(1, 0, 1), Manifest("1.0.1"));

        Assert.Equal(UpdateDecision.SameOrOlder, decision);
    }

    [Fact]
    public void Evaluate_RunningVersionNewer_ReturnsSameOrOlder()
    {
        var decision = UpdateEvaluator.Evaluate(new Version(1, 1, 0), Manifest("1.0.1"));

        Assert.Equal(UpdateDecision.SameOrOlder, decision);
    }

    [Fact]
    public void Evaluate_MissingCurrentOrBadManifest_ReturnsSameOrOlder()
    {
        Assert.Equal(UpdateDecision.SameOrOlder, UpdateEvaluator.Evaluate(null, Manifest("9.9.9")));
        Assert.Equal(UpdateDecision.SameOrOlder, UpdateEvaluator.Evaluate(new Version(1, 0, 0), null));
        Assert.Equal(UpdateDecision.SameOrOlder, UpdateEvaluator.Evaluate(new Version(1, 0, 0), Manifest("junk")));
    }
}

public class UpdateLogSectionTests
{
    private const string Log =
        """
        ## 0.4.2 — Beta (2026-09-07)

        ### Neu
        - Weiterleitung an andere Apps.

        ## 0.4.1 — Beta (2026-09-07)

        ### Behoben
        - History-Fixes.
        """;

    [Fact]
    public void Extract_ExistingVersion_ReturnsSectionUpToNextHeading()
    {
        var section = UpdateLogSection.Extract(Log, "0.4.2");

        Assert.NotNull(section);
        Assert.Contains("Weiterleitung an andere Apps", section);
        Assert.DoesNotContain("History-Fixes", section);
    }

    [Fact]
    public void Extract_LastVersion_ReturnsSectionToEnd()
    {
        var section = UpdateLogSection.Extract(Log, "0.4.1");

        Assert.NotNull(section);
        Assert.Contains("History-Fixes", section);
    }

    [Fact]
    public void Extract_MissingVersion_ReturnsNull()
    {
        Assert.Null(UpdateLogSection.Extract(Log, "9.9.9"));
    }

    [Fact]
    public void Extract_VersionPrefix_DoesNotMatchLongerVersion()
    {
        var log = "## 0.4.20 — Beta (2026-09-07)\n\n### Neu\n- Zwanzig.\n";

        Assert.Null(UpdateLogSection.Extract(log, "0.4.2"));
    }

    [Fact]
    public void Extract_HandlesCrLf()
    {
        var section = UpdateLogSection.Extract(Log.Replace("\n", "\r\n"), "0.4.2");

        Assert.NotNull(section);
        Assert.Contains("Weiterleitung an andere Apps", section);
    }
}

public class UpdateFileManifestParserTests
{
    private const string Sha = "AABB00112233445566778899AABBCCDDEEFF00112233445566778899AABBCCDD";
    private const string Sha2 = "CCDD00112233445566778899AABBCCDDEEFF00112233445566778899AABBCCDD";

    [Fact]
    public void Parse_ValidManifest_ReturnsAllEntries()
    {
        var manifest = UpdateFileManifestParser.Parse($$"""
            {"files":[
              {"path":"ERCTelemetry.dll","size":123456,"sha256":"{{Sha}}"},
              {"path":"wwwroot/index.html","size":42,"sha256":"{{Sha2}}"}
            ]}
            """);

        Assert.NotNull(manifest);
        Assert.Equal(2, manifest.Files.Count);
        Assert.Equal("ERCTelemetry.dll", manifest.Files[0].Path);
        Assert.Equal(123456, manifest.Files[0].Size);
        Assert.Equal(Sha, manifest.Files[0].Sha256);
        Assert.Equal("wwwroot/index.html", manifest.Files[1].Path);
    }

    [Fact]
    public void Parse_EntryWithInvalidSha256_IsSkipped()
    {
        var manifest = UpdateFileManifestParser.Parse($$"""
            {"files":[
              {"path":"bad.dll","size":1,"sha256":"AA"},
              {"path":"good.dll","size":2,"sha256":"{{Sha}}"}
            ]}
            """);

        Assert.NotNull(manifest);
        Assert.Single(manifest.Files);
        Assert.Equal("good.dll", manifest.Files[0].Path);
    }

    [Fact]
    public void Parse_AllEntriesInvalid_ReturnsNull()
    {
        Assert.Null(UpdateFileManifestParser.Parse("""{"files":[{"path":"x","size":1,"sha256":"AA"}]}"""));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{broken")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"files":[]}""")]
    public void Parse_MalformedOrEmpty_ReturnsNull(string? json) =>
        Assert.Null(UpdateFileManifestParser.Parse(json));
}

public class UpdateFileComparerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "erc-comparer-" + Guid.NewGuid().ToString("N"));

    public UpdateFileComparerTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // best effort
        }
    }

    private static string Sha256Of(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private static UpdateFileEntry Entry(string path, string content) =>
        new(path, content.Length, Sha256Of(content));

    [Fact]
    public void FindChanged_IdenticalFile_ReturnsEmpty()
    {
        var content = "hello";
        File.WriteAllText(Path.Combine(_root, "a.txt"), content);
        var manifest = new UpdateFileManifest(new[] { Entry("a.txt", content) });

        Assert.Empty(UpdateFileComparer.FindChanged(manifest, _root));
    }

    [Fact]
    public void FindChanged_MissingFile_ReturnsEntry()
    {
        var manifest = new UpdateFileManifest(new[] { Entry("missing.txt", "x") });

        var changed = UpdateFileComparer.FindChanged(manifest, _root);

        Assert.Single(changed);
        Assert.Equal("missing.txt", changed[0].Path);
    }

    [Fact]
    public void FindChanged_DifferentContent_ReturnsEntry()
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), "old");
        var manifest = new UpdateFileManifest(new[] { Entry("a.txt", "new") });

        var changed = UpdateFileComparer.FindChanged(manifest, _root);

        Assert.Single(changed);
        Assert.Equal("a.txt", changed[0].Path);
    }

    [Fact]
    public void FindChanged_SubdirectoryFile_ResolvesInsideRoot()
    {
        var content = "nested";
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        File.WriteAllText(Path.Combine(_root, "sub", "b.txt"), content);
        var manifest = new UpdateFileManifest(new[] { Entry("sub/b.txt", content) });

        Assert.Empty(UpdateFileComparer.FindChanged(manifest, _root));
    }

    [Fact]
    public void FindChanged_PathTraversal_IsSkipped()
    {
        var manifest = new UpdateFileManifest(new[] { Entry("../outside.txt", "x") });

        Assert.Empty(UpdateFileComparer.FindChanged(manifest, _root));
    }

    [Fact]
    public void ResolveLocalPath_AbsolutePath_ReturnsNull()
    {
        Assert.Null(UpdateFileComparer.ResolveLocalPath(_root, @"C:\Windows\system32\evil.dll"));
    }
}

public class UpdateDownloaderTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(_responder(request));
    }

    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
        new(new FakeHandler(responder));

    private static string Sha256Of(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), "erc-download-" + Guid.NewGuid().ToString("N") + ".bin");

    /// <summary>Synchronous IProgress — records every Report call in order. Progress&lt;T&gt;
    /// posts asynchronously and coalesces reports, so a fast download can deliver only the
    /// final value and make the intermediate-progress assertion flaky.</summary>
    private sealed class SyncProgress : IProgress<double>
    {
        public List<double> Values { get; } = new();

        public void Report(double value) => Values.Add(value);
    }

    [Fact]
    public async Task DownloadFileAsync_Success_MovesFileAndCleansPart()
    {
        var content = "hello world";
        var target = TempPath();
        var client = Client(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(content),
        });

        var result = await new UpdateDownloader(client).DownloadFileAsync("http://x/file", target, Sha256Of(content));

        Assert.Equal(target, result);
        Assert.Equal(content, await File.ReadAllTextAsync(target));
        Assert.False(File.Exists(target + ".part"));
        File.Delete(target);
    }

    [Fact]
    public async Task DownloadFileAsync_HashMismatch_ThrowsAndCleansPart()
    {
        var target = TempPath();
        var client = Client(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("hello"),
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new UpdateDownloader(client).DownloadFileAsync("http://x/file", target, "AA"));

        Assert.Contains("SHA256", ex.Message);
        Assert.False(File.Exists(target));
        Assert.False(File.Exists(target + ".part"));
    }

    [Fact]
    public async Task DownloadFileAsync_HttpError_ThrowsAndCleansPart()
    {
        var target = TempPath();
        var client = Client(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        await Assert.ThrowsAsync<HttpRequestException>(
            () => new UpdateDownloader(client).DownloadFileAsync("http://x/file", target, "AA"));

        Assert.False(File.Exists(target));
        Assert.False(File.Exists(target + ".part"));
    }

    [Fact]
    public async Task DownloadFileAsync_ReportsProgress()
    {
        var content = new string('x', 200_000);
        var target = TempPath();
        var client = Client(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(content),
        });
        var progress = new SyncProgress();

        await new UpdateDownloader(client).DownloadFileAsync(
            "http://x/file", target, Sha256Of(content), progress);

        Assert.Contains(progress.Values, p => p > 0 && p < 100);
        Assert.Equal(100, progress.Values[^1]);
        File.Delete(target);
    }

    [Fact]
    public async Task FetchFileManifestAsync_ValidJson_ReturnsManifest()
    {
        var json = """{"files":[{"path":"a.dll","size":1,"sha256":"AABB00112233445566778899AABBCCDDEEFF00112233445566778899AABBCCDD"}]}""";
        var client = Client(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json),
        });

        var manifest = await new UpdateDownloader(client).FetchFileManifestAsync("http://x");

        Assert.NotNull(manifest);
        Assert.Single(manifest.Files);
    }

    [Fact]
    public async Task FetchFileManifestAsync_HttpError_ReturnsNull()
    {
        var client = Client(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        Assert.Null(await new UpdateDownloader(client).FetchFileManifestAsync("http://x"));
    }

    [Fact]
    public async Task FetchFileManifestAsync_MalformedJson_ReturnsNull()
    {
        var client = Client(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{broken"),
        });

        Assert.Null(await new UpdateDownloader(client).FetchFileManifestAsync("http://x"));
    }
}