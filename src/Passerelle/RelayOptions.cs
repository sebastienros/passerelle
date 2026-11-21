using Microsoft.Azure.Relay;

namespace Passerelle;

public sealed class RelayOptions
{
    /// <summary>A SAS connection string for the Hybrid Connection, including EntityPath.</summary>
    public string? ConnectionString { get; set; }

    /// <summary>The sb:// Hybrid Connection URI when authenticating with a token provider.</summary>
    public Uri? Address { get; set; }

    /// <summary>The token provider paired with Address, for example for managed identity.</summary>
    public TokenProvider? TokenProvider { get; set; }

    /// <summary>Maximum bytes read from the Relay request body, or null for no limit.</summary>
    public long? MaxRequestBodySize { get; set; } = 30_000_000;

    internal HybridConnectionListener CreateListener()
    {
        ArgumentOutOfRangeException.ThrowIfNegative(MaxRequestBodySize ?? 0);
        var sas = !string.IsNullOrWhiteSpace(ConnectionString);
        if (sas ? Address is not null || TokenProvider is not null
                : Address is not { IsAbsoluteUri: true, Scheme: "sb" } ||
                  Address.AbsolutePath.Trim('/').Length == 0 ||
                  Address.Query.Length != 0 || Address.Fragment.Length != 0 ||
                  Address.UserInfo.Length != 0 || TokenProvider is null)
        {
            throw new InvalidOperationException(
                "Configure either a Relay connection string with EntityPath or an sb:// entity address with a token provider.");
        }

        try
        {
            return sas ? new HybridConnectionListener(ConnectionString!) :
                new HybridConnectionListener(Address!, TokenProvider!);
        }
        catch (Exception error) when (error is ArgumentException or FormatException or InvalidOperationException)
        {
            // SDK errors may include pieces of the connection string.
            throw new InvalidOperationException("Invalid Azure Relay listener configuration.");
        }
    }
}
