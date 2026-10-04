using System.Threading.Tasks;
using BTCPayServer.Plugins.Tando.ViewModels;

namespace BTCPayServer.Plugins.Tando.Services;

public interface ITandoMpesaPayoutClient
{
    Task<TandoPayoutTriggerResult> TriggerPayout(TandoPayoutRequest request);
}

public class UnconfiguredTandoMpesaPayoutClient : ITandoMpesaPayoutClient
{
    public Task<TandoPayoutTriggerResult> TriggerPayout(TandoPayoutRequest request)
    {
        return Task.FromResult(new TandoPayoutTriggerResult(Success: false, PayoutReference: null, Error: "mpesa_payout_client_not_configured"));
    }
}
public record TandoPayoutRequest(string InvoiceId, string StoreId, decimal AmountKes,
    TandoMpesaDestinationType DestinationType, string Destination);

public record TandoPayoutTriggerResult(bool Success, string? PayoutReference, string? Error);
