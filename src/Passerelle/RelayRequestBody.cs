using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Passerelle;

internal sealed class RelayRequestBody(Stream input, long? contentLength, long? limit, RequestLifetime lifetime)
    : Stream, IHttpMaxRequestBodySizeFeature
{
    private long _read;
    public bool IsReadOnly { get; private set; }

    public long? MaxRequestBodySize
    {
        get => limit;
        set
        {
            if (IsReadOnly)
            {
                throw new InvalidOperationException("The request body has already been read.");
            }

            ArgumentOutOfRangeException.ThrowIfNegative(value ?? 0);
            limit = value;
        }
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        lifetime.Token.ThrowIfCancellationRequested();
        var count = GetReadSize(buffer.Length);
        int read;
        try
        {
            read = input.Read(buffer[..count]);
        }
        catch (Exception error) when (RelayRequest.IsIoFailure(error))
        {
            lifetime.Abort();
            throw;
        }

        return Count(read);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        using var linked = lifetime.Link(cancellationToken);
        var token = linked?.Token ?? lifetime.Token;
        token.ThrowIfCancellationRequested();
        var count = GetReadSize(buffer.Length);
        int read;
        try
        {
            read = await input.ReadAsync(buffer[..count], token).ConfigureAwait(false);
        }
        catch (Exception error) when (RelayRequest.IsIoFailure(error))
        {
            lifetime.Abort();
            throw;
        }

        return Count(read);
    }

    private int GetReadSize(int size)
    {
        if (size == 0)
        {
            return 0;
        }

        IsReadOnly = true;
        if (limit is not long max)
        {
            return size;
        }

        if (contentLength > max || _read > max)
        {
            throw TooLarge();
        }

        // Read up to one byte past the limit to distinguish an exact-length body from overflow.
        var remaining = max - _read;
        return remaining >= size ? size : (int)remaining + 1;
    }

    private int Count(int read)
    {
        _read += read;
        if (limit is long max && _read > max)
        {
            throw TooLarge();
        }

        return read;
    }

    private static BadHttpRequestException TooLarge() =>
        new("The Relay request body exceeded its size limit.", StatusCodes.Status413PayloadTooLarge);

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
