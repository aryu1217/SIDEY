using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using Sidey.Core.Domain;

namespace Sidey.Core.Tests;

public sealed class CharacterCatalogTests
{
    [Fact]
    public void ProductCatalogContainsAllSeventeenRenderableCharactersAndFiveSelectableCharacters()
    {
        Assert.Equal(
            [
                "pixel_hamster", "pixel_cat", "pixel_puppy", "pixel_rabbit", "pixel_penguin",
                "pixel_guinea_pig", "pixel_monkey", "pixel_chinchilla", "pixel_starlight_upalupa",
                "pixel_otter", "pixel_pig", "pixel_tree", "pixel_shiba", "pixel_duck", "pixel_poop",
                "pixel_tteokbokki", "pixel_quokka",
            ],
            PixelCharacterCatalog.All.Select(character => character.Id));
        Assert.Equal(
            ["pixel_hamster", "pixel_cat", "pixel_puppy", "pixel_rabbit", "pixel_penguin"],
            PixelCharacterCatalog.Selectable.Select(character => character.Id));

        Assert.All(PixelCharacterCatalog.All, character =>
        {
            Assert.Equal($"Characters/{character.Id}/base.png", character.SpriteSheetResource);
            Assert.Equal($"Characters/{character.Id}/base.bgra", character.RawBgraResource);
            Assert.Equal($"Characters/{character.Id}/manifest.json", character.ManifestResource);
            Assert.Equal(24, character.FrameWidth);
            Assert.Equal(24, character.FrameHeight);
            Assert.Equal(10, character.FrameCount);
            Assert.Equal(3, character.FootBaselinePixel);
            Assert.Equal(0..2, character.Frames.Idle);
            Assert.Equal(2..6, character.Frames.Walk);
            Assert.Equal(6..8, character.Frames.Doze);
            Assert.Equal(8..10, character.Frames.Offline);
        });
    }

    [Fact]
    public void ActiveEntitlementsAddOnlyTheOwnedCharactersToTheSelectableCatalog()
    {
        var entitlements = new HashSet<string>(StringComparer.Ordinal)
        {
            "character:pixel_guinea_pig",
            "character:pixel_chinchilla",
        };

        Assert.Equal(
            [
                "pixel_hamster", "pixel_cat", "pixel_puppy", "pixel_rabbit", "pixel_penguin",
                "pixel_guinea_pig", "pixel_chinchilla",
            ],
            PixelCharacterCatalog.SelectableFor(entitlements).Select(character => character.Id));
    }

    [Fact]
    public void UnavailableCommerceSnapshotPreservesTheSelectedEntitledCharacter()
    {
        IReadOnlySet<string> resolved = PixelCharacterCatalog.ResolveActiveEntitlementKeys(
            remoteKeys: null,
            profileCharacterId: "pixel_monkey");

        Assert.Equal(["character:pixel_monkey"], resolved);
    }

    [Fact]
    public void SuccessfulEmptyCommerceSnapshotDoesNotPreserveARevokedCharacter()
    {
        IReadOnlySet<string> resolved = PixelCharacterCatalog.ResolveActiveEntitlementKeys(
            new HashSet<string>(StringComparer.Ordinal),
            "pixel_monkey");

        Assert.Empty(resolved);
    }

    [Fact]
    public void RevokedEntitledCharacterFallsBackToTheDefaultSelection()
    {
        var noEntitlements = new HashSet<string>(StringComparer.Ordinal);

        Assert.False(PixelCharacterCatalog.CanSelect("pixel_monkey", noEntitlements));
        Assert.Equal(
            PixelCharacterCatalog.FallbackId,
            PixelCharacterCatalog.SelectableId("pixel_monkey", noEntitlements));
        Assert.Equal(
            "pixel_monkey",
            PixelCharacterCatalog.SelectableId(
                "pixel_monkey",
                new HashSet<string>(["character:pixel_monkey"], StringComparer.Ordinal)));
    }

    [Fact]
    public void StarlightUpalupaDeclaresItsCatalogDrivenSparkleEffect()
    {
        Assert.Equal(
            PixelCharacterVisualEffect.StarlightSparkles,
            PixelCharacterCatalog.Get("pixel_starlight_upalupa").VisualEffect);
        Assert.All(
            PixelCharacterCatalog.All.Where(character => character.Id != "pixel_starlight_upalupa"),
            character => Assert.Equal(PixelCharacterVisualEffect.None, character.VisualEffect));
    }

    [Fact]
    public void MovementFacingMatchesTheMacCharacterContract()
    {
        Assert.Equal(
            ["pixel_guinea_pig", "pixel_starlight_upalupa"],
            PixelCharacterCatalog.All
                .Where(character => character.MirrorsToMovementDirection)
                .Select(character => character.Id));
    }

    [Theory]
    [InlineData("pixel_cat", "pixel_cat")]
    [InlineData("pixel_guinea_pig", "pixel_guinea_pig")]
    [InlineData("pixel_starlight_upalupa", "pixel_starlight_upalupa")]
    [InlineData("pixel_koala", "pixel_chinchilla")]
    [InlineData("minty_pup", "pixel_hamster")]
    [InlineData("not_a_character", "pixel_hamster")]
    [InlineData(null, "pixel_hamster")]
    public void AliasAndUnknownIdsNormalizeWithoutSpeciesBranches(string? value, string expected)
    {
        Assert.Equal(expected, PixelCharacterCatalog.NormalizeId(value));
        Assert.Equal(expected, PixelCharacterCatalog.Get(value).Id);
    }

    [Fact]
    public void EveryAssetAndManifestMatchesTheCatalogContract()
    {
        foreach (PixelCharacterDefinition character in PixelCharacterCatalog.All)
        {
            string pngPath = AssetPath(character.Id, "base.png");
            byte[] png = File.ReadAllBytes(pngPath);
            Assert.True(png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }));
            Assert.Equal("IHDR", System.Text.Encoding.ASCII.GetString(png, 12, 4));
            Assert.Equal(240, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4)));
            Assert.Equal(24, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4)));
            Assert.Equal(8, png[24]);
            Assert.Equal(6, png[25]);
            Assert.Equal(character.SpriteSheetSha256, Convert.ToHexStringLower(SHA256.HashData(png)));

            byte[] raw = File.ReadAllBytes(AssetPath(character.Id, "base.bgra"));
            Assert.Equal(240 * 24 * 4, raw.Length);

            using var manifest = JsonDocument.Parse(File.ReadAllBytes(
                AssetPath(character.Id, "manifest.json")));
            JsonElement root = manifest.RootElement;
            Assert.Equal(character.Id, root.GetProperty("character_id").GetString());
            Assert.Equal(character.DisplayName, root.GetProperty("display_name").GetString());
            Assert.Equal([24, 24], root.GetProperty("frame_pixel_size").EnumerateArray().Select(value => value.GetInt32()));
            Assert.Equal([240, 24], root.GetProperty("sheet_pixel_size").EnumerateArray().Select(value => value.GetInt32()));
            Assert.Equal(3, root.GetProperty("foot_baseline_pixel").GetInt32());
            Assert.Equal(character.SpriteSheetSha256, root.GetProperty("sha256").GetString());
            Assert.Equal(
                character.CompatibleAliases,
                [.. root.GetProperty("legacy_aliases").EnumerateArray().Select(value => value.GetString()!)]);
            JsonElement animations = root.GetProperty("animations");
            Assert.Equal([0, 1], Animation(animations, "idle"));
            Assert.Equal([2, 3, 4, 5], Animation(animations, "walk"));
            Assert.Equal([6, 7], Animation(animations, "doze"));
            Assert.Equal([8, 9], Animation(animations, "offline"));
            Assert.Equal(23040, root.GetProperty("runtime_bgra").GetProperty("byte_length").GetInt32());
            Assert.Equal("straight", root.GetProperty("runtime_bgra").GetProperty("alpha").GetString());
            Assert.Equal(
                Convert.ToHexStringLower(SHA256.HashData(raw)),
                root.GetProperty("runtime_bgra").GetProperty("sha256").GetString());
        }
    }

    private static string AssetPath(string characterId, string name) =>
        Path.Combine(AppContext.BaseDirectory, "TestAssets", characterId, name);

    private static int[] Animation(JsonElement animations, string name) =>
        [.. animations.GetProperty(name).EnumerateArray().Select(value => value.GetInt32())];
}
