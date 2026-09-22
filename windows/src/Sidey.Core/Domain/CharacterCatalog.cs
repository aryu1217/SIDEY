namespace Sidey.Core.Domain;

public enum PixelCharacterVisualEffect
{
    None,
    StarlightSparkles,
}

public sealed record PixelCharacterDefinition(
    string Id,
    string DisplayNameKey,
    string SpriteSheetResource,
    string RawBgraResource,
    string ManifestResource,
    string SpriteSheetSha256,
    int FrameWidth,
    int FrameHeight,
    int FrameCount,
    int FootBaselinePixel,
    PixelCharacterFrameContract Frames,
    string? EntitlementKey,
    bool MirrorsToMovementDirection,
    PixelCharacterVisualEffect VisualEffect,
    IReadOnlyList<string> CompatibleAliases)
{
    public string DisplayName => Localization.I18n.Get(DisplayNameKey);
}

public sealed record PixelCharacterFrameContract(
    Range Idle,
    Range Walk,
    Range Doze,
    Range Offline)
{
    public static PixelCharacterFrameContract Standard { get; } = new(
        0..2,
        2..6,
        6..8,
        8..10);
}

/// <summary>
/// The only product catalog for built-in Windows characters. Rendering code
/// resolves this data by id and never branches on a species.
/// </summary>
public static class PixelCharacterCatalog
{
    public const string FallbackId = "pixel_hamster";

    private static readonly PixelCharacterDefinition[] s_definitions =
    [
        Create(
            FallbackId,
            "characters.hamster",
            "43171c1dd614629058b6d593c57ca0e5841b0be03a04a05181dfda67c53a7f45",
            ["minty_pup"]),
        Create(
            "pixel_cat",
            "characters.cat",
            "d8b370c03b5cf0ede6aa0d9fa6210030e164b015a920622e89ae86f835e018b2"),
        Create(
            "pixel_puppy",
            "characters.dog",
            "8f56a5fda51a224802f41d6d1c359a138c83036b7da3e0a35777f9f4ed38d5f7"),
        Create(
            "pixel_rabbit",
            "characters.rabbit",
            "f8e53749200a284f7729ea9baac3237a9fac0caf8efedf9102dcee065e521342"),
        Create(
            "pixel_penguin",
            "characters.penguin",
            "f171503f8ffb938732583a4b6f42443e7a69120bb17496f6e8d34372da2ea886"),
        Create(
            "pixel_guinea_pig",
            "characters.guineaPig",
            "1a0bf85dae86f2e6bb460e8b0b852c2bd010d5ff6f7efd1477cbd6986da64f5b",
            entitlementKey: "character:pixel_guinea_pig",
            mirrorsToMovementDirection: true),
        Create(
            "pixel_monkey",
            "characters.monkey",
            "515fe377f5344dd4cbaa2b0faf58de3ce72fdc62be5aff6a9d9de683983c783b",
            entitlementKey: "character:pixel_monkey"),
        Create(
            "pixel_chinchilla",
            "characters.chinchilla",
            "c0009e007a7a63029fb58ad6f94d2b9a8c9ae7a55f139dd4892050f11614c5d4",
            ["pixel_koala"],
            "character:pixel_chinchilla"),
        Create(
            "pixel_starlight_upalupa",
            "characters.starlightUpalupa",
            "d180810a8796280077f3f70f6da681888c583c2f8d74776d0f5d300e943a079a",
            entitlementKey: "character:pixel_starlight_upalupa",
            mirrorsToMovementDirection: true,
            visualEffect: PixelCharacterVisualEffect.StarlightSparkles),
        Create(
            "pixel_otter",
            "characters.otter",
            "38ebed0f4588e4f776df44872c2e81e96d70056fb8c97b19c812433584e4b5db",
            entitlementKey: "character:pixel_otter"),
        Create(
            "pixel_pig",
            "characters.pig",
            "b383c07699cc40fe21c05f18aaf21730888685f9b0874a16b95cd82bebcd6f98",
            entitlementKey: "character:pixel_pig"),
        Create(
            "pixel_tree",
            "characters.tree",
            "ddf40aa115034c2c4fb3046673f6e20d2ca208d0b5a9ffa5737fb79c2e6cc97f",
            entitlementKey: "character:pixel_tree"),
        Create(
            "pixel_shiba",
            "characters.shiba",
            "f0fbdc42e774c4fc005b6712d7361f1032864c71f1dae6a62d4f631483de647f",
            entitlementKey: "character:pixel_shiba"),
        Create(
            "pixel_duck",
            "characters.duck",
            "007e181fc7546b5346fc52746daf60985fe6113f366795a8c90f21d1b084e559",
            entitlementKey: "character:pixel_duck"),
        Create(
            "pixel_poop",
            "characters.poop",
            "0f2853654e953abe92fbfbd8894df1396f28fc32fe06db9b5c3db60197da1c43",
            entitlementKey: "character:pixel_poop"),
        Create(
            "pixel_tteokbokki",
            "characters.tteokbokki",
            "1da9b6df95412bfc52df6dc21157d2708a40f508de48cb3f9c8764ca7a378b92",
            entitlementKey: "character:pixel_tteokbokki"),
        Create(
            "pixel_quokka",
            "characters.quokka",
            "55965cec0be9ac26787c255c92dd6ca5300164a505e12e506be2f87c010dcf3a",
            entitlementKey: "character:pixel_quokka"),
    ];

    private static readonly PixelCharacterDefinition[] s_selectableDefinitions = s_definitions[..5];

    private static readonly IReadOnlyDictionary<string, PixelCharacterDefinition> s_byId =
        s_definitions.ToDictionary(definition => definition.Id, StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, string> s_aliasToId = s_definitions
        .SelectMany(definition => definition.CompatibleAliases.Select(alias => (alias, definition.Id)))
        .ToDictionary(pair => pair.alias, pair => pair.Id, StringComparer.Ordinal);

    public static IReadOnlyList<PixelCharacterDefinition> All => s_definitions;

    /// <summary>
    /// Characters that every account can select without an entitlement.
    /// </summary>
    public static IReadOnlyList<PixelCharacterDefinition> Selectable => s_selectableDefinitions;

    public static IReadOnlyList<PixelCharacterDefinition> SelectableFor(
        IReadOnlySet<string> activeEntitlementKeys)
    {
        ArgumentNullException.ThrowIfNull(activeEntitlementKeys);
        return [.. s_definitions
            .Where(definition => definition.EntitlementKey is null
                || activeEntitlementKeys.Contains(definition.EntitlementKey))];
    }

    public static IReadOnlySet<string> ResolveActiveEntitlementKeys(
        IReadOnlySet<string>? remoteKeys,
        string? profileCharacterId)
    {
        if (remoteKeys is not null)
        {
            return new HashSet<string>(remoteKeys, StringComparer.Ordinal);
        }

        string? entitlementKey = Get(profileCharacterId).EntitlementKey;
        return entitlementKey is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>([entitlementKey], StringComparer.Ordinal);
    }

    public static PixelCharacterDefinition Fallback => s_byId[FallbackId];

    public static string NormalizeId(string? characterId)
    {
        if (characterId is not null && s_byId.ContainsKey(characterId))
        {
            return characterId;
        }

        return characterId is not null && s_aliasToId.TryGetValue(characterId, out string? canonical)
            ? canonical
            : FallbackId;
    }

    public static PixelCharacterDefinition Get(string? characterId) => s_byId[NormalizeId(characterId)];

    public static bool CanSelect(
        string? characterId,
        IReadOnlySet<string> activeEntitlementKeys)
    {
        ArgumentNullException.ThrowIfNull(activeEntitlementKeys);
        PixelCharacterDefinition definition = Get(characterId);
        return definition.EntitlementKey is null
            || activeEntitlementKeys.Contains(definition.EntitlementKey);
    }

    public static string SelectableId(
        string? characterId,
        IReadOnlySet<string> activeEntitlementKeys) =>
        CanSelect(characterId, activeEntitlementKeys)
            ? NormalizeId(characterId)
            : FallbackId;

    private static PixelCharacterDefinition Create(
        string id,
        string displayName,
        string spriteSheetSha256,
        IReadOnlyList<string>? aliases = null,
        string? entitlementKey = null,
        bool mirrorsToMovementDirection = false,
        PixelCharacterVisualEffect visualEffect = PixelCharacterVisualEffect.None) => new(
            id,
            displayName,
            $"Characters/{id}/base.png",
            $"Characters/{id}/base.bgra",
            $"Characters/{id}/manifest.json",
            spriteSheetSha256,
            FrameWidth: 24,
            FrameHeight: 24,
            FrameCount: 10,
            FootBaselinePixel: 3,
            Frames: PixelCharacterFrameContract.Standard,
            EntitlementKey: entitlementKey,
            MirrorsToMovementDirection: mirrorsToMovementDirection,
            VisualEffect: visualEffect,
            CompatibleAliases: aliases ?? []);
}
