using System.Net;

namespace Sidey.Infrastructure.Realtime;

internal sealed class RealtimeRestRequestException(
    string message, HttpStatusCode statusCode, TimeSpan? retryAfter)
    : HttpRequestException(message, inner: null, statusCode)
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

internal static class RealtimeRestRetryPolicy
{
    public static bool IsTransient(Exception exception) => exception switch
    {
        HttpRequestException { StatusCode: null } => true,
        HttpRequestException { StatusCode: HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests } => true,
        HttpRequestException { StatusCode: >= HttpStatusCode.InternalServerError } => true,
        IOException or TimeoutException or OperationCanceledException => true,
        _ => false,
    };

    public static TimeSpan Delay(int failures, Exception exception) =>
        FirebaseRealtimeRetryPolicy.CalculateDelay(
            failures,
            (exception as HttpRequestException)?.StatusCode,
            (exception as RealtimeRestRequestException)?.RetryAfter,
            Random.Shared.NextDouble());

    public static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        TimeSpan? delay = response.Headers.RetryAfter?.Delta;
        if (response.Headers.RetryAfter?.Date is { } deadline)
        {
            delay = deadline - (response.Headers.Date ?? DateTimeOffset.UtcNow);
        }
        return delay is { } value ? TimeSpan.FromTicks(Math.Max(0, value.Ticks)) : null;
    }

    public static RealtimeRestRequestException Failure(HttpResponseMessage response, string message) =>
        new(message, response.StatusCode, RetryAfter(response));
}
