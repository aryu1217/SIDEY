using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sidey.Core.Abstractions;
using Sidey.Core.Domain;
using Sidey.Core.Localization;
using Sidey.Presentation.Services;

namespace Sidey.Presentation.ViewModels;

public sealed partial class HistoryWindowViewModel : ObservableObject, IDisposable
{
    private const int PageSize = 50;
    private const int MaximumCachedRooms = 5;
    private static readonly TimeSpan s_cacheFreshness = TimeSpan.FromMinutes(5);

    private readonly IHistoryCoordinator _coordinator;
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<Guid, CachedHistoryPage> _cachedPages = [];
    private readonly Dictionary<Guid, ChatMessage> _pagedMessages = [];
    private CoordinatorState _state;
    private Guid? _loadedRoomId;
    private MessageHistoryCursor? _nextCursor;
    private CancellationTokenSource? _requestCancellation;
    private long _requestGeneration;
    private HistoryScope? _requestScope;
    private bool _hasMore;
    private bool _isActive;
    private bool _disposed;
    private readonly bool _ownsComposer;

    [ObservableProperty]
    public partial string Title { get; set; } = I18n.Get("history.recent.title");

    [ObservableProperty]
    public partial string EmptyMessage { get; set; } = I18n.Get("history.empty.title");

    [ObservableProperty]
    public partial string EmptyDescription { get; set; } = I18n.Get("history.empty.description");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmptyStateVisible))]
    public partial bool IsInitialLoading { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmptyStateVisible))]
    public partial bool IsInitialFailureVisible { get; set; }

    [ObservableProperty]
    public partial string InitialErrorMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsLoadingMore { get; set; }

    [ObservableProperty]
    public partial bool IsLoadMoreFailureVisible { get; set; }

    [ObservableProperty]
    public partial string LoadMoreErrorMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsExhaustedVisible { get; set; }

    public HistoryWindowViewModel(
        IHistoryCoordinator coordinator,
        ComposerViewModel? composer = null,
        TimeProvider? timeProvider = null)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _state = coordinator.State;
        _ownsComposer = composer is null;
        Composer = composer ?? new ComposerViewModel(autoCloseAfterSend: false);
        ApplyComposerState();
    }

    public ComposerViewModel Composer { get; }

    private void ApplyComposerState() => Composer.ApplyRoom(_state.ActiveRoomId,
        _state.ActiveRoomId is not null && !_state.NeedsOnboarding && _state.GroupOperation == GroupOperation.Idle,
        _state.RealtimeConnection.IsReady && _state.RealtimeConnection.ActiveRoomTransportConnected);

    public ObservableCollection<HistoryEntryViewModel> Items { get; } = [];

    public CoordinatorState CurrentState => _state;

    public bool IsEmptyStateVisible =>
        Items.Count == 0 && !IsInitialLoading && !IsInitialFailureVisible;

    public void ApplyState(CoordinatorState state)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        bool scopeChanged = HistoryScopeFor(_state, _state.ActiveRoomId)
            != HistoryScopeFor(state, state.ActiveRoomId);
        bool historyInvalidated = state.RealtimeConnection.IsReady != _state.RealtimeConnection.IsReady;
        if (state.Profile?.Id != _state.Profile?.Id
            || historyInvalidated)
        {
            _cachedPages.Clear();
        }
        HashSet<Guid> confirmedIds = [.. state.Messages
            .Where(entry => entry.State == MessageDeliveryState.Confirmed)
            .Select(entry => entry.Id)];
        foreach (MessageLedgerEntry removed in _state.Messages.Where(entry =>
            entry.State == MessageDeliveryState.Confirmed && !confirmedIds.Contains(entry.Id)))
        {
            if (WasPrunedFromRecentWindow(removed, state.Messages))
            {
                continue;
            }
            _cachedPages.Remove(removed.RoomId);
            _pagedMessages.Remove(removed.Id);
            historyInvalidated |= removed.RoomId == _loadedRoomId;
        }
        bool restartRequest = historyInvalidated && _requestCancellation is not null;
        _state = state;
        RemoveInvalidCachedPages();
        RefreshCachedMessages();
        if (scopeChanged || restartRequest)
        {
            CancelRequest();
            ResetLoadedHistory(roomId: null);
        }
        ApplyComposerState();
        if (!_isActive)
        {
            return;
        }

        if (scopeChanged || restartRequest)
        {
            _ = ReloadAsync();
        }
        else
        {
            RebuildEntries();
        }
    }

    public void RefreshLocalizedText()
    {
        Title = I18n.Get("history.recent.title");
        RebuildEntries();
        UpdateEmptyState();
    }

    public async Task ActivateAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _isActive = true;
        if (_loadedRoomId != _state.ActiveRoomId)
        {
            await ReloadAsync();
        }
        else
        {
            RebuildEntries();
        }
    }

    public void Deactivate()
    {
        if (_disposed)
        {
            return;
        }

        _isActive = false;
        Composer.OnHidden();
        CancelRequest();
        ResetLoadedHistory(roomId: null);
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task ReloadAsync()
    {
        if (!_isActive || _disposed)
        {
            return;
        }

        Guid? roomId = _state.ActiveRoomId;
        HistoryScope? scope = HistoryScopeFor(_state, roomId);
        CancelRequest();
        ResetLoadedHistory(scope?.RoomId);
        Title = I18n.Get("history.recent.title");
        if (scope is not { } currentScope)
        {
            UpdateEmptyState();
            return;
        }

        if (_cachedPages.TryGetValue(currentScope.RoomId, out CachedHistoryPage? cached)
            && cached.Scope == currentScope)
        {
            MergePage(cached.Page.Messages, currentScope.RoomId);
            _nextCursor = cached.Page.NextCursor;
            _hasMore = _nextCursor is not null;
            RebuildEntries();
            IsExhaustedVisible = !_hasMore && Items.Count > 0;
            if (_timeProvider.GetElapsedTime(cached.FetchedAt) < s_cacheFreshness)
            {
                return;
            }
        }
        else
        {
            RebuildEntries();
        }

        IsInitialLoading = true;
        (long generation, CancellationToken cancellationToken) = StartRequest();
        try
        {
            MessageHistoryPage page = await _coordinator.FetchMessagePageAsync(
                currentScope.RoomId,
                before: null,
                PageSize,
                cancellationToken);
            if (!IsCurrentRequest(currentScope.RoomId, generation))
            {
                return;
            }

            _pagedMessages.Clear();
            MergePage(page.Messages, currentScope.RoomId);
            CacheFirstPage(currentScope, page);
            _nextCursor = page.NextCursor;
            _hasMore = page.NextCursor is not null;
            RebuildEntries();
            IsExhaustedVisible = !_hasMore && Items.Count > 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (IsCurrentRequest(currentScope.RoomId, generation))
            {
                InitialErrorMessage = exception.Message;
                IsInitialFailureVisible = true;
            }
        }
        finally
        {
            if (IsCurrentRequest(currentScope.RoomId, generation))
            {
                IsInitialLoading = false;
                CompleteRequest();
                UpdateEmptyState();
            }
        }
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task LoadMoreAsync()
    {
        if (!_isActive
            || _disposed
            || IsInitialLoading
            || IsLoadingMore
            || !_hasMore
            || _nextCursor is not { } cursor
            || _loadedRoomId is not { } roomId)
        {
            return;
        }

        IsLoadingMore = true;
        IsLoadMoreFailureVisible = false;
        LoadMoreErrorMessage = string.Empty;
        (long generation, CancellationToken cancellationToken) = StartRequest();
        try
        {
            MessageHistoryPage page = await _coordinator.FetchMessagePageAsync(
                roomId,
                cursor,
                PageSize,
                cancellationToken);
            if (!IsCurrentRequest(roomId, generation))
            {
                return;
            }

            MergePage(page.Messages, roomId);
            _nextCursor = page.NextCursor;
            _hasMore = page.NextCursor is not null && page.NextCursor != cursor;
            IsExhaustedVisible = !_hasMore;
            RebuildEntries();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (IsCurrentRequest(roomId, generation))
            {
                LoadMoreErrorMessage = exception.Message;
                IsLoadMoreFailureVisible = true;
            }
        }
        finally
        {
            if (IsCurrentRequest(roomId, generation))
            {
                IsLoadingMore = false;
                CompleteRequest();
            }
        }
    }

    [RelayCommand]
    private async Task RetryInitialAsync()
    {
        if (IsInitialFailureVisible)
        {
            await ReloadAsync();
        }
    }

    [RelayCommand]
    private async Task RetryLoadMoreAsync()
    {
        if (IsLoadMoreFailureVisible)
        {
            IsLoadMoreFailureVisible = false;
            await LoadMoreAsync();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Composer.OnHidden();
        if (_ownsComposer)
            Composer.Dispose();
        _isActive = false;
        CancelRequest();
        _cachedPages.Clear();
        _pagedMessages.Clear();
        Items.Clear();
    }

    private void MergePage(IEnumerable<ChatMessage> messages, Guid roomId)
    {
        foreach (ChatMessage message in messages.Where(message => message.RoomId == roomId))
        {
            _pagedMessages[message.Id] = message;
        }
    }

    private void RebuildEntries()
    {
        if (_loadedRoomId is not { } roomId)
        {
            Items.Clear();
            UpdateEmptyState();
            return;
        }

        DateTimeOffset cutoff = DateTimeOffset.UtcNow - MessageLedger.ConfirmedRetention;
        var entriesById = _pagedMessages.Values
            .Where(message => message.RoomId == roomId && message.CreatedAt >= cutoff)
            .ToDictionary(
                message => message.Id,
                message => new MessageLedgerEntry(
                    message.Id,
                    message.RoomId,
                    message.SenderId,
                    message.Body,
                    message.CreatedAt,
                    MessageDeliveryState.Confirmed));
        foreach (MessageLedgerEntry entry in _state.Messages.Where(
            entry => entry.RoomId == roomId && entry.CreatedAt >= cutoff
                && entry.State != MessageDeliveryState.Failed))
        {
            // Postgres history is authoritative when a response was lost after commit.
            if (entriesById.ContainsKey(entry.Id))
            {
                continue;
            }
            entriesById[entry.Id] = entry;
        }

        HistoryEntryViewModel[] desired = [.. entriesById.Values
            .OrderBy(entry => entry.State == MessageDeliveryState.Confirmed ? 0 : 1)
            .ThenBy(entry => entry.CreatedAt)
            .ThenBy(entry => entry.Id.ToString("D"), StringComparer.Ordinal)
            .Select(ToViewModel)];
        ReplaceItems(desired);
        UpdateEmptyState();
    }

    private void ReplaceItems(IReadOnlyList<HistoryEntryViewModel> desired)
    {
        for (int index = Items.Count - 1; index >= 0; index--)
        {
            if (desired.All(item => item.Id != Items[index].Id))
            {
                Items.RemoveAt(index);
            }
        }

        for (int index = 0; index < desired.Count; index++)
        {
            HistoryEntryViewModel item = desired[index];
            int existingIndex = IndexOf(item.Id);
            if (existingIndex < 0)
            {
                Items.Insert(index, item);
                continue;
            }

            if (existingIndex != index)
            {
                Items.Move(existingIndex, index);
            }

            if (Items[index] != item)
            {
                Items[index] = item;
            }
        }
    }

    private int IndexOf(Guid id)
    {
        for (int index = 0; index < Items.Count; index++)
        {
            if (Items[index].Id == id)
            {
                return index;
            }
        }

        return -1;
    }

    private HistoryEntryViewModel ToViewModel(MessageLedgerEntry entry)
    {
        RoomMember? member = ActiveRoom()?.Members.FirstOrDefault(
            candidate => candidate.UserId == entry.SenderId);
        return new HistoryEntryViewModel(
            entry.Id,
            member?.Nickname ?? I18n.Get("history.user.unknown"),
            entry.Body,
            entry.CreatedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture),
            PixelCharacterCatalog.NormalizeId(member?.CharacterId),
            entry.SenderId == _state.Profile?.Id,
            entry.State == MessageDeliveryState.Pending,
            entry.State == MessageDeliveryState.Failed,
            entry.CreatedAt);
    }

    private Room? ActiveRoom() => _loadedRoomId is { } roomId
        ? _state.Rooms.FirstOrDefault(room => room.Id == roomId)
        : null;

    private (long Generation, CancellationToken CancellationToken) StartRequest()
    {
        _requestCancellation = new CancellationTokenSource();
        _requestScope = HistoryScopeFor(_state, _loadedRoomId);
        long generation = ++_requestGeneration;
        return (generation, _requestCancellation.Token);
    }

    private void CompleteRequest()
    {
        _requestCancellation?.Dispose();
        _requestCancellation = null;
        _requestScope = null;
    }

    private void CancelRequest()
    {
        _requestGeneration++;
        _requestCancellation?.Cancel();
        _requestCancellation?.Dispose();
        _requestCancellation = null;
        _requestScope = null;
        IsInitialLoading = false;
        IsLoadingMore = false;
    }

    private bool IsCurrentRequest(Guid roomId, long generation) =>
        _isActive
        && !_disposed
        && _loadedRoomId == roomId
        && _requestScope is { } scope
        && scope == HistoryScopeFor(_state, roomId)
        && _requestGeneration == generation;

    private static HistoryScope? HistoryScopeFor(CoordinatorState state, Guid? roomId)
    {
        if (state.Profile is not { } profile || roomId is not { } id)
        {
            return null;
        }
        Room? room = state.Rooms.FirstOrDefault(candidate => candidate.Id == id);
        return room is not null && room.Members.Any(member => member.UserId == profile.Id)
            ? new HistoryScope(profile.Id, id, room.RealtimeEpoch)
            : null;
    }

    private void RemoveInvalidCachedPages()
    {
        foreach (Guid roomId in _cachedPages.Keys.ToArray())
        {
            if (_cachedPages[roomId].Scope != HistoryScopeFor(_state, roomId))
            {
                _cachedPages.Remove(roomId);
            }
        }
    }

    private void CacheFirstPage(HistoryScope scope, MessageHistoryPage page)
    {
        RemoveInvalidCachedPages();
        if (!_cachedPages.ContainsKey(scope.RoomId) && _cachedPages.Count >= MaximumCachedRooms)
        {
            Guid oldestRoomId = _cachedPages.MinBy(entry => entry.Value.FetchedAt).Key;
            _cachedPages.Remove(oldestRoomId);
        }
        MessageHistoryPage boundedPage = new(
            [.. page.Messages.Where(message => message.RoomId == scope.RoomId).Take(PageSize)],
            page.NextCursor);
        _cachedPages[scope.RoomId] = new CachedHistoryPage(scope, boundedPage, _timeProvider.GetTimestamp());
        RefreshCachedMessages();
    }

    private static bool WasPrunedFromRecentWindow(
        MessageLedgerEntry removed,
        IReadOnlyList<MessageLedgerEntry> messages)
    {
        MessageLedgerEntry[] confirmed = [.. messages.Where(entry =>
            entry.RoomId == removed.RoomId && entry.State == MessageDeliveryState.Confirmed)];
        if (confirmed.Length < MessageLedger.MaximumConfirmedPerRoom)
        {
            return false;
        }
        MessageLedgerEntry oldest = confirmed
            .OrderBy(entry => entry.CreatedAt)
            .ThenBy(entry => entry.Id.ToString("D"), StringComparer.Ordinal)
            .First();
        return removed.CreatedAt < oldest.CreatedAt
            || (removed.CreatedAt == oldest.CreatedAt
                && StringComparer.Ordinal.Compare(removed.Id.ToString("D"), oldest.Id.ToString("D")) < 0);
    }

    private void RefreshCachedMessages()
    {
        foreach (Guid roomId in _cachedPages.Keys.ToArray())
        {
            CachedHistoryPage cached = _cachedPages[roomId];
            Dictionary<Guid, ChatMessage> messages = new(cached.Page.Messages.Select(message =>
                new KeyValuePair<Guid, ChatMessage>(message.Id, message)));
            foreach (MessageLedgerEntry entry in _state.Messages.Where(entry =>
                entry.RoomId == roomId && entry.State == MessageDeliveryState.Confirmed))
            {
                messages.TryAdd(entry.Id, new ChatMessage(
                    entry.Id, entry.RoomId, entry.SenderId, entry.Body, entry.CreatedAt, entry.BubbleStyleId));
            }
            ChatMessage[] latest = [.. messages.Values
                .Where(message => message.CreatedAt >= DateTimeOffset.UtcNow - MessageLedger.ConfirmedRetention)
                .OrderByDescending(message => message.CreatedAt)
                .ThenByDescending(message => message.Id.ToString("D"), StringComparer.Ordinal)
                .Take(PageSize)];
            MessageHistoryCursor? cursor = messages.Count > PageSize && latest.LastOrDefault() is { } oldest
                ? new MessageHistoryCursor(oldest.CreatedAt, oldest.Id)
                : cached.Page.NextCursor;
            _cachedPages[roomId] = cached with { Page = new MessageHistoryPage(latest, cursor) };
        }
    }

    private readonly record struct HistoryScope(Guid AccountId, Guid RoomId, long RoomEpoch);
    private sealed record CachedHistoryPage(HistoryScope Scope, MessageHistoryPage Page, long FetchedAt);

    private void ResetLoadedHistory(Guid? roomId)
    {
        _loadedRoomId = roomId;
        _pagedMessages.Clear();
        _nextCursor = null;
        _hasMore = false;
        Items.Clear();
        IsInitialFailureVisible = false;
        InitialErrorMessage = string.Empty;
        IsLoadMoreFailureVisible = false;
        LoadMoreErrorMessage = string.Empty;
        IsExhaustedVisible = false;
    }

    private void UpdateEmptyState()
    {
        if (Items.Count == 0 && !IsInitialLoading && !IsInitialFailureVisible)
        {
            EmptyMessage = _loadedRoomId is null
                ? I18n.Get("history.group.none")
                : I18n.Get("history.empty.title");
            EmptyDescription = _loadedRoomId is null
                ? I18n.Get("history.group.required")
                : I18n.Get("history.empty.description");
        }

        OnPropertyChanged(nameof(IsEmptyStateVisible));
    }
}
