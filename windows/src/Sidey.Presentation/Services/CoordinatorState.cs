using Sidey.Core.Abstractions;
using Sidey.Core.Domain;

namespace Sidey.Presentation.Services;

public enum GoogleAuthenticationState { Checking, Required, SigningIn, Verified }

public enum GroupOperation
{
    Idle,
    Creating,
    Joining,
    Switching,
    Mutating,
}

public sealed record RemoteDataLoadState(bool HasValue, bool IsLoading)
{
    public static RemoteDataLoadState Initial { get; } = new(false, true);
    public static RemoteDataLoadState Ready { get; } = new(true, false);
    public bool NeedsSkeleton => IsLoading && !HasValue;
    public RemoteDataLoadState Begin() => this with { IsLoading = true };
    public RemoteDataLoadState EndAttempt() => this with { IsLoading = false };
}

// Profile, equipment entitlements and rooms arrive in one atomic BackendSnapshot.
// Development commerce has its own request and can finish after that snapshot.
public sealed record RemoteContentLoadingState(RemoteDataLoadState Snapshot, RemoteDataLoadState Store)
{
    public static RemoteContentLoadingState Initial { get; } = new(
        RemoteDataLoadState.Initial, RemoteDataLoadState.Initial);
}

public sealed record CoordinatorState(
    Profile? Profile,
    IReadOnlyList<Room> Rooms,
    IReadOnlySet<string> ActiveEntitlementKeys,
    Guid? ActiveRoomId,
    IReadOnlyList<MessageLedgerEntry> Messages,
    AppPreferences Preferences,
    RealtimeConnectionStatus RealtimeConnection,
    IReadOnlyList<CommerceProductState> CommerceProducts,
    bool DevelopmentCommerceEnabled,
    GroupOperation GroupOperation,
    Guid? SwitchingRoomId,
    string? ErrorMessage)
{
    public RemoteContentLoadingState ContentLoading { get; init; } = RemoteContentLoadingState.Initial;

    public GoogleAuthenticationState GoogleAuthentication { get; init; } = GoogleAuthenticationState.Checking;
    public bool GoogleVerified => GoogleAuthentication == GoogleAuthenticationState.Verified;
    public bool NeedsOnboarding => !GoogleVerified || !Preferences.OnboardingCompleted;

    public bool Connected => RealtimeConnection.IsReady;

    public bool ActiveRoomConnected => RealtimeConnection.ActiveRoomTransportConnected;

    public bool ActiveRoomReady => ActiveRoomConnected && RealtimeConnection.RecoveryReconciled;

    public static CoordinatorState Initial { get; } = new(
        null,
        [],
        new HashSet<string>(StringComparer.Ordinal),
        null,
        [],
        AppPreferences.Default,
        RealtimeConnectionStatus.Disconnected,
        WindowsCommerceCatalog.LockedStates(),
        false,
        GroupOperation.Idle,
        null,
        null);
}
