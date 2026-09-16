using System.IO;

namespace ERCTelemetry.App.Share;

/// <summary>Read-only bounded view of a seekable source stream (a clip file): presents
/// exactly <paramref name="count"/> bytes starting at <paramref name="start"/> as the
/// stream's Length, so an HttpClient upload sends precisely one clip part with the correct
/// Content-Length. The source stream is owned by the caller and not disposed here.</summary>
public sealed class ClipSubStream : Stream
{
    private readonly Stream _source;
    private readonly long _start;
    private long _position;

    public ClipSubStream(Stream source, long start, long count)
    {
        _source = source;
        _start = start;
        Length = count;
        if (source.CanSeek)
        {
            source.Position = start;
        }
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length { get; }

    public override long Position
    {
        get => _position;
        set
        {
            if (value < 0 || value > Length)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }
            _position = value;
            if (_source.CanSeek)
            {
                _source.Position = _start + value;
            }
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var remaining = Length - _position;
        if (remaining <= 0)
        {
            return 0;
        }
        var read = _source.Read(buffer, offset, (int)Math.Min(count, remaining));
        _position += read;
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        Position = target;
        return target;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
