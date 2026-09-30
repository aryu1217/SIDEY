using System.Net;

namespace Sidey.Infrastructure.Realtime;

internal enum FirebaseRtdbFailureKind
{
    AccessDenied,
    RateLimited,
    RedirectRejected,
    Transport,
    Server,
    Protocol,
}

internal sealed class FirebaseRtdbRequestException(
    FirebaseRtdbFailureKind failureKind,
    HttpStatusCode? statusCode,
    string message,
    Exception? innerException = null,
    TimeSpan? retryAfter = null) : Exception(message, innerException)
{
    public FirebaseRtdbFailureKind FailureKind { get; } = failureKind;
    public HttpStatusCode? StatusCode { get; } = statusCode;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}
