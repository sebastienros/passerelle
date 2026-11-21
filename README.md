# Passerelle

Passerelle hosts an ASP.NET Core HTTP application over [Azure Relay Hybrid Connections](https://learn.microsoft.com/azure/azure-relay/relay-hybrid-connections-protocol). It uses `Microsoft.Azure.Relay` for the wire protocol and targets .NET 10 and .NET 11 only.

## Host an application

Provision a Hybrid Connection and supply a connection string with Listen permission and `EntityPath` through configuration, such as the `Relay__ConnectionString` environment variable. Keep it out of source control and command-line arguments. Add a reference to `Passerelle`, then:

```csharp
using Passerelle;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UsePasserelle(options =>
{
    options.ConnectionString = builder.Configuration["Relay:ConnectionString"];
});

var app = builder.Build();
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.Run();
```

`UsePasserelle` replaces the local HTTP listener. To also listen locally, see the next section.

### Listen locally and over Relay at the same time

Use `AddPasserelle` instead of `UsePasserelle` to serve the same application pipeline on Kestrel endpoints and the Relay simultaneously:

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://0.0.0.0:5010"); // any Kestrel configuration
builder.WebHost.AddPasserelle(options =>
{
    options.ConnectionString = builder.Configuration["Relay:ConnectionString"];
});
```

Requests from both paths run concurrently through the same middleware, routes, and services. Relay requests report `Scheme = https` and the Relay host; local requests keep Kestrel's values. Starting the host starts both listeners (if Relay fails to start, Kestrel is stopped again), and shutdown drains both. `IServerAddressesFeature` reports the Kestrel addresses.

Call `AddPasserelle` after Kestrel is registered and do not register another server afterward (the default `WebApplication.CreateBuilder` already registers Kestrel). Local addresses, TLS, and limits remain Kestrel settings.

For an entity named `agent`, the public Relay URL is `https://<namespace>.servicebus.windows.net/agent/health`; the app sees `PathBase = /agent` and `Path = /health`. For an entity with a multi-segment name, the whole entity path becomes `PathBase`. Configure sender authentication on the Hybrid Connection separately; the listener credential is not a substitute for app authentication. Any Kestrel endpoint is separately reachable and should be secured as needed.

For Microsoft Entra authentication, set `Address` to `sb://<namespace>.servicebus.windows.net/<entity>` and provide a `Microsoft.Azure.Relay.TokenProvider`, leaving `ConnectionString` unset. Use the Relay SDK's `TokenProvider.CreateAzureActiveDirectoryTokenProvider` with your managed identity or client certificate credential, requesting `$"{audience}/.default"` from the credential. Grant the identity permission to listen on the entity (for example, Azure Relay Listener).

The [echo sample](samples/RelayEcho) uses the configuration above. To try its routes without a Relay, run:

```sh
dotnet run --project samples/RelayEcho -f net11.0 -- --Relay:LocalOnly=true --urls http://127.0.0.1:5080
curl -f http://127.0.0.1:5080/health
curl -f --data-binary 'hello' http://127.0.0.1:5080/echo
```

Omit `--Relay:LocalOnly=true` and supply `Relay__ConnectionString` to exercise Azure Relay. To keep Kestrel in that mode, add `--Relay:KeepLocalHttp=true`. For a live smoke test, call `/health` and POST a file to `/echo` using the entity's HTTPS URL and the appropriate **sender** credential, then compare the echoed bytes.

## Migrating from legacy Relay hosting

Replace the legacy ASP.NET Core Relay package reference with Passerelle in an application targeting .NET 10 or .NET 11. Switch from `UseAzureRelay` to `UsePasserelle`, or use `AddPasserelle` to keep Kestrel alongside Relay without a custom composite server.

For SAS, set `options.ConnectionString` to the entity-specific string. For identity or certificate authentication, set `options.Address` to the entity's `sb://` URI and `options.TokenProvider` to the configured provider.

Set `options.MaxRequestBodySize` independently of Kestrel's limit if needed. Endpoint request-size attributes can override it before reading. The default is 30,000,000 bytes; `null` disables the adapter's limit. It counts wire body bytes before app-level gzip decoding, and enforces the limit when the application reads the body. Azure Relay limits still apply.

## Projects using Passerelle

- [Crank](https://github.com/dotnet/crank) is a planned integration; it does not yet use Passerelle.

## Scope and limits

Passerelle supports one Hybrid Connection per host, request/response streams and pipes, file responses, response callbacks, and graceful drain until the host's shutdown deadline. It does not implement raw Relay connections, WebSockets, HTTP upgrades, trailers, multiple listeners, or IIS/HTTP.sys integration.

The adapter does not buffer entire bodies. The Relay SDK can buffer a small response on its control channel and its flush is a no-op there; ASP.NET Core `StartAsync` and `FlushAsync` commit response headers at the adapter, not necessarily at the sender. The SDK represents wire header values as strings, so independently repeated fields that cannot be comma-joined (such as multiple `Set-Cookie` fields) are not supported. The Relay service does not forward the sender's `Content-Length` request header, so `Request.ContentLength` is typically `null` for relayed requests and the size limit is enforced while the body is read. Response completion after a failed write is best-effort. `RequestAborted` is signaled on application abort, detectable stream I/O failure, or expiry of the shutdown deadline; the SDK has no independent sender-disconnect notification or public per-response transport abort.

Do not rely on simultaneous bidirectional HTTP streaming through Relay. In live testing, an 8 MB echo that wrote the response while reading the request intermittently returned a Relay 500; the same failure also occurred with a direct Relay SDK listener. A client reading and sending concurrently did not avoid it. The precise cause within the SDK or service is unknown. To avoid this pattern, consume the request before writing the response, using disk-backed storage if necessary.

## Build and test

Install the .NET 11 SDK and both ASP.NET Core runtimes. The integration tests run the ASP.NET Core pipeline against an in-process Relay transport, with real Kestrel for the coexistence case. They require no Azure account and do not validate the Azure wire protocol.

The adapter has also been validated manually against a live Azure Relay namespace on .NET 10 and .NET 11, using SAS and Microsoft Entra listener authentication. The validation covered routing and query mapping, multi-megabyte streamed uploads, downloads, and gzip, range file results, status codes, response callbacks, concurrent local and relayed requests, and graceful shutdown.

```sh
dotnet build Passerelle.slnx -c Release
dotnet test Passerelle.slnx -c Release --no-build
dotnet pack src/Passerelle -c Release --no-build -o artifacts
```
