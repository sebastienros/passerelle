using System.IO.Compression;
using System.IO.Pipelines;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Passerelle.Tests;

public sealed class HostingTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Routes_relay_HTTP_and_optionally_keeps_Kestrel(bool keepLocal)
    {
        var (app, listener) = Build(keepLocal: keepLocal);
        await using var host = app;
        app.MapGet("/info/{name}", (HttpRequest request, string name) => Results.Json(new
        {
            name,
            pathBase = request.PathBase.Value,
            query = request.Query["q"].ToString(),
            request.Scheme,
            host = request.Host.Value
        }));
        app.MapDelete("/info/{name}", () => Results.NoContent());
        await app.StartAsync();

        var get = listener.Send("/bench/agent/info/hello%20world?q=a%2Bb");
        await get.Closed.WaitAsync(Deadline);
        Assert.Equal(200, get.Status);
        using var json = JsonDocument.Parse(get.Output.ToArray());
        Assert.Equal("hello world", json.RootElement.GetProperty("name").GetString());
        Assert.Equal("/bench/agent", json.RootElement.GetProperty("pathBase").GetString());
        Assert.Equal("a+b", json.RootElement.GetProperty("query").GetString());
        Assert.Equal("https", json.RootElement.GetProperty("scheme").GetString());
        Assert.Equal("relay.example", json.RootElement.GetProperty("host").GetString());

        var delete = listener.Send("/bench/agent/info/gone", "DELETE");
        await delete.Closed.WaitAsync(Deadline);
        Assert.Equal(204, delete.Status);
        var wrongBase = listener.Send("/bench/agent-other/info/no");
        await wrongBase.Closed.WaitAsync(Deadline);
        Assert.Equal(404, wrongBase.Status);

        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        if (keepLocal)
        {
            using var client = new HttpClient { BaseAddress = new Uri(Assert.Single(addresses!.Addresses)) };
            using var request = await client.GetAsync("/info/local");
            Assert.Equal(HttpStatusCode.OK, request.StatusCode);
        }
        else
        {
            Assert.Null(addresses);
        }
    }

    [Fact]
    public async Task Streams_upload_and_download_and_runs_callbacks_in_reverse_order()
    {
        var (app, listener) = Build();
        await using var host = app;
        var order = new List<string>();
        var finished = Signal();
        app.MapPost("/upload", async context =>
        {
            var response = context.Response;
            response.OnCompleted(() => { finished.TrySetResult(); return Task.CompletedTask; });
            response.RegisterForDispose(new OnDispose(() => order.Add("dispose")));
            response.OnCompleted(() => { order.Add("complete"); return Task.CompletedTask; });
            response.OnStarting(() => { order.Add("start-first"); return Task.CompletedTask; });
            response.OnStarting(() =>
            {
                order.Add("start-second");
                response.StatusCode = 201;
                response.Headers.Location = "/bench/agent/jobs/1";
                response.Headers["FileLength"] = "6";
                response.Headers["Destination"] = context.Request.Headers["destinationFilename"];
                return Task.CompletedTask;
            });

            var first = await context.Request.BodyReader.ReadAsync(context.RequestAborted);
            await response.Body.WriteAsync(first.Buffer.First, context.RequestAborted);
            context.Request.BodyReader.AdvanceTo(first.Buffer.GetPosition(first.Buffer.First.Length));
            await response.Body.FlushAsync(context.RequestAborted);
            await context.Request.BodyReader.CopyToAsync(response.BodyWriter, context.RequestAborted);
            await response.CompleteAsync();
        });
        await app.StartAsync();

        var upload = new Pipe();
        var output = new ObservedStream();
        var exchange = listener.Send("/bench/agent/upload", "POST", upload.Reader.AsStream(),
            new HeaderDictionary { ["destinationFilename"] = "artifact.tar" }, output);
        await upload.Writer.WriteAsync("abc"u8.ToArray());
        await output.FirstWrite.Task.WaitAsync(Deadline);
        Assert.Equal(201, exchange.Status);
        Assert.Equal("abc", Encoding.UTF8.GetString(output.ToArray()));
        Assert.Equal("/bench/agent/jobs/1", exchange.Headers.Location);
        Assert.Equal("artifact.tar", exchange.Headers["Destination"]);
        Assert.Equal("6", exchange.Headers["FileLength"]);
        Assert.False(exchange.Closed.IsCompleted);
        await upload.Writer.WriteAsync("def"u8.ToArray());
        await upload.Writer.CompleteAsync();
        await finished.Task.WaitAsync(Deadline);
        Assert.Equal("abcdef", Encoding.UTF8.GetString(output.ToArray()));
        Assert.Equal(["start-second", "start-first", "complete", "dispose"], order);
        Assert.Equal(1, exchange.CloseCount);
        await upload.Reader.CompleteAsync();
    }

    [Fact]
    public async Task Supports_physical_files_and_gzip_wrapping_the_response_stream()
    {
        var (app, listener) = Build();
        await using var host = app;
        var file = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(file, "artifact");
            app.MapGet("/file", () => Results.File(file, enableRangeProcessing: true));
            app.MapPost("/gzip", async context =>
            {
                Assert.Equal("gzip", context.Request.Headers.ContentEncoding);
                context.Response.Headers.ContentEncoding = "gzip";
                context.Response.Headers.Vary = "Accept-Encoding";
                await using var input = new GZipStream(context.Request.Body, CompressionMode.Decompress);
                await using var output = new GZipStream(context.Response.Body, CompressionLevel.Fastest);
                await input.CopyToAsync(output, context.RequestAborted);
            });
            await app.StartAsync();
            var download = listener.Send("/bench/agent/file", headers: new HeaderDictionary { ["Range"] = "bytes=1-3" });
            await download.Closed.WaitAsync(Deadline);
            Assert.Equal(206, download.Status);
            Assert.Equal("bytes 1-3/8", download.Headers.ContentRange);
            Assert.Equal("rti", Encoding.UTF8.GetString(download.Output.ToArray()));

            using var upload = new MemoryStream();
            await using (var zip = new GZipStream(upload, CompressionLevel.Fastest, leaveOpen: true))
                await zip.WriteAsync("streamed gzip"u8.ToArray());
            upload.Position = 0;
            var exchange = listener.Send("/bench/agent/gzip", "POST", upload,
                new HeaderDictionary { ["Content-Encoding"] = "gzip" });
            await exchange.Closed.WaitAsync(Deadline);
            Assert.Equal("gzip", exchange.Headers.ContentEncoding);
            Assert.Equal("Accept-Encoding", exchange.Headers.Vary);
            using var zipped = new MemoryStream(exchange.Output.ToArray());
            using var reader = new StreamReader(new GZipStream(zipped, CompressionMode.Decompress));
            Assert.Equal("streamed gzip", await reader.ReadToEndAsync());
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Theory]
    [InlineData(5L, 5, false, 413)]
    [InlineData(null, 5, false, 413)]
    [InlineData(null, 4, false, 204)]
    [InlineData(5L, 5, true, 204)]
    public async Task Enforces_body_limit_and_allows_endpoint_override(long? declared, int bytes, bool permit, int status)
    {
        var (app, listener) = Build(limit: 4);
        await using var host = app;
        app.MapPost("/upload", async context =>
        {
            var limit = context.Features.Get<IHttpMaxRequestBodySizeFeature>()!;
            Assert.False(limit.IsReadOnly);
            if (permit)
            {
                limit.MaxRequestBodySize = 5;
            }
            await context.Request.Body.CopyToAsync(Stream.Null, context.RequestAborted);
            Assert.True(limit.IsReadOnly);
            context.Response.StatusCode = 204;
        });
        await app.StartAsync();
        using var body = new MemoryStream(new byte[bytes]);
        var request = listener.Send("/bench/agent/upload", "POST", body, new HeaderDictionary { ContentLength = declared });
        await request.Closed.WaitAsync(Deadline);
        Assert.Equal(status, request.Status);
        if (declared > 4 && !permit)
        {
            Assert.Equal(0, body.Position);
        }
    }

    [Fact]
    public async Task Starting_callback_failure_sends_500_and_still_disposes_request_resources()
    {
        var (app, listener) = Build();
        await using var host = app;
        var disposed = Signal();
        app.MapGet("/error", async context =>
        {
            context.Response.RegisterForDispose(new OnDispose(() => disposed.TrySetResult()));
            context.Response.OnCompleted(() => throw new InvalidOperationException("completion failed"));
            context.Response.OnStarting(() => throw new InvalidOperationException("start failed"));
            await context.Response.StartAsync();
        });
        await app.StartAsync();
        var request = listener.Send("/bench/agent/error");
        await disposed.Task.WaitAsync(Deadline);
        Assert.Equal(500, request.Status);
        Assert.Equal(1, request.CloseCount);
    }

    [Fact]
    public async Task Completion_flushes_advanced_pipe_bytes_and_head_keeps_headers_only()
    {
        var (app, listener) = Build();
        await using var host = app;
        app.MapMethods("/pipe", ["GET", "HEAD"], (HttpContext context) =>
        {
            context.Response.ContentLength = 4;
            var writer = context.Response.BodyWriter;
            "pipe"u8.CopyTo(writer.GetSpan(4));
            writer.Advance(4);
            return Task.CompletedTask;
        });
        await app.StartAsync();
        var get = listener.Send("/bench/agent/pipe");
        await get.Closed.WaitAsync(Deadline);
        Assert.Equal(200, get.Status);
        Assert.Equal("pipe", Encoding.UTF8.GetString(get.Output.ToArray()));
        var head = listener.Send("/bench/agent/pipe", "HEAD");
        await head.Closed.WaitAsync(Deadline);
        Assert.Equal(200, head.Status);
        Assert.Equal(4, head.Headers.ContentLength);
        Assert.Equal(0, head.Output.Length);
    }

    [Fact]
    public async Task Transport_write_failure_signals_request_abort_without_replacing_started_status()
    {
        var (app, listener) = Build();
        await using var host = app;
        var aborted = Signal();
        app.MapGet("/broken", async context =>
        {
            context.Response.StatusCode = 202;
            using var registration = context.RequestAborted.Register(() => aborted.TrySetResult());
            await context.Response.Body.WriteAsync("payload"u8.ToArray());
        });
        await app.StartAsync();
        var exchange = listener.Send("/bench/agent/broken", output: new BrokenStream());
        await aborted.Task.WaitAsync(Deadline);
        await exchange.Closed.WaitAsync(Deadline);
        Assert.Equal(202, exchange.Status);
        Assert.Equal(1, exchange.CloseCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Shutdown_drains_requests_until_deadline_then_signals_abort(bool cancel)
    {
        var (app, listener) = Build();
        await using var host = app;
        var processing = Signal();
        var release = Signal();
        var aborted = Signal();
        app.MapGet("/wait", async context =>
        {
            processing.TrySetResult();
            try
            {
                await release.Task.WaitAsync(context.RequestAborted);
            }
            catch (OperationCanceledException)
            {
                aborted.TrySetResult();
                throw;
            }
        });
        await app.StartAsync();
        var request = listener.Send("/bench/agent/wait");
        await processing.Task.WaitAsync(Deadline);
        using var deadline = new CancellationTokenSource(Deadline);
        var stopped = app.StopAsync(deadline.Token);
        Assert.False(listener.Closed.IsCompleted);
        var rejected = listener.Send("/bench/agent/wait");
        await rejected.Closed.WaitAsync(Deadline);
        Assert.Equal(503, rejected.Status);
        if (cancel)
        {
            await deadline.CancelAsync();
            await aborted.Task.WaitAsync(Deadline);
        }
        else
        {
            release.TrySetResult();
        }
        await stopped.WaitAsync(Deadline);
        await request.Closed.WaitAsync(Deadline);
        Assert.True(listener.Closed.IsCompleted);
        Assert.Equal(cancel ? 503 : 200, request.Status);
    }

    [Fact]
    public async Task Serves_local_and_relay_requests_concurrently_through_one_pipeline()
    {
        var (app, listener) = Build(keepLocal: true);
        await using var host = app;
        var relayArrived = Signal();
        var localArrived = Signal();
        app.MapGet("/wait/{source}", async (string source) =>
        {
            // Each request completes only once the other path is in flight at the same time.
            (source == "relay" ? relayArrived : localArrived).TrySetResult();
            await (source == "relay" ? localArrived : relayArrived).Task.WaitAsync(Deadline);
            return source;
        });
        await app.StartAsync();

        var relay = listener.Send("/bench/agent/wait/relay");
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
        using var client = new HttpClient { BaseAddress = new Uri(Assert.Single(address)) };
        var local = client.GetStringAsync("/wait/local");

        Assert.Equal("local", await local.WaitAsync(Deadline));
        await relay.Closed.WaitAsync(Deadline);
        Assert.Equal(200, relay.Status);
        Assert.Equal("relay", Encoding.UTF8.GetString(relay.Output.ToArray()));
    }

    private static (WebApplication, FakeListener) Build(bool keepLocal = false, long? limit = null)
    {
        var listener = new FakeListener();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        if (keepLocal)
        {
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.WebHost.AddPasserelle(o => o.MaxRequestBodySize = limit);
        }
        else
        {
            builder.WebHost.UsePasserelle(o => o.MaxRequestBodySize = limit);
        }
        builder.Services.AddSingleton(sp => new RelayServer(sp.GetRequiredService<IOptions<RelayOptions>>(),
            sp.GetRequiredService<ILogger<RelayServer>>(), _ => listener));
        return (builder.Build(), listener);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class OnDispose(Action action) : IDisposable
    {
        public void Dispose() => action();
    }

    private sealed class ObservedStream : MemoryStream
    {
        public TaskCompletionSource FirstWrite { get; } = Signal();
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await base.WriteAsync(buffer, cancellationToken);
            FirstWrite.TrySetResult();
        }
    }

    private sealed class BrokenStream : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new System.Net.WebSockets.WebSocketException("transport disconnected"));
    }

    private sealed class FakeListener : IRelayListener
    {
        private Action<RelayRequest>? _dispatch;
        private readonly TaskCompletionSource _closed = Signal();
        public Uri Address { get; } = new("sb://relay.example/bench/agent");
        public Task Closed => _closed.Task;

        public Task OpenAsync(Action<RelayRequest> dispatch, CancellationToken cancellationToken)
        {
            _dispatch = dispatch;
            return Task.CompletedTask;
        }

        public Task CloseAsync(CancellationToken cancellationToken)
        {
            _closed.TrySetResult();
            return Task.CompletedTask;
        }

        public Exchange Send(string path, string method = "GET", Stream? input = null,
            IHeaderDictionary? headers = null, MemoryStream? output = null)
        {
            var exchange = new Exchange(output ?? new MemoryStream());
            _dispatch!(new RelayRequest(new Uri(Address, path), method, headers ?? new HeaderDictionary(),
                input ?? Stream.Null, input is not null, null, exchange.Output, exchange.Start, exchange.Close));
            return exchange;
        }
    }

    private sealed class Exchange(MemoryStream output)
    {
        private readonly TaskCompletionSource _closed = Signal();
        public MemoryStream Output { get; } = output;
        public int Status { get; private set; }
        public int CloseCount { get; private set; }
        public IHeaderDictionary Headers { get; private set; } = new HeaderDictionary();
        public Task Closed => _closed.Task;

        public void Start(int status, string? reason, IHeaderDictionary headers) => (Status, Headers) = (status, headers);

        public Task Close()
        {
            CloseCount++;
            _closed.TrySetResult();
            return Task.CompletedTask;
        }
    }
}
