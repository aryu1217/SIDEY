using Sidey.Core.Abstractions;
using Sidey.Core.Domain;
using Sidey.Presentation.Services;
using Sidey.Presentation.ViewModels;

namespace Sidey.Presentation.Tests;

public sealed class RecentHistoryCacheTests
{
    private static readonly Guid s_roomId = new("11111111-1111-4111-8111-111111111111");
    private static readonly Guid s_userId = new("22222222-2222-4222-8222-222222222222");

    [Fact]
    public async Task ColdAccessDisplaysLiveMessagesBeforeTheServerPageArrives()
    {
        ChatMessage message = Message("already received");
        TaskCompletionSource<MessageHistoryPage> response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeSideyCoordinator coordinator = new()
        {
            State = State() with { Messages = [Confirmed(message)] },
            MessagePageLoader = (_, _, _, _) => response.Task,
        };
        using HistoryWindowViewModel viewModel = new(coordinator);

        Task activation = viewModel.ActivateAsync();

        Assert.Equal(message.Id, Assert.Single(viewModel.Items).Id);
        Assert.True(viewModel.IsInitialLoading);
        response.SetResult(new MessageHistoryPage([message], null));
        await activation;
        Assert.False(viewModel.IsInitialLoading);
    }

    [Fact]
    public async Task FreshReopenSkipsTheQueryAndRetainsTheOlderPageCursor()
    {
        ManualTimeProvider time = new();
        ChatMessage recent = Message("recent");
        ChatMessage older = Message("older", -2);
        MessageHistoryCursor cursor = new(recent.CreatedAt, recent.Id);
        int recentQueries = 0;
        MessageHistoryCursor? requestedCursor = null;
        FakeSideyCoordinator coordinator = new()
        {
            State = State(),
            MessagePageLoader = (_, before, _, _) =>
            {
                requestedCursor = before;
                if (before is null)
                {
                    recentQueries++;
                    return Task.FromResult(new MessageHistoryPage([recent], cursor));
                }
                return Task.FromResult(new MessageHistoryPage([older], null));
            },
        };
        using HistoryWindowViewModel viewModel = new(coordinator, timeProvider: time);

        await viewModel.ActivateAsync();
        viewModel.Deactivate();
        time.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1));
        await viewModel.ActivateAsync();

        Assert.Equal(1, recentQueries);
        Assert.Equal(recent.Id, Assert.Single(viewModel.Items).Id);
        await viewModel.LoadMoreCommand.ExecuteAsync(null);
        Assert.Equal(cursor, requestedCursor);
        Assert.Equal([older.Id, recent.Id], viewModel.Items.Select(item => item.Id));
    }

    [Fact]
    public async Task ExpiredReopenDisplaysCachedHistoryWhileRefreshingAndReplacesThePage()
    {
        ManualTimeProvider time = new();
        ChatMessage old = Message("cached");
        ChatMessage current = Message("current");
        TaskCompletionSource<MessageHistoryPage> refresh = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        FakeSideyCoordinator coordinator = new()
        {
            State = State(),
            MessagePageLoader = (_, _, _, _) => ++calls == 1
                ? Task.FromResult(new MessageHistoryPage([old], null))
                : refresh.Task,
        };
        using HistoryWindowViewModel viewModel = new(coordinator, timeProvider: time);
        await viewModel.ActivateAsync();
        viewModel.Deactivate();
        time.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));

        Task reopening = viewModel.ActivateAsync();

        Assert.Equal(old.Id, Assert.Single(viewModel.Items).Id);
        Assert.True(viewModel.IsInitialLoading);
        refresh.SetResult(new MessageHistoryPage([current], null));
        await reopening;
        Assert.Equal(current.Id, Assert.Single(viewModel.Items).Id);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task FailedRefreshKeepsCachedHistoryAndRetryFetchesAgain()
    {
        ManualTimeProvider time = new();
        ChatMessage cached = Message("cached");
        ChatMessage current = Message("retry succeeded");
        int calls = 0;
        FakeSideyCoordinator coordinator = new()
        {
            State = State(),
            MessagePageLoader = (_, _, _, _) => ++calls switch
            {
                1 => Task.FromResult(new MessageHistoryPage([cached], null)),
                2 => Task.FromException<MessageHistoryPage>(new InvalidOperationException("refresh failed")),
                _ => Task.FromResult(new MessageHistoryPage([current], null)),
            },
        };
        using HistoryWindowViewModel viewModel = new(coordinator, timeProvider: time);
        await viewModel.ActivateAsync();
        viewModel.Deactivate();
        time.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));

        await viewModel.ActivateAsync();

        Assert.Equal(cached.Id, Assert.Single(viewModel.Items).Id);
        Assert.True(viewModel.IsInitialFailureVisible);
        await viewModel.RetryInitialCommand.ExecuteAsync(null);
        Assert.Equal(current.Id, Assert.Single(viewModel.Items).Id);
        Assert.False(viewModel.IsInitialFailureVisible);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task DeletionWhileHiddenInvalidatesTheFreshPage()
    {
        ChatMessage deleted = Message("deleted");
        int calls = 0;
        FakeSideyCoordinator coordinator = new()
        {
            State = State() with { Messages = [Confirmed(deleted)] },
            MessagePageLoader = (_, _, _, _) => Task.FromResult(new MessageHistoryPage(
                ++calls == 1 ? [deleted] : [], null)),
        };
        using HistoryWindowViewModel viewModel = new(coordinator);
        await viewModel.ActivateAsync();
        viewModel.Deactivate();
        coordinator.State = coordinator.State with { Messages = [] };
        viewModel.ApplyState(coordinator.State);

        await viewModel.ActivateAsync();

        Assert.Empty(viewModel.Items);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task DeletionDuringARequestDiscardsTheLateResponseAndUsesTheReplacementQuery()
    {
        ChatMessage deleted = Message("deleted during query");
        TaskCompletionSource<MessageHistoryPage> oldResponse = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        CancellationToken oldToken = default;
        FakeSideyCoordinator coordinator = new()
        {
            State = State() with { Messages = [Confirmed(deleted)] },
            MessagePageLoader = (_, _, _, token) =>
            {
                if (++calls == 1)
                {
                    oldToken = token;
                    return oldResponse.Task;
                }
                return Task.FromResult(new MessageHistoryPage([], null));
            },
        };
        using HistoryWindowViewModel viewModel = new(coordinator);
        Task activation = viewModel.ActivateAsync();
        Assert.Equal(deleted.Id, Assert.Single(viewModel.Items).Id);
        coordinator.State = coordinator.State with { Messages = [] };

        viewModel.ApplyState(coordinator.State);

        Assert.True(oldToken.IsCancellationRequested);
        Assert.Equal(2, calls);
        oldResponse.SetResult(new MessageHistoryPage([deleted], null));
        await activation;
        Assert.Empty(viewModel.Items);
        Assert.False(viewModel.IsInitialLoading);
        viewModel.Deactivate();
        await viewModel.ActivateAsync();
        Assert.Empty(viewModel.Items);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task LiveMessagePruningTheFiftyMessageLedgerUpdatesTheFreshPageAndItsCursor()
    {
        DateTimeOffset createdAt = DateTimeOffset.UtcNow;
        ChatMessage[] initial = [.. Enumerable.Range(0, MessageLedger.MaximumConfirmedPerRoom)
            .Select(index => new ChatMessage(Guid.NewGuid(), s_roomId, s_userId,
                $"history {index}", createdAt.AddSeconds(index - 60)))];
        ChatMessage incoming = new(Guid.NewGuid(), s_roomId, s_userId, "live", createdAt);
        int recentQueries = 0;
        MessageHistoryCursor? requestedCursor = null;
        FakeSideyCoordinator coordinator = new()
        {
            State = State() with { Messages = [.. initial.Select(Confirmed)] },
            MessagePageLoader = (_, before, _, _) =>
            {
                requestedCursor = before;
                if (before is null)
                {
                    recentQueries++;
                    return Task.FromResult(new MessageHistoryPage([.. initial.Reverse()],
                        new MessageHistoryCursor(initial[0].CreatedAt, initial[0].Id)));
                }
                return Task.FromResult(new MessageHistoryPage([], null));
            },
        };
        using HistoryWindowViewModel viewModel = new(coordinator);
        await viewModel.ActivateAsync();
        viewModel.Deactivate();
        coordinator.State = coordinator.State with
        {
            Messages = [.. initial.Skip(1).Select(Confirmed), Confirmed(incoming)],
        };
        viewModel.ApplyState(coordinator.State);

        await viewModel.ActivateAsync();

        Assert.Equal(1, recentQueries);
        Assert.Equal(MessageLedger.MaximumConfirmedPerRoom, viewModel.Items.Count);
        Assert.DoesNotContain(viewModel.Items, item => item.Id == initial[0].Id);
        Assert.Equal(incoming.Id, viewModel.Items[^1].Id);
        await viewModel.LoadMoreCommand.ExecuteAsync(null);
        Assert.Equal(new MessageHistoryCursor(initial[1].CreatedAt, initial[1].Id), requestedCursor);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SameRoomScopeChangeDiscardsAResponseThatIgnoresCancellation(bool accountChanged)
    {
        ChatMessage stale = Message("old scope");
        ChatMessage current = Message("new scope");
        TaskCompletionSource<MessageHistoryPage> oldResponse = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        CancellationToken oldToken = default;
        FakeSideyCoordinator coordinator = new()
        {
            State = State(),
            MessagePageLoader = (_, _, _, token) =>
            {
                if (++calls == 1)
                {
                    oldToken = token;
                    return oldResponse.Task;
                }
                return Task.FromResult(new MessageHistoryPage([current], null));
            },
        };
        using HistoryWindowViewModel viewModel = new(coordinator);
        Task oldActivation = viewModel.ActivateAsync();
        Guid nextUser = accountChanged ? new("33333333-3333-4333-8333-333333333333") : s_userId;
        coordinator.State = State(nextUser, epoch: accountChanged ? 1 : 2);

        viewModel.ApplyState(coordinator.State);

        Assert.True(oldToken.IsCancellationRequested);
        oldResponse.SetResult(new MessageHistoryPage([stale], null));
        await oldActivation;
        Assert.Equal(current.Id, Assert.Single(viewModel.Items).Id);
        viewModel.Deactivate();
        await viewModel.ActivateAsync();
        Assert.Equal(current.Id, Assert.Single(viewModel.Items).Id);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task LossOfMembershipClearsCachedHistoryEvenWhenTheActiveRoomIdIsUnchanged()
    {
        ChatMessage message = Message("private history");
        int calls = 0;
        FakeSideyCoordinator coordinator = new()
        {
            State = State(),
            MessagePageLoader = (_, _, _, _) =>
            {
                calls++;
                return Task.FromResult(new MessageHistoryPage([message], null));
            },
        };
        using HistoryWindowViewModel viewModel = new(coordinator);
        await viewModel.ActivateAsync();
        viewModel.Deactivate();
        coordinator.State = coordinator.State with
        {
            Rooms = [coordinator.State.Rooms[0] with { Members = [] }],
        };
        viewModel.ApplyState(coordinator.State);

        await viewModel.ActivateAsync();

        Assert.Empty(viewModel.Items);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ReopeningRetainsOnlyTheRecentPageInsteadOfPreviouslyLoadedOlderPages()
    {
        ChatMessage recent = Message("recent");
        ChatMessage older = Message("older", -2);
        MessageHistoryCursor cursor = new(recent.CreatedAt, recent.Id);
        FakeSideyCoordinator coordinator = new()
        {
            State = State(),
            MessagePageLoader = (_, before, _, _) => Task.FromResult(before is null
                ? new MessageHistoryPage([recent], cursor)
                : new MessageHistoryPage([older], null)),
        };
        using HistoryWindowViewModel viewModel = new(coordinator);
        await viewModel.ActivateAsync();
        await viewModel.LoadMoreCommand.ExecuteAsync(null);
        Assert.Equal(2, viewModel.Items.Count);
        viewModel.Deactivate();

        await viewModel.ActivateAsync();

        Assert.Equal(recent.Id, Assert.Single(viewModel.Items).Id);
        await viewModel.LoadMoreCommand.ExecuteAsync(null);
        Assert.Equal(2, viewModel.Items.Count);
    }

    private static CoordinatorState State(Guid? userId = null, long epoch = 1)
    {
        Guid accountId = userId ?? s_userId;
        Room room = new(s_roomId, "room", accountId,
            [new RoomMember(accountId, "member", "pixel_hamster", PresenceState.Online)],
            "hint", true, epoch);
        return CoordinatorState.Initial with
        {
            Profile = new Profile(accountId, "member", "pixel_hamster"),
            Rooms = [room],
            ActiveRoomId = s_roomId,
            GoogleAuthentication = GoogleAuthenticationState.Verified,
        };
    }

    private static ChatMessage Message(string body, int minutes = -1) =>
        new(Guid.NewGuid(), s_roomId, s_userId, body, DateTimeOffset.UtcNow.AddMinutes(minutes));

    private static MessageLedgerEntry Confirmed(ChatMessage message) =>
        new(message.Id, message.RoomId, message.SenderId, message.Body, message.CreatedAt,
            MessageDeliveryState.Confirmed);

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _initialUtcNow = DateTimeOffset.UtcNow;
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _timestamp;

        public override DateTimeOffset GetUtcNow() => _initialUtcNow.AddTicks(_timestamp);

        public void Advance(TimeSpan elapsed) => _timestamp += elapsed.Ticks;
    }
}
