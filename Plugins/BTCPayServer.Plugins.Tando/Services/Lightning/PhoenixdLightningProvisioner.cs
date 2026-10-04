using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Tando.Services.Lightning;

public class PhoenixdLightningProvisioner : ITandoLightningProvisioner
{
    public TandoLightningProviderType ProviderType => TandoLightningProviderType.Phoenixd;

    public Task<TandoLightningProvisionResult> Provision(TandoLightningProvisionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.PhoenixdServerUrl))
            return Task.FromResult(new TandoLightningProvisionResult(null, "phoenixd_server_url_required"));
        if (string.IsNullOrWhiteSpace(request.PhoenixdApiPassword))
            return Task.FromResult(new TandoLightningProvisionResult(null, "phoenixd_api_password_required"));

        var connectionString = $"type=phoenixd;server={request.PhoenixdServerUrl.TrimEnd('/')};token={request.PhoenixdApiPassword}";
        return Task.FromResult(new TandoLightningProvisionResult(connectionString, null));
    }
}
