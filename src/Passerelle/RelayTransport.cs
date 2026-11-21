using System.Net;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Relay;

namespace Passerelle;

internal interface IRelayListener
{
    Uri Address { get; }
    Task OpenAsync(Action<RelayRequest> dispatch, CancellationToken cancellationToken);
    Task CloseAsync(CancellationToken cancellationToken);
}

internal sealed record RelayRequest(Uri Url, string Method, IHeaderDictionary Headers, Stream Body, bool HasBody,
    IPEndPoint? RemoteEndPoint, Stream ResponseBody, Action<int, string?, IHeaderDictionary> Start, Func<Task> CloseAsync)
{
    internal static bool IsIoFailure(Exception error) =>
        error is IOException or WebSocketException or RelayException;
}

internal sealed class AzureRelayListener(HybridConnectionListener listener) : IRelayListener
{
    public Uri Address => listener.Address;

    public Task OpenAsync(Action<RelayRequest> dispatch, CancellationToken cancellationToken)
    {
        listener.AcceptHandler = _ => Task.FromResult(false);
        listener.RequestHandler = context =>
        {
            var request = context.Request;
            var response = context.Response;
            var headers = new HeaderDictionary();
            foreach (var name in request.Headers.AllKeys)
            {
                if (name is not null)
                {
                    headers[name] = request.Headers.GetValues(name);
                }
            }

            dispatch(new RelayRequest(request.Url, request.HttpMethod, headers, request.InputStream ?? Stream.Null,
                request.HasEntityBody, request.RemoteEndPoint, response.OutputStream, (status, reason, values) =>
                {
                    response.Headers.Clear();
                    response.StatusCode = (HttpStatusCode)status;
                    response.StatusDescription = reason;
                    foreach (var (name, entries) in values)
                    {
                        foreach (var value in entries)
                        {
                            response.Headers.Add(name, value);
                        }
                    }
                }, async () =>
                {
                    try
                    {
                        await response.CloseAsync().ConfigureAwait(false);
                    }
                    finally
                    {
                        request.InputStream?.Dispose();
                    }
                }));
        };
        return listener.OpenAsync(cancellationToken);
    }

    public Task CloseAsync(CancellationToken cancellationToken) => listener.CloseAsync(cancellationToken);
}
