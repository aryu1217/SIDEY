using System.Text.Json;

namespace Sidey.Infrastructure.Realtime;

internal enum RealtimeSubscriptionFailureKind
{
    Authorization,
    Capacity,
    Configuration,
    Transient,
}

internal sealed class RealtimeSubscriptionException(RealtimeSubscriptionFailureKind failureKind)
    : Exception("Realtime subscription was rejected.")
{
    public RealtimeSubscriptionFailureKind FailureKind { get; } = failureKind;

    public bool SessionFailure { get; private init; }

    public bool AuthorizationFailure => FailureKind == RealtimeSubscriptionFailureKind.Authorization;

    public static RealtimeSubscriptionException FromServerPayload(JsonElement payload)
    {
        // Inspect only the documented error category. Never retain/log raw replies, topics or tokens.
        string? reason = ExtractReason(payload);
        string? code = reason?.Split(':', 2)[0].Trim();
        RealtimeSubscriptionFailureKind kind = code switch
        {
            "InvalidJWTExpiration" or "InvalidJWTToken" or "JwtSignatureError" or "MalformedJWT"
                or "Unauthorized" => RealtimeSubscriptionFailureKind.Authorization,

            "ChannelRateLimitReached" or "ClientJoinRateLimitReached"
                or "ClientPresenceRateLimitReached" or "ConnectionRateLimitReached"
                or "DatabaseConnectionRateLimitReached" or "DatabaseLackOfConnections"
                or "IncreaseConnectionPool" or "IncreaseSubscriptionConnectionPool"
                or "JoinsRateLimitReached" or "MessagePerSecondRateLimitReached"
                or "PresenceRateLimitReached" or "ProjectConnectionLimitReached"
                or "ReplicationMaxWalSendersReached" or "UnableCheckoutConnection" =>
                RealtimeSubscriptionFailureKind.Capacity,

            "InvalidJoinPayload" or "MissingAPIKey" or "PrivateOnly"
                or "RealtimeDisabledForConfiguration" or "RealtimeDisabledForTenant"
                or "TenantNotFound" or "TopicNameRequired" =>
                RealtimeSubscriptionFailureKind.Configuration,

            _ => RealtimeSubscriptionFailureKind.Transient,
        };
        if (reason?.StartsWith("You do not have permissions", StringComparison.Ordinal) == true)
        {
            kind = RealtimeSubscriptionFailureKind.Authorization;
        }
        else if (reason?.Contains("Token has expired", StringComparison.OrdinalIgnoreCase) == true
            || reason?.Contains("Fields `role` and `exp` are required", StringComparison.OrdinalIgnoreCase) == true)
        {
            kind = RealtimeSubscriptionFailureKind.Authorization;
        }
        else if (reason?.Contains("Too many messages per second", StringComparison.OrdinalIgnoreCase) == true
            || reason?.Contains("Too many presence messages per second", StringComparison.OrdinalIgnoreCase) == true
            || reason?.Contains("Client presence rate limit exceeded", StringComparison.OrdinalIgnoreCase) == true)
        {
            kind = RealtimeSubscriptionFailureKind.Capacity;
        }
        bool sessionFailure = code is "InvalidJWTExpiration" or "InvalidJWTToken"
            or "JwtSignatureError" or "MalformedJWT"
            || reason?.Contains("Token has expired", StringComparison.OrdinalIgnoreCase) == true
            || reason?.Contains("Fields `role` and `exp` are required", StringComparison.OrdinalIgnoreCase) == true;
        return new(kind) { SessionFailure = sessionFailure };
    }

    private static string? ExtractReason(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (payload.TryGetProperty("response", out JsonElement response)
            && response.ValueKind == JsonValueKind.Object)
        {
            if (response.TryGetProperty("reason", out JsonElement responseReason)
                && responseReason.ValueKind == JsonValueKind.String)
            {
                return responseReason.GetString();
            }
            if (response.TryGetProperty("error", out JsonElement responseError)
                && responseError.ValueKind == JsonValueKind.String)
            {
                return responseError.GetString();
            }
        }

        if (payload.TryGetProperty("reason", out JsonElement reason)
            && reason.ValueKind == JsonValueKind.String)
        {
            return reason.GetString();
        }

        if (payload.TryGetProperty("message", out JsonElement message)
            && message.ValueKind == JsonValueKind.String)
        {
            return message.GetString();
        }

        return null;
    }
}
