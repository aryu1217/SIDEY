using System.Text;
using System.Text.Json.Nodes;

namespace Sidey.Platform.Windows.Tests;

public sealed class FirebaseRealtimeProtocolTests
{
    private static readonly Guid s_roomId = Guid.Parse("73527218-54b9-4a6b-9541-8a19072579f3");
    private static readonly Guid s_userId = Guid.Parse("88557669-c870-4903-b545-e42228922306");
    private static readonly Guid s_sessionId = Guid.Parse("80cc101a-aa1d-4248-bbb0-792db9f1cdaa");
    private static readonly long s_receivedAt =
        DateTimeOffset.Parse("2026-01-01T00:00:00Z").ToUnixTimeMilliseconds();

    [Fact]
    public void ExactCompactBootstrapParsesAndRedactsSecrets()
    {
        Assert.Equal(
            "{}",
            Encoding.UTF8.GetString(FirebaseRealtimeProtocol.CreateBootstrapRequestBody()));
        Assert.Equal(
            "{\"minimumAccessRevision\":\"00000000000000000042\"}",
            Encoding.UTF8.GetString(FirebaseRealtimeProtocol.CreateBootstrapRequestBody(
                "00000000000000000042")));

        FirebaseRealtimeBootstrapConfiguration result = ParseBootstrap(CreateBootstrapJson());

        Assert.Equal("https://sidey.asia-southeast1.firebasedatabase.app/", result.DatabaseUrl.AbsoluteUri);
        Assert.Equal("api-key-secret", result.FirebaseApiKey);
        Assert.Equal("custom-token-secret", result.CustomToken);
        Assert.Equal("00000000000000000042", result.AccessRevision);
        Assert.Equal(s_receivedAt + 270_000, result.RefreshAfter);
        Assert.Equal(s_receivedAt + 300_000, result.RolloutLeaseExpiresAt);
        Assert.Equal([s_roomId], result.Rooms);
        Assert.Equal(["0"], result.WireItems);
        Assert.DoesNotContain("api-key-secret", result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("custom-token-secret", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ThirtySecondLeaseAllowsRefreshAfterAtReceiptTime()
    {
        FirebaseRealtimeBootstrapConfiguration result = ParseBootstrap(
            CreateBootstrapJson(root =>
            {
                root["refreshAfter"] = s_receivedAt;
                root["rolloutLeaseExpiresAt"] = s_receivedAt + 30_000;
            }));

        Assert.Equal(s_receivedAt, result.RefreshAfter);
        Assert.Equal(s_receivedAt + 30_000, result.RolloutLeaseExpiresAt);
    }

    [Theory]
    [InlineData("protocolVersion", 1)]
    [InlineData("databaseURL", "https://sidey.asia-southeast1.firebasedatabase.app/")]
    [InlineData("databaseURL", "https://sidey.asia-southeast1.firebasedatabase.app.evil.example")]
    [InlineData("permissionSync", "polling")]
    [InlineData("authTokenLifetimeSeconds", 3_599)]
    [InlineData("accessRevision", "42")]
    [InlineData("refreshAfter", 1)]
    [InlineData("rolloutLeaseExpiresAt", 1)]
    public void BootstrapFixedValuesAreExact(string propertyName, object value)
    {
        string json = CreateBootstrapJson(root => root[propertyName] = JsonValue.Create(value));

        Assert.Throws<InvalidDataException>(() => ParseBootstrap(json));
    }

    [Theory]
    [InlineData(300_001, 270_001)]
    [InlineData(300_000, 269_999)]
    [InlineData(300_000, 300_001)]
    public void BootstrapLeaseAndRefreshWindowAreBounded(long leaseOffset, long refreshOffset)
    {
        string json = CreateBootstrapJson(root =>
        {
            root["rolloutLeaseExpiresAt"] = s_receivedAt + leaseOffset;
            root["refreshAfter"] = s_receivedAt + refreshOffset;
        });

        Assert.Throws<InvalidDataException>(() => ParseBootstrap(json));
    }

    [Theory]
    [InlineData("")]
    [InlineData("42")]
    [InlineData("0000000000000000004x")]
    public void BootstrapMinimumRevisionMustBeFixedWidthDecimal(string revision)
    {
        Assert.Throws<ArgumentException>(() =>
            FirebaseRealtimeProtocol.CreateBootstrapRequestBody(revision));
    }

    [Fact]
    public void BootstrapAccessRevisionMustMeetRequestedBarrier()
    {
        byte[] response = Encoding.UTF8.GetBytes(CreateBootstrapJson());

        FirebaseRealtimeBootstrapConfiguration equal =
            FirebaseRealtimeProtocol.ParseBootstrapResponse(
                response,
                s_receivedAt,
                "00000000000000000042");

        Assert.Equal("00000000000000000042", equal.AccessRevision);
        Assert.Throws<InvalidDataException>(() =>
            FirebaseRealtimeProtocol.ParseBootstrapResponse(
                response,
                s_receivedAt,
                "00000000000000000043"));
    }

    [Fact]
    public void BootstrapRoomsAreZeroToFiveUniqueUuidsAndUnknownKeysFailClosed()
    {
        Assert.Empty(ParseBootstrap(CreateBootstrapJson(root => root["rooms"] = new JsonArray())).Rooms);

        string duplicate = CreateBootstrapJson(root => root["rooms"] = new JsonArray(s_roomId, s_roomId));
        string malformed = CreateBootstrapJson(root => root["rooms"] = new JsonArray("not-a-uuid"));
        string tooMany = CreateBootstrapJson(root => root["rooms"] = new JsonArray(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()));
        string unknown = CreateBootstrapJson(root => root["sessionId"] = s_sessionId.ToString("D"));
        string badWireCode = CreateBootstrapJson(root => root["wireItems"] = new JsonArray("01"));
        string duplicateWireCode = CreateBootstrapJson(root => root["wireItems"] = new JsonArray("7", "7"));

        Assert.Throws<InvalidDataException>(() => ParseBootstrap(duplicate));
        Assert.Throws<InvalidDataException>(() => ParseBootstrap(malformed));
        Assert.Throws<InvalidDataException>(() => ParseBootstrap(tooMany));
        Assert.Throws<InvalidDataException>(() => ParseBootstrap(unknown));
        Assert.Throws<InvalidDataException>(() => ParseBootstrap(badWireCode));
        Assert.Throws<InvalidDataException>(() => ParseBootstrap(duplicateWireCode));
        Assert.Empty(ParseBootstrap(
            CreateBootstrapJson(root => root["wireItems"] = new JsonArray())).WireItems);
    }

    [Fact]
    public void DuplicateBootstrapSecretIsRejectedWithoutExceptionDisclosure()
    {
        string json = CreateBootstrapJson().Replace(
            "\"customToken\":\"custom-token-secret\"",
            "\"customToken\":\"custom-token-secret\",\"customToken\":\"attacker-secret\"",
            StringComparison.Ordinal);

        InvalidDataException failure = Assert.Throws<InvalidDataException>(() => ParseBootstrap(json));

        Assert.DoesNotContain("custom-token-secret", failure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("attacker-secret", failure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void CompactTransientPathsAndWritesMatchFirebaseContract()
    {
        Assert.Equal($"v2/l/{s_roomId:D}", FirebaseRealtimeProtocol.RoomPath(s_roomId));
        Assert.Equal($"v2/n/{s_userId:D}", FirebaseRealtimeProtocol.InboxPath(s_userId));
        Assert.Equal($"v2/l/{s_roomId:D}/e", FirebaseRealtimeProtocol.ServerEventPath(s_roomId));

        Assert.Equal(
            $"v2/l/{s_roomId:D}/t/{s_userId:D}/{s_sessionId:D}",
            FirebaseRealtimeProtocol.TypingPath(s_roomId, s_userId, s_sessionId));
        Assert.Equal(
            $"v2/l/{s_roomId:D}/c/{s_userId:D}",
            FirebaseRealtimeProtocol.CharacterPulsePath(s_roomId, s_userId));
        Assert.Equal(
            $"v2/l/{s_roomId:D}/x/{s_userId:D}",
            FirebaseRealtimeProtocol.CharacterThrowPath(s_roomId, s_userId));
        Assert.Equal(
            "{\".sv\":\"timestamp\"}",
            Encoding.UTF8.GetString(FirebaseRealtimeProtocol.CreateServerTimestampBody()));
        JsonNode throwBody = JsonNode.Parse(
            FirebaseRealtimeProtocol.CreateCharacterThrowBody(s_userId, "0"))!;
        Assert.Equal(s_userId.ToString("D"), throwBody["u"]!.GetValue<string>());
        Assert.Equal("0", throwBody["k"]!.GetValue<string>());
        Assert.Equal("timestamp", throwBody["t"]![".sv"]!.GetValue<string>());
    }

    [Fact]
    public void RoomPayloadParsesStrictCompactShapesAndRedactsMessageBody()
    {
        var messageId = Guid.Parse("7d3bb557-56b2-4409-a0bb-daf9046cc555");
        string json = CreateRoomJson(messageId);

        FirebaseRealtimeRoomPayload payload = ParseRoom(json);

        Assert.Equal(1_800_000_000_000, payload.Typing[s_userId][s_sessionId]);
        Assert.Equal(1_800_000_000_001, payload.CharacterPulses[s_userId]);
        FirebaseRealtimeThrow characterThrow = payload.CharacterThrows[s_userId];
        Assert.Equal(s_userId, characterThrow.TargetUserId);
        Assert.Equal("123456", characterThrow.WireCode);
        Assert.Equal(1_800_000_000_002, characterThrow.Timestamp);
        FirebaseRealtimeRoomEvent roomEvent = Assert.IsType<FirebaseRealtimeRoomEvent>(payload.ServerEvent);
        Assert.Equal(messageId, roomEvent.MessageId);
        Assert.Equal("secret message", roomEvent.Body);
        Assert.DoesNotContain("secret message", payload.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret message", roomEvent.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"z\":{}}")]
    [InlineData("{\"e\":{\"i\":\"7d3bb557-56b2-4409-a0bb-daf9046cc555\",\"s\":\"88557669-c870-4903-b545-e42228922306\",\"b\":\"body\",\"t\":1,\"n\":1,\"k\":\"0\"}}")]
    [InlineData("{\"e\":{\"i\":\"7d3bb557-56b2-4409-a0bb-daf9046cc555\",\"s\":\"88557669-c870-4903-b545-e42228922306\",\"b\":\"body\",\"t\":1,\"n\":9007199254740992}}")]
    public void MalformedRoomShapesFailClosed(string json)
    {
        Assert.Throws<InvalidDataException>(() => ParseRoom(json));
    }

    [Fact]
    public void MalformedCompactTransientPayloadsFailClosed()
    {
        Assert.Throws<InvalidDataException>(() => ParseRoom(
            "{\"t\":false,\"c\":\"reserved\",\"x\":[1,2,3]}"));
    }

    [Fact]
    public void NullRoomIsEmptyAndInboxUsesExactRevisionShapes()
    {
        FirebaseRealtimeRoomPayload emptyRoom = ParseRoom("null");
        Assert.Empty(emptyRoom.Typing);
        Assert.Empty(emptyRoom.CharacterPulses);

        FirebaseRealtimeInboxPayload inbox = ParseInbox(CreateInboxJson());
        Assert.Equal("00000000000000000042", inbox.AccessRevision);
        Assert.Equal("00000000000000000123", inbox.Rooms[s_roomId].Revision);
        Assert.Equal(9, inbox.Rooms[s_roomId].ChatSequence);
    }

    [Theory]
    [InlineData("{\"a\":42,\"r\":{}}")]
    [InlineData("{\"a\":\"0000000000000000042\",\"r\":{}}")]
    [InlineData("{\"a\":\"00000000000000000042\",\"r\":{},\"extra\":true}")]
    [InlineData("{\"a\":\"00000000000000000042\",\"r\":{\"73527218-54b9-4a6b-9541-8a19072579f3\":{\"v\":\"00000000000000000123\",\"n\":0}}}")]
    [InlineData("{\"a\":\"00000000000000000042\",\"r\":{\"73527218-54b9-4a6b-9541-8a19072579f3\":{\"v\":\"00000000000000000123\",\"n\":1,\"x\":1}}}")]
    public void MalformedInboxShapesFailClosed(string json)
    {
        Assert.Throws<InvalidDataException>(() => ParseInbox(json));
    }

    private static FirebaseRealtimeBootstrapConfiguration ParseBootstrap(string json) =>
        FirebaseRealtimeProtocol.ParseBootstrapResponse(Encoding.UTF8.GetBytes(json), s_receivedAt);

    private static FirebaseRealtimeRoomPayload ParseRoom(string json) =>
        FirebaseRealtimeProtocol.ParseRoomPayload(Encoding.UTF8.GetBytes(json));

    private static FirebaseRealtimeInboxPayload ParseInbox(string json) =>
        FirebaseRealtimeProtocol.ParseInboxPayload(Encoding.UTF8.GetBytes(json));

    private static string CreateRoomJson(Guid messageId) => new JsonObject
    {
        ["t"] = new JsonObject
        {
            [s_userId.ToString("D")] = new JsonObject
            {
                [s_sessionId.ToString("D")] = 1_800_000_000_000,
            },
        },
        ["c"] = new JsonObject { [s_userId.ToString("D")] = 1_800_000_000_001 },
        ["x"] = new JsonObject
        {
            [s_userId.ToString("D")] = new JsonObject
            {
                ["u"] = s_userId,
                ["k"] = "123456",
                ["t"] = 1_800_000_000_002,
            },
        },
        ["e"] = new JsonObject
        {
            ["i"] = messageId,
            ["s"] = s_userId,
            ["b"] = "secret message",
            ["t"] = 1_800_000_000_003,
            ["n"] = 1,
            ["k"] = "7",
        },
    }.ToJsonString();

    private static string CreateInboxJson() => new JsonObject
    {
        ["a"] = "00000000000000000042",
        ["r"] = new JsonObject
        {
            [s_roomId.ToString("D")] = new JsonObject
            {
                ["v"] = "00000000000000000123",
                ["n"] = 9,
            },
        },
    }.ToJsonString();

    private static string CreateBootstrapJson(Action<JsonObject>? mutate = null)
    {
        var root = new JsonObject
        {
            ["protocolVersion"] = 2,
            ["databaseURL"] = "https://sidey.asia-southeast1.firebasedatabase.app",
            ["firebaseApiKey"] = "api-key-secret",
            ["customToken"] = "custom-token-secret",
            ["permissionSync"] = "event-driven",
            ["authTokenLifetimeSeconds"] = 3_600,
            ["refreshAfter"] = s_receivedAt + 270_000,
            ["rolloutLeaseExpiresAt"] = s_receivedAt + 300_000,
            ["accessRevision"] = "00000000000000000042",
            ["rooms"] = new JsonArray(s_roomId),
            ["wireItems"] = new JsonArray("0"),
        };
        mutate?.Invoke(root);
        return root.ToJsonString();
    }
}
