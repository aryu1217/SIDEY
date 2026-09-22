using System.Text.Json;
using Sidey.Core.Domain;

namespace Sidey.Platform.Windows.Tests;

public sealed class WindowsCommerceCatalogTests
{
    [Fact]
    public void BundledProductsDecodeThePinnedPlatformCatalog()
    {
        // The generator separately verifies this platform snapshot against its pinned Git source.
        using var approved = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestAssets", "commerce-catalog.json")));
        Assert.Equal(approved.RootElement.GetArrayLength(), WindowsCommerceCatalog.Products.Count);
        foreach (JsonElement entry in approved.RootElement.EnumerateArray())
        {
            CommerceProduct product = Assert.IsType<CommerceProduct>(WindowsCommerceCatalog.Find(entry.GetProperty("id").GetString()!));
            Assert.Equal(entry.GetProperty("entitlement").GetString(), product.EntitlementKey);
            Assert.Equal(entry.GetProperty("item_id").GetString(), product.EffectiveCatalogItemId);
            Assert.Equal(entry.GetProperty("direct_price").GetInt32(), product.AmountKrw);
            Assert.Equal(entry.GetProperty("sort_order").GetInt32(), product.SortOrder);
            if (entry.TryGetProperty("render_asset_id", out JsonElement asset))
            {
                Assert.Equal(asset.GetString(), product.RenderAssetId);
            }
        }
    }

    [Fact]
    public void CatalogContainsAllApprovedProductsAndSeparatesKeepsakesFromCharacters()
    {
        Assert.Equal(33, WindowsCommerceCatalog.Products.Count);
        Assert.Equal(12, WindowsCommerceCatalog.Products.Count(product => product.Kind == CommerceProductKind.Character));
        Assert.Equal(3, WindowsCommerceCatalog.Products.Count(product => product.Kind == CommerceProductKind.Bubble));
        Assert.Equal(18, WindowsCommerceCatalog.Products.Count(product => product.Kind == CommerceProductKind.Throwable));
        Assert.Equal(33, WindowsCommerceCatalog.Products.Select(product => product.Id).Distinct().Count());
        foreach (CommerceProduct character in WindowsCommerceCatalog.Products.Where(product => product.Kind == CommerceProductKind.Character))
        {
            CommerceProduct keepsake = Assert.IsType<CommerceProduct>(WindowsCommerceCatalog.KeepsakeFor(character.CharacterId));
            Assert.Equal(character.Id, keepsake.RelatedCharacterProductId);
            Assert.NotEqual(character.EntitlementKey, keepsake.EntitlementKey);
            Assert.Contains(keepsake.EffectiveCatalogItemId, CosmeticCatalog.ThrowableIds);
        }
        Assert.All(WindowsCommerceCatalog.Products, product => Assert.Equal(
            $"{product.Kind.ToString().ToLowerInvariant()}:{product.EffectiveCatalogItemId}", product.EntitlementKey));
        Assert.Equal("banana", CosmeticCatalog.ResolveThrowableAssetId("throwable_banana"));
        Assert.Equal("timber", CosmeticCatalog.ResolveThrowableAssetId("throwable_timber"));
        Assert.Null(CosmeticCatalog.NormalizeThrowableId("banana"));
        Assert.Null(CosmeticCatalog.NormalizeThrowableId("unknown"));
    }

    [Fact]
    public void PubliclyLockedStateDoesNotExposePurchaseActions()
    {
        IReadOnlyList<CommerceProductState> states = WindowsCommerceCatalog.LockedStates();

        Assert.Equal(33, states.Count);
        Assert.All(states, state =>
        {
            Assert.False(state.GoogleConnected);
            Assert.False(state.IsWorking);
            Assert.Equal(CommercePurchaseState.Unavailable, state.PurchaseState);
        });
    }
}
