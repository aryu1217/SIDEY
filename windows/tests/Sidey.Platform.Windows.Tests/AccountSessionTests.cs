using System.Net;
using System.Reflection;
using System.Text.Json;
using Sidey.App.Services;
using Sidey.Core.Abstractions;
using Sidey.Core.Domain;
using Sidey.Infrastructure;
using Sidey.Infrastructure.Backend;
using Sidey.Presentation.Services;

namespace Sidey.Platform.Windows.Tests;

public sealed class AccountSessionTests
{
    [Fact]
    public async Task RemoteLogoutFailureStillClearsTheLocalSession()
    {
        var userId = Guid.NewGuid();
        var credentials = new MemoryCredentials(SessionJson(userId));
        using var client = new HttpClient(new AccountHandler { FailLogout = true });
        using var auth = new SupabaseAnonymousAuthService(Configuration(), credentials, client);

        await auth.SignOutAsync();

        Assert.Null(credentials.SessionJson);
        Assert.Null(credentials.FirebaseRealtimeJson);
    }

    [Fact]
    public async Task AccountDeletionUsesTheAuthenticatedRpcThenReturnsToGoogleOnboarding()
    {
        var userId = Guid.NewGuid();
        var roomId = Guid.NewGuid();
        var credentials = new MemoryCredentials(SessionJson(userId));
        var preferences = new MemoryPreferences(AppPreferences.Default with
        {
            OnboardingCompleted = true,
            CachedNickname = "Sidey",
            CachedCharacterId = "pixel_cat",
            ActiveRoomId = roomId,
            OverlayVisible = false,
        });
        var handler = new AccountHandler();
        using var client = new HttpClient(handler);
        var auth = new SupabaseAnonymousAuthService(Configuration(), credentials, client);
        var backend = new SupabaseBackendGateway(Configuration(), auth, credentials, client);
        await using var coordinator = new AppCoordinator(preferences, credentials);
        SetField(coordinator, "_auth", auth);
        SetField(coordinator, "_backend", backend);
        SetField(coordinator, "_state", CoordinatorState.Initial with
        {
            Profile = new Profile(userId, "Sidey", "pixel_cat"),
            Rooms =
            [
                new Room(
                    roomId,
                    "Friends",
                    userId,
                    [new RoomMember(userId, "Sidey", "pixel_cat", PresenceState.Online)],
                    "TEST",
                    true,
                    1),
            ],
            ActiveRoomId = roomId,
            Preferences = preferences.Value,
            GoogleAuthentication = GoogleAuthenticationState.Verified,
        });

        await coordinator.DeleteAccountAsync();

        Assert.Equal(
            ["/rest/v1/rpc/delete_own_account", "/auth/v1/logout?scope=local"],
            handler.RequestTargets);
        Assert.All(handler.AuthorizationSchemes, scheme => Assert.Equal("Bearer", scheme));
        Assert.Null(credentials.SessionJson);
        Assert.Null(credentials.FirebaseRealtimeJson);
        Assert.Contains(roomId, credentials.DeletedInviteRooms);
        Assert.Equal(GoogleAuthenticationState.Required, coordinator.State.GoogleAuthentication);
        Assert.True(coordinator.State.NeedsOnboarding);
        Assert.Null(coordinator.State.Profile);
        Assert.Empty(coordinator.State.Rooms);
        Assert.Empty(coordinator.State.Messages);
        Assert.Empty(coordinator.State.ActiveEntitlementKeys);
        Assert.Null(coordinator.State.Preferences.CachedNickname);
        Assert.Null(coordinator.State.Preferences.CachedCharacterId);
        Assert.Null(coordinator.State.Preferences.ActiveRoomId);
        Assert.False(coordinator.State.Preferences.OnboardingCompleted);
    }

    private static SupabaseRuntimeConfiguration Configuration() =>
        new(new Uri("https://staging.example.com"), "test-key");

    private static string SessionJson(Guid userId) => JsonSerializer.Serialize(
        new
        {
            AccessToken = "access-token",
            RefreshToken = "refresh-token",
            UserId = userId,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
        },
        new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static void SetField(object target, string name, object value) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(target, value);

    private sealed class AccountHandler : HttpMessageHandler
    {
        public bool FailLogout { get; init; }
        public List<string> RequestTargets { get; } = [];
        public List<string?> AuthorizationSchemes { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            string target = request.RequestUri!.PathAndQuery;
            RequestTargets.Add(target);
            AuthorizationSchemes.Add(request.Headers.Authorization?.Scheme);
            if (FailLogout && target.StartsWith("/auth/v1/logout", StringComparison.Ordinal))
                throw new HttpRequestException("offline");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }
    }

    private sealed class MemoryPreferences(AppPreferences value) : IPreferencesStore
    {
        public AppPreferences Value { get; private set; } = value;
        public ValueTask<AppPreferences> LoadAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Value);
        public ValueTask SaveAsync(
            AppPreferences preferences,
            CancellationToken cancellationToken = default)
        {
            Value = preferences;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class MemoryCredentials(string? sessionJson) : ICredentialStore
    {
        public string? SessionJson { get; private set; } = sessionJson;
        public string? FirebaseRealtimeJson { get; private set; } = "firebase-realtime-session";
        public List<Guid> DeletedInviteRooms { get; } = [];
        public ValueTask<string?> ReadAsync(
            CredentialKey key,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(key switch
            {
                CredentialKey.SupabaseSession => SessionJson,
                CredentialKey.FirebaseRealtimeSession => FirebaseRealtimeJson,
                _ => null,
            });
        public ValueTask WriteAsync(
            CredentialKey key,
            string value,
            CancellationToken cancellationToken = default)
        {
            if (key == CredentialKey.SupabaseSession)
            {
                SessionJson = value;
            }
            else if (key == CredentialKey.FirebaseRealtimeSession)
            {
                FirebaseRealtimeJson = value;
            }
            return ValueTask.CompletedTask;
        }
        public ValueTask DeleteAsync(
            CredentialKey key,
            CancellationToken cancellationToken = default)
        {
            if (key == CredentialKey.SupabaseSession)
            {
                SessionJson = null;
            }
            else if (key == CredentialKey.FirebaseRealtimeSession)
            {
                FirebaseRealtimeJson = null;
            }
            return ValueTask.CompletedTask;
        }
        public ValueTask<string?> ReadInviteCodeAsync(
            Guid roomId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<string?>(null);
        public ValueTask WriteInviteCodeAsync(
            Guid roomId,
            string inviteCode,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask DeleteInviteCodeAsync(
            Guid roomId,
            CancellationToken cancellationToken = default)
        {
            DeletedInviteRooms.Add(roomId);
            return ValueTask.CompletedTask;
        }
    }
}
