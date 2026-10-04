using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Lightning;
using NBitcoin;
using NBitcoin.Secp256k1;
using NNostr.Client;

namespace BTCPayServer.Plugins.Tando.Services.Lightning;

public class NwcLightningClient(NwcConnectionString connection, HttpClient httpClient) : ILightningClient
{
    private NostrClient? _relayClient;
    private readonly ECPrivKey _ourPrivateKey = Context.Instance.CreateECPrivKey(Convert.FromHexString(connection.SecretHex));

    private async Task<NostrClient> GetOrConnectRelay(CancellationToken cancellation)
    {
        if (_relayClient is not null)
            return _relayClient;

        _relayClient = new NostrClient(new Uri(connection.RelayUrls[0]));
        _ = _relayClient.Connect();
        await _relayClient.WaitUntilConnected();
        return _relayClient;
    }

    // NIP-04, implemented directly against the published spec rather than a third-party
    // wrapper: ECDH shared secret = raw (unhashed) X-coordinate of privkey * pubkey,
    // used directly as the AES-256 key. Random 16-byte IV, PKCS7 padding.
    // Verified against a real working NBitcoin.Secp256k1 usage example (GetSharedPubkey
    // + strip the 1-byte 0x02/0x03 prefix) plus the NIP-04 spec's reference JS sample.
    private byte[] GetSharedSecret(string theirPubkeyHex)
    {
        var xOnlyBytes = Convert.FromHexString(theirPubkeyHex);
        var compressedBytes = new byte[33];
        compressedBytes[0] = 0x02; // nostr pubkeys are BIP-340 x-only => implicitly even-Y
        Array.Copy(xOnlyBytes, 0, compressedBytes, 1, 32);
        var theirPubKey = ECPubKey.Create(compressedBytes);

        var sharedPubkey = theirPubKey.GetSharedPubkey(_ourPrivateKey);
        var sharedBytes = sharedPubkey.ToBytes();
        return sharedBytes[1..]; // strip the compressed-point prefix, leaving the 32-byte X-coordinate
    }

    private string EncryptNip04(string plaintext, string theirPubkeyHex)
    {
        var key = GetSharedSecret(theirPubkeyHex);
        using var aes = Aes.Create();
        aes.Key = key;
        aes.GenerateIV();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using var encryptor = aes.CreateEncryptor();
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = encryptor.TransformFinalBlock(plaintextBytes, 0, plaintextBytes.Length);

        return $"{Convert.ToBase64String(ciphertext)}?iv={Convert.ToBase64String(aes.IV)}";
    }

    private string DecryptNip04(string content, string theirPubkeyHex)
    {
        var parts = content.Split("?iv=");
        if (parts.Length != 2)
            throw new FormatException("Malformed NIP-04 content - expected '<ciphertext>?iv=<iv>'.");

        var key = GetSharedSecret(theirPubkeyHex);
        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = Convert.FromBase64String(parts[1]);
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using var decryptor = aes.CreateDecryptor();
        var ciphertext = Convert.FromBase64String(parts[0]);
        var plaintextBytes = decryptor.TransformFinalBlock(ciphertext, 0, ciphertext.Length);
        return Encoding.UTF8.GetString(plaintextBytes);
    }

    private async Task<JsonDocument> SendNwcRequest(object requestPayload, CancellationToken cancellation)
    {
        var relay = await GetOrConnectRelay(cancellation);
        var requestJson = JsonSerializer.Serialize(requestPayload);
        var encryptedContent = EncryptNip04(requestJson, connection.WalletPubkeyHex);

        var requestEvent = new NostrEvent
        {
            Kind = 23194,
            Content = encryptedContent,
            CreatedAt = DateTimeOffset.UtcNow,
            Tags = new List<NostrEventTag>
            {
                new() { TagIdentifier = "p", Data = { connection.WalletPubkeyHex } }
            }
        };
        // ComputeIdAndSignAsync derives and sets the pubkey from the key itself - no need
        // to set PublicKey manually beforehand (per NNostr's own README usage example).
        await requestEvent.ComputeIdAndSignAsync(_ourPrivateKey);

        var responseTcs = new TaskCompletionSource<NostrEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        const string subscriptionId = "nwc-request";

        void OnEvent(object? sender, (string subscriptionId, NostrEvent[] events) args)
        {
            if (args.subscriptionId == subscriptionId && args.events.Length > 0)
                responseTcs.TrySetResult(args.events[0]);
        }
        relay.EventsReceived += OnEvent;

        try
        {
            relay.CreateSubscription(subscriptionId, new[]
            {
                new NostrSubscriptionFilter
                {
                    Kinds = new[] { 23195 },
                    ReferencedEventIds = new[] { requestEvent.Id },
                    Authors = new[] { connection.WalletPubkeyHex }
                }
            });

            await relay.SendEventsAndWaitUntilReceived(new[] { requestEvent }, cancellation);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(30)); // wallet may be asleep - generous but bounded
            await using var registration = timeoutCts.Token.Register(() => responseTcs.TrySetCanceled());

            var responseEvent = await responseTcs.Task;
            var decrypted = DecryptNip04(responseEvent.Content, connection.WalletPubkeyHex);
            var doc = JsonDocument.Parse(decrypted);

            if (doc.RootElement.TryGetProperty("error", out var errEl))
            {
                var message = errEl.TryGetProperty("message", out var m) ? m.GetString() : "unknown_nwc_error";
                throw new InvalidOperationException($"NWC wallet returned an error: {message}");
            }
            return doc;
        }
        finally
        {
            relay.EventsReceived -= OnEvent;
        }
    }

    public async Task<LightningInvoice> CreateInvoice(LightMoney amount, string description, TimeSpan expiry,
        CancellationToken cancellation = default)
    {
        var response = await SendNwcRequest(new
        {
            method = "make_invoice",
            @params = new { amount = amount.MilliSatoshi, description, expiry = (int)expiry.TotalSeconds }
        }, cancellation);

        var result = response.RootElement.GetProperty("result");
        return new LightningInvoice
        {
            Id = result.GetProperty("payment_hash").GetString(),
            BOLT11 = result.GetProperty("invoice").GetString(),
            Amount = amount,
            Status = LightningInvoiceStatus.Unpaid,
            ExpiresAt = DateTimeOffset.UtcNow.Add(expiry)
        };
    }

    public Task<LightningInvoice> CreateInvoice(CreateInvoiceParams req, CancellationToken cancellation = default)
        => CreateInvoice(req.Amount, req.Description ?? "", req.Expiry, cancellation);

    public async Task<LightningInvoice> GetInvoice(string invoiceId, CancellationToken cancellation = default)
    {
        var response = await SendNwcRequest(new { method = "lookup_invoice", @params = new { payment_hash = invoiceId } }, cancellation);
        var result = response.RootElement.GetProperty("result");
        var settledAt = result.TryGetProperty("settled_at", out var s) && s.ValueKind != JsonValueKind.Null
            ? DateTimeOffset.FromUnixTimeSeconds(s.GetInt64())
            : (DateTimeOffset?)null;

        return new LightningInvoice
        {
            Id = invoiceId,
            BOLT11 = result.GetProperty("invoice").GetString(),
            Status = settledAt is not null ? LightningInvoiceStatus.Paid : LightningInvoiceStatus.Unpaid,
            PaidAt = settledAt,
            AmountReceived = settledAt is not null ? LightMoney.MilliSatoshis(result.GetProperty("amount").GetInt64()) : null
        };
    }

    public Task<LightningInvoice> GetInvoice(uint256 paymentHash, CancellationToken cancellation = default)
        => GetInvoice(paymentHash.ToString(), cancellation);

    public async Task<LightningNodeInformation> GetInfo(CancellationToken cancellation = default)
    {
        var response = await SendNwcRequest(new { method = "get_info", @params = new { } }, cancellation);
        var result = response.RootElement.GetProperty("result");
        return new LightningNodeInformation
        {
            Alias = result.TryGetProperty("alias", out var a) ? a.GetString() : "NWC wallet"
        };
    }

    public Task<ILightningInvoiceListener> Listen(CancellationToken cancellation = default)
        => throw new NotSupportedException(
            "Needs NIP-47's kind:23196 notification events - separate from the request/response flow above. " +
            "BTCPay's existing GetInvoice polling still detects payments in the meantime, just not instantly.");

    public async Task<PayResponse> Pay(string bolt11, PayInvoiceParams payParams, CancellationToken cancellation = default)
    {
        var response = await SendNwcRequest(new { method = "pay_invoice", @params = new { invoice = bolt11 } }, cancellation);
        var result = response.RootElement.GetProperty("result");
        return new PayResponse(PayResult.Ok, new PayDetails
        {
            Preimage = uint256.Parse(result.GetProperty("preimage").GetString()!),
            TotalAmount = result.TryGetProperty("fees_paid", out var fee) ? LightMoney.MilliSatoshis(fee.GetInt64()) : null
        });
    }

    public Task<PayResponse> Pay(string bolt11, CancellationToken cancellation = default)
        => Pay(bolt11, new PayInvoiceParams(), cancellation);

    public Task<PayResponse> Pay(PayInvoiceParams payParams, CancellationToken cancellation = default)
        => throw new NotSupportedException("pay_invoice needs the bolt11 string directly - no keysend-only path wired up.");

    public async Task<LightningNodeBalance> GetBalance(CancellationToken cancellation = default)
    {
        var response = await SendNwcRequest(new { method = "get_balance", @params = new { } }, cancellation);
        var msats = response.RootElement.GetProperty("result").GetProperty("balance").GetInt64();
        var balance = LightMoney.MilliSatoshis(msats);
        // NWC has no local/remote/opening/closing channel-level breakdown - it's a single
        // wallet-level number. Everything but Local is left at zero rather than guessed.
        var offchain = new OffchainBalance
        {
            Local = balance,
            Remote = LightMoney.Zero,
            Opening = LightMoney.Zero,
            Closing = LightMoney.Zero
        };
        return new LightningNodeBalance(onchain: null, offchain: offchain);
    }

    // Permanent, not a TODO - NIP-47 has no channel-level concept at all. NWC is a
    // wallet-level abstraction by design; there is no method this can ever map to.
    public Task<LightningChannel[]> ListChannels(CancellationToken cancellation = default)
        => throw new NotSupportedException("NWC (NIP-47) has no channel-level concept - this is a protocol limitation, not a gap.");

    public Task<LightningInvoice[]> ListInvoices(CancellationToken cancellation = default) => throw new NotSupportedException();
    public Task<LightningInvoice[]> ListInvoices(ListInvoicesParams request, CancellationToken cancellation = default) => throw new NotSupportedException();
    public Task<LightningPayment> GetPayment(string paymentHash, CancellationToken cancellation = default) => throw new NotSupportedException();
    public Task<LightningPayment[]> ListPayments(CancellationToken cancellation = default) => throw new NotSupportedException();
    public Task<LightningPayment[]> ListPayments(ListPaymentsParams request, CancellationToken cancellation = default) => throw new NotSupportedException();
    public Task<OpenChannelResponse> OpenChannel(OpenChannelRequest openChannelRequest, CancellationToken cancellation = default) => throw new NotSupportedException();
    public Task<BitcoinAddress> GetDepositAddress(CancellationToken cancellation = default) => throw new NotSupportedException();
    public Task<ConnectionResult> ConnectTo(NodeInfo nodeInfo, CancellationToken cancellation = default) => throw new NotSupportedException();
    public Task CancelInvoice(string invoiceId, CancellationToken cancellation = default) => throw new NotSupportedException();
}
