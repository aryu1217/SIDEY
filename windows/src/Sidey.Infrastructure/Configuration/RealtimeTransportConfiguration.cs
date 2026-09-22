namespace Sidey.Infrastructure.Configuration;

public enum RealtimeTransportMode
{
    LegacySupabase,
    FirebaseV2,
}

public sealed record RealtimeTransportSelection(
    RealtimeTransportMode Requested,
    RealtimeTransportMode Effective,
    string Reason);

public static class RealtimeTransportConfiguration
{
    internal const string EnvironmentVariable = "SIDEY_REALTIME_TRANSPORT";

    public static RealtimeTransportSelection FromEnvironment(bool firebaseV2Ready) =>
        Parse(Environment.GetEnvironmentVariable(EnvironmentVariable), firebaseV2Ready);

    internal static RealtimeTransportSelection Parse(string? value, bool firebaseV2Ready)
    {
        RealtimeTransportMode requested = value?.Trim().ToLowerInvariant() switch
        {
            null or "" or "automatic" or "firebase-v2" => RealtimeTransportMode.FirebaseV2,
            "legacy" or "supabase" => RealtimeTransportMode.LegacySupabase,
            _ => throw new InvalidOperationException("SIDEY realtime transport selection is invalid."),
        };
        if (requested == RealtimeTransportMode.FirebaseV2 && !firebaseV2Ready)
        {
            return new RealtimeTransportSelection(
                requested,
                RealtimeTransportMode.LegacySupabase,
                "firebase-v2-not-ready");
        }

        return new RealtimeTransportSelection(requested, requested, "explicit-selection");
    }
}
