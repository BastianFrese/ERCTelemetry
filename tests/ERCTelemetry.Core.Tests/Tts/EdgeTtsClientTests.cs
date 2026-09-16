using ERCTelemetry.Core.Tts;
using Xunit;

namespace ERCTelemetry.Core.Tests.Tts;

/// <summary>Pure builders of the Edge-TTS wire protocol (speech.config, ssml) and the
/// binary-frame → audio parser. No live websocket — every frame is reconstructed and
/// asserted byte-for-byte where deterministic.</summary>
public sealed class EdgeTtsClientTests
{
    [Fact]
    public void BuildSpeechConfig_requests_the_output_format()
    {
        var frame = EdgeTtsClient.BuildSpeechConfig();

        Assert.Contains("Path:speech.config", frame);
        Assert.Contains("Content-Type:application/json; charset=utf-8", frame);
        Assert.Contains($"\"outputFormat\":\"{EdgeTtsClient.OutputFormat}\"", frame);
        Assert.StartsWith("X-Timestamp:", frame);
    }

    [Fact]
    public void BuildSsml_contains_voice_prosody_and_escaped_text()
    {
        const string text = "Box, Box, Reifen sind durch!";
        var frame = EdgeTtsClient.BuildSsml("req1", text, "de-DE-KatjaNeural");

        Assert.Contains("Path:ssml", frame);
        Assert.Contains("Content-Type:application/ssml+xml", frame);
        Assert.Contains("X-RequestId:req1", frame);
        Assert.Contains($"<voice name='de-DE-KatjaNeural'><prosody pitch='+0Hz' rate='+10%' volume='+0%'>", frame);
        Assert.Contains(text, frame);
        Assert.EndsWith("</prosody></voice></speak>", frame);
    }

    [Fact]
    public void BuildSsml_uses_the_full_locale_from_the_voice_name()
    {
        Assert.Contains("xml:lang='de-DE'", EdgeTtsClient.BuildSsml("r", "x", "de-DE-KatjaNeural"));
        Assert.Contains("xml:lang='de-AT'", EdgeTtsClient.BuildSsml("r", "x", "de-AT-JonasNeural"));
    }

    [Fact]
    public void BuildSsml_xml_escapes_special_characters()
    {
        var frame = EdgeTtsClient.BuildSsml("r", "Druck von hinten & <mehr>", "de-DE-KatjaNeural");

        Assert.DoesNotContain("hinten & <", frame);
        Assert.Contains("hinten &amp; &lt;mehr&gt;", frame);
    }

    [Fact]
    public void BuildSsml_timestamp_keeps_the_trailing_z()
    {
        // The canonical client deliberately appends 'Z' ("Microsoft Edge bug") — must not
        // be stripped. The separator proves the Z sits at the end of X-Timestamp.
        Assert.Contains("Z\r\nPath:ssml", EdgeTtsClient.BuildSsml("r", "x", "de-DE-KatjaNeural"));
    }

    [Fact]
    public void AppendAudioPayload_skips_the_header_and_writes_audio_bytes()
    {
        // 2-byte big-endian header length = 3 → bytes [..2+3] are the header, rest is audio.
        var frame = new byte[] { 0x00, 0x03, 0x01, 0x02, 0x03, 0xAA, 0xBB };
        var audio = new MemoryStream();

        EdgeTtsClient.AppendAudioPayload(audio, frame);

        Assert.Equal(new byte[] { 0xAA, 0xBB }, audio.ToArray());
    }

    [Fact]
    public void AppendAudioPayload_concatenates_frames_in_order()
    {
        var audio = new MemoryStream();
        EdgeTtsClient.AppendAudioPayload(audio, new byte[] { 0x00, 0x01, 0x09, 0x10 });
        EdgeTtsClient.AppendAudioPayload(audio, new byte[] { 0x00, 0x00, 0x20 });

        Assert.Equal(new byte[] { 0x10, 0x20 }, audio.ToArray());
    }

    [Fact]
    public void AppendAudioPayload_ignores_malformed_frames()
    {
        var audio = new MemoryStream();

        EdgeTtsClient.AppendAudioPayload(audio, new byte[] { 0x00 });            // too short
        EdgeTtsClient.AppendAudioPayload(audio, new byte[] { 0xFF, 0xFF, 0x01 }); // header longer than frame
        EdgeTtsClient.AppendAudioPayload(audio, Array.Empty<byte>());

        Assert.Empty(audio.ToArray());
    }
}
