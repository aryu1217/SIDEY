using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Sidey.Core.Abstractions;
using Sidey.Core.Domain;

namespace Sidey.Platform.Windows.Tests;

public sealed class StoreStateGatewayTests
{
    [Fact]
    public async Task PublicStoreStateRpcLoadsTheCompleteCatalogAndMapsPurchaseEligibility()
    {
        List<Dictionary<string, object?>> rows = CatalogRows();
        rows[0]["entitlement_status"] = "active";
        rows[0]["latest_order_status"] = "refunded";
        rows[1]["latest_order_status"] = "refunded";
        rows[2]["google_connected"] = false;
        await using var context = new StoreRequestContext(rows);

        IReadOnlyList<CommerceProductState> states = await context.Backend.GetWindowsCommerceStateAsync();

        Assert.Equal(33, states.Count);
        Assert.Equal(WindowsCommerceCatalog.Products.Select(product => product.Id), states.Select(state => state.Product.Id));
        Assert.Equal(CommercePurchaseState.Owned, states[0].PurchaseState);
        Assert.Equal(CommercePurchaseState.Refunded, states[1].PurchaseState);
        Assert.Equal(CommercePurchaseState.GoogleConnectionRequired, states[2].PurchaseState);
        Assert.False(states[2].GoogleConnected);
        Assert.All(states.Skip(3), state =>
        {
            Assert.Equal(CommercePurchaseState.Available, state.PurchaseState);
            Assert.True(state.GoogleConnected);
        });
        Assert.Equal(1, context.Handler.RequestCount);
    }

    [Fact]
    public async Task ActiveServerPricesReplaceBundledPricesWithoutChangingProductIdentity()
    {
        List<Dictionary<string, object?>> rows = CatalogRows();
        int[] prices = [990, 1900, 2900];
        for (int index = 0; index < rows.Count; index++)
        {
            rows[index]["amount_krw"] = prices[index % prices.Length];
        }
        await using var context = new StoreRequestContext(rows);

        IReadOnlyList<CommerceProductState> states = await context.Backend.GetWindowsCommerceStateAsync();

        for (int index = 0; index < states.Count; index++)
        {
            Assert.Equal(WindowsCommerceCatalog.Products[index] with
            {
                AmountKrw = prices[index % prices.Length],
            }, states[index].Product);
            Assert.Equal(CommercePurchaseState.Available, states[index].PurchaseState);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task NonPositiveServerPriceCannotEnablePurchases(int amount)
    {
        List<Dictionary<string, object?>> rows = CatalogRows();
        rows[0]["amount_krw"] = amount;
        await using var context = new StoreRequestContext(rows);

        await Assert.ThrowsAsync<InvalidDataException>(() => context.Backend.GetWindowsCommerceStateAsync());
    }

    [Fact]
    public async Task MissingServerProductStaysUnavailableWhileOtherProductsRemainPurchasable()
    {
        List<Dictionary<string, object?>> rows = CatalogRows();
        string missingId = (string)rows[^1]["product_id"]!;
        rows.RemoveAt(rows.Count - 1);
        await using var context = new StoreRequestContext(rows);

        IReadOnlyList<CommerceProductState> states = await context.Backend.GetWindowsCommerceStateAsync();

        CommerceProductState missing = Assert.Single(states, state => state.Product.Id == missingId);
        Assert.Equal(CommercePurchaseState.Unavailable, missing.PurchaseState);
        Assert.False(missing.GoogleConnected);
        Assert.All(states.Where(state => state.Product.Id != missingId), state =>
            Assert.Equal(CommercePurchaseState.Available, state.PurchaseState));
    }

    [Theory]
    [InlineData("currency", "USD")]
    [InlineData("product_kind", "bubble")]
    [InlineData("entitlement_key", "character:unapproved")]
    public async Task IncompatibleServerCatalogCannotEnablePurchases(string field, string value)
    {
        List<Dictionary<string, object?>> rows = CatalogRows();
        rows[0][field] = value;
        await using var context = new StoreRequestContext(rows);

        await Assert.ThrowsAsync<InvalidDataException>(() => context.Backend.GetWindowsCommerceStateAsync());
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task RpcFailureReachesTheCallerInsteadOfReturningAnAvailableCatalog(HttpStatusCode status)
    {
        await using var context = new StoreRequestContext(CatalogRows(), status);

        HttpRequestException exception = await Assert.ThrowsAnyAsync<HttpRequestException>(
            () => context.Backend.GetWindowsCommerceStateAsync());

        Assert.Equal(status, exception.StatusCode);
        Assert.Equal(1, context.Handler.RequestCount);
    }

    private static List<Dictionary<string, object?>> CatalogRows() =>
        [.. WindowsCommerceCatalog.Products.Select(product => new Dictionary<string, object?>
        {
            ["product_id"] = product.Id,
            ["product_kind"] = product.Kind.ToString().ToLowerInvariant(),
            ["catalog_item_id"] = product.EffectiveCatalogItemId,
            ["character_id"] = product.Kind == CommerceProductKind.Character ? product.CharacterId : null,
            ["entitlement_key"] = product.EntitlementKey,
            ["sort_order"] = product.SortOrder,
            ["amount_krw"] = product.AmountKrw,
            ["currency"] = "KRW",
            ["google_connected"] = true,
            ["entitlement_status"] = null,
            ["latest_order_status"] = null,
        })];

    private sealed class StoreRequestContext : IAsyncDisposable
    {
        private readonly HttpClient _client;
        private readonly SupabaseAnonymousAuthService _auth;

        public StoreRequestContext(List<Dictionary<string, object?>> rows, HttpStatusCode status = HttpStatusCode.OK)
        {
            Handler = new StoreStateHandler(JsonSerializer.Serialize(rows), status);
            _client = new HttpClient(Handler);
            ICredentialStore credentials = DispatchProxy.Create<ICredentialStore, ReadOnlySessionCredentials>();
            var configuration = new SupabaseRuntimeConfiguration(new Uri("https://store.example.invalid"), "test-key");
            _auth = new SupabaseAnonymousAuthService(configuration, credentials, _client);
            Backend = new SupabaseBackendGateway(configuration, _auth, credentials, _client);
        }

        public StoreStateHandler Handler { get; }
        public SupabaseBackendGateway Backend { get; }

        public async ValueTask DisposeAsync()
        {
            await Backend.DisposeAsync();
            _auth.Dispose();
            _client.Dispose();
        }
    }

    private sealed class StoreStateHandler(string responseJson, HttpStatusCode status) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://store.example.invalid/rest/v1/rpc/get_store_state", request.RequestUri?.AbsoluteUri);
            Assert.Equal("test-key", Assert.Single(request.Headers.GetValues("apikey")));
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("store-test-token", request.Headers.Authorization?.Parameter);
            Assert.Equal("{}", await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
            };
        }
    }

    public class ReadOnlySessionCredentials : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Assert.Equal(nameof(ICredentialStore.ReadAsync), targetMethod?.Name);
            Assert.Equal(CredentialKey.SupabaseSession, args![0]);
            string session = JsonSerializer.Serialize(new StoredSupabaseSession(
                "store-test-token", "test-refresh-token", Guid.Parse("0d5ab36b-56e0-49f7-afb3-c9cb32c25b29"), DateTimeOffset.MaxValue),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return new ValueTask<string?>(session);
        }
    }
}
