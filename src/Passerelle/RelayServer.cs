using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.Azure.Relay;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Passerelle;

internal sealed class RelayServer : IDisposable
{
    private readonly IOptions<RelayOptions> _options;
    private readonly ILogger<RelayServer> _logger;
    private readonly Func<RelayOptions, IRelayListener> _createListener;
    private readonly CancellationTokenSource _abort = new();
    private readonly object _gate = new();
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IRelayListener? _listener;
    private bool _stopping;
    private int _active;
    private Task? _stopTask;

    public RelayServer(IOptions<RelayOptions> options, ILogger<RelayServer> logger)
        : this(options, logger, o => new AzureRelayListener(o.CreateListener())) { }

    internal RelayServer(IOptions<RelayOptions> options, ILogger<RelayServer> logger,
        Func<RelayOptions, IRelayListener> createListener)
        => (_options, _logger, _createListener) = (options, logger, createListener);

    public async Task StartAsync<T>(IHttpApplication<T> application, CancellationToken cancellationToken) where T : notnull
    {
        var options = _options.Value;
        IRelayListener listener;
        lock (_gate)
        {
            if (_stopping || _listener is not null)
            {
                throw new InvalidOperationException("The Relay server cannot be restarted.");
            }

            _listener = listener = _createListener(options);
        }

        var dispatcher = new RelayDispatcher<T>(application, listener.Address, options.MaxRequestBodySize, _logger);
        try
        {
            await listener.OpenAsync(request => Dispatch(dispatcher, request), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            await StopAsync(new CancellationToken(true)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            cancellationToken.ThrowIfCancellationRequested();
            // Do not let the web host log exception details that may contain credentials.
            throw new InvalidOperationException($"Could not open the Azure Relay listener ({error.GetType().Name}).");
        }
    }

    private void Dispatch<T>(RelayDispatcher<T> dispatcher, RelayRequest request) where T : notnull
    {
        CancellationToken? abort = null;
        lock (_gate)
        {
            if (!_stopping)
            {
                abort = _abort.Token;
                _active++;
            }
        }

        if (abort is not { } token)
        {
            _ = RejectAsync(request);
            return;
        }

        // The SDK invokes callbacks on its receive loop; never run application code on that loop.
        _ = Task.Run(async () =>
        {
            try
            {
                await dispatcher.ProcessAsync(request, token).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                _logger.LogError("Relay request processing failed ({ErrorType}).", error.GetType().Name);
            }
            finally
            {
                lock (_gate)
                {
                    if (--_active == 0 && _stopping)
                    {
                        _drained.TrySetResult();
                    }
                }
            }
        });
    }

    private async Task RejectAsync(RelayRequest request)
    {
        try
        {
            request.Start(503, null, new Microsoft.AspNetCore.Http.HeaderDictionary());
            await request.CloseAsync().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            _logger.LogDebug("Could not reject a Relay request during shutdown ({ErrorType}).", error.GetType().Name);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _stopping = true;
            if (_active == 0)
            {
                _drained.TrySetResult();
            }

            return _stopTask ??= Task.Run(() => StopCoreAsync(cancellationToken));
        }
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _drained.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _abort.CancelAsync().ConfigureAwait(false);
            }
            catch (AggregateException error)
            {
                _logger.LogWarning("Relay request cancellation callback failed ({ErrorType}).", error.GetType().Name);
            }
        }

        if (_listener is not null)
        {
            try
            {
                // Cleanup still needs a bounded token after the host's drain deadline expires.
                using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _listener.CloseAsync(closeTimeout.Token).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                throw new InvalidOperationException($"Could not close the Azure Relay listener ({error.GetType().Name}).");
            }
        }
    }

    public void Dispose()
    {
        try
        {
            StopAsync(new CancellationToken(true)).GetAwaiter().GetResult();
        }
        finally
        {
            _abort.Dispose();
        }
    }
}
