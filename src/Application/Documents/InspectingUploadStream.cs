using System.Security.Cryptography;

namespace AISupportOps.Application.Documents;

/// <summary>
/// Read-only pass-through stream used while copying an upload to storage. In a single pass it
/// replays the already-read header bytes, computes SHA-256, counts bytes, and aborts as soon
/// as the size limit is exceeded — so the file is never fully buffered in memory and a lying
/// Content-Length header cannot bypass the limit.
/// </summary>
internal sealed class InspectingUploadStream(ReadOnlyMemory<byte> header, Stream inner, long maxBytes) : Stream
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private int _headerPosition;

    public long BytesRead { get; private set; }

    public string GetHashHex() => Convert.ToHexStringLower(_hash.GetHashAndReset());

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int read;
        if (_headerPosition < header.Length)
        {
            read = Math.Min(buffer.Length, header.Length - _headerPosition);
            header.Slice(_headerPosition, read).CopyTo(buffer);
            _headerPosition += read;
        }
        else
        {
            read = await inner.ReadAsync(buffer, cancellationToken);
        }

        Track(buffer.Span[..read]);
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    private void Track(ReadOnlySpan<byte> data)
    {
        BytesRead += data.Length;
        if (BytesRead > maxBytes)
        {
            throw new FileTooLargeException();
        }

        _hash.AppendData(data);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hash.Dispose();
        }

        base.Dispose(disposing);
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => BytesRead;
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

internal sealed class FileTooLargeException : Exception;
