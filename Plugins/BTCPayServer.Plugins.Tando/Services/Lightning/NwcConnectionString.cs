using System;
using System.Web;

namespace BTCPayServer.Plugins.Tando.Services.Lightning;

public record NwcConnectionString(string WalletPubkeyHex, string[] RelayUrls, string SecretHex)
{
    public static bool TryParse(string uri, out NwcConnectionString? result, out string? error)
    {
        result = null;
        error = null;
        if (!uri.StartsWith("nostr+walletconnect://", StringComparison.OrdinalIgnoreCase))
        {
            error = "invalid_nwc_uri_scheme";
            return false;
        }

        Uri parsed;
        try
        {
            parsed = new Uri(uri);
        }
        catch (UriFormatException)
        {
            error = "invalid_nwc_uri_format"; 
            return false;
        }
        var pubkey = parsed.Host;
        if (string.IsNullOrEmpty(pubkey) || pubkey.Length != 64)
        {
            error = "invalid_nwc_wallet_pubkey";
            return false;
        }
        var query = HttpUtility.ParseQueryString(parsed.Query);
        var relays = query.GetValues("relay");
        if (relays is null || relays.Length == 0)
        {
            error = "nwc_uri_missing_relay";
            return false;
        }
        var secret = query.Get("secret");
        if (string.IsNullOrEmpty(secret) || secret.Length != 64)
        {
            error = "nwc_uri_missing_or_invalid_secret";
            return false;
        }
        result = new NwcConnectionString(pubkey, relays, secret);
        return true;
    }
}
