using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.Extensions.DependencyInjection;

namespace Passerelle;

public static class RelayWebHostExtensions
{
    private static readonly object LocalServerKey = new();

    /// <summary>Hosts this application over Relay without binding a local HTTP port.</summary>
    public static IWebHostBuilder UsePasserelle(this IWebHostBuilder builder, Action<RelayOptions> configure)
        => Register(builder, configure, keepLocalServer: false);

    /// <summary>Adds Relay to the application without replacing its local HTTP endpoints. Call after registering Kestrel.</summary>
    public static IWebHostBuilder AddPasserelle(this IWebHostBuilder builder, Action<RelayOptions> configure)
        => Register(builder, configure, keepLocalServer: true);

    private static IWebHostBuilder Register(IWebHostBuilder builder, Action<RelayOptions> configure, bool keepLocalServer)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        return builder.ConfigureServices(services =>
        {
            if (services.Any(d => d.ServiceType == typeof(RelayServer)))
            {
                throw new InvalidOperationException("Passerelle can only be registered once.");
            }

            if (keepLocalServer)
            {
                // Preserve the actual default IServer registration without depending on Kestrel internals.
                var local = services.LastOrDefault(d => d.ServiceType == typeof(IServer) && !d.IsKeyedService);
                if (local is not { Lifetime: ServiceLifetime.Singleton })
                {
                    throw new InvalidOperationException("Register Kestrel before calling AddPasserelle.");
                }

                services.Remove(local);
                services.Add(local.ImplementationType is { } type
                    ? ServiceDescriptor.KeyedSingleton(typeof(IServer), LocalServerKey, type)
                    : local.ImplementationFactory is { } factory
                        ? ServiceDescriptor.KeyedSingleton(typeof(IServer), LocalServerKey, (sp, _) => factory(sp))
                        : ServiceDescriptor.KeyedSingleton(typeof(IServer), LocalServerKey, local.ImplementationInstance!));
            }

            services.Configure(configure);
            services.AddSingleton<RelayServer>();
            services.AddSingleton<IServer>(sp => new HostingServer(sp.GetRequiredService<RelayServer>(),
                keepLocalServer ? sp.GetRequiredKeyedService<IServer>(LocalServerKey) : null));
        });
    }

    private sealed class HostingServer(RelayServer relay, IServer? local) : IServer
    {
        private readonly Microsoft.AspNetCore.Http.Features.FeatureCollection _features = new();

        public Microsoft.AspNetCore.Http.Features.IFeatureCollection Features => local?.Features ?? _features;

        public async Task StartAsync<TContext>(IHttpApplication<TContext> application, CancellationToken cancellationToken)
            where TContext : notnull
        {
            if (local is not null)
            {
                await local.StartAsync(application, cancellationToken).ConfigureAwait(false);
            }

            try
            {
                await relay.StartAsync(application, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                if (local is not null)
                {
                    await local.StopAsync(CancellationToken.None).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                }

                throw;
            }
        }

        public Task StopAsync(CancellationToken cancellationToken) => local is null
            ? relay.StopAsync(cancellationToken)
            : Task.WhenAll(relay.StopAsync(cancellationToken), local.StopAsync(cancellationToken));

        // The container owns both servers.
        public void Dispose() { }
    }
}
