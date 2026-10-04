using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Tando.Services.Lightning;

public class NwcLightningProvisioner : ITandoLightningProvisioner
{
    public TandoLightningProviderType ProviderType => TandoLightningProviderType.Nwc;

    public Task<TandoLightningProvisionResult> Provision(TandoLightningProvisionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.NwcConnectionUri))
            return Task.FromResult(new TandoLightningProvisionResult(null, "nwc_connection_uri_required"));

        if (!NwcConnectionString.TryParse(request.NwcConnectionUri, out _, out var error))
            return Task.FromResult(new TandoLightningProvisionResult(null, error));

        return Task.FromResult(new TandoLightningProvisionResult(request.NwcConnectionUri, null));
    }
}
