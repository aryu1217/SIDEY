using System.Net;

namespace Sidey.Infrastructure.Authentication;

internal static class FirebaseRealtimeRetryPolicy
{
    public static readonly TimeSpan MaximumLocalDelay = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan StableConnection = TimeSpan.FromSeconds(30);

    public static async Task DelayAsync(TimeSpan delay, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        long started = timeProvider.GetTimestamp();
        TimeSpan remaining = delay;
        while (remaining > TimeSpan.Zero)
        {
            // Timers have a smaller range than Retry-After; retain the full deadline.
            TimeSpan interval = remaining > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : remaining;
            await Task.Delay(interval, timeProvider, cancellationToken).ConfigureAwait(false);
            remaining = delay - timeProvider.GetElapsedTime(started);
        }
    }

    public static TimeSpan CalculateDelay(
        int failureCount,
        HttpStatusCode? statusCode,
        TimeSpan? retryAfter,
        double jitter)
    {
        int exponent = Math.Clamp(failureCount, 1, 17) - 1;
        double baseSeconds = Math.Min(5 * Math.Pow(2, exponent), MaximumLocalDelay.TotalSeconds);
        double clampedJitter = double.IsFinite(jitter) ? Math.Clamp(jitter, 0, 1) : 0.5;
        double local = Math.Min(baseSeconds * (0.8 + 0.4 * clampedJitter), MaximumLocalDelay.TotalSeconds);
        double minimum = statusCode == HttpStatusCode.TooManyRequests ? 30 : 0;
        double server = retryAfter is { } delay ? Math.Max(delay.TotalSeconds, 0) : 0;
        double required = Math.Max(minimum, server);
        double seconds = Math.Max(local, required) + (required > 0 ? required * 0.2 * clampedJitter : 0);
        return seconds >= TimeSpan.MaxValue.TotalSeconds ? TimeSpan.MaxValue : TimeSpan.FromSeconds(seconds);
    }
}
