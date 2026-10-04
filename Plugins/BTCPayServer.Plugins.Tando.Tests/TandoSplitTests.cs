using System.Text.Json;
using BTCPayServer.Client;
using BTCPayServer.Client.Models;
using BTCPayServer.Controllers;
using BTCPayServer.Plugins.Tando.Services;
using BTCPayServer.Plugins.Tando.ViewModels;
using BTCPayServer.Tests;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Playwright;

namespace BTCPayServer.Plugins.Tando.Tests;

[Collection("Plugin Tests")]
[Trait("Category", "PlaywrightUITest")]
public class TandoSplitTests : PlaywrightBaseTest
{
    private readonly SharedPluginTestFixture _fixture;

    public TandoSplitTests(SharedPluginTestFixture fixture, ITestOutputHelper helper) : base(helper)
    {
        _fixture = fixture;
        if (_fixture.ServerTester == null) _fixture.Initialize(this);
        ServerTester = _fixture.ServerTester;
    }

    public ServerTester ServerTester { get; }

    private async Task EnsureTandoSubscriptionConfigured(TestAccount admin)
    {
        var client = await admin.CreateClient();
        var offering = await client.CreateOffering(admin.StoreId, new OfferingModel { AppName = "Tando Test Offering" });
        var plan = await client.CreateOfferingPlan(admin.StoreId, offering.Id, new() { Name = "Trial Plan", Price = 0m, TrialDays = 7 });
        var subscriptionService = ServerTester.PayTester.GetService<TandoSubscriptionService>();
        await subscriptionService.SaveSettings(new TandoSettings
        {
            SubscriptionOfferingId = offering.Id,
            SubscriptionPlanId = plan.Id
        });
    }

    private async Task<TestAccount> NewAdminAccount()
    {
        var a = ServerTester.NewAccount();
        await a.GrantAccessAsync();
        await a.MakeAdmin();
        return a;
    }

    private async Task<string> MintApiKey(TestAccount account, string storeId = null)
    {
        var manageController = ServerTester.PayTester.GetController<UIManageController>(
            account.UserId, account.StoreId, account.IsAdmin);

        var permissionValue = new UIManageController.AddApiKeyViewModel.PermissionValueItem
        {
            Permission = Policies.CanModifyStoreSettings,
            Value = true,
            StoreMode = storeId is null
                ? UIManageController.AddApiKeyViewModel.ApiKeyStoreMode.AllStores
                : UIManageController.AddApiKeyViewModel.ApiKeyStoreMode.Specific,
            SpecificStores = storeId is null ? new List<string>() : new List<string> { storeId }
        };

        var label = $"tando-test-{Guid.NewGuid():N}";
        var createResult = await manageController.AddApiKey(new UIManageController.AddApiKeyViewModel
        {
            Label = label,
            PermissionValues = new List<UIManageController.AddApiKeyViewModel.PermissionValueItem> { permissionValue }
        });
        Assert.IsType<RedirectToActionResult>(createResult);

        var listResult = await manageController.APIKeys();
        var view = Assert.IsType<ViewResult>(listResult);
        var vm = Assert.IsType<UIManageController.ApiKeysViewModel>(view.Model);
        return vm.ApiKeyDatas.Single(k => k.Label == label).Id;
    }

    private async Task<IAPIRequestContext> ApiContextFor(string apiKey)
    {
        return await Playwright.APIRequest.NewContextAsync(new APIRequestNewContextOptions
        {
            BaseURL = ServerUri.ToString(),
            ExtraHTTPHeaders = new Dictionary<string, string>
            {
                ["Authorization"] = $"token {apiKey}",
                ["Content-Type"] = "application/json"
            },
            IgnoreHTTPSErrors = true
        });
    }

    private static string RandomKenyanPhone() => $"07{Random.Shared.Next(10000000, 99999999)}";

    private static async Task<JsonElement> ParseJson(IAPIResponse response)
        => JsonDocument.Parse(await response.TextAsync()).RootElement;

    private async Task<string> SignupAndGetStoreId(IAPIRequestContext api)
    {
        var response = await api.PostAsync("/plugins/api/tando/signup",
            new APIRequestContextOptions { DataObject = new { phoneNumber = RandomKenyanPhone() } });
        var body = await response.TextAsync();
        Assert.True(response.Ok, $"Signup failed ({response.Status}): {body}");
        return JsonDocument.Parse(body).RootElement.GetProperty("storeId").GetString();
    }

    private async Task<string> CreateInvoice(TestAccount admin, string storeId, decimal amount)
    {
        var client = await admin.CreateClient();
        var invoice = await client.CreateInvoice(storeId, new CreateInvoiceRequest
        {
            Amount = amount,
            Currency = "KES"
        });
        return invoice.Id;
    }

    [Fact]
    public async Task ComputeSplit_AtZeroPercent_DefaultsToNotTriggeredAndSettled()
    {
        await InitializePlaywright(ServerTester);
        var admin = await NewAdminAccount();
        await EnsureTandoSubscriptionConfigured(admin);
        var adminKey = await MintApiKey(admin);
        var api = await ApiContextFor(adminKey);

        var storeId = await SignupAndGetStoreId(api);
        var invoiceId = await CreateInvoice(admin, storeId, 1000m);

        // split-config defaults to 0% (no PUT called), so ComputeAndRecordSplit
        // should never reach CreateAndClaimPayout at all.
        var compute = await api.PostAsync($"/plugins/api/tando/stores/{storeId}/invoices/{invoiceId}/split");
        Assert.Equal(200, compute.Status);
        var record = await ParseJson(compute);

        Assert.Equal("NotTriggered", record.GetProperty("mpesaPayoutStatus").GetString());
        Assert.True(record.GetProperty("mpesaSettled").GetBoolean());
        Assert.Equal(0m, record.GetProperty("mpesaPortionAmount").GetDecimal());
    }

    [Fact]
    public async Task UpdateMpesaPayoutStatus_ToConfirmed_MarksRecordSettledWithReference()
    {
        await InitializePlaywright(ServerTester);
        var admin = await NewAdminAccount();
        await EnsureTandoSubscriptionConfigured(admin);
        var adminKey = await MintApiKey(admin);
        var api = await ApiContextFor(adminKey);

        var storeId = await SignupAndGetStoreId(api);
        var invoiceId = await CreateInvoice(admin, storeId, 500m);

        var compute = await api.PostAsync($"/plugins/api/tando/stores/{storeId}/invoices/{invoiceId}/split");
        Assert.Equal(200, compute.Status);

        var update = await api.PutAsync($"/plugins/api/tando/stores/{storeId}/invoices/{invoiceId}/mpesa-payout-status",
            new APIRequestContextOptions
            {
                DataObject = new { status = "Confirmed", payoutReference = "TANDO-REF-123" }
            });
        Assert.Equal(200, update.Status);

        var get = await api.GetAsync($"/plugins/api/tando/stores/{storeId}/invoices/{invoiceId}/split");
        Assert.Equal(200, get.Status);
        var record = await ParseJson(get);

        Assert.Equal("Confirmed", record.GetProperty("mpesaPayoutStatus").GetString());
        Assert.Equal("TANDO-REF-123", record.GetProperty("mpesaPayoutReference").GetString());
        Assert.True(record.GetProperty("mpesaSettled").GetBoolean());
        Assert.False(record.GetProperty("mpesaSettledAt").ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task UpdateMpesaPayoutStatus_UnknownInvoice_Returns404()
    {
        await InitializePlaywright(ServerTester);
        var admin = await NewAdminAccount();
        await EnsureTandoSubscriptionConfigured(admin);
        var adminKey = await MintApiKey(admin);
        var api = await ApiContextFor(adminKey);

        var storeId = await SignupAndGetStoreId(api);

        var update = await api.PutAsync($"/plugins/api/tando/stores/{storeId}/invoices/does-not-exist/mpesa-payout-status",
            new APIRequestContextOptions
            {
                DataObject = new { status = "Confirmed", payoutReference = (string)null }
            });

        Assert.Equal(404, update.Status);
        var body = await ParseJson(update);
        Assert.Equal("invoice_not_found", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task ListSplits_ReturnsComputedRecordsForStore()
    {
        await InitializePlaywright(ServerTester);
        var admin = await NewAdminAccount();
        await EnsureTandoSubscriptionConfigured(admin);
        var adminKey = await MintApiKey(admin);
        var api = await ApiContextFor(adminKey);

        var storeId = await SignupAndGetStoreId(api);

        var invoiceAId = await CreateInvoice(admin, storeId, 200m);
        var invoiceBId = await CreateInvoice(admin, storeId, 300m);
        await api.PostAsync($"/plugins/api/tando/stores/{storeId}/invoices/{invoiceAId}/split");
        await api.PostAsync($"/plugins/api/tando/stores/{storeId}/invoices/{invoiceBId}/split");

        var list = await api.GetAsync($"/plugins/api/tando/stores/{storeId}/splits");
        Assert.Equal(200, list.Status);
        var records = await ParseJson(list);

        Assert.True(records.GetArrayLength() >= 2,
            $"Expected at least 2 split records, got {records.GetArrayLength()}.");
        var totals = records.EnumerateArray().Select(r => r.GetProperty("totalAmount").GetDecimal()).ToList();
        Assert.Contains(200m, totals);
        Assert.Contains(300m, totals);
    }
}