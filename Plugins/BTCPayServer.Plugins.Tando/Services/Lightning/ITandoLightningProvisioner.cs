using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Tando.Services;

public enum TandoLightningProviderType
{
    Phoenixd,
    Nwc
}

public class TandoLightningProvisionRequest
{
    public TandoLightningProviderType ProviderType { get; set; }
    public string? PhoenixdServerUrl { get; set; }
    public string? PhoenixdApiPassword { get; set; }
    public string? NwcConnectionUri { get; set; }
}

public record TandoLightningProvisionResult(string? ConnectionString, string? Error)
{
    public bool IsSuccess => Error is null;
}

public interface ITandoLightningProvisioner
{
    TandoLightningProviderType ProviderType { get; }
    Task<TandoLightningProvisionResult> Provision(TandoLightningProvisionRequest request);
}

public class TandoLightningProvisionerFactory(IEnumerable<ITandoLightningProvisioner> provisioners)
{
    public ITandoLightningProvisioner? Get(TandoLightningProviderType providerType)
        => provisioners.FirstOrDefault(p => p.ProviderType == providerType);
}
