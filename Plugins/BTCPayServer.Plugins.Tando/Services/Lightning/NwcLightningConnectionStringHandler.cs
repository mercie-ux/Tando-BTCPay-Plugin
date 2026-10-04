#nullable enable
using System.Net.Http;
using BTCPayServer.Lightning;
using NBitcoin;

namespace BTCPayServer.Plugins.Tando.Services.Lightning;

// Registered as: services.AddSingleton<ILightningConnectionStringHandler, NwcLightningConnectionStringHandler>();
// LightningClientFactoryService (BTCPay core) collects every registered handler via DI and
// tries each one when parsing a connection string - this is the actual extension point,
// confirmed by reading BTCPayServer/Services/LightningClientFactoryService.cs. No core changes,
// no upstream PR required.
public class NwcLightningConnectionStringHandler(IHttpClientFactory httpClientFactory) : ILightningConnectionStringHandler
{
    public ILightningClient? Create(string connectionString, Network network, out string? error)
    {
        error = null;
        if (!connectionString.StartsWith("nostr+walletconnect://"))
            return null; // not ours - let the next handler in the chain try it

        if (!NwcConnectionString.TryParse(connectionString, out var nwc, out error))
            return null;

        var httpClient = httpClientFactory.CreateClient("tando-nwc-relay-probe");
        return new NwcLightningClient(nwc!, httpClient);
    }
}
