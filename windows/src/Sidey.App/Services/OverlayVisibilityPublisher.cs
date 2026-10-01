using System.Diagnostics;
using System.Net;

namespace Sidey.App.Services;

/// <summary>Coalesces local preference changes into one bounded server request at a time.</summary>
internal sealed class OverlayVisibilityPublisher : IAsyncDisposable
{
    private static readonly TimeSpan s_debounce = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan s_minimumInterval = TimeSpan.FromMilliseconds(3100);
    private static readonly TimeSpan s_ambiguousRepairDelay = TimeSpan.FromSeconds(15);
    private readonly Lock _gate = new();
    private readonly Func<bool, CancellationToken, Task> _publish;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _timerCancellation;
    private CancellationTokenSource? _requestCancellation;
    private Task? _timer;
    private Task? _request;
    private bool? _desired;
    private bool? _confirmed;
    private TimeSpan _changedAt;
    private TimeSpan? _lastAttempt;
    private TimeSpan? _retryAt;
    private bool _blocked;
    private int _repairAttemptsRemaining = 1;
    private int _generation;

    public OverlayVisibilityPublisher(Func<bool, CancellationToken, Task> publish) =>
        _publish = publish;

    public void Update(bool suppressed)
    {
        lock (_gate)
        {
            if (_lifetime.IsCancellationRequested)
                return;
            if (_desired != suppressed)
            {
                _desired = suppressed;
                _changedAt = Elapsed();
                _retryAt = null;
                _blocked = false;
                _repairAttemptsRemaining = 1;
            }
            ScheduleLocked();
        }
    }

    public void Reconnect(bool suppressed)
    {
        lock (_gate)
        {
            if (_lifetime.IsCancellationRequested)
                return;
            _confirmed = null;
            _desired = suppressed;
            _changedAt = Elapsed();
            _retryAt = null;
            _blocked = false;
            _repairAttemptsRemaining = 1;
            ScheduleLocked();
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _generation++;
            _timerCancellation?.Cancel();
            _requestCancellation?.Cancel();
            _desired = null;
            _confirmed = null;
            _retryAt = null;
            _lastAttempt = null;
            _blocked = false;
            _repairAttemptsRemaining = 1;
        }
    }

    private void ScheduleLocked()
    {
        _timerCancellation?.Cancel();
        if (_request is { IsCompleted: false } || _desired is not { } desired
            || _confirmed == desired || _blocked)
            return;
        TimeSpan now = Elapsed();
        TimeSpan due = Max(_changedAt + s_debounce,
            (_lastAttempt ?? TimeSpan.MinValue) + s_minimumInterval,
            _retryAt ?? TimeSpan.MinValue);
        if (due > now)
        {
            var timerCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _timerCancellation = timerCancellation;
            _timer = WaitAndScheduleAsync(due - now, timerCancellation);
            return;
        }
        _lastAttempt = now;
        _retryAt = null;
        int generation = _generation;
        var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _requestCancellation = requestCancellation;
        _request = PublishAsync(desired, generation, requestCancellation);
    }

    private async Task WaitAndScheduleAsync(TimeSpan delay, CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(delay, cancellation.Token).ConfigureAwait(false);
            lock (_gate)
            {
                if (!cancellation.IsCancellationRequested)
                    ScheduleLocked();
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_timerCancellation, cancellation))
                    _timerCancellation = null;
            }
            cancellation.Dispose();
        }
    }

    private async Task PublishAsync(bool sent, int generation, CancellationTokenSource cancellation)
    {
        bool succeeded = false;
        bool rateLimited = false;
        try
        {
            await _publish(sent, cancellation.Token).ConfigureAwait(false);
            succeeded = true;
        }
        catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.TooManyRequests)
        {
            rateLimited = true;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception) { } // A lost response may follow a committed server mutation.
        lock (_gate)
        {
            _request = null;
            if (ReferenceEquals(_requestCancellation, cancellation))
                _requestCancellation = null;
            cancellation.Dispose();
            if (generation != _generation || _lifetime.IsCancellationRequested)
            {
                ScheduleLocked();
                return;
            }
            if (succeeded)
                _confirmed = sent;
            else if (rateLimited)
                _retryAt = Elapsed() + s_minimumInterval;
            else if (_repairAttemptsRemaining-- > 0)
            {
                _confirmed = null;
                _retryAt = Elapsed() + s_ambiguousRepairDelay;
            }
            else
                _blocked = true;
            if (_desired != sent)
                _blocked = false;
            ScheduleLocked();
        }
    }

    private static TimeSpan Elapsed() => Stopwatch.GetElapsedTime(0);

    private static TimeSpan Max(TimeSpan a, TimeSpan b, TimeSpan c) =>
        a >= b && a >= c ? a : b >= c ? b : c;

    public async ValueTask DisposeAsync()
    {
        Task? request;
        Task? timer;
        lock (_gate)
        {
            _lifetime.Cancel();
            _timerCancellation?.Cancel();
            _requestCancellation?.Cancel();
            request = _request;
            timer = _timer;
        }
        if (request is not null)
            await request.ConfigureAwait(false);
        if (timer is not null)
            await timer.ConfigureAwait(false);
        _lifetime.Dispose();
    }
}
