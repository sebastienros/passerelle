using System.IO.Pipelines;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;

namespace Passerelle;

internal sealed class RelayResponse(RelayRequest transport, RequestLifetime lifetime) : IHttpResponseFeature
{
    private readonly Stack<(Func<object, Task> Callback, object State)> _starting = new();
    private readonly Stack<(Func<object, Task> Callback, object State)> _completed = new();
    private IHeaderDictionary _headers = new HeaderDictionary();
    private int _statusCode = StatusCodes.Status200OK;
    private string? _reasonPhrase;
    private bool _startAttempted;
    private Task? _close;

    public int StatusCode { get => _statusCode; set { ThrowIfStarted(); _statusCode = value; } }
    public string? ReasonPhrase { get => _reasonPhrase; set { ThrowIfStarted(); _reasonPhrase = value; } }
    public IHeaderDictionary Headers { get => _headers; set { ThrowIfStarted(); _headers = value; } }
    public Stream Body { get; set; } = Stream.Null;
    public bool HasStarted { get; private set; }

    public bool CanWriteBody => !HttpMethods.IsHead(transport.Method) && StatusCode >= 200 &&
        StatusCode is not (204 or 205 or 304);

    public void OnStarting(Func<object, Task> callback, object state)
    {
        ThrowIfStarted();
        _starting.Push((callback, state));
    }

    public void OnCompleted(Func<object, Task> callback, object state) => _completed.Push((callback, state));

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (HasStarted)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (_startAttempted)
        {
            throw new InvalidOperationException("The Relay response could not be started.");
        }

        _startAttempted = true;
        while (_starting.TryPop(out var entry))
        {
            await entry.Callback(entry.State).ConfigureAwait(false);
        }

        lifetime.Token.ThrowIfCancellationRequested();
        // Relay owns HTTP framing. Do not forward app-provided transport framing headers.
        var wireHeaders = new HeaderDictionary();
        foreach (var (name, value) in Headers)
        {
            if (!name.Equals("Connection", StringComparison.OrdinalIgnoreCase)
                && !name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
                && !(StatusCode is < 200 or 204 && name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)))
            {
                wireHeaders[name] = value;
            }
        }

        transport.Start(StatusCode, ReasonPhrase, wireHeaders);
        if (Headers is HeaderDictionary dictionary)
        {
            dictionary.IsReadOnly = true;
        }

        HasStarted = true;
    }

    public Task CloseAsync() => _close ??= transport.CloseAsync();

    public async Task RunCompletedAsync(ILogger logger)
    {
        while (_completed.TryPop(out var entry))
        {
            try
            {
                await entry.Callback(entry.State).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                logger.LogWarning("Relay response completion callback failed ({ErrorType}).", error.GetType().Name);
            }
        }
    }

    private void ThrowIfStarted()
    {
        if (HasStarted)
        {
            throw new InvalidOperationException("The Relay response has already started.");
        }
    }
}

internal sealed class RelayResponseBody : StreamResponseBodyFeature, IHttpResponseBodyFeature
{
    private readonly RelayResponse _response;
    private readonly ResponseStream _stream;
    private PipeWriter? _writer;
    private Task? _complete;

    public RelayResponseBody(RelayResponse response, Stream output, RequestLifetime lifetime)
        : base(new ResponseStream(response, output, lifetime))
    {
        _response = response;
        _stream = (ResponseStream)Stream;
        response.Body = Stream;
    }

    PipeWriter IHttpResponseBodyFeature.Writer =>
        _writer ??= PipeWriter.Create(Stream, new(leaveOpen: true));

    public override Task StartAsync(CancellationToken cancellationToken = default) =>
        Stream.FlushAsync(cancellationToken);

    public override Task CompleteAsync() => _complete ??= CompleteCoreAsync();

    private async Task CompleteCoreAsync()
    {
        await StartAsync().ConfigureAwait(false);
        if (_writer is not null)
        {
            // Completing a StreamPipeWriter flushes any advanced bytes to the underlying stream.
            await _writer.CompleteAsync().ConfigureAwait(false);
        }

        _stream.Finish();
        await _response.CloseAsync().ConfigureAwait(false);
    }

    public ValueTask DiscardPendingWritesAsync() => _writer is null || _complete?.IsCompletedSuccessfully == true
        ? ValueTask.CompletedTask
        : _writer.CompleteAsync(new InvalidOperationException("The Relay response did not complete."));

    private sealed class ResponseStream(RelayResponse response, Stream output, RequestLifetime lifetime) : Stream
    {
        private long _written;
        private bool _finished;

        public void Finish()
        {
            _finished = true;
            if (response.CanWriteBody && response.Headers.ContentLength is long expected && _written != expected)
            {
                throw new InvalidOperationException("The Relay response body does not match Content-Length.");
            }
        }

        public override void Flush() => FlushAsync(CancellationToken.None).GetAwaiter().GetResult();

        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            if (_finished)
            {
                return;
            }

            using var linked = lifetime.Link(cancellationToken);
            var token = linked?.Token ?? lifetime.Token;
            token.ThrowIfCancellationRequested();
            await response.StartAsync(token).ConfigureAwait(false);
            try
            {
                await output.FlushAsync(token).ConfigureAwait(false);
            }
            catch (Exception error) when (RelayRequest.IsIoFailure(error))
            {
                lifetime.Abort();
                throw;
            }
        }

        public override void Write(byte[] buffer, int offset, int count) =>
            WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_finished)
            {
                throw new InvalidOperationException("The Relay response has completed.");
            }

            using var linked = lifetime.Link(cancellationToken);
            var token = linked?.Token ?? lifetime.Token;
            token.ThrowIfCancellationRequested();
            await response.StartAsync(token).ConfigureAwait(false);
            if (!response.CanWriteBody || buffer.IsEmpty)
            {
                return;
            }

            if (response.Headers.ContentLength is long expected && buffer.Length > expected - _written)
            {
                throw new InvalidOperationException("The Relay response body exceeds Content-Length.");
            }

            try
            {
                await output.WriteAsync(buffer, token).ConfigureAwait(false);
                _written += buffer.Length;
            }
            catch (Exception error) when (RelayRequest.IsIoFailure(error))
            {
                lifetime.Abort();
                throw;
            }
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
