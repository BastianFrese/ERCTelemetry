using System.IO.Compression;
using System.Text;

namespace ERCTelemetry.Core.Telemetry;

/// <summary>One recorded UDP datagram with its UTC capture time.</summary>
public readonly record struct RecordedFrame(long UtcTicks, byte[] Data);

/// <summary>Records raw telemetry datagrams to a gzipped <c>.f1rec</c> file so test
/// fixtures and replays use the game's original bytes. Format: magic "F1REC",
/// version u8, then frames of [utcTicks int64, length int32, bytes].</summary>
public sealed class PacketRecorder : IDisposable
{
    private const string Magic = "F1REC";
    private const byte FormatVersion = 1;

    private readonly object _gate = new();
    private FileStream? _file;
    private GZipStream? _gzip;

    /// <summary>Path of the file currently being written, or null when not recording.</summary>
    public string? ActivePath { get; private set; }

    public bool IsRecording => ActivePath is not null;

    public void Start(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        lock (_gate)
        {
            if (_file is not null)
            {
                throw new InvalidOperationException("Recording is already active.");
            }

            _file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            _gzip = new GZipStream(_file, CompressionLevel.Fastest, leaveOpen: true);
            _gzip.Write(Encoding.ASCII.GetBytes(Magic));
            _gzip.WriteByte(FormatVersion);
            ActivePath = path;
        }
    }

    /// <summary>Appends one datagram. Called on the UDP receive thread; cheap (buffered).</summary>
    public void Record(ReadOnlySpan<byte> data)
    {
        lock (_gate)
        {
            if (_gzip is null)
            {
                return; // not recording: drop silently rather than throw on the hot path
            }

            _gzip.Write(BitConverter.GetBytes(DateTime.UtcNow.Ticks));
            _gzip.Write(BitConverter.GetBytes(data.Length));
            _gzip.Write(data);
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _gzip?.Dispose();
            _gzip = null;
            _file?.Dispose();
            _file = null;
            ActivePath = null;
        }
    }

    public void Dispose() => Stop();

    /// <summary>Reads all frames from a recording. The whole file must be a complete,
    /// well-formed <c>.f1rec</c> stream; a truncated final frame (crash mid-record) is ignored.</summary>
    public static IReadOnlyList<RecordedFrame> ReadFrames(string path)
    {
        using var file = File.OpenRead(path);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new BinaryReader(gzip);

        if (Encoding.ASCII.GetString(reader.ReadBytes(Magic.Length)) != Magic)
        {
            throw new InvalidDataException($"Not an ERCTelemetry recording: {path}");
        }

        var version = reader.ReadByte();
        if (version != FormatVersion)
        {
            throw new InvalidDataException($"Unsupported recording version {version}: {path}");
        }

        var frames = new List<RecordedFrame>();
        while (TryReadFrame(reader, out var frame))
        {
            frames.Add(frame);
        }

        return frames;
    }

    private static bool TryReadFrame(BinaryReader reader, out RecordedFrame frame)
    {
        frame = default;
        try
        {
            var ticks = reader.ReadInt64();
            var length = reader.ReadInt32();
            if (length < 0 || length > 1_000_000)
            {
                return false;
            }

            var data = reader.ReadBytes(length);
            if (data.Length != length)
            {
                return false; // truncated tail from an interrupted recording
            }

            frame = new RecordedFrame(ticks, data);
            return true;
        }
        catch (EndOfStreamException)
        {
            return false;
        }
    }
}