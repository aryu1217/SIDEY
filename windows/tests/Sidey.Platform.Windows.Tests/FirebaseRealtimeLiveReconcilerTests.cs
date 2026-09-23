using System.Text;
using System.Text.Json;
using Sidey.Infrastructure.Realtime;

namespace Sidey.Platform.Windows.Tests;

public sealed class FirebaseRealtimeLiveReconcilerTests
{
    private static readonly Guid s_actorId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid s_targetId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid s_sessionId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

    [Fact]
    public void BaselineSuppressesPersistentActionsAndFreshAdvancesAnimateOnce()
    {
        var reconciler = new FirebaseRealtimeLiveReconciler();

        IReadOnlyList<FirebaseRealtimeLiveAction> baseline = reconciler.Consume(
            ParseRoom(10_000, includeTyping: true),
            receivedAtMilliseconds: 10_100);
        IReadOnlyList<FirebaseRealtimeLiveAction> advanced = reconciler.Consume(
            ParseRoom(11_000, includeTyping: false),
            receivedAtMilliseconds: 11_100);
        IReadOnlyList<FirebaseRealtimeLiveAction> duplicate = reconciler.Consume(
            ParseRoom(11_000, includeTyping: false),
            receivedAtMilliseconds: 11_200);

        Assert.Collection(
            baseline,
            action => Assert.Equal(
                new FirebaseRealtimeLiveAction.Typing(s_actorId, true),
                action));
        Assert.Collection(
            advanced,
            action => Assert.Equal(
                new FirebaseRealtimeLiveAction.Typing(s_actorId, false),
                action),
            action => Assert.Equal(new FirebaseRealtimeLiveAction.Pulse(s_actorId), action),
            action =>
            {
                FirebaseRealtimeLiveAction.Throw characterThrow =
                    Assert.IsType<FirebaseRealtimeLiveAction.Throw>(action);
                Assert.Equal(s_actorId, characterThrow.ActorUserId);
                Assert.Equal(s_targetId, characterThrow.Payload.TargetUserId);
                Assert.Equal("18", characterThrow.Payload.WireCode);
            });
        Assert.Empty(duplicate);
    }

    [Fact]
    public void TypingExpiresAfterSixSecondsAndStaleActionsDoNotAnimate()
    {
        var reconciler = new FirebaseRealtimeLiveReconciler();
        _ = reconciler.Consume(
            ParseRoom(10_000, includeTyping: true),
            receivedAtMilliseconds: 10_100);

        Assert.Empty(reconciler.ExpireTyping(15_999));
        Assert.Equal(
            new FirebaseRealtimeLiveAction.Typing(s_actorId, false),
            Assert.Single(reconciler.ExpireTyping(16_000)));

        IReadOnlyList<FirebaseRealtimeLiveAction> stale = reconciler.Consume(
            ParseRoom(20_000, includeTyping: false),
            receivedAtMilliseconds: 25_001);
        Assert.Empty(stale);
    }

    private static FirebaseRealtimeRoomPayload ParseRoom(long timestamp, bool includeTyping)
    {
        Dictionary<string, object> payload = new()
        {
            ["c"] = new Dictionary<string, long> { [s_actorId.ToString("D")] = timestamp },
            ["x"] = new Dictionary<string, object>
            {
                [s_actorId.ToString("D")] = new
                {
                    u = s_targetId,
                    k = "18",
                    t = timestamp,
                },
            },
        };
        if (includeTyping)
        {
            payload["t"] = new Dictionary<string, object>
            {
                [s_actorId.ToString("D")] = new Dictionary<string, long>
                {
                    [s_sessionId.ToString("D")] = timestamp,
                },
            };
        }
        string json = JsonSerializer.Serialize(payload);
        return FirebaseRealtimeProtocol.ParseRoomPayload(Encoding.UTF8.GetBytes(json));
    }
}
