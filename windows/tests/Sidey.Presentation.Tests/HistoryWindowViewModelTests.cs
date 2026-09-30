using System.Globalization;
using Sidey.Core.Abstractions;
using Sidey.Core.Domain;
using Sidey.Presentation.Services;
using Sidey.Presentation.ViewModels;

namespace Sidey.Presentation.Tests;

public sealed class HistoryWindowViewModelTests
{
    [Fact]
    public void HistoryComposerKeepsDraftWhileRoomConnectionRecovers()
    {
        Guid roomId;
        roomId = Guid.NewGuid();
        Guid userId;
        userId = Guid.NewGuid();
        CoordinatorState state = StateWithRooms(roomId, null, userId) with
        {
            GoogleAuthentication = GoogleAuthenticationState.Verified,
            Preferences = AppPreferences.Default with { OnboardingCompleted = true },
        };
        FakeSideyCoordinator coordinator = new() { State = state };
        using HistoryWindowViewModel history = new(coordinator);
        history.Composer.Draft = "Waiting to send";

        Assert.True(history.Composer.CanCompose);
        Assert.True(history.Composer.IsConnectionRecovering);
        Assert.False(history.Composer.SendCommand.CanExecute(null));
        history.ApplyState(state with
        {
            RealtimeConnection = new RealtimeConnectionStatus(true, false, true),
        });
        Assert.False(history.Composer.SendCommand.CanExecute(null));
        history.ApplyState(state with
        {
            RealtimeConnection = new RealtimeConnectionStatus(true, true, true),
        });
        Assert.True(history.Composer.SendCommand.CanExecute(null));
        history.ApplyState(state with
        {
            RealtimeConnection = new RealtimeConnectionStatus(false, true, true)
            {
                SocketConnected = true,
            },
        });
        Assert.True(history.Composer.SendCommand.CanExecute(null));
        Assert.False(history.Composer.IsConnectionRecovering);
        history.ApplyState(state with
        {
            RealtimeConnection = new RealtimeConnectionStatus(false, true, false)
            {
                SocketConnected = true,
            },
        });
        Assert.False(history.Composer.SendCommand.CanExecute(null));
        Assert.True(history.Composer.IsConnectionRecovering);
        Assert.Equal("Waiting to send", history.Composer.Draft);
    }

    [Fact]
    public async Task ActivationLoadsOldestFirstAndFormatsSystemLocalTime()
    {
        var roomId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        DateTimeOffset newerTime = DateTimeOffset.UtcNow.AddMinutes(-1);
        var room = new Room(
            roomId,
            "테스트",
            userId,
            [new RoomMember(userId, "aryu", "pixel_hamster", PresenceState.Online)],
            "••••-TEST",
            true,
            1);
        var coordinator = new FakeSideyCoordinator
        {
            State = CoordinatorState.Initial with
            {
                Profile = new Profile(userId, "aryu", "pixel_hamster"),
                Rooms = [room],
                ActiveRoomId = roomId,
            },
            MessagePage =
            [
                new ChatMessage(Guid.NewGuid(), roomId, userId, "최신", newerTime),
                new ChatMessage(Guid.NewGuid(), roomId, userId, "이전", newerTime.AddMinutes(-1)),
            ],
        };
        var viewModel = new HistoryWindowViewModel(coordinator);

        await viewModel.ActivateAsync();

        Assert.Equal("최근 기록", viewModel.Title);
        Assert.Equal(["이전", "최신"], viewModel.Items.Select(item => item.Body));
        Assert.Equal(
            newerTime.ToLocalTime().ToString("g", CultureInfo.CurrentCulture),
            viewModel.Items[1].LocalTimeText);
        Assert.True(viewModel.Items[1].IsCurrentUser);
        Assert.False(viewModel.IsEmptyStateVisible);
    }

    [Fact]
    public async Task ActivationWithoutRoomShowsMacParityEmptyState()
    {
        var viewModel = new HistoryWindowViewModel(new FakeSideyCoordinator());

        await viewModel.ActivateAsync();

        Assert.Equal("사용 중인 그룹 없음", viewModel.EmptyMessage);
        Assert.Equal("그룹에 참여하면 최근 메시지를 확인할 수 있습니다.", viewModel.EmptyDescription);
        Assert.True(viewModel.IsEmptyStateVisible);
    }

    [Fact]
    public async Task LiveLedgerReplacesPendingWithConfirmedAndHidesFailedMessages()
    {
        var roomId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        DateTimeOffset createdAt = DateTimeOffset.UtcNow;
        Room room = RoomWithMember(roomId, userId);
        var pending = new MessageLedgerEntry(
            messageId,
            roomId,
            userId,
            "상태 변경",
            createdAt,
            MessageDeliveryState.Pending);
        CoordinatorState state = CoordinatorState.Initial with
        {
            Profile = new Profile(userId, "aryu", "pixel_hamster"),
            Rooms = [room],
            ActiveRoomId = roomId,
            Messages = [pending],
        };
        var coordinator = new FakeSideyCoordinator { State = state };
        var viewModel = new HistoryWindowViewModel(coordinator);

        await viewModel.ActivateAsync();
        Assert.True(Assert.Single(viewModel.Items).IsPending);

        viewModel.ApplyState(state with
        {
            Messages = [pending with { State = MessageDeliveryState.Confirmed }],
        });
        HistoryEntryViewModel confirmed = Assert.Single(viewModel.Items);
        Assert.False(confirmed.IsPending);
        Assert.False(confirmed.IsFailed);

        viewModel.ApplyState(state with
        {
            Messages = [pending with { State = MessageDeliveryState.Failed }],
        });
        Assert.Empty(viewModel.Items);
    }

    [Theory]
    [InlineData(MessageDeliveryState.Pending)]
    [InlineData(MessageDeliveryState.Failed)]
    public async Task ConfirmedServerHistoryOverridesStaleLocalDeliveryState(
        MessageDeliveryState localState)
    {
        var roomId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        DateTimeOffset serverTime = DateTimeOffset.UtcNow.AddSeconds(-1);
        var local = new MessageLedgerEntry(
            messageId, roomId, userId, "보내는 중", serverTime.AddSeconds(-2), localState);
        var coordinator = new FakeSideyCoordinator
        {
            State = StateWithRooms(roomId, null, userId) with { Messages = [local] },
            MessagePage = [new ChatMessage(messageId, roomId, userId, "서버 확정", serverTime)],
        };
        var viewModel = new HistoryWindowViewModel(coordinator);

        await viewModel.ActivateAsync();

        HistoryEntryViewModel item = Assert.Single(viewModel.Items);
        Assert.Equal(messageId, item.Id);
        Assert.Equal("서버 확정", item.Body);
        Assert.False(item.IsPending);
        Assert.False(item.IsFailed);
    }

    [Fact]
    public async Task LocalPendingMessageStaysBelowConfirmedHistoryWhenDeviceClockIsBehind()
    {
        var roomId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        DateTimeOffset confirmedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var pending = new MessageLedgerEntry(
            Guid.NewGuid(), roomId, userId, "보내는 중", confirmedAt.AddMinutes(-2),
            MessageDeliveryState.Pending);
        var coordinator = new FakeSideyCoordinator
        {
            State = StateWithRooms(roomId, null, userId) with { Messages = [pending] },
            MessagePage = [new ChatMessage(Guid.NewGuid(), roomId, userId, "확정", confirmedAt)],
        };
        var viewModel = new HistoryWindowViewModel(coordinator);

        await viewModel.ActivateAsync();

        Assert.Equal(["확정", "보내는 중"], viewModel.Items.Select(item => item.Body));
        Assert.True(viewModel.Items[^1].IsPending);
    }

    [Fact]
    public async Task RoomChangeCancelsStalePageAndLoadsOnlyTheNewRoom()
    {
        var firstRoomId = Guid.NewGuid();
        var secondRoomId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new FakeSideyCoordinator
        {
            State = StateWithRooms(firstRoomId, secondRoomId, userId),
            MessagePageLoader = async (roomId, _, _, cancellationToken) =>
            {
                if (roomId == firstRoomId)
                {
                    firstStarted.TrySetResult();
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        firstCancelled.TrySetResult();
                        throw;
                    }
                }

                return new MessageHistoryPage(
                    [new ChatMessage(Guid.NewGuid(), roomId, userId, "새 그룹", DateTimeOffset.UtcNow)],
                    null);
            },
        };
        var viewModel = new HistoryWindowViewModel(coordinator);

        Task activation = viewModel.ActivateAsync();
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        CoordinatorState secondState = coordinator.State with { ActiveRoomId = secondRoomId };
        coordinator.State = secondState;
        viewModel.ApplyState(secondState);

        await firstCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => viewModel.Items.Count == 1);
        Assert.Equal("새 그룹", Assert.Single(viewModel.Items).Body);
        await activation;
    }

    [Fact]
    public async Task LoadsFiftyAtATimeAndUsesCompositeNextCursor()
    {
        var roomId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        DateTimeOffset createdAt = DateTimeOffset.UtcNow;
        MessageHistoryCursor? requestedCursor = null;
        int requestedPageSize = 0;
        var nextCursor = new MessageHistoryCursor(createdAt.AddMinutes(-1), Guid.NewGuid());
        var coordinator = new FakeSideyCoordinator
        {
            State = StateWithRooms(roomId, null, userId),
            MessagePageLoader = (requestedRoom, cursor, pageSize, _) =>
            {
                requestedCursor = cursor;
                requestedPageSize = pageSize;
                MessageHistoryPage page = cursor is null
                    ? new MessageHistoryPage(
                        [new ChatMessage(Guid.NewGuid(), requestedRoom, userId, "첫 페이지", createdAt)],
                        nextCursor)
                    : new MessageHistoryPage(
                        [new ChatMessage(Guid.NewGuid(), requestedRoom, userId, "이전 페이지", createdAt.AddMinutes(-2))],
                        null);
                return Task.FromResult(page);
            },
        };
        var viewModel = new HistoryWindowViewModel(coordinator);

        await viewModel.ActivateAsync();
        Assert.Equal(50, requestedPageSize);

        await viewModel.LoadMoreCommand.ExecuteAsync(null);
        Assert.Equal(nextCursor, requestedCursor);
        Assert.Equal(["이전 페이지", "첫 페이지"], viewModel.Items.Select(item => item.Body));
        Assert.True(viewModel.IsExhaustedVisible);
    }

    [Fact]
    public async Task InitialFailureHasAnIndependentRetryState()
    {
        var roomId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        int calls = 0;
        var coordinator = new FakeSideyCoordinator
        {
            State = StateWithRooms(roomId, null, userId),
            MessagePageLoader = (requestedRoom, _, _, _) =>
            {
                calls++;
                return calls == 1
                    ? Task.FromException<MessageHistoryPage>(new InvalidOperationException("initial failed"))
                    : Task.FromResult(new MessageHistoryPage(
                        [new ChatMessage(Guid.NewGuid(), requestedRoom, userId, "재시도 성공", DateTimeOffset.UtcNow)],
                        null));
            },
        };
        var viewModel = new HistoryWindowViewModel(coordinator);

        await viewModel.ActivateAsync();
        Assert.True(viewModel.IsInitialFailureVisible);
        Assert.False(viewModel.IsEmptyStateVisible);
        Assert.Equal("initial failed", viewModel.InitialErrorMessage);

        await viewModel.RetryInitialCommand.ExecuteAsync(null);
        Assert.False(viewModel.IsInitialFailureVisible);
        Assert.Equal("재시도 성공", Assert.Single(viewModel.Items).Body);
    }

    [Fact]
    public async Task LoadMoreFailureKeepsTheFirstPageAndCanRetryIndependently()
    {
        var roomId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        DateTimeOffset createdAt = DateTimeOffset.UtcNow;
        var cursor = new MessageHistoryCursor(createdAt.AddMinutes(-1), Guid.NewGuid());
        int loadMoreCalls = 0;
        var coordinator = new FakeSideyCoordinator
        {
            State = StateWithRooms(roomId, null, userId),
            MessagePageLoader = (requestedRoom, requestedCursor, _, _) =>
            {
                if (requestedCursor is null)
                {
                    return Task.FromResult(new MessageHistoryPage(
                        [new ChatMessage(Guid.NewGuid(), requestedRoom, userId, "첫 페이지", createdAt)],
                        cursor));
                }

                loadMoreCalls++;
                return loadMoreCalls == 1
                    ? Task.FromException<MessageHistoryPage>(new InvalidOperationException("more failed"))
                    : Task.FromResult(new MessageHistoryPage(
                        [new ChatMessage(Guid.NewGuid(), requestedRoom, userId, "이전 페이지", createdAt.AddMinutes(-2))],
                        null));
            },
        };
        var viewModel = new HistoryWindowViewModel(coordinator);

        await viewModel.ActivateAsync();
        await viewModel.LoadMoreCommand.ExecuteAsync(null);
        Assert.True(viewModel.IsLoadMoreFailureVisible);
        Assert.False(viewModel.IsInitialFailureVisible);
        Assert.Equal("첫 페이지", Assert.Single(viewModel.Items).Body);

        await viewModel.RetryLoadMoreCommand.ExecuteAsync(null);
        Assert.False(viewModel.IsLoadMoreFailureVisible);
        Assert.Equal(["이전 페이지", "첫 페이지"], viewModel.Items.Select(item => item.Body));
        Assert.True(viewModel.IsExhaustedVisible);
    }

    private static Room RoomWithMember(Guid roomId, Guid userId) => new(
        roomId,
        roomId.ToString("N"),
        userId,
        [new RoomMember(userId, "aryu", "pixel_hamster", PresenceState.Online)],
        "••••-TEST",
        true,
        1);

    private static CoordinatorState StateWithRooms(Guid firstRoomId, Guid? secondRoomId, Guid userId)
    {
        Room[] rooms = secondRoomId is { } second
            ? [RoomWithMember(firstRoomId, userId), RoomWithMember(second, userId)]
            : [RoomWithMember(firstRoomId, userId)];
        return CoordinatorState.Initial with
        {
            Profile = new Profile(userId, "aryu", "pixel_hamster"),
            Rooms = rooms,
            ActiveRoomId = firstRoomId,
        };
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!predicate())
        {
            await Task.Delay(10, timeout.Token);
        }
    }
}
