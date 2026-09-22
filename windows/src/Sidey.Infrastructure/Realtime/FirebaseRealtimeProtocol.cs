using System.Collections.ObjectModel;
using System.Text.Json;

namespace Sidey.Infrastructure.Realtime;

internal sealed class FirebaseRealtimeBootstrapConfiguration(
    Uri databaseUrl,
    string firebaseApiKey,
    string customToken,
    string accessRevision,
    long refreshAfter,
    long rolloutLeaseExpiresAt,
    IReadOnlyList<Guid> rooms,
    IReadOnlyList<string> wireItems)
{
    public int ProtocolVersion => FirebaseRealtimeProtocol.ProtocolVersion;
    public Uri DatabaseUrl { get; } = databaseUrl;
    public string FirebaseApiKey { get; } = firebaseApiKey;
    public string CustomToken { get; } = customToken;
    public string PermissionSync => "event-driven";
    public int AuthTokenLifetimeSeconds => 3_600;
    public string AccessRevision { get; } = accessRevision;
    public long RefreshAfter { get; } = refreshAfter;
    public long RolloutLeaseExpiresAt { get; } = rolloutLeaseExpiresAt;
    public IReadOnlyList<Guid> Rooms { get; } = rooms;
    public IReadOnlyList<string> WireItems { get; } = wireItems;

    public override string ToString() =>
        $"FirebaseRealtimeBootstrapConfiguration {{ ProtocolVersion = {ProtocolVersion}, DatabaseUrl = {DatabaseUrl}, FirebaseApiKey = <redacted>, CustomToken = <redacted>, AccessRevision = {AccessRevision}, Rooms = {Rooms.Count}, WireItems = {WireItems.Count} }}";
}

internal sealed record FirebaseRealtimeThrow(Guid TargetUserId, string WireCode, long Timestamp);

internal sealed class FirebaseRealtimeRoomEvent(
    Guid messageId,
    Guid senderId,
    string body,
    long timestamp,
    long sequence,
    string? bubbleWireCode)
{
    public Guid MessageId { get; } = messageId;
    public Guid SenderId { get; } = senderId;
    public string Body { get; } = body;
    public long Timestamp { get; } = timestamp;
    public long Sequence { get; } = sequence;
    public string? BubbleWireCode { get; } = bubbleWireCode;

    public override string ToString() =>
        $"FirebaseRealtimeRoomEvent {{ MessageId = {MessageId:D}, SenderId = {SenderId:D}, Body = <redacted>, Timestamp = {Timestamp}, Sequence = {Sequence}, BubbleWireCode = {BubbleWireCode ?? "<none>"} }}";
}

internal sealed record FirebaseRealtimeInboxRoom(string Revision, long ChatSequence);

internal sealed class FirebaseRealtimeRoomPayload(
    IReadOnlyDictionary<Guid, IReadOnlyDictionary<Guid, long>> typing,
    IReadOnlyDictionary<Guid, long> characterPulses,
    IReadOnlyDictionary<Guid, FirebaseRealtimeThrow> characterThrows,
    FirebaseRealtimeRoomEvent? serverEvent)
{
    public IReadOnlyDictionary<Guid, IReadOnlyDictionary<Guid, long>> Typing { get; } = typing;
    public IReadOnlyDictionary<Guid, long> CharacterPulses { get; } = characterPulses;
    public IReadOnlyDictionary<Guid, FirebaseRealtimeThrow> CharacterThrows { get; } = characterThrows;
    public FirebaseRealtimeRoomEvent? ServerEvent { get; } = serverEvent;

    public override string ToString() =>
        $"FirebaseRealtimeRoomPayload {{ TypingUsers = {Typing.Count}, CharacterPulses = {CharacterPulses.Count}, CharacterThrows = {CharacterThrows.Count}, ServerEvent = {(ServerEvent is null ? "<none>" : "<redacted>")} }}";
}

internal sealed class FirebaseRealtimeInboxPayload(
    string accessRevision,
    IReadOnlyDictionary<Guid, FirebaseRealtimeInboxRoom> rooms)
{
    public string AccessRevision { get; } = accessRevision;
    public IReadOnlyDictionary<Guid, FirebaseRealtimeInboxRoom> Rooms { get; } = rooms;
}

internal static class FirebaseRealtimeProtocol
{
    public const int ProtocolVersion = 2;

    private const long MaxSafeInteger = 9_007_199_254_740_991;
    private const int MaxPayloadBytes = 256 * 1024;
    private const long MaximumRolloutLeaseMilliseconds = 300_000;
    private const long RefreshLeadMilliseconds = 30_000;
    private static readonly Uri s_databaseUrl =
        new("https://sidey.asia-southeast1.firebasedatabase.app");
    private static readonly HashSet<string> s_bootstrapProperties =
    [
        "accessRevision", "authTokenLifetimeSeconds", "customToken", "databaseURL",
        "firebaseApiKey", "permissionSync", "protocolVersion", "refreshAfter",
        "rolloutLeaseExpiresAt", "rooms", "wireItems",
    ];
    private static readonly HashSet<string> s_roomProperties = ["t", "c", "x", "e"];
    private static readonly HashSet<string> s_eventProperties = ["i", "s", "b", "t", "n", "k"];
    private static readonly HashSet<string> s_eventRequiredProperties = ["i", "s", "b", "t", "n"];
    private static readonly HashSet<string> s_inboxProperties = ["a", "r"];
    private static readonly HashSet<string> s_inboxRoomProperties = ["v", "n"];

    public static byte[] CreateBootstrapRequestBody(string? minimumAccessRevision = null)
    {
        if (minimumAccessRevision is null)
        {
            return "{}"u8.ToArray();
        }
        if (!IsRevision(minimumAccessRevision))
        {
            throw new ArgumentException(
                "Minimum access revision must be a 20-digit decimal string.",
                nameof(minimumAccessRevision));
        }
        return JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string>
        {
            ["minimumAccessRevision"] = minimumAccessRevision,
        });
    }

    public static FirebaseRealtimeBootstrapConfiguration ParseBootstrapResponse(
        ReadOnlyMemory<byte> utf8Json,
        long receivedAtUnixMilliseconds,
        string? minimumAccessRevision = null)
    {
        if (receivedAtUnixMilliseconds is < 1 or > MaxSafeInteger)
        {
            throw new ArgumentOutOfRangeException(nameof(receivedAtUnixMilliseconds));
        }
        if (minimumAccessRevision is not null && !IsRevision(minimumAccessRevision))
        {
            throw new ArgumentException(
                "Minimum access revision must be a 20-digit decimal string.",
                nameof(minimumAccessRevision));
        }
        using JsonDocument document = ParseDocument(utf8Json);
        JsonElement root = RequireObject(document.RootElement);
        ValidateProperties(root, s_bootstrapProperties, s_bootstrapProperties);
        RequireExactInteger(root, "protocolVersion", ProtocolVersion);
        RequireExactString(root, "databaseURL", s_databaseUrl.AbsoluteUri.TrimEnd('/'));
        RequireExactString(root, "permissionSync", "event-driven");
        RequireExactInteger(root, "authTokenLifetimeSeconds", 3_600);
        string accessRevision = GetRevision(root, "accessRevision");
        if (minimumAccessRevision is not null
            && StringComparer.Ordinal.Compare(accessRevision, minimumAccessRevision) < 0)
        {
            throw InvalidPayload();
        }
        long refreshAfter = GetPositiveSafeInteger(root, "refreshAfter");
        long rolloutLeaseExpiresAt = GetPositiveSafeInteger(root, "rolloutLeaseExpiresAt");
        if (rolloutLeaseExpiresAt <= receivedAtUnixMilliseconds
            || rolloutLeaseExpiresAt > receivedAtUnixMilliseconds
                + MaximumRolloutLeaseMilliseconds
            || refreshAfter > rolloutLeaseExpiresAt
            || refreshAfter < rolloutLeaseExpiresAt - RefreshLeadMilliseconds)
        {
            throw InvalidPayload();
        }

        string apiKey = GetSecret(root, "firebaseApiKey", 200);
        string customToken = GetSecret(root, "customToken", 32 * 1024);
        JsonElement roomsValue = GetRequiredProperty(root, "rooms");
        if (roomsValue.ValueKind != JsonValueKind.Array || roomsValue.GetArrayLength() > 5)
        {
            throw InvalidPayload();
        }

        var rooms = new List<Guid>(roomsValue.GetArrayLength());
        HashSet<Guid> uniqueRooms = [];
        foreach (JsonElement roomValue in roomsValue.EnumerateArray())
        {
            Guid roomId = GetGuid(roomValue);
            if (!uniqueRooms.Add(roomId))
            {
                throw InvalidPayload();
            }
            rooms.Add(roomId);
        }

        JsonElement wireItemsValue = GetRequiredProperty(root, "wireItems");
        if (wireItemsValue.ValueKind != JsonValueKind.Array
            || wireItemsValue.GetArrayLength() != 1)
        {
            throw InvalidPayload();
        }
        var wireItems = new List<string>(wireItemsValue.GetArrayLength());
        HashSet<string> uniqueWireItems = new(StringComparer.Ordinal);
        foreach (JsonElement wireItemValue in wireItemsValue.EnumerateArray())
        {
            string wireCode = GetString(wireItemValue, 6);
            if (!IsWireCode(wireCode, allowZero: true) || !uniqueWireItems.Add(wireCode))
            {
                throw InvalidPayload();
            }
            wireItems.Add(wireCode);
        }

        return new FirebaseRealtimeBootstrapConfiguration(
            s_databaseUrl,
            apiKey,
            customToken,
            accessRevision,
            refreshAfter,
            rolloutLeaseExpiresAt,
            rooms.AsReadOnly(),
            wireItems.AsReadOnly());
    }

    public static FirebaseRealtimeRoomPayload ParseRoomPayload(ReadOnlyMemory<byte> utf8Json)
    {
        using JsonDocument document = ParseDocument(utf8Json);
        if (document.RootElement.ValueKind == JsonValueKind.Null)
        {
            return CreateRoomPayload([], [], [], null);
        }

        JsonElement root = RequireObject(document.RootElement);
        ValidateProperties(root, s_roomProperties, new HashSet<string>());
        FirebaseRealtimeRoomEvent? serverEvent = root.TryGetProperty("e", out JsonElement eventValue) ? ParseRoomEvent(eventValue) : null;
        return CreateRoomPayload([], [], [], serverEvent);
    }

    public static FirebaseRealtimeInboxPayload ParseInboxPayload(ReadOnlyMemory<byte> utf8Json)
    {
        using JsonDocument document = ParseDocument(utf8Json);
        JsonElement root = RequireObject(document.RootElement);
        ValidateProperties(root, s_inboxProperties, s_inboxProperties);
        string accessRevision = GetRevision(root, "a");
        JsonElement roomsValue = RequireObject(GetRequiredProperty(root, "r"));
        Dictionary<Guid, FirebaseRealtimeInboxRoom> rooms = [];
        HashSet<string> names = [];
        foreach (JsonProperty property in roomsValue.EnumerateObject())
        {
            if (!names.Add(property.Name))
            {
                throw InvalidPayload();
            }
            Guid roomId = ParseGuid(property.Name);
            JsonElement roomValue = RequireObject(property.Value);
            ValidateProperties(roomValue, s_inboxRoomProperties, s_inboxRoomProperties);
            var room = new FirebaseRealtimeInboxRoom(
                GetRevision(roomValue, "v"),
                GetPositiveSafeInteger(roomValue, "n"));
            if (!rooms.TryAdd(roomId, room))
            {
                throw InvalidPayload();
            }
        }
        return new FirebaseRealtimeInboxPayload(
            accessRevision,
            new ReadOnlyDictionary<Guid, FirebaseRealtimeInboxRoom>(rooms));
    }

    public static string RoomPath(Guid roomId) => $"v2/l/{RequireId(roomId, nameof(roomId)):D}";
    public static string InboxPath(Guid userId) => $"v2/n/{RequireId(userId, nameof(userId)):D}";
    public static string TypingPath(Guid roomId, Guid userId, Guid sessionId) =>
        throw ReservedTransientEvent();
    public static string CharacterPulsePath(Guid roomId, Guid userId) =>
        throw ReservedTransientEvent();
    public static string CharacterThrowPath(Guid roomId, Guid userId) =>
        throw ReservedTransientEvent();
    public static string ServerEventPath(Guid roomId) => $"{RoomPath(roomId)}/e";

    public static byte[] CreateServerTimestampBody() => throw ReservedTransientEvent();

    public static byte[] CreateCharacterThrowBody(Guid targetUserId, string wireCode) =>
        throw ReservedTransientEvent();

    private static FirebaseRealtimeRoomPayload CreateRoomPayload(
        Dictionary<Guid, IReadOnlyDictionary<Guid, long>> typing,
        Dictionary<Guid, long> pulses,
        Dictionary<Guid, FirebaseRealtimeThrow> throws,
        FirebaseRealtimeRoomEvent? serverEvent) => new(
            new ReadOnlyDictionary<Guid, IReadOnlyDictionary<Guid, long>>(typing),
            new ReadOnlyDictionary<Guid, long>(pulses),
            new ReadOnlyDictionary<Guid, FirebaseRealtimeThrow>(throws),
            serverEvent);

    private static FirebaseRealtimeRoomEvent ParseRoomEvent(JsonElement value)
    {
        JsonElement eventValue = RequireObject(value);
        ValidateProperties(eventValue, s_eventProperties, s_eventRequiredProperties);
        string? bubbleCode = null;
        if (eventValue.TryGetProperty("k", out JsonElement bubbleValue))
        {
            bubbleCode = GetString(bubbleValue, 6);
            if (!IsWireCode(bubbleCode, allowZero: false))
            {
                throw InvalidPayload();
            }
        }
        return new FirebaseRealtimeRoomEvent(
            GetGuid(eventValue, "i"),
            GetGuid(eventValue, "s"),
            GetString(eventValue, "b", MaxPayloadBytes),
            GetPositiveSafeInteger(eventValue, "t"),
            GetPositiveSafeInteger(eventValue, "n"),
            bubbleCode);
    }

    private static JsonDocument ParseDocument(ReadOnlyMemory<byte> utf8Json)
    {
        if (utf8Json.IsEmpty || utf8Json.Length > MaxPayloadBytes)
        {
            throw InvalidPayload();
        }
        try
        {
            return JsonDocument.Parse(utf8Json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 10,
            });
        }
        catch (JsonException)
        {
            throw InvalidPayload();
        }
    }

    private static JsonElement RequireObject(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw InvalidPayload();
        }
        return value;
    }

    private static void ValidateProperties(
        JsonElement value,
        IReadOnlySet<string> allowed,
        IReadOnlySet<string> required)
    {
        HashSet<string> found = [];
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (!allowed.Contains(property.Name) || !found.Add(property.Name))
            {
                throw InvalidPayload();
            }
        }
        if (!required.IsSubsetOf(found))
        {
            throw InvalidPayload();
        }
    }

    private static JsonElement GetRequiredProperty(JsonElement value, string propertyName)
    {
        if (!value.TryGetProperty(propertyName, out JsonElement property))
        {
            throw InvalidPayload();
        }
        return property;
    }

    private static Guid GetGuid(JsonElement value, string propertyName) =>
        GetGuid(GetRequiredProperty(value, propertyName));

    private static Guid GetGuid(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            throw InvalidPayload();
        }
        return ParseGuid(value.GetString());
    }

    private static Guid ParseGuid(string? value)
    {
        if (!Guid.TryParseExact(value, "D", out Guid result) || result == Guid.Empty)
        {
            throw InvalidPayload();
        }
        return result;
    }

    private static string GetSecret(JsonElement value, string propertyName, int maximumLength)
    {
        string result = GetString(value, propertyName, maximumLength);
        if (result.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)))
        {
            throw InvalidPayload();
        }
        return result;
    }

    private static string GetRevision(JsonElement value, string propertyName)
    {
        string revision = GetString(value, propertyName, 20);
        if (revision.Length != 20 || revision.Any(character => character is < '0' or > '9'))
        {
            throw InvalidPayload();
        }
        return revision;
    }

    private static bool IsRevision(string? value) =>
        value is { Length: 20 }
        && value.All(character => character is >= '0' and <= '9');

    private static string GetString(JsonElement value, string propertyName, int maximumLength) =>
        GetString(GetRequiredProperty(value, propertyName), maximumLength);

    private static string GetString(JsonElement value, int maximumLength)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            throw InvalidPayload();
        }
        string? result = value.GetString();
        if (string.IsNullOrEmpty(result) || result.Length > maximumLength || result.Any(char.IsControl))
        {
            throw InvalidPayload();
        }
        return result;
    }

    private static long GetPositiveSafeInteger(JsonElement value, string propertyName) =>
        GetPositiveSafeInteger(GetRequiredProperty(value, propertyName));

    private static long GetPositiveSafeInteger(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt64(out long result)
            || result is < 1 or > MaxSafeInteger)
        {
            throw InvalidPayload();
        }
        return result;
    }

    private static void RequireExactInteger(JsonElement value, string propertyName, long expected)
    {
        JsonElement property = GetRequiredProperty(value, propertyName);
        if (property.ValueKind != JsonValueKind.Number
            || !property.TryGetInt64(out long result)
            || result != expected)
        {
            throw InvalidPayload();
        }
    }

    private static void RequireExactString(JsonElement value, string propertyName, string expected)
    {
        if (!StringComparer.Ordinal.Equals(GetString(value, propertyName, 256), expected))
        {
            throw InvalidPayload();
        }
    }

    private static bool IsWireCode(string? value, bool allowZero) =>
        !string.IsNullOrEmpty(value)
        && value.Length <= 6
        && (value == "0"
            ? allowZero
            : value[0] is >= '1' and <= '9'
                && value.All(character => character is >= '0' and <= '9'));

    private static Guid RequireId(Guid value, string parameterName) => value == Guid.Empty
        ? throw new ArgumentException("Identifier must not be empty.", parameterName)
        : value;

    private static InvalidDataException InvalidPayload() =>
        new("Firebase realtime protocol payload is invalid.");

    private static NotSupportedException ReservedTransientEvent() =>
        new("Compact Firebase transient events are reserved and disabled for clients.");
}
