using System.Net.WebSockets;
using System.Security;
using System.Security.Cryptography;
using System.Text;

namespace ERCTelemetry.Core.Tts;

/// <summary>Thin Edge-TTS client for the readaloud websocket — the same service Edge's
/// "Read Aloud" uses. Synthesizes one short German alert into MP3 bytes. Returns null on
/// ANY failure (offline, auth, malformed response) so the app falls back to the local
/// Windows voice; the TTS layer is never mandatory. Headless-testable: the endpoint URL,
/// the speech.config/ssml frames and the binary-frame parsing are pure static methods
/// covered by unit tests — only the transport shell (ClientWebSocket) needs the network.</summary>
public sealed class EdgeTtsClient
{
    /// <summary>Readaloud output format. MP3 is what the canonical edge-tts client sends
    /// and what the endpoint is verified against (the older riff/WAV formats are not).</summary>
    public const string OutputFormat = "audio-24khz-48kbitrate-mono-mp3";

    /// <summary>Hard cap per utterance (connect + synthesize + receive). Offline, the
    /// connect fails fast with a WebSocketException; 15 s only guards pathological cases.</summary>
    private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Synthesizes <paramref name="text"/> into MP3 bytes with <paramref name="voice"/>,
    /// or null when the request failed or returned no audio. A 403 handshake (outdated
    /// <c>Sec-MS-GEC</c> token, e.g. after a clock drift) is retried once with the clock
    /// skew corrected.</summary>
    public async Task<byte[]?> SynthesizeAsync(string text, string voice, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(voice))
        {
            return null;
        }

        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var ws = new ClientWebSocket();
            ConfigureHeaders(ws);
            var connectionId = Guid.NewGuid().ToString("N"); // no dashes, like the reference client
            var uri = EdgeTtsAuth.BuildEndpoint(DateTimeOffset.UtcNow.ToUnixTimeSeconds(), connectionId);

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(ReceiveTimeout);
                var token = timeout.Token;

                await ws.ConnectAsync(uri, token).ConfigureAwait(false);
                await SendText(ws, BuildSpeechConfig(), token).ConfigureAwait(false);
                await SendText(ws, BuildSsml(connectionId, text, voice), token).ConfigureAwait(false);

                return await ReceiveAudioAsync(ws, token).ConfigureAwait(false);
            }
            catch (WebSocketException ex) when (attempt == 0 &&
                ex.Message.Contains("403", StringComparison.Ordinal) &&
                EdgeTtsAuth.TryAdjustClockSkew())
            {
                // Clock skew corrected — retry with the adjusted token.
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (WebSocketException)
            {
                return null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>Reads frames until <c>turn.end</c>, concatenating the audio payload of every
    /// binary frame into the complete MP3. Null when no audio arrived.</summary>
    private static async Task<byte[]?> ReceiveAudioAsync(ClientWebSocket ws, CancellationToken token)
    {
        var buffer = new byte[8192];
        var audio = new MemoryStream();
        while (true)
        {
            var message = new MemoryStream();
            WebSocketMessageType type;
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(buffer, token).ConfigureAwait(false);
                type = result.MessageType;
                message.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            if (type == WebSocketMessageType.Close)
            {
                break;
            }

            if (type == WebSocketMessageType.Binary)
            {
                AppendAudioPayload(audio, message.ToArray());
                continue;
            }

            // Text frame — turn end ends the stream; anything else (audio.metadata) is skipped.
            if (Encoding.UTF8.GetString(message.ToArray()).Contains("Path:turn.end", StringComparison.Ordinal))
            {
                break;
            }
        }

        return audio.Length > 0 ? audio.ToArray() : null;
    }

    /// <summary>Appends the audio payload of one binary websocket frame: 2-byte big-endian
    /// header length, then a header, then the actual audio bytes. Malformed frames are skipped.</summary>
    public static void AppendAudioPayload(MemoryStream audio, ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 2)
        {
            return;
        }

        var headerLength = (frame[0] << 8) | frame[1];
        var audioStart = headerLength + 2;
        if (audioStart > frame.Length)
        {
            return;
        }

        audio.Write(frame[audioStart..]);
    }

    /// <summary>The first text frame — the audio output-format handshake. Same JSON as the
    /// canonical client; metadata offsets are off (we only need the audio).</summary>
    public static string BuildSpeechConfig() =>
        Frames.SpeechConfig(OutputFormat);

    /// <summary>The synthesis request frame. <paramref name="requestId"/> is the dash-less
    /// connection id (the reference client reuses it as the per-turn request id).</summary>
    public static string BuildSsml(string requestId, string text, string voice) =>
        Frames.Ssml(requestId, text, voice);

    private static void ConfigureHeaders(ClientWebSocket ws)
    {
        ws.Options.SetRequestHeader("Pragma", "no-cache");
        ws.Options.SetRequestHeader("Cache-Control", "no-cache");
        ws.Options.SetRequestHeader("Origin", "chrome-extension://jdiccldimpdaibmpdkjnbmckianbfold");
        // A browser-ish UA is expected by the endpoint (like the community clients send).
        ws.Options.SetRequestHeader("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/143.0.0.0 Safari/537.36 Edg/143.0.0.0");
        ws.Options.SetRequestHeader("Accept-Language", "en-US,en;q=0.9");
        // A random MUID cookie emulates a browser session — the reference client sends it.
        ws.Options.SetRequestHeader("Cookie", $"muid={Convert.ToHexString(RandomNumberGenerator.GetBytes(16))};");
    }

    private static async Task SendText(ClientWebSocket ws, string message, CancellationToken token)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, token).ConfigureAwait(false);
    }
}

/// <summary>Pure frame builders — kept separate so every byte can be unit-tested against
/// the documented wire protocol without a socket.</summary>
internal static class Frames
{
    /// <summary><c>Path: speech.config</c> frame with the JSON body.</summary>
    public static string SpeechConfig(string outputFormat) =>
        $"X-Timestamp:{Timestamp()}\r\n" +
        "Content-Type:application/json; charset=utf-8\r\n" +
        "Path:speech.config\r\n" +
        "\r\n" +
        $"{{\"context\":{{\"synthesis\":{{\"audio\":{{\"metadataoptions\":{{\"sentenceBoundaryEnabled\":\"false\",\"wordBoundaryEnabled\":\"false\"}},\"outputFormat\":\"{outputFormat}\"}}}}}}}}";

    /// <summary><c>Path: ssml</c> frame — the voice + <c>prosody</c> + XML-escaped text.
    /// The trailing 'Z' on the timestamp mirrors the reference client ("Microsoft Edge bug" —
    /// it must not be stripped).</summary>
    public static string Ssml(string requestId, string text, string voice) =>
        $"X-RequestId:{requestId}\r\n" +
        "Content-Type:application/ssml+xml\r\n" +
        $"X-Timestamp:{Timestamp()}Z\r\n" +
        "Path:ssml\r\n" +
        "\r\n" +
        $"<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='{VoiceLang(voice)}'>" +
        $"<voice name='{voice}'><prosody pitch='+0Hz' rate='+10%' volume='+0%'>" +
        $"{SecurityElement.Escape(text)}" +
        "</prosody></voice></speak>";

    /// <summary>ISO-8601-ish UTC timestamp, e.g. <c>2026-09-07T12:00:00Z</c>. The server only
    /// uses it loosely — the exact format is not validated.</summary>
    private static string Timestamp() => DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");

    /// <summary>SSML xml:lang from the voice name — the full locale (first two hyphen
    /// segments), e.g. <c>de-DE-KatjaNeural</c> → <c>de-DE</c>, like the reference client.</summary>
    private static string VoiceLang(string voice)
    {
        var dash = voice.IndexOf('-');
        if (dash <= 0)
        {
            return "de-DE";
        }

        var second = voice.IndexOf('-', dash + 1);
        return second > 0 ? voice[..second] : voice[..dash];
    }
}
