using Microsoft.Azure.Relay;
using Xunit;

namespace Passerelle.Tests;

public sealed class RelayOptionsTests
{
    private static readonly Uri Address = new("sb://relay.example/bench/agent");

    [Fact]
    public void Configures_SDK_listeners_without_connecting()
    {
        const string connectionString =
            "Endpoint=sb://relay.example/;SharedAccessKeyName=Listen;SharedAccessKey=not-a-secret;EntityPath=bench/agent";
        var sas = new RelayOptions { ConnectionString = connectionString }.CreateListener();
        Assert.Equal(Address, sas.Address);

        var provider = TokenProvider.CreateAzureActiveDirectoryTokenProvider(
            (_, _, _) => throw new InvalidOperationException("Authentication must not run during configuration."), null);
        var entra = new RelayOptions { Address = Address, TokenProvider = provider }.CreateListener();
        Assert.Equal(Address, entra.Address);
        Assert.Same(provider, entra.TokenProvider);
    }

    [Fact]
    public void Invalid_SAS_configuration_does_not_expose_the_input()
    {
        var options = new RelayOptions { ConnectionString = "malformed=test-sensitive-value" };
        var failure = Assert.Throws<InvalidOperationException>(() => options.CreateListener());
        Assert.DoesNotContain("test-sensitive-value", failure.ToString(), StringComparison.Ordinal);
    }
}
