namespace Sidey.Core.Domain;

public enum PresenceState
{
    Online,
    Typing,
    Away,
    Offline,
    Reconnecting,
}

public enum OverlayEdge
{
    Bottom,
    Left,
    Right,
    Top,
}

public enum OverlaySpan
{
    Third,
    Half,
    Full,
}

public static class OverlaySpanExtensions
{
    public static double Fraction(this OverlaySpan span) => span switch
    {
        OverlaySpan.Third => 1d / 3d,
        OverlaySpan.Half => 1d / 2d,
        OverlaySpan.Full => 1d,
        _ => throw new ArgumentOutOfRangeException(nameof(span)),
    };
}

public sealed record Profile(
    Guid Id,
    string Nickname,
    string CharacterId,
    string? EquippedBubbleStyleId = null,
    string? EquippedThrowableId = null,
    bool TreeMovementPaused = false,
    long? TreeMovementRevision = null);

public sealed record RoomMember(
    Guid UserId,
    string Nickname,
    string CharacterId,
    PresenceState Presence,
    string? EquippedBubbleStyleId = null,
    bool TreeMovementPaused = false,
    long? TreeMovementRevision = null);

public sealed record Room(
    Guid Id,
    string Name,
    Guid OwnerId,
    IReadOnlyList<RoomMember> Members,
    string InviteCodeHint,
    bool InviteCodeReady,
    long RealtimeEpoch);

public sealed record ChatMessage(
    Guid Id,
    Guid RoomId,
    Guid SenderId,
    string Body,
    DateTimeOffset CreatedAt,
    string? BubbleStyleId = null);

public sealed record PixelWorldMember(
    Guid Id,
    string Nickname,
    string CharacterId,
    PresenceState Presence,
    bool IsTyping,
    bool IsCurrentUser,
    string? EquippedBubbleStyleId = null,
    bool TreeMovementPaused = false,
    long? TreeMovementRevision = null,
    bool IsOverlaySuppressed = false);

public sealed record CharacterPulseEvent(Guid Id, Guid RoomId, Guid UserId);

public sealed record CharacterThrowEvent(
    Guid Id,
    Guid RoomId,
    Guid ActorUserId,
    Guid TargetUserId,
    string SourceCharacterId,
    string? ThrowableId = null);

public enum CommerceProductKind
{
    Character,
    Bubble,
    Throwable,
}

public enum CommercePurchaseState
{
    Unavailable,
    GoogleConnectionRequired,
    Available,
    OpeningCheckout,
    Confirming,
    Owned,
    Refunded,
    Error,
}

public sealed record CommerceProduct(
    string Id,
    string CharacterId,
    string EntitlementKey,
    int SortOrder,
    int AmountKrw,
    CommerceProductKind Kind = CommerceProductKind.Character,
    string? CatalogItemId = null,
    string? RenderAssetId = null,
    string? RelatedCharacterProductId = null)
{
    public string EffectiveCatalogItemId => CatalogItemId ?? CharacterId;
}

public sealed record CommerceProductState(
    CommerceProduct Product,
    bool GoogleConnected,
    CommercePurchaseState PurchaseState,
    bool IsWorking = false,
    string? ErrorMessage = null);

public static class CharacterThrowTargetPolicy
{
    public static bool CanTarget(PixelWorldMember member) => !member.IsCurrentUser;
}

public sealed record ActiveBubble(
    Guid SenderId,
    Guid MessageId,
    string Body,
    DateTimeOffset ExpiresAt,
    string? BubbleStyleId = null);

public static class CosmeticCatalog
{
    public static IReadOnlySet<string> BubbleStyleIds { get; } = new HashSet<string>(
        WindowsCommerceCatalog.Products
            .Where(product => product.Kind == CommerceProductKind.Bubble)
            .Select(product => product.EffectiveCatalogItemId),
        StringComparer.Ordinal);

    public static IReadOnlySet<string> ThrowableIds { get; } = new HashSet<string>(
        WindowsCommerceCatalog.Products
            .Where(product => product.Kind == CommerceProductKind.Throwable)
            .Select(product => product.EffectiveCatalogItemId),
        StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, string> s_throwableAssets =
        WindowsCommerceCatalog.Products.Where(product => product.Kind == CommerceProductKind.Throwable)
            .SelectMany(product => new[] { product.EffectiveCatalogItemId, product.RenderAssetId! }
                .Distinct(StringComparer.Ordinal).Select(id => KeyValuePair.Create(id, product.RenderAssetId!)))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

    public static string ResolveThrowableAssetId(string? id) =>
        id is not null && s_throwableAssets.TryGetValue(id, out string? assetId) ? assetId : "patch_soft_ball";

    public static string? NormalizeBubbleStyleId(string? id) =>
        id is not null && BubbleStyleIds.Contains(id) ? id : null;

    public static string? NormalizeThrowableId(string? id) =>
        id is not null && ThrowableIds.Contains(id) ? id : null;
}

public sealed record OverlayRegionPreference(
    OverlayEdge Edge,
    OverlaySpan Span,
    string? MonitorIdentifier)
{
    public static OverlayRegionPreference Default { get; } =
        new(OverlayEdge.Bottom, OverlaySpan.Full, null);
}

public sealed record WorldSnapshot(
    Guid? RoomId,
    IReadOnlyList<PixelWorldMember> Members,
    IReadOnlyList<ActiveBubble> Bubbles,
    IReadOnlyList<CharacterPulseEvent> Pulses,
    IReadOnlyList<CharacterThrowEvent> Throws,
    OverlayEdge Edge,
    long InstallationSeed,
    bool TreeMovementPaused = false);
