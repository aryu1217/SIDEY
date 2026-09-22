using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Authentication;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Sidey.Core.Abstractions;
using Sidey.Core.Domain;
using Sidey.Core.Localization;
using Sidey.Core.Realtime;

namespace Sidey.Infrastructure.Backend;

public sealed class SupabaseBackendGateway : IBackendGateway, IAsyncDisposable
{
    private static readonly TimeSpan s_structuralCoalescingWindow = TimeSpan.FromMilliseconds(150);
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web);

    private readonly SupabaseRuntimeConfiguration _configuration;
    private readonly ICredentialStore _credentials;
    private readonly IAuthSessionAccessor _sessions;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly IRealtimeTransport _realtime;
    private IReadOnlyDictionary<Guid, long> _roomEpochs = new Dictionary<Guid, long>();
    private Guid? _activeRoomId;

    public SupabaseBackendGateway(
        SupabaseRuntimeConfiguration configuration,
        SupabaseAnonymousAuthService auth,
        ICredentialStore credentials,
        HttpClient? httpClient = null,
        string? appVersion = null,
        bool firebaseV2Capable = true)
        : this(
            configuration,
            auth,
            credentials,
            httpClient,
            realtime: null,
            appVersion,
            firebaseV2Capable)
    {
    }

    internal SupabaseBackendGateway(
        SupabaseRuntimeConfiguration configuration,
        SupabaseAnonymousAuthService auth,
        ICredentialStore credentials,
        HttpClient? httpClient,
        IRealtimeTransport? realtime,
        string? appVersion = null,
        bool firebaseV2Capable = true)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _sessions = auth ?? throw new ArgumentNullException(nameof(auth));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _httpClient = httpClient ?? new HttpClient();
        _ownsHttpClient = httpClient is null;
        _realtime = realtime ?? CreateRealtimeTransport(
            configuration,
            auth,
            credentials,
            _httpClient,
            appVersion,
            firebaseV2Capable);
    }

    public async Task<BackendSnapshot> FetchSnapshotAsync(CancellationToken cancellationToken = default)
    {
        StoredSupabaseSession session = await RequiredSessionAsync(cancellationToken).ConfigureAwait(false);
        Task<DatabaseProfile[]> profileTask = GetAsync<DatabaseProfile[]>(
            $"/rest/v1/profiles?id=eq.{session.UserId:D}&select=*",
            cancellationToken);
        Task<DatabaseRoom[]> roomsTask = GetAsync<DatabaseRoom[]>(
            "/rest/v1/rooms?select=*&order=created_at.asc",
            cancellationToken);
        Task<DatabaseMembership[]> membershipsTask = GetAsync<DatabaseMembership[]>(
            "/rest/v1/room_members?select=*&order=joined_at.asc",
            cancellationToken);
        Task<DatabaseProfile[]> profilesTask = GetAsync<DatabaseProfile[]>(
            "/rest/v1/profiles?select=*",
            cancellationToken);
        Task<IReadOnlySet<string>?> entitlementsTask = LoadActiveEntitlementKeysIfAvailableAsync(cancellationToken);

        await Task.WhenAll(profileTask, roomsTask, membershipsTask, profilesTask, entitlementsTask)
            .ConfigureAwait(false);
        Dictionary<Guid, DatabaseProfile> peers = (await profilesTask.ConfigureAwait(false)).ToDictionary(profile => profile.Id);
        var memberships = (await membershipsTask.ConfigureAwait(false))
            .GroupBy(membership => membership.RoomId)
            .ToDictionary(group => group.Key, group => group.ToArray());
        Room[] rooms = [.. (await roomsTask.ConfigureAwait(false)).Select(room => new Room(
            room.Id,
            room.Name,
            room.OwnerId,
            [.. memberships.GetValueOrDefault(room.Id, [])
                .Select(membership =>
                {
                    peers.TryGetValue(membership.UserId, out DatabaseProfile? peer);
                    return new RoomMember(
                        membership.UserId,
                        peer?.Nickname ?? I18n.Get("common.friend"),
                        PixelCharacterCatalog.NormalizeId(peer?.CharacterId),
                        PresenceState.Offline,
                        CosmeticCatalog.NormalizeBubbleStyleId(peer?.EquippedBubbleStyleId),
                        peer?.TreeMovementPaused ?? false,
                        peer?.TreeMovementRevision);
                })],
            room.InviteCodeHint,
            room.InviteCodeReady,
            room.RealtimeEpoch))];
        foreach (Room? room in rooms.Where(room => !room.InviteCodeReady))
        {
            await _credentials.DeleteInviteCodeAsync(room.Id, cancellationToken).ConfigureAwait(false);
        }
        DatabaseProfile? profile = (await profileTask.ConfigureAwait(false)).FirstOrDefault();
        IReadOnlySet<string> activeEntitlementKeys = PixelCharacterCatalog.ResolveActiveEntitlementKeys(
            await entitlementsTask.ConfigureAwait(false),
            profile?.CharacterId);
        return new BackendSnapshot(
            profile is null
                ? null
                : new Profile(
                    profile.Id,
                    profile.Nickname,
                    PixelCharacterCatalog.NormalizeId(profile.CharacterId),
                    OwnedCosmeticOrNull(
                        profile.EquippedBubbleStyleId,
                        CommerceProductKind.Bubble,
                        activeEntitlementKeys),
                    OwnedCosmeticOrNull(
                        profile.EquippedThrowableId,
                        CommerceProductKind.Throwable,
                        activeEntitlementKeys),
                    profile.TreeMovementPaused,
                    profile.TreeMovementRevision),
            rooms,
            session.UserId,
            activeEntitlementKeys);
    }

    /// <summary>
    /// Commerce is optional. A missing or temporarily unavailable commerce
    /// schema must not turn the core messenger snapshot into a connection failure.
    /// </summary>
    private async Task<IReadOnlySet<string>?> LoadActiveEntitlementKeysIfAvailableAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            DatabaseCommerceEntitlement[] rows = await GetAsync<DatabaseCommerceEntitlement[]>(
                "/rest/v1/commerce_entitlements?status=eq.active&select=entitlement_key,status",
                cancellationToken).ConfigureAwait(false);
            return rows.Select(row => row.EntitlementKey).ToHashSet(StringComparer.Ordinal);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<CommerceProductState>> GetWindowsCommerceStateAsync(
        CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = await CreateRequestAsync(
            HttpMethod.Post,
            "/rest/v1/rpc/get_store_state",
            cancellationToken).ConfigureAwait(false);
        request.Content = JsonContent.Create(new { }, options: s_jsonOptions);
        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken)
            .ConfigureAwait(false);
        DatabaseCommerceState[] rows = await ReadRequiredAsync<DatabaseCommerceState[]>(
            response,
            cancellationToken).ConfigureAwait(false);

        var states = new List<CommerceProductState>();
        foreach (CommerceProduct product in WindowsCommerceCatalog.Products)
        {
            DatabaseCommerceState row = rows.SingleOrDefault(candidate =>
                    StringComparer.Ordinal.Equals(candidate.ProductId, product.Id))
                ?? throw new InvalidDataException("Windows commerce product is missing.");
            string expectedKind = product.Kind.ToString().ToLowerInvariant();
            string? expectedCharacterId = product.Kind == CommerceProductKind.Character
                ? product.CharacterId
                : null;
            if (!StringComparer.Ordinal.Equals(row.ProductKind, expectedKind)
                || !StringComparer.Ordinal.Equals(row.CatalogItemId, product.EffectiveCatalogItemId)
                || !StringComparer.Ordinal.Equals(row.CharacterId, expectedCharacterId)
                || !StringComparer.Ordinal.Equals(row.EntitlementKey, product.EntitlementKey)
                || row.SortOrder != product.SortOrder
                || row.AmountKrw <= 0
                || !StringComparer.Ordinal.Equals(row.Currency, "KRW"))
            {
                throw new InvalidDataException("Windows commerce catalog does not match the server.");
            }

            CommercePurchaseState purchaseState = row.EntitlementStatus == "active"
                ? CommercePurchaseState.Owned
                : row.LatestOrderStatus == "refunded"
                    ? CommercePurchaseState.Refunded
                    : row.GoogleConnected
                        ? CommercePurchaseState.Available
                        : CommercePurchaseState.GoogleConnectionRequired;
            states.Add(new CommerceProductState(
                product with { AmountKrw = row.AmountKrw },
                row.GoogleConnected,
                purchaseState));
        }
        return states;
    }

    public async Task<CommerceCheckout> CreateWindowsCommerceOrderAsync(
        string productId,
        CancellationToken cancellationToken = default)
    {
        if (WindowsCommerceCatalog.Find(productId) is null)
        {
            throw new ArgumentOutOfRangeException(nameof(productId));
        }

        using HttpRequestMessage request = await CreateRequestAsync(
            HttpMethod.Post,
            "/functions/v1/commerce-order",
            cancellationToken).ConfigureAwait(false);
        request.Content = JsonContent.Create(new { product_id = productId }, options: s_jsonOptions);
        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken)
            .ConfigureAwait(false);
        CommerceOrderResponse order = await ReadRequiredAsync<CommerceOrderResponse>(
            response,
            cancellationToken).ConfigureAwait(false);
        if (order.OrderId == Guid.Empty
            || !Uri.TryCreate(order.CheckoutUrl, UriKind.Absolute, out Uri? checkoutUri)
            || checkoutUri.Scheme != Uri.UriSchemeHttps
            || !checkoutUri.IsDefaultPort
            || checkoutUri.Host != "sidey-app.github.io"
            || checkoutUri.AbsolutePath != "/SIDEY/checkout/"
            || !string.IsNullOrEmpty(checkoutUri.UserInfo)
            || !string.IsNullOrEmpty(checkoutUri.Query))
        {
            throw new InvalidDataException("Commerce checkout URL is invalid.");
        }
        return new CommerceCheckout(order.OrderId, checkoutUri);
    }

    public async Task<Profile> SetTreeMovementPausedAsync(
        bool paused, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
        DatabaseProfile row = await RpcSingleAsync<DatabaseProfile>(
            "set_tree_movement_paused",
            new { p_paused = paused, p_expected_revision = expectedRevision },
            cancellationToken).ConfigureAwait(false);
        return MapProfile(row);
    }

    public async Task<Profile> SaveProfileAsync(
        string nickname,
        string characterId,
        CancellationToken cancellationToken = default)
    {
        if (!ProfileValidator.IsValidNickname(nickname))
        {
            throw new ArgumentException(I18n.Get("validation.nicknameLength"), nameof(nickname));
        }
        // Rendering may fall back for unknown IDs; a profile mutation must never
        // turn a cleared UI selection into a saved hamster.
        if (!PixelCharacterCatalog.All.Any(character => character.Id == characterId
            || character.CompatibleAliases.Contains(characterId, StringComparer.Ordinal)))
        {
            throw new ArgumentException("A known character selection is required.", nameof(characterId));
        }

        DatabaseProfile row = await RpcSingleAsync<DatabaseProfile>(
            "upsert_profile",
            new
            {
                p_nickname = ProfileValidator.NormalizeNickname(nickname),
                p_character_id = PixelCharacterCatalog.NormalizeId(characterId),
            },
            cancellationToken).ConfigureAwait(false);
        return MapProfile(row);
    }

    public async Task<Profile> SetEquippedCosmeticAsync(
        CommerceProductKind kind,
        string? catalogItemId,
        CancellationToken cancellationToken = default)
    {
        if (kind == CommerceProductKind.Character)
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }
        string? normalized = kind switch
        {
            CommerceProductKind.Bubble => CosmeticCatalog.NormalizeBubbleStyleId(catalogItemId),
            CommerceProductKind.Throwable => CosmeticCatalog.NormalizeThrowableId(catalogItemId),
            _ => null,
        };
        if (catalogItemId is not null && normalized is null)
        {
            throw new ArgumentOutOfRangeException(nameof(catalogItemId));
        }

        var parameters = new
        {
            p_product_kind = kind.ToString().ToLowerInvariant(),
            p_catalog_item_id = normalized,
        };
        if (_realtime.UsesFirebaseChat)
        {
            EquippedCosmeticV2Row v2 = await RpcSingleAsync<EquippedCosmeticV2Row>(
                "set_equipped_cosmetic_v2",
                parameters,
                cancellationToken).ConfigureAwait(false);
            await _realtime.ConvergeGrantAsync(
                RequireAccessRevision(v2.AccessRevision),
                CancellationToken.None).ConfigureAwait(false);
            return MapProfile(v2.Profile);
        }

        DatabaseProfile row = await RpcSingleAsync<DatabaseProfile>(
            "set_equipped_cosmetic",
            parameters,
            cancellationToken).ConfigureAwait(false);
        return MapProfile(row);
    }

    public async Task<CreateRoomResult> CreateRoomAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        ValidateRoomName(name);
        object parameters = new { p_name = RoomNameValidator.Normalize(name) };
        CreateRoomRow row;
        if (_realtime.UsesFirebaseChat)
        {
            CreateRoomV2Row v2 = await RpcSingleAsync<CreateRoomV2Row>(
                "create_room_v2",
                parameters,
                cancellationToken).ConfigureAwait(false);
            await _realtime.ConvergeGrantAsync(
                RequireAccessRevision(v2.AccessRevision),
                CancellationToken.None).ConfigureAwait(false);
            row = new CreateRoomRow(v2.RoomId, v2.InviteCode);
        }
        else
        {
            row = await RpcSingleAsync<CreateRoomRow>(
                "create_room",
                parameters,
                cancellationToken).ConfigureAwait(false);
        }
        await _credentials.WriteInviteCodeAsync(row.RoomId, row.InviteCode, cancellationToken)
            .ConfigureAwait(false);
        BackendSnapshot snapshot = await FetchSnapshotAsync(cancellationToken).ConfigureAwait(false);
        Room room = snapshot.Rooms.SingleOrDefault(room => room.Id == row.RoomId)
            ?? throw new InvalidDataException(I18n.Get("backend.createdRoomMissing"));
        return new CreateRoomResult(room, row.InviteCode);
    }

    public async Task<Room> JoinRoomAsync(
        string inviteCode,
        CancellationToken cancellationToken = default)
    {
        string normalized = inviteCode.Trim().ToUpperInvariant();
        if (normalized.Length == 0)
        {
            throw new ArgumentException(I18n.Get("onboarding.inviteRequired"), nameof(inviteCode));
        }

        object parameters = new { p_invite_code = normalized };
        JoinRoomRow row;
        if (_realtime.UsesFirebaseChat)
        {
            JoinRoomV2Row v2 = await RpcSingleAsync<JoinRoomV2Row>(
                "join_room_v2",
                parameters,
                cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(v2.ErrorCode))
            {
                throw new InvalidOperationException(I18n.Format("backend.joinFailed", v2.ErrorCode));
            }
            await _realtime.ConvergeGrantAsync(
                RequireAccessRevision(v2.AccessRevision),
                CancellationToken.None).ConfigureAwait(false);
            row = new JoinRoomRow(v2.RoomId, v2.ErrorCode);
        }
        else
        {
            row = await RpcSingleAsync<JoinRoomRow>(
                "join_room",
                parameters,
                cancellationToken).ConfigureAwait(false);
        }
        if (!string.IsNullOrEmpty(row.ErrorCode))
        {
            throw new InvalidOperationException(I18n.Format("backend.joinFailed", row.ErrorCode));
        }

        if (row.RoomId is not { } roomId)
        {
            throw new InvalidDataException(I18n.Get("backend.joinRoomIdMissing"));
        }

        await _credentials.WriteInviteCodeAsync(roomId, normalized, cancellationToken)
            .ConfigureAwait(false);
        BackendSnapshot snapshot = await FetchSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return snapshot.Rooms.SingleOrDefault(room => room.Id == roomId)
            ?? throw new InvalidDataException(I18n.Get("backend.joinedRoomMissing"));
    }

    public async Task LeaveRoomAsync(Guid roomId, CancellationToken cancellationToken = default)
    {
        await RpcNoResultAsync("leave_room", new { p_room_id = roomId }, cancellationToken)
            .ConfigureAwait(false);
        await _credentials.DeleteInviteCodeAsync(roomId, cancellationToken).ConfigureAwait(false);
    }

    public Task RenameRoomAsync(
        Guid roomId,
        string name,
        CancellationToken cancellationToken = default)
    {
        ValidateRoomName(name);
        return RpcNoResultAsync(
            "rename_room",
            new { p_room_id = roomId, p_name = RoomNameValidator.Normalize(name) },
            cancellationToken);
    }

    public async Task<string> RotateInviteCodeAsync(
        Guid roomId,
        CancellationToken cancellationToken = default)
    {
        string inviteCode = await RpcSingleAsync<string>(
            "rotate_invite_code",
            new { p_room_id = roomId },
            cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(inviteCode))
        {
            throw new InvalidDataException(I18n.Get("backend.emptyInviteCode"));
        }

        await _credentials.WriteInviteCodeAsync(roomId, inviteCode, cancellationToken)
            .ConfigureAwait(false);
        return inviteCode;
    }

    public Task RemoveRoomMemberAsync(
        Guid roomId,
        Guid userId,
        CancellationToken cancellationToken = default) =>
        RpcNoResultAsync(
            "remove_room_member",
            new { p_room_id = roomId, p_user_id = userId },
            cancellationToken);

    public async Task DeleteRoomAsync(Guid roomId, CancellationToken cancellationToken = default)
    {
        await RpcNoResultAsync("delete_room", new { p_room_id = roomId }, cancellationToken)
            .ConfigureAwait(false);
        await _credentials.DeleteInviteCodeAsync(roomId, cancellationToken).ConfigureAwait(false);
    }

    public Task DeleteOwnAccountAsync(CancellationToken cancellationToken = default) =>
        RpcNoResultAsync("delete_own_account", new { }, cancellationToken);

    public async Task<IReadOnlyList<ChatMessage>> FetchRecentMessagesAsync(
        Guid roomId,
        CancellationToken cancellationToken = default)
    {
        MessageHistoryPage page = await FetchMessagePageAsync(
            roomId,
            before: null,
            limit: 50,
            cancellationToken).ConfigureAwait(false);
        return [.. page.Messages.Reverse()];
    }

    public async Task<MessageHistoryPage> FetchMessagePageAsync(
        Guid roomId,
        MessageHistoryCursor? before,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        int boundedLimit = Math.Clamp(limit, 1, 50);
        string cutoff = Uri.EscapeDataString(
            (DateTimeOffset.UtcNow - MessageLedger.ConfirmedRetention)
            .UtcDateTime
            .ToString("O"));
        string beforeFilter = string.Empty;
        if (before is { } cursor)
        {
            string timestamp = Uri.EscapeDataString(cursor.CreatedAt.UtcDateTime.ToString("O"));
            beforeFilter =
                $"&or=(created_at.lt.{timestamp},and(created_at.eq.{timestamp},id.lt.{cursor.Id:D}))";
        }

        DatabaseMessage[] rows = await GetAsync<DatabaseMessage[]>(
            $"/rest/v1/messages?room_id=eq.{roomId:D}&created_at=gte.{cutoff}{beforeFilter}" +
            $"&select=*&order=created_at.desc,id.desc&limit={boundedLimit + 1}",
            cancellationToken).ConfigureAwait(false);
        ChatMessage[] messages = [.. rows.Take(boundedLimit).Select(MapMessage)];
        MessageHistoryCursor? nextCursor = rows.Length > boundedLimit && messages.LastOrDefault() is { } last
            ? new MessageHistoryCursor(last.CreatedAt, last.Id)
            : null;
        return new MessageHistoryPage(messages, nextCursor);
    }

    public async Task<ChatMessage> SendMessageAsync(
        Guid id,
        Guid roomId,
        string body,
        CancellationToken cancellationToken = default)
    {
        string normalized = MessageValidator.Normalize(body);
        if (!MessageValidator.IsValid(normalized))
        {
            throw new ArgumentException(I18n.Get("validation.messageLength"), nameof(body));
        }

        if (!_realtime.UsesFirebaseChat)
        {
            DatabaseMessage legacyRow = await RpcSingleAsync<DatabaseMessage>(
                "send_message",
                new { p_id = id, p_room_id = roomId, p_body = normalized },
                cancellationToken).ConfigureAwait(false);
            return MapMessage(legacyRow);
        }

        FirebaseRealtimeChatResult? committed;
        try
        {
            committed = await _realtime.PublishChatAsync(
                id,
                roomId,
                normalized,
                cancellationToken).ConfigureAwait(false);
        }
        catch (FirebaseRealtimeChatException exception) when (
            exception.Classification == FirebaseRealtimeChatFailureClassification.CommitAmbiguous)
        {
            DatabaseMessage? reconciled;
            try
            {
                reconciled = await ReconcileMessageAsync(
                    id,
                    roomId,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception reconciliationFailure)
            {
                throw new ChatCommitAmbiguousException(id, roomId, reconciliationFailure);
            }
            if (reconciled is not null)
            {
                return MapMessage(reconciled);
            }
            throw;
        }

        if (committed is null)
        {
            throw new InvalidDataException("Firebase chat response did not contain a committed message.");
        }
        return new ChatMessage(
            committed.MessageId,
            committed.RoomId,
            committed.SenderId,
            committed.Body,
            DateTimeOffset.FromUnixTimeMilliseconds(committed.TimestampMilliseconds));
    }

    public Task PublishPresenceAsync(
        Guid roomId,
        PresenceState state,
        CancellationToken cancellationToken = default) =>
        _realtime.PublishPresenceAsync(roomId, state, cancellationToken);

    public async Task BroadcastTypingAsync(
        Guid roomId,
        bool active,
        bool keepalive,
        CancellationToken cancellationToken = default)
    {
        _ = keepalive;
        await BroadcastRoomEventAsync(
            roomId,
            active ? "typing_start" : "typing_stop",
            eventId: null,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task BroadcastCharacterPulseAsync(
        Guid roomId,
        Guid eventId,
        CancellationToken cancellationToken = default)
    {
        await BroadcastRoomEventAsync(
            roomId,
            "character_pulse",
            eventId,
            cancellationToken).ConfigureAwait(false);
    }

    public Task BroadcastCharacterThrowAsync(
        Guid roomId,
        Guid eventId,
        Guid targetUserId,
        CancellationToken cancellationToken = default)
    {
        if (!_roomEpochs.TryGetValue(roomId, out long realtimeEpoch))
        {
            throw new InvalidOperationException(I18n.Get("backend.realtimeEpochMissing"));
        }

        return RpcNoResultAsync(
            "broadcast_character_throw",
            new
            {
                p_room_id = roomId,
                p_realtime_epoch = realtimeEpoch,
                p_event_id = eventId,
                p_target_user_id = targetUserId,
            },
            cancellationToken);
    }

    public void RetryRealtimeConnection(bool userInitiated = false) => _realtime.RequestReconnect(userInitiated);
    public bool IsRealtimeRecoveryPaused => _realtime.IsRecoveryPaused;

    public async Task SynchronizeRealtimeRoomsAsync(
        IReadOnlyDictionary<Guid, long> roomEpochs,
        Guid? activeRoomId,
        PresenceState localPresence,
        CancellationToken cancellationToken = default)
    {
        await _realtime.SynchronizeAsync(
            roomEpochs,
            activeRoomId,
            localPresence,
            cancellationToken).ConfigureAwait(false);
        _roomEpochs = roomEpochs.ToDictionary(pair => pair.Key, pair => pair.Value);
        _activeRoomId = activeRoomId is { } id && roomEpochs.ContainsKey(id) ? id : null;
    }

    public async IAsyncEnumerable<BackendEvent> SubscribeAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var output = Channel.CreateBounded<BackendEvent>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task pump = PumpEventsAsync(output.Writer, linked.Token);
        try
        {
            await foreach (BackendEvent backendEvent in output.Reader.ReadAllAsync(cancellationToken))
            {
                yield return backendEvent;
            }
        }
        finally
        {
            linked.Cancel();
            try
            {
                await pump.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _realtime.DisposeAsync().ConfigureAwait(false);
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private static IRealtimeTransport CreateRealtimeTransport(
        SupabaseRuntimeConfiguration configuration,
        SupabaseAnonymousAuthService auth,
        ICredentialStore credentials,
        HttpClient httpClient,
        string? appVersion,
        bool firebaseV2Capable)
    {
        var legacy = new SupabaseRealtimeTransport(configuration, auth);
        if (!firebaseV2Capable)
        {
            return legacy;
        }

        string version = string.IsNullOrWhiteSpace(appVersion) ? "2.0.0" : appVersion;
        var credentialProvider = new FirebaseRealtimeCredentialProvider(
            auth,
            credentials,
            httpClient);
        var selector = new FirebaseRealtimeRolloutSelector(
            configuration,
            auth,
            version,
            httpClient);
        var chat = new FirebaseRealtimeChatClient(credentialProvider, httpClient);
        return new FirebaseV2RealtimeTransport(
            legacy,
            selector,
            chat,
            credentialProvider,
            sink => new FirebaseRealtimeListener(credentialProvider, sink));
    }

    private async Task<DatabaseMessage?> FindMessageAsync(
        Guid messageId,
        Guid roomId,
        CancellationToken cancellationToken)
    {
        DatabaseMessage[] rows = await GetAsync<DatabaseMessage[]>(
            $"/rest/v1/messages?id=eq.{messageId:D}&room_id=eq.{roomId:D}&select=*&limit=1",
            cancellationToken).ConfigureAwait(false);
        return rows.SingleOrDefault();
    }

    public ValueTask InvalidateRealtimeSessionAsync(
        CancellationToken cancellationToken = default) =>
        _realtime.InvalidateSessionAsync(cancellationToken);

    private async Task<DatabaseMessage?> ReconcileMessageAsync(
        Guid messageId,
        Guid roomId,
        CancellationToken cancellationToken)
    {
        TimeSpan[] delays =
        [
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(250),
            TimeSpan.FromMilliseconds(500),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(4),
            TimeSpan.FromSeconds(8),
            TimeSpan.FromSeconds(16),
        ];
        bool completedLookup = false;
        foreach (TimeSpan delay in delays)
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            DatabaseMessage? message;
            try
            {
                message = await FindMessageAsync(
                    messageId,
                    roomId,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException)
            {
                continue;
            }
            completedLookup = true;
            if (message is not null)
            {
                return message;
            }
        }
        if (!completedLookup)
        {
            throw new HttpRequestException("Chat reconciliation was unavailable.");
        }
        return null;
    }

    private async Task PumpEventsAsync(
        ChannelWriter<BackendEvent> output,
        CancellationToken cancellationToken)
    {
        CancellationTokenSource? structuralDelay = null;
        Task structuralRefresh = Task.CompletedTask;
        RealtimeConnectionStatus connectionStatus = RealtimeConnectionStatus.Disconnected;
        try
        {
            await foreach (BackendEvent backendEvent in _realtime.ReadEventsAsync(cancellationToken))
            {
                if (backendEvent is BackendEvent.MessageChanged change)
                {
                    try
                    {
                        await output.WriteAsync(
                            new BackendEvent.Diagnostic(
                                $"realtime-message-change-received operation={change.Operation.ToLowerInvariant()}"),
                            cancellationToken).ConfigureAwait(false);
                        await HandleMessageChangeAsync(change, output, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        await output.WriteAsync(
                            new BackendEvent.Diagnostic(
                                $"message-recheck-error {FailureDiagnostic(exception)}"),
                            cancellationToken).ConfigureAwait(false);
                        await output.WriteAsync(
                            new BackendEvent.TechnicalError(
                                I18n.Format("backend.messageRecheckFailed", exception.Message)),
                            cancellationToken).ConfigureAwait(false);
                    }
                    continue;
                }

                if (backendEvent is BackendEvent.MessagesInvalidated invalidated)
                {
                    try
                    {
                        IReadOnlyList<ChatMessage> messages = await FetchRecentMessagesAsync(
                            invalidated.RoomId,
                            cancellationToken).ConfigureAwait(false);
                        await output.WriteAsync(
                            new BackendEvent.MessagesReplaced(invalidated.RoomId, messages),
                            cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        await output.WriteAsync(
                            new BackendEvent.Diagnostic(
                                $"expired-messages-error {FailureDiagnostic(exception)}"),
                            cancellationToken).ConfigureAwait(false);
                        await output.WriteAsync(
                            new BackendEvent.TechnicalError(
                                I18n.Format("backend.expiredMessagesFailed", exception.Message)),
                            cancellationToken).ConfigureAwait(false);
                    }
                    continue;
                }

                if (backendEvent is BackendEvent.RoomStructureChanged)
                {
                    if (structuralDelay is not null)
                    {
                        structuralDelay.Cancel();
                        await structuralRefresh.ConfigureAwait(false);
                        structuralDelay.Dispose();
                    }

                    structuralDelay = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    structuralRefresh = EmitCoalescedSnapshotAsync(output, structuralDelay.Token);
                    continue;
                }

                if (backendEvent is BackendEvent.ReconciliationRequired)
                {
                    connectionStatus = connectionStatus.WithRecoveryReconciled(false);
                    await output.WriteAsync(
                        new BackendEvent.ConnectionChanged(connectionStatus),
                        cancellationToken).ConfigureAwait(false);
                    if (!await _realtime.RunWhileConnectedAsync(
                        token => EmitReconciliationWithRetryAsync(output, token), cancellationToken).ConfigureAwait(false))
                        continue;
                    connectionStatus = _realtime.ConnectionStatus.WithRecoveryReconciled(true);
                    await output.WriteAsync(
                        new BackendEvent.ConnectionChanged(connectionStatus),
                        cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (backendEvent is BackendEvent.ConnectionChanged connection)
                {
                    connectionStatus = connection.Status;
                    await output.WriteAsync(
                        new BackendEvent.ConnectionChanged(connectionStatus),
                        cancellationToken).ConfigureAwait(false);
                    if (connectionStatus.TransportConnected)
                    {
                        if (!await _realtime.RunWhileConnectedAsync(
                            token => EmitReconciliationWithRetryAsync(output, token), cancellationToken).ConfigureAwait(false))
                            continue;
                        connectionStatus = _realtime.ConnectionStatus.WithRecoveryReconciled(true);
                        await output.WriteAsync(
                            new BackendEvent.ConnectionChanged(connectionStatus),
                            cancellationToken).ConfigureAwait(false);
                    }
                    continue;
                }

                await output.WriteAsync(backendEvent, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (structuralDelay is not null)
            {
                structuralDelay.Cancel();
                await structuralRefresh.ConfigureAwait(false);
                structuralDelay.Dispose();
            }

            output.TryComplete();
        }
    }

    private async Task EmitReconciliationWithRetryAsync(
        ChannelWriter<BackendEvent> output,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                await EmitReconciliationAsync(output, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                await output.WriteAsync(
                    new BackendEvent.Diagnostic(
                        $"realtime-reconciliation-error {FailureDiagnostic(exception)}"),
                    cancellationToken).ConfigureAwait(false);
                await output.WriteAsync(
                    new BackendEvent.TechnicalError(
                        I18n.Format("backend.realtimeResyncFailed", exception.Message)),
                    cancellationToken).ConfigureAwait(false);
                await Task.Delay(
                    RealtimeRecoveryPolicy.DelayForAttempt(attempt + 1),
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task EmitReconciliationAsync(
        ChannelWriter<BackendEvent> output,
        CancellationToken cancellationToken)
    {
        BackendSnapshot snapshot = await FetchSnapshotAsync(cancellationToken).ConfigureAwait(false);
        await output.WriteAsync(
            new BackendEvent.SnapshotReceived(snapshot),
            cancellationToken).ConfigureAwait(false);
        if (_activeRoomId is { } activeRoomId
            && snapshot.Rooms.Any(room => room.Id == activeRoomId))
        {
            await output.WriteAsync(
                new BackendEvent.MessagesReplaced(
                    activeRoomId,
                    await FetchRecentMessagesAsync(activeRoomId, cancellationToken)
                        .ConfigureAwait(false)),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandleMessageChangeAsync(
        BackendEvent.MessageChanged change,
        ChannelWriter<BackendEvent> output,
        CancellationToken cancellationToken)
    {
        if (change.Operation == "DELETE")
        {
            await output.WriteAsync(
                new BackendEvent.Diagnostic("message-delete-forwarded"),
                cancellationToken).ConfigureAwait(false);
            await output.WriteAsync(
                new BackendEvent.MessageDeleted(change.RoomId, change.MessageId),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (change.Operation is not ("INSERT" or "UPDATE"))
        {
            return;
        }

        await output.WriteAsync(
            new BackendEvent.Diagnostic("message-recheck-started"),
            cancellationToken).ConfigureAwait(false);
        DatabaseMessage[] rows;
        try
        {
            rows = await GetAsync<DatabaseMessage[]>(
                $"/rest/v1/messages?room_id=eq.{change.RoomId:D}&id=eq.{change.MessageId:D}&select=*&limit=1",
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            await output.WriteAsync(
                new BackendEvent.Diagnostic("message-recheck-completed result=failed"),
                cancellationToken).ConfigureAwait(false);
            throw;
        }
        if (rows.FirstOrDefault() is { } row)
        {
            await output.WriteAsync(
                new BackendEvent.Diagnostic("message-recheck-completed result=found"),
                cancellationToken).ConfigureAwait(false);
            await output.WriteAsync(
                new BackendEvent.MessageReceived(MapMessage(row)),
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await output.WriteAsync(
                new BackendEvent.Diagnostic("message-recheck-completed result=missing"),
                cancellationToken).ConfigureAwait(false);
            await output.WriteAsync(
                new BackendEvent.MessageDeleted(change.RoomId, change.MessageId),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task EmitCoalescedSnapshotAsync(
        ChannelWriter<BackendEvent> output,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(s_structuralCoalescingWindow, cancellationToken).ConfigureAwait(false);
            BackendSnapshot snapshot = await FetchSnapshotAsync(cancellationToken).ConfigureAwait(false);
            await output.WriteAsync(new BackendEvent.SnapshotReceived(snapshot), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            output.TryWrite(new BackendEvent.Diagnostic(
                $"room-snapshot-error {FailureDiagnostic(exception)}"));
            output.TryWrite(new BackendEvent.TechnicalError(
                I18n.Format("backend.roomSnapshotFailed", exception.Message)));
        }
    }

    private async Task<T> GetAsync<T>(string relativePath, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = await CreateRequestAsync(HttpMethod.Get, relativePath, cancellationToken)
            .ConfigureAwait(false);
        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return await ReadRequiredAsync<T>(response, cancellationToken).ConfigureAwait(false);
    }

    private static string FailureDiagnostic(Exception exception)
    {
        Exception current = exception;
        while (current.InnerException is { } inner)
        {
            current = inner;
        }

        string category = current switch
        {
            SocketException socket when socket.SocketErrorCode is
                SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain => "dns",
            SocketException => "socket",
            AuthenticationException => "tls",
            TimeoutException => "timeout",
            TaskCanceledException => "timeout",
            HttpRequestException => "http",
            _ => "unknown",
        };
        string status = current is HttpRequestException { StatusCode: { } statusCode }
            ? $" status={(int)statusCode}"
            : string.Empty;
        return $"category={category} type={current.GetType().Name} hresult=0x{current.HResult:X8}{status}";
    }

    private async Task<T> RpcSingleAsync<T>(
        string function,
        object body,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = await CreateRequestAsync(
            HttpMethod.Post,
            $"/rest/v1/rpc/{function}",
            cancellationToken).ConfigureAwait(false);
        request.Headers.TryAddWithoutValidation("Prefer", "return=representation");
        request.Content = JsonContent.Create(body, options: s_jsonOptions);
        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        using JsonDocument document = await ReadDocumentAsync(response, cancellationToken).ConfigureAwait(false);
        JsonElement root = document.RootElement;
        JsonElement element = root.ValueKind == JsonValueKind.Array
            ? root.GetArrayLength() == 0
                ? throw new InvalidDataException($"RPC {function} returned no rows.")
                : root[0]
            : root;
        return element.Deserialize<T>(s_jsonOptions)
            ?? throw new InvalidDataException($"RPC {function} returned an invalid row.");
    }

    private async Task RpcNoResultAsync(
        string function,
        object body,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = await CreateRequestAsync(
            HttpMethod.Post,
            $"/rest/v1/rpc/{function}",
            cancellationToken).ConfigureAwait(false);
        request.Content = JsonContent.Create(body, options: s_jsonOptions);
        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Supabase RPC {function} failed with HTTP {(int)response.StatusCode}.",
                inner: null,
                response.StatusCode);
        }
    }

    private Task BroadcastRoomEventAsync(
        Guid roomId,
        string eventName,
        Guid? eventId,
        CancellationToken cancellationToken)
    {
        if (!_roomEpochs.TryGetValue(roomId, out long realtimeEpoch))
        {
            throw new InvalidOperationException(I18n.Get("backend.realtimeEpochMissing"));
        }

        return RpcNoResultAsync(
            "broadcast_room_event",
            new
            {
                p_room_id = roomId,
                p_realtime_epoch = realtimeEpoch,
                p_event = eventName,
                p_event_id = eventId,
            },
            cancellationToken);
    }

    private async Task<HttpRequestMessage> CreateRequestAsync(
        HttpMethod method,
        string relativePath,
        CancellationToken cancellationToken)
    {
        StoredSupabaseSession session = await RequiredSessionAsync(cancellationToken).ConfigureAwait(false);
        var request = new HttpRequestMessage(method, new Uri(_configuration.Url, relativePath));
        request.Headers.Add("apikey", _configuration.PublishableKey);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        return request;
    }

    private async ValueTask<StoredSupabaseSession> RequiredSessionAsync(
        CancellationToken cancellationToken) =>
        await _sessions.GetStoredSessionAsync(cancellationToken).ConfigureAwait(false)
        ?? throw new InvalidOperationException(I18n.Get("auth.sessionMissing"));

    private static async Task<T> ReadRequiredAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Supabase REST request failed with HTTP {(int)response.StatusCode}.",
                inner: null,
                response.StatusCode);
        }

        return await response.Content.ReadFromJsonAsync<T>(s_jsonOptions, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException("Supabase REST response was empty.");
    }

    private static async Task<JsonDocument> ReadDocumentAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Supabase RPC failed with HTTP {(int)response.StatusCode}.",
                inner: null,
                response.StatusCode);
        }

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    private static ChatMessage MapMessage(DatabaseMessage row) => new(
        row.Id,
        row.RoomId,
        row.SenderId,
        row.Body,
        PostgresTimestampParser.Parse(row.CreatedAt),
        CosmeticCatalog.NormalizeBubbleStyleId(row.BubbleStyleId));

    private static Profile MapProfile(DatabaseProfile row) => new(
        row.Id,
        row.Nickname,
        PixelCharacterCatalog.NormalizeId(row.CharacterId),
        CosmeticCatalog.NormalizeBubbleStyleId(row.EquippedBubbleStyleId),
        CosmeticCatalog.NormalizeThrowableId(row.EquippedThrowableId),
        row.TreeMovementPaused,
        row.TreeMovementRevision);

    private static string? OwnedCosmeticOrNull(
        string? catalogItemId,
        CommerceProductKind kind,
        IReadOnlySet<string> activeEntitlementKeys)
    {
        string? normalized = kind == CommerceProductKind.Bubble
            ? CosmeticCatalog.NormalizeBubbleStyleId(catalogItemId)
            : CosmeticCatalog.NormalizeThrowableId(catalogItemId);
        if (normalized is null)
        {
            return null;
        }
        CommerceProduct? product = WindowsCommerceCatalog.Products.FirstOrDefault(candidate =>
            candidate.Kind == kind
            && StringComparer.Ordinal.Equals(candidate.EffectiveCatalogItemId, normalized));
        return product is not null && activeEntitlementKeys.Contains(product.EntitlementKey)
            ? normalized
            : null;
    }

    private static void ValidateRoomName(string name)
    {
        if (!RoomNameValidator.IsValid(name))
        {
            throw new ArgumentException(I18n.Get("validation.roomNameLength"), nameof(name));
        }
    }

    private static string RequireAccessRevision(string? value)
    {
        if (value is null
            || value.Length != 20
            || value.Any(character => character is < '0' or > '9'))
        {
            throw new InvalidDataException("Realtime access revision is invalid.");
        }
        return value;
    }

    private sealed record DatabaseProfile(
        Guid Id,
        string Nickname,
        [property: JsonPropertyName("character_id")] string CharacterId,
        [property: JsonPropertyName("equipped_bubble_style_id")] string? EquippedBubbleStyleId,
        [property: JsonPropertyName("equipped_throwable_id")] string? EquippedThrowableId,
        [property: JsonPropertyName("tree_movement_paused")] bool TreeMovementPaused = false,
        [property: JsonPropertyName("tree_movement_revision")] long? TreeMovementRevision = null);

    private sealed record DatabaseCommerceEntitlement(
        [property: JsonPropertyName("entitlement_key")] string EntitlementKey,
        string Status);

    private sealed record DatabaseCommerceState(
        [property: JsonPropertyName("product_id")] string ProductId,
        [property: JsonPropertyName("product_kind")] string ProductKind,
        [property: JsonPropertyName("catalog_item_id")] string CatalogItemId,
        [property: JsonPropertyName("character_id")] string? CharacterId,
        [property: JsonPropertyName("entitlement_key")] string EntitlementKey,
        [property: JsonPropertyName("sort_order")] int SortOrder,
        [property: JsonPropertyName("amount_krw")] int AmountKrw,
        string Currency,
        [property: JsonPropertyName("google_connected")] bool GoogleConnected,
        [property: JsonPropertyName("entitlement_status")] string? EntitlementStatus,
        [property: JsonPropertyName("latest_order_status")] string? LatestOrderStatus);

    private sealed record CommerceOrderResponse(
        [property: JsonPropertyName("order_id")] Guid OrderId,
        [property: JsonPropertyName("checkout_url")] string CheckoutUrl);

    private sealed record DatabaseRoom(
        Guid Id,
        string Name,
        [property: JsonPropertyName("owner_id")] Guid OwnerId,
        [property: JsonPropertyName("invite_code_hint")] string InviteCodeHint,
        [property: JsonPropertyName("invite_code_ready")] bool InviteCodeReady,
        [property: JsonPropertyName("realtime_epoch")] long RealtimeEpoch);

    private sealed record DatabaseMembership(
        [property: JsonPropertyName("room_id")] Guid RoomId,
        [property: JsonPropertyName("user_id")] Guid UserId);

    private sealed record DatabaseMessage(
        Guid Id,
        [property: JsonPropertyName("room_id")] Guid RoomId,
        [property: JsonPropertyName("sender_id")] Guid SenderId,
        string Body,
        [property: JsonPropertyName("created_at")] string CreatedAt,
        [property: JsonPropertyName("bubble_style_id")] string? BubbleStyleId);

    private sealed record CreateRoomRow(
        [property: JsonPropertyName("room_id")] Guid RoomId,
        [property: JsonPropertyName("invite_code")] string InviteCode);

    private sealed record CreateRoomV2Row(
        [property: JsonPropertyName("room_id")] Guid RoomId,
        [property: JsonPropertyName("invite_code")] string InviteCode,
        [property: JsonPropertyName("accessRevision")] string? AccessRevision);

    private sealed record JoinRoomRow(
        [property: JsonPropertyName("room_id")] Guid? RoomId,
        [property: JsonPropertyName("error_code")] string? ErrorCode);

    private sealed record JoinRoomV2Row(
        [property: JsonPropertyName("room_id")] Guid? RoomId,
        [property: JsonPropertyName("error_code")] string? ErrorCode,
        [property: JsonPropertyName("accessRevision")] string? AccessRevision);

    private sealed record EquippedCosmeticV2Row(
        DatabaseProfile Profile,
        [property: JsonPropertyName("accessRevision")] string? AccessRevision);
}
