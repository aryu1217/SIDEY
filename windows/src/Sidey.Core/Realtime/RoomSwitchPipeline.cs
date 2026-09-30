using Sidey.Core.Domain;

namespace Sidey.Core.Realtime;

public sealed class RoomSwitchPipeline(
    Func<Guid, CancellationToken, Task<IReadOnlyList<ChatMessage>>> performSwitch,
    Func<Guid?, CancellationToken, Task> restoreCommittedRoom,
    Action<Guid, IReadOnlyList<ChatMessage>> commit,
    TimeSpan? debounce = null) : IAsyncDisposable
{
    private sealed record PendingRoom(Guid RoomId);

    public static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(150);

    private readonly Func<Guid, CancellationToken, Task<IReadOnlyList<ChatMessage>>> _performSwitch =
        performSwitch ?? throw new ArgumentNullException(nameof(performSwitch));
    private readonly Func<Guid?, CancellationToken, Task> _restoreCommittedRoom =
        restoreCommittedRoom ?? throw new ArgumentNullException(nameof(restoreCommittedRoom));
    private readonly Action<Guid, IReadOnlyList<ChatMessage>> _commit =
        commit ?? throw new ArgumentNullException(nameof(commit));
    private readonly SemaphoreSlim _networkGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TimeSpan _debounce = debounce ?? DefaultDebounce;
    private long _generation;
    private PendingRoom? _pendingRoom;

    public Guid? CommittedRoomId { get; private set; }
    public Guid? PendingRoomId => Volatile.Read(ref _pendingRoom)?.RoomId;

    public void InitializeCommittedRoom(Guid? roomId) => CommittedRoomId = roomId;

    public async Task RequestAsync(Guid roomId, CancellationToken cancellationToken = default)
    {
        long generation = Interlocked.Increment(ref _generation);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _shutdown.Token);
        await Task.Delay(_debounce, linked.Token);
        if (generation != Volatile.Read(ref _generation))
        {
            return;
        }

        await _networkGate.WaitAsync(linked.Token);
        try
        {
            if (generation != Volatile.Read(ref _generation))
            {
                return;
            }

            var pending = new PendingRoom(roomId);
            Volatile.Write(ref _pendingRoom, pending);
            try
            {
                IReadOnlyList<ChatMessage> messages = await _performSwitch(roomId, linked.Token);
                if (generation != Volatile.Read(ref _generation))
                {
                    return;
                }

                CommittedRoomId = roomId;
                _commit(roomId, messages);
            }
            catch when (generation != Volatile.Read(ref _generation))
            {
                // A stale request neither commits nor rolls the latest request back.
            }
            catch
            {
                Interlocked.CompareExchange(ref _pendingRoom, null, pending);
                await _restoreCommittedRoom(CommittedRoomId, linked.Token);
                throw;
            }
            finally
            {
                Interlocked.CompareExchange(ref _pendingRoom, null, pending);
            }
        }
        finally
        {
            _networkGate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        _shutdown.Dispose();
        _networkGate.Dispose();
        return ValueTask.CompletedTask;
    }
}
