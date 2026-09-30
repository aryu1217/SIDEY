using System.Reflection;
using Sidey.App;
using Sidey.Core.Abstractions;
using Sidey.Core.Domain;
using Sidey.Core.Realtime;
using Sidey.Presentation.Services;
using Sidey.Presentation.ViewModels;

namespace Sidey.Platform.Windows.Tests;

public sealed class OnboardingGroupIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartupRequiresGoogleWithoutCreatingOrReplacingAnAnonymousSession(bool existingUser)
    {
        var userId = Guid.NewGuid();
        var preferences = new MemoryPreferences();
        await preferences.SaveAsync(AppPreferences.Default with
        {
            OnboardingCompleted = existingUser,
            OverlayVisible = false,
            CachedNickname = "친구",
        });
        var saved = new AuthCredentials();
        saved.Session = existingUser ? System.Text.Json.JsonSerializer.Serialize(new StoredSupabaseSession(
            "old-token", "refresh-token", userId, DateTimeOffset.UtcNow.AddHours(1)),
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)) : null;
        string? original = saved.Session;
        using var handler = new AnonymousIdentityHandler(userId);
        using var client = new HttpClient(handler);
        var auth = new SupabaseAnonymousAuthService(new SupabaseRuntimeConfiguration(
            new Uri("https://test.example.com"), "test-key"), saved, client);
        await using var coordinator = new AppCoordinator(preferences, saved, new DisabledStartup());
        SetField(coordinator, "_auth", auth);

        await coordinator.InitializeAsync();

        Assert.Equal(GoogleAuthenticationState.Required, coordinator.State.GoogleAuthentication);
        Assert.True(coordinator.State.NeedsOnboarding);
        Assert.Equal(existingUser, coordinator.State.Preferences.OnboardingCompleted);
        Assert.False(coordinator.State.Connected);
        Assert.Null(coordinator.State.Profile);
        Assert.Equal(original, saved.Session);
        Assert.Equal(0, saved.Writes);
        Assert.Equal(existingUser ? 1 : 0, handler.Requests);
    }

    [Fact]
    public async Task IdentityAlreadyExistsRecoveryPreservesAnUnlinkedCurrentUser()
    {
        var userId = Guid.NewGuid();
        var saved = new AuthCredentials
        {
            Session = System.Text.Json.JsonSerializer.Serialize(new StoredSupabaseSession(
                "old-token", "refresh-token", userId, DateTimeOffset.UtcNow.AddHours(1)),
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)),
        };
        using var handler = new AnonymousIdentityHandler(userId);
        using var client = new HttpClient(handler);
        var auth = new SupabaseAnonymousAuthService(new SupabaseRuntimeConfiguration(
            new Uri("https://test.example.com"), "test-key"), saved, client);
        await using var coordinator = new AppCoordinator(new MemoryPreferences(), saved, new DisabledStartup());
        SetField(coordinator, "_auth", auth);
        SetField(coordinator, "_state", CoordinatorState.Initial with
        {
            GoogleAuthentication = GoogleAuthenticationState.SigningIn,
        });

        bool recovered = await coordinator.RecoverGoogleIdentityLinkAsync();

        Assert.False(recovered);
        Assert.Equal(GoogleAuthenticationState.SigningIn, coordinator.State.GoogleAuthentication);
        Assert.Equal(1, handler.Requests);
        Assert.Equal(0, saved.Writes);
    }

    [Fact]
    public async Task StaleIdentityConflictDoesNotReinitializeVerifiedSession()
    {
        var userId = Guid.NewGuid();
        var saved = new AuthCredentials
        {
            Session = System.Text.Json.JsonSerializer.Serialize(new StoredSupabaseSession(
                "old-token", "refresh-token", userId, DateTimeOffset.UtcNow.AddHours(1)),
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)),
        };
        using var handler = new AnonymousIdentityHandler(userId);
        using var client = new HttpClient(handler);
        var auth = new SupabaseAnonymousAuthService(new SupabaseRuntimeConfiguration(
            new Uri("https://test.example.com"), "test-key"), saved, client);
        await using var coordinator = new AppCoordinator(new MemoryPreferences(), saved, new DisabledStartup());
        SetField(coordinator, "_auth", auth);
        SetField(coordinator, "_state", CoordinatorState.Initial with
        {
            GoogleAuthentication = GoogleAuthenticationState.Verified,
        });

        bool recovered = await coordinator.RecoverGoogleIdentityLinkAsync();

        Assert.True(recovered);
        Assert.Equal(GoogleAuthenticationState.Verified, coordinator.State.GoogleAuthentication);
        Assert.Equal(0, handler.Requests);
        Assert.Equal(0, saved.Writes);
    }

    [Fact]
    public async Task CachedCompletionCannotAuthorizeMutationsOrCompleteOnboarding()
    {
        await using var coordinator = new AppCoordinator(new MemoryPreferences());
        IBackendGateway backend = DispatchProxy.Create<IBackendGateway, GroupBackend>();
        SetField(coordinator, "_backend", backend);
        SetField(coordinator, "_state", CoordinatorState.Initial with
        {
            GoogleAuthentication = GoogleAuthenticationState.Required,
            Preferences = AppPreferences.Default with { OnboardingCompleted = true, OverlayVisible = false },
        });
        Assert.True(coordinator.State.NeedsOnboarding);
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.CompleteOnboardingAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.SaveProfileAsync("친구", "pixel_cat"));
        Assert.True(coordinator.State.Preferences.OnboardingCompleted);
    }

    [Fact]
    public async Task TypingFeedbackWaitsForInjectedPresentationDispatcher()
    {
        var pending = new Queue<Action>();
        await using var coordinator = new AppCoordinator(
            new MemoryPreferences(), dispatchTypingFeedback: pending.Enqueue);
        IBackendGateway backend = DispatchProxy.Create<IBackendGateway, GroupBackend>();
        var server = (GroupBackend)backend;
        var roomId = Guid.NewGuid();
        SetField(coordinator, "_backend", backend);
        SetField(coordinator, "_state", CoordinatorState.Initial with
        {
            GoogleAuthentication = GoogleAuthenticationState.Verified,
            Profile = server.Profile,
            ActiveRoomId = roomId,
            Preferences = AppPreferences.Default with { OverlayVisible = false },
        });
        Func<Guid, bool> isLocallyTyping = Bind<Func<Guid, bool>>(coordinator, "IsLocallyTyping");

        await coordinator.SetTypingAsync(true);

        Assert.False(isLocallyTyping(roomId));
        Assert.Single(pending);
        pending.Dequeue()();
        Assert.True(isLocallyTyping(roomId));
        await coordinator.SetTypingAsync(false);
        pending.Dequeue()();
        Assert.False(isLocallyTyping(roomId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SnapshotContentFinishesBeforeRealtimeAndIndependentCommerce(bool developmentCommerce)
    {
        await using var coordinator = new AppCoordinator(new MemoryPreferences());
        IBackendGateway backend = DispatchProxy.Create<IBackendGateway, GroupBackend>();
        var server = (GroupBackend)backend;
        SetField(coordinator, "_backend", backend);
        SetField(coordinator, "_state", CoordinatorState.Initial with
        {
            GoogleAuthentication = GoogleAuthenticationState.Verified,
            DevelopmentCommerceEnabled = developmentCommerce,
            Preferences = AppPreferences.Default with { OverlayVisible = false },
        });
        var syncing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.Synchronize = () => { syncing.TrySetResult(); return release.Task; };
        Task reconciliation = Bind<Func<BackendSnapshot, CancellationToken, Task>>(coordinator, "ReconcileSnapshotAsync")(
            new BackendSnapshot(server.Profile, [], server.Profile.Id, new HashSet<string>()), CancellationToken.None);
        try
        {
            await syncing.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(reconciliation.IsCompleted);
            Assert.True(coordinator.IsRemoteContentLoading);
            Assert.False(coordinator.State.ContentLoading.Snapshot.NeedsSkeleton);
            Assert.Equal(developmentCommerce, coordinator.State.ContentLoading.Store.NeedsSkeleton);
        }
        finally { release.TrySetResult(); }
        await reconciliation.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task SavedCharacterImmediatelyUpdatesRoomsAndTheRestartCache()
    {
        var preferences = new MemoryPreferences();
        await using var coordinator = new AppCoordinator(preferences);
        IBackendGateway backend = DispatchProxy.Create<IBackendGateway, GroupBackend>();
        var server = (GroupBackend)backend;
        Room room = server.NewRoom("Friends");
        SetField(coordinator, "_backend", backend);
        SetField(coordinator, "_state", CoordinatorState.Initial with
        {
            GoogleAuthentication = GoogleAuthenticationState.Verified,
            Profile = server.Profile,
            Rooms = [room],
            Preferences = AppPreferences.Default with { OverlayVisible = false },
        });

        await coordinator.SaveProfileAsync("Friend", "pixel_penguin");

        Assert.Equal("pixel_penguin", coordinator.State.Profile!.CharacterId);
        Assert.Equal("pixel_penguin", Assert.Single(Assert.Single(coordinator.State.Rooms).Members).CharacterId);
        Assert.Equal("pixel_penguin", (await preferences.LoadAsync()).CachedCharacterId);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SuccessfulGroupSetupReachesReadyAndSelectsTheRoom(bool joining, bool existingRoom)
    {
        await using var coordinator = new AppCoordinator(new MemoryPreferences());
        IBackendGateway backend = DispatchProxy.Create<IBackendGateway, GroupBackend>();
        var server = (GroupBackend)backend;
        Room? previous = existingRoom ? server.NewRoom("Existing") : null;
        server.Rooms = previous is null ? [] : [previous];
        SetField(coordinator, "_backend", backend);
        SetField(coordinator, "_state", CoordinatorState.Initial with
        {
            GoogleAuthentication = GoogleAuthenticationState.Verified,
            Profile = server.Profile,
            Rooms = server.Rooms,
            ActiveRoomId = previous?.Id,
            Preferences = AppPreferences.Default with { OverlayVisible = false },
            RealtimeConnection = new RealtimeConnectionStatus(true, true, true),
        });
        var pipeline = new RoomSwitchPipeline(
            Bind<Func<Guid, CancellationToken, Task<IReadOnlyList<ChatMessage>>>>(coordinator, "PerformRoomSwitchAsync"),
            Bind<Func<Guid?, CancellationToken, Task>>(coordinator, "RestoreCommittedRoomAsync"),
            Bind<Action<Guid, IReadOnlyList<ChatMessage>>>(coordinator, "CommitRoomSwitch"),
            TimeSpan.Zero);
        pipeline.InitializeCommittedRoom(previous?.Id);
        RoomSessionLifetime session = Assert.IsType<RoomSessionLifetime>(typeof(AppCoordinator)
            .GetField("_roomSession", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(coordinator));
        session.SwitchPipeline = pipeline;
        using var onboarding = new OnboardingViewModel(coordinator)
        {
            Step = 2,
            RoomName = "Friends",
            InviteCode = "TEST",
        };
        var operations = new List<GroupOperation>();
        coordinator.StateChanged += state => operations.Add(state.GroupOperation);
        var syncing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.Synchronize = () =>
        {
            syncing.TrySetResult();
            return release.Task;
        };

        Task request = joining
            ? onboarding.JoinRoomCommand.ExecuteAsync(null)
            : onboarding.CreateRoomCommand.ExecuteAsync(null);
        await syncing.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.Equal(joining ? GroupOperation.Joining : GroupOperation.Creating, coordinator.State.GroupOperation);
            await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.CreateRoomAsync("Duplicate"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.JoinRoomAsync("TEST"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.SwitchRoomAsync(server.Rooms.Last().Id));
            Assert.False(onboarding.CanCreateRoom);
            Assert.False(onboarding.CanJoinRoom);
        }
        finally
        {
            release.TrySetResult();
        }
        await request.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Null(onboarding.ErrorMessage);
        Assert.True(onboarding.IsReadyStep);
        Assert.Equal(1, server.Mutations);
        Assert.Equal(existingRoom ? 2 : 1, coordinator.State.Rooms.Count);
        Assert.Equal(server.Rooms.Last().Id, coordinator.State.ActiveRoomId);
        Assert.Equal(GroupOperation.Idle, coordinator.State.GroupOperation);
        Assert.DoesNotContain(GroupOperation.Idle, operations.SkipLast(1));
        Assert.False(coordinator.State.Preferences.OnboardingCompleted);
    }

    [Fact]
    public async Task SwitchingRoomsPreservesOtherRoomPresenceDuringConnectionTransition()
    {
        await using var coordinator = new AppCoordinator(new MemoryPreferences());
        var currentUserId = Guid.NewGuid();
        var firstFriendId = Guid.NewGuid();
        var nextFriendId = Guid.NewGuid();
        var nextSleepingFriendId = Guid.NewGuid();
        Room firstRoom = new(Guid.NewGuid(), "First", currentUserId,
            [new RoomMember(currentUserId, "Me", "pixel_cat", PresenceState.Online),
             new RoomMember(firstFriendId, "First friend", "pixel_cat", PresenceState.Online)],
            "TEST", true, 1);
        Room nextRoom = new(Guid.NewGuid(), "Next", currentUserId,
            [new RoomMember(currentUserId, "Me", "pixel_cat", PresenceState.Online),
             new RoomMember(nextFriendId, "Next friend", "pixel_cat", PresenceState.Online),
             new RoomMember(nextSleepingFriendId, "Sleeping friend", "pixel_cat", PresenceState.Away)],
            "TEST", true, 1);
        SetField(coordinator, "_state", CoordinatorState.Initial with
        {
            Profile = new Profile(currentUserId, "Me", "pixel_cat"),
            Rooms = [firstRoom, nextRoom],
            ActiveRoomId = firstRoom.Id,
            Preferences = AppPreferences.Default with
            {
                OverlayVisible = false,
                ShowOfflineMembers = false,
            },
            RealtimeConnection = new RealtimeConnectionStatus(true, true, true),
        });
        Dictionary<(Guid RoomId, Guid UserId), PresenceState> knownPresence =
            Assert.IsType<Dictionary<(Guid RoomId, Guid UserId), PresenceState>>(
            typeof(AppCoordinator).GetField("_basePresence", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(coordinator));
        knownPresence[(firstRoom.Id, firstFriendId)] = PresenceState.Online;
        knownPresence[(nextRoom.Id, nextFriendId)] = PresenceState.Online;
        knownPresence[(nextRoom.Id, nextSleepingFriendId)] = PresenceState.Away;

        Bind<Action<RealtimeConnectionStatus>>(coordinator, "SetRealtimeConnection")(
            new RealtimeConnectionStatus(false, false, false) { SocketConnected = true });
        Assert.Equal(PresenceState.Online, knownPresence[(nextRoom.Id, nextFriendId)]);
        Assert.Equal(PresenceState.Online, coordinator.State.Rooms.Single(room => room.Id == nextRoom.Id)
            .Members.Single(member => member.UserId == nextFriendId).Presence);
        Bind<Action<RealtimeConnectionStatus>>(coordinator, "SetRealtimeConnection")(
            new RealtimeConnectionStatus(true, true, true));

        Bind<Action<RealtimeConnectionStatus>>(coordinator, "SetRealtimeConnection")(
            new RealtimeConnectionStatus(true, false, false));
        Assert.Equal(PresenceState.Online, coordinator.State.Rooms.Single(room => room.Id == nextRoom.Id)
            .Members.Single(member => member.UserId == nextFriendId).Presence);

        Bind<Action<Guid, IReadOnlyList<ChatMessage>>>(coordinator, "CommitRoomSwitch")(
            nextRoom.Id, []);
        Bind<Action<RealtimeConnectionStatus>>(coordinator, "SetRealtimeConnection")(
            new RealtimeConnectionStatus(true, true, false));
        WorldSnapshot world = Bind<Func<WorldSnapshot>>(coordinator, "CurrentWorldSnapshot")();

        Assert.Equal(nextRoom.Id, world.RoomId);
        Assert.Equal(PresenceState.Online,
            Assert.Single(world.Members, member => member.Id == nextFriendId).Presence);
        Assert.Equal(PresenceState.Away,
            Assert.Single(world.Members, member => member.Id == nextSleepingFriendId).Presence);

        Bind<Action<RealtimeConnectionStatus>>(coordinator, "SetRealtimeConnection")(
            new RealtimeConnectionStatus(true, false, false));
        Bind<Action<Guid, IReadOnlyList<ChatMessage>>>(coordinator, "CommitRoomSwitch")(
            firstRoom.Id, []);
        Bind<Action<RealtimeConnectionStatus>>(coordinator, "SetRealtimeConnection")(
            new RealtimeConnectionStatus(true, true, false));
        world = Bind<Func<WorldSnapshot>>(coordinator, "CurrentWorldSnapshot")();

        Assert.Equal(firstRoom.Id, world.RoomId);
        Assert.Equal(PresenceState.Online,
            Assert.Single(world.Members, member => member.Id == firstFriendId).Presence);

        Bind<Action<RealtimeConnectionStatus>>(coordinator, "SetRealtimeConnection")(
            RealtimeConnectionStatus.Disconnected);
        Assert.Equal(PresenceState.Reconnecting,
            coordinator.State.Rooms.Single(room => room.Id == firstRoom.Id)
                .Members.Single(member => member.UserId == firstFriendId).Presence);
        Assert.Equal(PresenceState.Offline, knownPresence[(nextRoom.Id, nextFriendId)]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SnapshotDuringRoomSwitchKeepsPendingRealtimeTargetOnlyWhileMembershipExists(
        bool pendingRoomRemoved)
    {
        await using var coordinator = new AppCoordinator(new MemoryPreferences());
        IBackendGateway backend = DispatchProxy.Create<IBackendGateway, GroupBackend>();
        var server = (GroupBackend)backend;
        Room firstRoom = server.NewRoom("First");
        Room secondRoom = server.NewRoom("Second");
        server.Rooms = [firstRoom, secondRoom];
        SetField(coordinator, "_backend", backend);
        SetField(coordinator, "_state", CoordinatorState.Initial with
        {
            GoogleAuthentication = GoogleAuthenticationState.Verified,
            Profile = server.Profile,
            Rooms = server.Rooms,
            ActiveRoomId = firstRoom.Id,
            Preferences = AppPreferences.Default with { OverlayVisible = false },
            RealtimeConnection = new RealtimeConnectionStatus(true, true, true),
        });
        var switching = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pipeline = new RoomSwitchPipeline(async (_, token) =>
        {
            switching.TrySetResult();
            await release.Task.WaitAsync(token);
            return [];
        }, (_, _) => Task.CompletedTask,
            Bind<Action<Guid, IReadOnlyList<ChatMessage>>>(coordinator, "CommitRoomSwitch"),
            TimeSpan.Zero);
        pipeline.InitializeCommittedRoom(firstRoom.Id);
        RoomSessionLifetime session = Assert.IsType<RoomSessionLifetime>(typeof(AppCoordinator)
            .GetField("_roomSession", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(coordinator));
        session.SwitchPipeline = pipeline;
        using var cancel = new CancellationTokenSource();
        Task request = pipeline.RequestAsync(secondRoom.Id, cancel.Token);
        await switching.Task.WaitAsync(TimeSpan.FromSeconds(5));

        IReadOnlyList<Room> snapshotRooms = pendingRoomRemoved ? [firstRoom] : [firstRoom, secondRoom];
        await Bind<Func<BackendSnapshot, CancellationToken, Task>>(coordinator, "ReconcileSnapshotAsync")(
            new BackendSnapshot(server.Profile, snapshotRooms, server.Profile.Id, new HashSet<string>()),
            CancellationToken.None);

        Assert.Equal(firstRoom.Id, coordinator.State.ActiveRoomId);
        Assert.Equal(pendingRoomRemoved ? firstRoom.Id : secondRoom.Id, server.LastSynchronizedRoomId);
        if (pendingRoomRemoved)
        {
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        }
        else
        {
            release.SetResult();
            await request.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(secondRoom.Id, coordinator.State.ActiveRoomId);
        }
        Assert.Null(pipeline.PendingRoomId);
    }

    private static void SetField(AppCoordinator coordinator, string name, object value) =>
        typeof(AppCoordinator).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(coordinator, value);

    private static T Bind<T>(AppCoordinator coordinator, string name) where T : Delegate =>
        typeof(AppCoordinator).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.CreateDelegate<T>(coordinator);

    private sealed class MemoryPreferences : IPreferencesStore
    {
        private AppPreferences _saved = AppPreferences.Default;
        public ValueTask<AppPreferences> LoadAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_saved);
        public ValueTask SaveAsync(AppPreferences preferences, CancellationToken cancellationToken = default)
        {
            _saved = preferences;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class DisabledStartup : IWindowsStartupService
    {
        public bool IsEnabled() => false;
        public void SetEnabled(bool value) { }
        public void UpgradeEnabledRegistration() { }
    }

    private sealed class AnonymousIdentityHandler(Guid userId) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/auth/v1/user", request.RequestUri?.AbsolutePath);
            Assert.Equal("old-token", request.Headers.Authorization?.Parameter);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new { id = userId, identities = Array.Empty<object>() }),
                    System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class AuthCredentials : ICredentialStore
    {
        public string? Session { get; set; }
        public int Writes { get; private set; }
        public ValueTask<string?> ReadAsync(CredentialKey key, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Session);
        public ValueTask WriteAsync(CredentialKey key, string value, CancellationToken cancellationToken = default) =>
            throw UnexpectedWrite();
        public ValueTask DeleteAsync(CredentialKey key, CancellationToken cancellationToken = default) =>
            throw UnexpectedWrite();
        public ValueTask<string?> ReadInviteCodeAsync(Guid roomId, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Startup must not read invite codes before Google authentication.");
        public ValueTask WriteInviteCodeAsync(Guid roomId, string inviteCode, CancellationToken cancellationToken = default) =>
            throw UnexpectedWrite();
        public ValueTask DeleteInviteCodeAsync(Guid roomId, CancellationToken cancellationToken = default) =>
            throw UnexpectedWrite();
        private InvalidOperationException UnexpectedWrite()
        {
            Writes++;
            return new InvalidOperationException("Startup must not replace credentials before Google authentication.");
        }
    }

    public class GroupBackend : DispatchProxy
    {
        public Profile Profile { get; } = new(Guid.NewGuid(), "Friend", "pixel_cat");
        public IReadOnlyList<Room> Rooms { get; set; } = [];
        public int Mutations { get; private set; }
        public Func<Task> Synchronize { get; set; } = () => Task.CompletedTask;
        public Guid? LastSynchronizedRoomId { get; private set; }

        public Room NewRoom(string name) => new(Guid.NewGuid(), name, Profile.Id,
            [new RoomMember(Profile.Id, Profile.Nickname, Profile.CharacterId, PresenceState.Online)], "TEST", true, 1);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod!.Name)
            {
                case nameof(IBackendGateway.SaveProfileAsync):
                    return Task.FromResult(Profile with { Nickname = (string)args![0]!, CharacterId = (string)args[1]! });
                case nameof(IBackendGateway.CreateRoomAsync):
                case nameof(IBackendGateway.JoinRoomAsync):
                    Mutations++;
                    Room room = NewRoom("Friends");
                    Rooms = [.. Rooms, room];
                    return targetMethod.Name == nameof(IBackendGateway.CreateRoomAsync)
                        ? Task.FromResult(new CreateRoomResult(room, "TEST"))
                        : Task.FromResult(room);
                case nameof(IBackendGateway.FetchSnapshotAsync):
                    return Task.FromResult(new BackendSnapshot(Profile, Rooms, Profile.Id, new HashSet<string>()));
                case nameof(IBackendGateway.FetchRecentMessagesAsync):
                    return Task.FromResult<IReadOnlyList<ChatMessage>>([]);
                case nameof(IBackendGateway.SynchronizeRealtimeRoomsAsync):
                    LastSynchronizedRoomId = (Guid?)args![1];
                    return Synchronize();
                default:
                    throw new NotSupportedException(targetMethod.Name);
            }
        }
    }
}
