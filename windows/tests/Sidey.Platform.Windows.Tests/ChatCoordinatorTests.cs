using System.Reflection;
using Sidey.Core.Abstractions;
using Sidey.Core.Domain;
using Sidey.Presentation.Services;

namespace Sidey.Platform.Windows.Tests;

public sealed class ChatCoordinatorTests
{
    [Fact]
    public async Task RealtimeConfirmationBeforeLostHttpResponseKeepsTheSendSuccessful()
    {
        await using var fixture = new ChatFixture();
        fixture.Backend.Send = (id, roomId, body) =>
        {
            fixture.Messages.Confirm(new ChatMessage(id, roomId, fixture.Profile.Id, body, DateTimeOffset.UtcNow));
            return Task.FromException<ChatMessage>(new HttpRequestException("Response was lost."));
        };

        await fixture.Coordinator.SendMessageAsync(fixture.Room.Id, "Delivered once");

        MessageLedgerEntry entry = Assert.Single(fixture.Messages.Entries);
        Assert.Equal(MessageDeliveryState.Confirmed, entry.State);
        Assert.Equal("Delivered once", entry.Body);
        Assert.Equal(1, fixture.Backend.Sends);
    }

    [Fact]
    public async Task UnconfirmedSendFailureMarksTheMessageFailedAndReachesItsComposer()
    {
        await using var fixture = new ChatFixture();
        var failure = new HttpRequestException("Server unreachable.");
        fixture.Backend.Send = (_, _, _) => Task.FromException<ChatMessage>(failure);

        HttpRequestException observed = await Assert.ThrowsAsync<HttpRequestException>(
            () => fixture.Coordinator.SendMessageAsync(fixture.Room.Id, "Retry me"));

        Assert.Same(failure, observed);
        MessageLedgerEntry entry = Assert.Single(fixture.Coordinator.State.Messages);
        Assert.Equal(MessageDeliveryState.Failed, entry.State);
        Assert.Equal("Retry me", entry.Body);
        Assert.Empty(fixture.Bubbles.Bubbles);
    }

    [Fact]
    public async Task AmbiguousCommitKeepsOriginalMessagePendingForHistoryReconciliation()
    {
        await using var fixture = new ChatFixture();
        fixture.Backend.Send = (id, roomId, _) => Task.FromException<ChatMessage>(
            new ChatCommitAmbiguousException(
                id,
                roomId,
                new HttpRequestException("Response was lost.")));

        await fixture.Coordinator.SendMessageAsync(fixture.Room.Id, "Reconcile me");

        MessageLedgerEntry entry = Assert.Single(fixture.Coordinator.State.Messages);
        Assert.Equal(MessageDeliveryState.Pending, entry.State);
        Assert.Equal("Reconcile me", entry.Body);
        Assert.Single(fixture.Bubbles.Bubbles);
        Assert.Equal(1, fixture.Backend.Sends);
    }

    [Fact]
    public async Task ComposerFromThePreviousRoomCannotSendIntoTheNewActiveRoom()
    {
        await using var fixture = new ChatFixture();
        SetField(fixture.Coordinator, "_state", fixture.Coordinator.State with { ActiveRoomId = Guid.NewGuid() });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Coordinator.SendMessageAsync(fixture.Room.Id, "Old room draft"));

        Assert.Equal(0, fixture.Backend.Sends);
        Assert.Empty(fixture.Messages.Entries);
        Assert.Empty(fixture.Bubbles.Bubbles);
    }

    [Fact]
    public async Task QuietModeHidesLocalAndRemoteTypingAndBubblesWhilePreservingUnreadAndHistory()
    {
        await using var fixture = new ChatFixture();
        SetField(fixture.Coordinator, "_localTypingRoom", fixture.Room.Id);
        GetField<HashSet<(Guid RoomId, Guid UserId)>>(fixture.Coordinator, "_typing")
            .Add((fixture.Room.Id, fixture.Friend.Id));
        GetField<Dictionary<Guid, int>>(fixture.Coordinator, "_unreadByRoom")[fixture.Room.Id] = 3;
        var message = new ChatMessage(Guid.NewGuid(), fixture.Room.Id, fixture.Friend.Id, "Still recorded", DateTimeOffset.UtcNow);
        fixture.Messages.Confirm(message);
        fixture.Bubbles.Show(fixture.Friend.Id, message.Id, message.Body);
        Func<WorldSnapshot> snapshot = typeof(AppCoordinator)
            .GetMethod("CurrentWorldSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Func<WorldSnapshot>>(fixture.Coordinator);
        Assert.All(snapshot().Members, member => Assert.True(member.IsTyping));
        Assert.Single(snapshot().Bubbles);

        await fixture.Coordinator.SetQuietModeAsync(true);

        WorldSnapshot quiet = snapshot();
        Assert.Equal(2, quiet.Members.Count);
        Assert.All(quiet.Members, member => Assert.False(member.IsTyping));
        Assert.Empty(quiet.Bubbles);
        Assert.Equal(3, fixture.Coordinator.TotalUnreadCount);
        Assert.Equal(message.Id, Assert.Single(fixture.Coordinator.State.Messages).Id);

        await fixture.Coordinator.SetQuietModeAsync(false);

        Assert.All(snapshot().Members, member => Assert.True(member.IsTyping));
        Assert.Equal(message.Id, Assert.Single(snapshot().Bubbles).MessageId);
        Assert.Equal(3, fixture.Coordinator.TotalUnreadCount);
        Assert.Equal(message.Id, Assert.Single(fixture.Coordinator.State.Messages).Id);
    }

    private static void SetField(AppCoordinator coordinator, string name, object value) =>
        typeof(AppCoordinator).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(coordinator, value);

    private static T GetField<T>(AppCoordinator coordinator, string name) =>
        Assert.IsType<T>(typeof(AppCoordinator).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(coordinator));

    private sealed class ChatFixture : IAsyncDisposable
    {
        public Profile Profile { get; } = new(Guid.NewGuid(), "Me", "pixel_cat");
        public Profile Friend { get; } = new(Guid.NewGuid(), "Friend", "pixel_cat");
        public Room Room { get; }
        public AppCoordinator Coordinator { get; }
        public ChatBackend Backend { get; }
        public MessageLedger Messages => GetField<MessageLedger>(Coordinator, "_messages");
        public ActiveBubbleLedger Bubbles => GetField<ActiveBubbleLedger>(Coordinator, "_bubbles");

        public ChatFixture()
        {
            Coordinator = new AppCoordinator(new MemoryPreferences());
            IBackendGateway backend = DispatchProxy.Create<IBackendGateway, ChatBackend>();
            Backend = (ChatBackend)backend;
            Room = new Room(Guid.NewGuid(), "Friends", Profile.Id,
                [new RoomMember(Profile.Id, Profile.Nickname, Profile.CharacterId, PresenceState.Online),
                 new RoomMember(Friend.Id, Friend.Nickname, Friend.CharacterId, PresenceState.Online)], "TEST", true, 1);
            SetField(Coordinator, "_backend", backend);
            SetField(Coordinator, "_initialSnapshotReceived", true);
            SetField(Coordinator, "_state", CoordinatorState.Initial with
            {
                Profile = Profile,
                Rooms = [Room],
                ActiveRoomId = Room.Id,
                GoogleAuthentication = GoogleAuthenticationState.Verified,
                Preferences = AppPreferences.Default with { OverlayVisible = false, OnboardingCompleted = true },
                RealtimeConnection = new RealtimeConnectionStatus(true, true, true),
            });
        }

        public ValueTask DisposeAsync() => Coordinator.DisposeAsync();
    }

    private sealed class MemoryPreferences : IPreferencesStore
    {
        private AppPreferences _saved = AppPreferences.Default;
        public ValueTask<AppPreferences> LoadAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(_saved);
        public ValueTask SaveAsync(AppPreferences preferences, CancellationToken cancellationToken = default)
        {
            _saved = preferences;
            return ValueTask.CompletedTask;
        }
    }

    public class ChatBackend : DispatchProxy
    {
        public int Sends { get; private set; }
        public Func<Guid, Guid, string, Task<ChatMessage>> Send { get; set; } =
            (_, _, _) => throw new InvalidOperationException("Unexpected send.");

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(IBackendGateway.SendMessageAsync))
            {
                Sends++;
                return Send((Guid)args![0]!, (Guid)args[1]!, (string)args[2]!);
            }
            if (targetMethod.Name == nameof(IBackendGateway.BroadcastTypingAsync))
                return Task.CompletedTask;
            throw new NotSupportedException(targetMethod.Name);
        }
    }
}
