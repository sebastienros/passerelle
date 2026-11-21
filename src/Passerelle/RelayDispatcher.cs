using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;

namespace Passerelle;

internal sealed class RelayDispatcher<T>(
    IHttpApplication<T> application, Uri address, long? maxRequestBodySize, ILogger logger) where T : notnull
{
    private readonly PathString _pathBase = PathString.FromUriComponent(address.AbsolutePath).Value!.TrimEnd('/');

    public async Task ProcessAsync(RelayRequest transport, CancellationToken shutdown)
    {
        using var lifetime = new RequestLifetime(shutdown);
        var response = new RelayResponse(transport, lifetime);
        var body = new RelayResponseBody(response, transport.ResponseBody, lifetime);
        T? context = default;
        bool created = false;
        Exception? failure = null;

        try
        {
            var path = PathString.FromUriComponent(transport.Url.AbsolutePath);
            if (!path.StartsWithSegments(_pathBase, out var matched, out var remaining))
            {
                response.StatusCode = StatusCodes.Status404NotFound;
                await body.CompleteAsync().ConfigureAwait(false);
                return;
            }

            var requestBody = new RelayRequestBody(transport.Body, transport.Headers.ContentLength,
                maxRequestBodySize, lifetime);
            var request = new HttpRequestFeature
            {
                Protocol = "HTTP/1.1",
                Scheme = "https",
                Method = transport.Method,
                PathBase = matched.Value ?? string.Empty,
                Path = remaining.Value ?? string.Empty,
                QueryString = transport.Url.Query,
                RawTarget = transport.Url.PathAndQuery,
                Headers = transport.Headers,
                Body = requestBody
            };

            if (!request.Headers.ContainsKey("Host"))
            {
                request.Headers.Host = transport.Url.Authority;
            }

            var features = new FeatureCollection();
            features.Set<IHttpRequestFeature>(request);
            features.Set<IHttpRequestBodyDetectionFeature>(new BodyDetection(transport.HasBody));
            features.Set<IHttpMaxRequestBodySizeFeature>(requestBody);
            features.Set<IHttpResponseFeature>(response);
            features.Set<IHttpResponseBodyFeature>(body);
            features.Set<IHttpRequestLifetimeFeature>(lifetime);
            features.Set<IHttpConnectionFeature>(new HttpConnectionFeature
            {
                ConnectionId = Guid.NewGuid().ToString("N"),
                RemoteIpAddress = transport.RemoteEndPoint?.Address,
                RemotePort = transport.RemoteEndPoint?.Port ?? 0
            });

            context = application.CreateContext(features);
            created = true;
            await application.ProcessRequestAsync(context).ConfigureAwait(false);
            await body.CompleteAsync().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            failure = error;
            if (error is not OperationCanceledException || !lifetime.Token.IsCancellationRequested)
            {
                logger.LogWarning("Relay HTTP request failed ({ErrorType}).", error.GetType().Name);
            }

            if (!response.HasStarted)
            {
                var status = error is BadHttpRequestException badRequest
                    ? badRequest.StatusCode
                    : lifetime.Token.IsCancellationRequested
                        ? StatusCodes.Status503ServiceUnavailable
                        : StatusCodes.Status500InternalServerError;
                transport.Start(status, null, new HeaderDictionary());
            }
            else
            {
                lifetime.Abort();
            }
        }
        finally
        {
            try
            {
                try
                {
                    await body.DiscardPendingWritesAsync().ConfigureAwait(false);
                }
                finally
                {
                    await response.CloseAsync().ConfigureAwait(false);
                }
            }
            catch (Exception error)
            {
                failure ??= error;
                logger.LogWarning("Could not finish the Relay response ({ErrorType}).", error.GetType().Name);
            }
            finally
            {
                await response.RunCompletedAsync(logger).ConfigureAwait(false);
                if (created)
                {
                    application.DisposeContext(context!, failure);
                }
            }
        }
    }

    private sealed record BodyDetection(bool CanHaveBody) : IHttpRequestBodyDetectionFeature;
}

internal sealed class RequestLifetime(CancellationToken shutdown) : IHttpRequestLifetimeFeature, IDisposable
{
    private readonly CancellationTokenSource _source = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
    private CancellationToken? _requestAborted;

    public CancellationToken Token => _source.Token;

    public CancellationToken RequestAborted
    {
        get => _requestAborted ?? Token;
        set => _requestAborted = value;
    }

    public void Abort() => _source.Cancel();
    public void Dispose() => _source.Dispose();

    public CancellationTokenSource? Link(CancellationToken cancellationToken) =>
        cancellationToken.CanBeCanceled && cancellationToken != Token
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, Token)
            : null;
}
