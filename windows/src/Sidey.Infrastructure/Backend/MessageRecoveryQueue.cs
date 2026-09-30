using Sidey.Core.Domain;

namespace Sidey.Infrastructure.Backend;

/// <summary>Retains at most one failed reconciliation per joined room and serializes its reads.</summary>
internal sealed class MessageRecoveryQueue : IAsyncDisposable
{
    internal const int Capacity = 5;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _fetchGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown;
    private readonly Func<Guid, CancellationToken, Task<IReadOnlyList<ChatMessage>>> _fetch;
    private readonly Func<Guid, long, IReadOnlyList<ChatMessage>, CancellationToken, Task> _publish;
    private readonly Func<int, Exception, TimeSpan> _retryDelay;
    private readonly Dictionary<Guid, Pending> _pending = [];
    private IReadOnlyDictionary<Guid, long> _rooms = new Dictionary<Guid, long>();
    private readonly List<Task> _retired = [];
    private int _disposed;

    public MessageRecoveryQueue(
        Func<Guid, CancellationToken, Task<IReadOnlyList<ChatMessage>>> fetch,
        Func<Guid, long, IReadOnlyList<ChatMessage>, CancellationToken, Task> publish,
        CancellationToken cancellationToken,
        Func<int, Exception, TimeSpan>? retryDelay = null)
    {
        _fetch = fetch;
        _publish = publish;
        _shutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _retryDelay = retryDelay ?? RealtimeRestRetryPolicy.Delay;
    }

    public void UpdateRooms(IReadOnlyDictionary<Guid, long> rooms)
    {
        lock (_gate)
        {
            _rooms = rooms.ToDictionary(pair => pair.Key, pair => pair.Value);
            foreach ((Guid roomId, Pending pending) in _pending.ToArray())
            {
                if (!_rooms.TryGetValue(roomId, out long epoch) || epoch != pending.Epoch)
                {
                    pending.Cancellation.Cancel();
                    _pending.Remove(roomId);
                }
            }
        }
    }

    public void Mark(Guid roomId, Exception failure)
    {
        if (!RealtimeRestRetryPolicy.IsTransient(failure))
        {
            return;
        }
        lock (_gate)
        {
            if (_shutdown.IsCancellationRequested || !_rooms.TryGetValue(roomId, out long epoch))
            {
                return;
            }
            if (_pending.TryGetValue(roomId, out Pending? existing))
            {
                existing.Revision++;
                return;
            }
            if (_pending.Count >= Capacity)
            {
                return;
            }
            var pending = new Pending(epoch, CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token));
            _pending.Add(roomId, pending);
            _retired.RemoveAll(task => task.IsCompletedSuccessfully);
            _retired.Add(Task.Run(() => RecoverAsync(roomId, pending, failure)));
        }
    }

    private async Task RecoverAsync(Guid roomId, Pending pending, Exception failure)
    {
        int failures = 1;
        CancellationToken token = pending.Cancellation.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                await FirebaseRealtimeRetryPolicy.DelayAsync(_retryDelay(failures, failure), TimeProvider.System, token)
                    .ConfigureAwait(false);
                await _fetchGate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    long revision;
                    lock (_gate)
                    {
                        if (!IsCurrent(roomId, pending))
                            return;
                        revision = pending.Revision;
                    }
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
                    IReadOnlyList<ChatMessage> messages = await _fetch(roomId, attempt.Token).ConfigureAwait(false);
                    lock (_gate)
                    {
                        if (!IsCurrent(roomId, pending))
                            return;
                    }
                    token.ThrowIfCancellationRequested();
                    await _publish(roomId, pending.Epoch, messages, token).ConfigureAwait(false);
                    lock (_gate)
                    {
                        if (!IsCurrent(roomId, pending))
                            return;
                        if (revision == pending.Revision)
                        {
                            _pending.Remove(roomId);
                            return;
                        }
                    }
                    failures = 1;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    if (!RealtimeRestRetryPolicy.IsTransient(exception))
                        return;
                    failure = exception;
                    failures = Math.Min(failures + 1, 17);
                }
                finally
                {
                    _fetchGate.Release();
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        finally
        {
            lock (_gate)
            {
                if (_pending.TryGetValue(roomId, out Pending? current) && ReferenceEquals(current, pending))
                    _pending.Remove(roomId);
            }
            pending.Cancellation.Dispose();
        }
    }

    private bool IsCurrent(Guid roomId, Pending pending) =>
        !pending.Cancellation.IsCancellationRequested
        && _rooms.TryGetValue(roomId, out long epoch) && epoch == pending.Epoch
        && _pending.TryGetValue(roomId, out Pending? current) && ReferenceEquals(current, pending);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _shutdown.Cancel();
        Task[] workers;
        lock (_gate)
        {
            workers = [.. _retired];
            _rooms = new Dictionary<Guid, long>();
        }
        await Task.WhenAll(workers).ConfigureAwait(false);
        _shutdown.Dispose();
        _fetchGate.Dispose();
    }

    private sealed class Pending(long epoch, CancellationTokenSource cancellation)
    {
        public long Epoch { get; } = epoch;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public long Revision { get; set; }
    }
}
