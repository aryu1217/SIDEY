using System.Net;
using System.Text;
using System.Text.Json;
using Sidey.Core.Abstractions;
using Sidey.Infrastructure;

namespace Sidey.Platform.Windows.Tests;

public sealed class SupabaseIdentityLinkTests
{
    [Fact]
    public async Task PkceIdentityLinkPreservesTheAnonymousUser()
    {
        var userId = Guid.NewGuid();
        var credentials = new MemoryCredentialStore(SessionJson(userId, "old-token"));
        var handler = new IdentityLinkHandler(userId);
        using var client = new HttpClient(handler);
        using var auth = new SupabaseAnonymousAuthService(
            new SupabaseRuntimeConfiguration(new Uri("https://staging.example.com"), "test-key"),
            credentials,
            client);

        Uri authorization = await auth.BeginGoogleIdentityLinkAsync(
            new Uri("sidey-dev://auth/google"));
        await auth.CompleteGoogleIdentityLinkAsync("returned-code");

        Assert.Equal("accounts.google.com", authorization.Host);
        Assert.Equal(3, handler.RequestCount);
        Assert.Contains("/auth/v1/user/identities/authorize", handler.AuthorizationRequestUri);
        Assert.Contains("provider=google", handler.AuthorizationRequestUri);
        Assert.Contains("prompt=select_account", handler.AuthorizationRequestUri);
        Assert.Contains("redirect_to=sidey-dev%3A%2F%2Fauth%2Fgoogle", handler.AuthorizationRequestUri);
        Assert.Contains("code_challenge_method=s256", handler.AuthorizationRequestUri);
        Assert.DoesNotContain("code_verifier", handler.AuthorizationRequestUri);
        Assert.Contains("returned-code", handler.ExchangeBody);
        Assert.Contains("code_verifier", handler.ExchangeBody);
        Assert.Contains("new-token", credentials.SessionJson!);
    }

    [Fact]
    public async Task IdentityLinkRejectsAChangedUserWithoutReplacingTheStoredSession()
    {
        var originalUserId = Guid.NewGuid();
        string originalSession = SessionJson(originalUserId, "old-token");
        var credentials = new MemoryCredentialStore(originalSession);
        var handler = new IdentityLinkHandler(Guid.NewGuid());
        using var client = new HttpClient(handler);
        using var auth = new SupabaseAnonymousAuthService(
            new SupabaseRuntimeConfiguration(new Uri("https://staging.example.com"), "test-key"),
            credentials,
            client);
        _ = await auth.BeginGoogleIdentityLinkAsync(new Uri("sidey-dev://auth/google"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => auth.CompleteGoogleIdentityLinkAsync("returned-code"));

        Assert.Equal(originalSession, credentials.SessionJson);
    }

    [Fact]
    public async Task FreshGoogleSignInUsesPkceWithoutCreatingAnAnonymousUser()
    {
        var userId = Guid.NewGuid();
        var credentials = new MemoryCredentialStore(null);
        var handler = new IdentityLinkHandler(userId);
        using var client = new HttpClient(handler);
        using var auth = new SupabaseAnonymousAuthService(
            new SupabaseRuntimeConfiguration(new Uri("https://staging.example.com"), "test-key"), credentials, client);
        Uri uri = await auth.BeginGoogleSignInAsync(new Uri("sidey://auth/google"));
        Assert.Equal("/auth/v1/authorize", uri.AbsolutePath);
        Assert.Contains("provider=google", uri.Query);
        Assert.Contains("prompt=select_account", uri.Query);
        Assert.Contains("code_challenge_method=s256", uri.Query);
        Assert.Null(credentials.SessionJson);
        Assert.Equal(0, handler.RequestCount);

        await auth.CompleteGoogleIdentityLinkAsync("returned-code");

        Assert.Equal(2, handler.RequestCount);
        using var exchange = JsonDocument.Parse(handler.ExchangeBody);
        string verifier = exchange.RootElement.GetProperty("code_verifier").GetString()!;
        string challenge = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(verifier)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.Contains($"code_challenge={challenge}", uri.Query);
        Assert.Contains(userId.ToString(), credentials.SessionJson!);
    }

    [Fact]
    public async Task GoogleLoginWithLargeTokenSurvivesRestartOnIndependentInstallations()
    {
        var userId = Guid.NewGuid();
        string accessToken = new('x', 4096);
        var configuration = new SupabaseRuntimeConfiguration(new Uri("https://staging.example.com"), "test-key");

        for (int installation = 0; installation < 2; installation++)
        {
            string prefix = $"SIDEY-Diagnostic/Auth/{Guid.NewGuid():N}/";
            var credentials = new WindowsCredentialStore(prefix);
            try
            {
                var handler = new IdentityLinkHandler(userId) { AccessToken = accessToken };
                using var client = new HttpClient(handler);
                using (var auth = new SupabaseAnonymousAuthService(configuration, credentials, client))
                {
                    await auth.BeginGoogleSignInAsync(new Uri("sidey://auth/google"));
                    await auth.CompleteGoogleIdentityLinkAsync("returned-code");
                }

                using var restarted = new SupabaseAnonymousAuthService(
                    configuration, new WindowsCredentialStore(prefix), client);
                Assert.Equal(userId, (await restarted.RestoreSessionAsync())!.UserId);
                Assert.True(await restarted.HasGoogleIdentityAsync());
                StoredSupabaseSession session = (await ((IAuthSessionAccessor)restarted).GetStoredSessionAsync())!;
                Assert.Equal(accessToken, session.AccessToken);
                Assert.Equal("new-refresh-token", session.RefreshToken);
            }
            finally
            {
                await credentials.DeleteAsync(CredentialKey.SupabaseSession);
            }
        }
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    public async Task LinkFailureOrUnverifiedProviderPreservesOriginalCredentials(bool exchangeFailure, bool verificationFailure, bool google)
    {
        var userId = Guid.NewGuid();
        string original = SessionJson(userId, "old-token");
        var credentials = new MemoryCredentialStore(original);
        var handler = new IdentityLinkHandler(userId)
        {
            FailExchange = exchangeFailure,
            FailVerification = verificationFailure,
            GoogleIdentity = google,
        };
        using var client = new HttpClient(handler);
        using var auth = new SupabaseAnonymousAuthService(
            new SupabaseRuntimeConfiguration(new Uri("https://staging.example.com"), "test-key"), credentials, client);
        await auth.BeginGoogleIdentityLinkAsync(new Uri("sidey://auth/google"));

        await Assert.ThrowsAnyAsync<Exception>(() => auth.CompleteGoogleIdentityLinkAsync("returned-code"));

        Assert.Equal(original, credentials.SessionJson);
        await Assert.ThrowsAnyAsync<Exception>(() => auth.CompleteGoogleIdentityLinkAsync("returned-code"));
        Assert.Equal(original, credentials.SessionJson);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelDiscardsPendingPkceButKeepsTheExistingSession(bool existingUser)
    {
        var userId = Guid.NewGuid();
        string? original = existingUser ? SessionJson(userId, "old-token") : null;
        var credentials = new MemoryCredentialStore(original);
        var handler = new IdentityLinkHandler(userId);
        using var client = new HttpClient(handler);
        using var auth = new SupabaseAnonymousAuthService(
            new SupabaseRuntimeConfiguration(new Uri("https://staging.example.com"), "test-key"), credentials, client);
        if (existingUser)
            await auth.BeginGoogleIdentityLinkAsync(new Uri("sidey://auth/google"));
        else
            await auth.BeginGoogleSignInAsync(new Uri("sidey://auth/google"));
        await auth.CancelGoogleAuthenticationAsync();
        int requests = handler.RequestCount;

        await Assert.ThrowsAsync<InvalidOperationException>(() => auth.CompleteGoogleIdentityLinkAsync("late-code"));

        Assert.Equal(original, credentials.SessionJson);
        Assert.Equal(requests, handler.RequestCount);
    }

    [Fact]
    public async Task ExistingCredentialsCannotUseReplacementSignIn()
    {
        var userId = Guid.NewGuid();
        string original = SessionJson(userId, "old-token");
        var credentials = new MemoryCredentialStore(original);
        var handler = new IdentityLinkHandler(userId);
        using var client = new HttpClient(handler);
        using var auth = new SupabaseAnonymousAuthService(
            new SupabaseRuntimeConfiguration(new Uri("https://staging.example.com"), "test-key"), credentials, client);
        await Assert.ThrowsAsync<InvalidOperationException>(() => auth.BeginGoogleSignInAsync(new Uri("sidey://auth/google")));
        Assert.Equal(0, handler.RequestCount);
        Assert.Equal(original, credentials.SessionJson);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestoreChecksServerIdentitiesRatherThanUserMetadata(bool google)
    {
        var userId = Guid.NewGuid();
        var credentials = new MemoryCredentialStore(SessionJson(userId, "old-token"));
        var handler = new IdentityLinkHandler(userId) { GoogleIdentity = google };
        using var client = new HttpClient(handler);
        using var auth = new SupabaseAnonymousAuthService(
            new SupabaseRuntimeConfiguration(new Uri("https://staging.example.com"), "test-key"), credentials, client);
        Assert.Equal(google, await auth.HasGoogleIdentityAsync());
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task CancelDuringServerVerificationCannotReplaceCredentials()
    {
        var userId = Guid.NewGuid();
        string original = SessionJson(userId, "old-token");
        var credentials = new MemoryCredentialStore(original);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new IdentityLinkHandler(userId) { VerificationStarted = started };
        using var client = new HttpClient(handler);
        using var auth = new SupabaseAnonymousAuthService(
            new SupabaseRuntimeConfiguration(new Uri("https://staging.example.com"), "test-key"), credentials, client);
        await auth.BeginGoogleIdentityLinkAsync(new Uri("sidey://auth/google"));
        using var cancellation = new CancellationTokenSource();
        Task completion = auth.CompleteGoogleIdentityLinkAsync("returned-code", cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await auth.CancelGoogleAuthenticationAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => completion);
        Assert.Equal(original, credentials.SessionJson);
    }

    [Fact]
    public async Task LateCancelledCodeCannotConsumeTheNextAttemptsVerifier()
    {
        var userId = Guid.NewGuid();
        var credentials = new MemoryCredentialStore(null);
        var handler = new IdentityLinkHandler(userId);
        using var client = new HttpClient(handler);
        using var auth = new SupabaseAnonymousAuthService(
            new SupabaseRuntimeConfiguration(new Uri("https://staging.example.com"), "test-key"), credentials, client);
        await auth.BeginGoogleSignInAsync(new Uri("sidey://auth/google"));
        await auth.CancelGoogleAuthenticationAsync();
        Uri current = await auth.BeginGoogleSignInAsync(new Uri("sidey://auth/google"));
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => auth.CompleteGoogleIdentityLinkAsync("stale-code"));
        Assert.Null(credentials.SessionJson);

        await auth.CompleteGoogleIdentityLinkAsync("current-code");

        Assert.Contains("new-token", credentials.SessionJson!);
        using var exchange = JsonDocument.Parse(handler.ExchangeBody);
        string verifier = exchange.RootElement.GetProperty("code_verifier").GetString()!;
        string challenge = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(verifier)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.Contains($"code_challenge={challenge}", current.Query);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReauthenticationCanOnlyRestoreThePreservedUser(bool matchingUser)
    {
        var oldUser = Guid.NewGuid();
        string original = SessionJson(oldUser, "old-token");
        var credentials = new MemoryCredentialStore(original);
        var handler = new IdentityLinkHandler(matchingUser ? oldUser : Guid.NewGuid());
        using var client = new HttpClient(handler);
        using var auth = new SupabaseAnonymousAuthService(
            new SupabaseRuntimeConfiguration(new Uri("https://staging.example.com"), "test-key"), credentials, client);
        Uri uri = await auth.BeginGoogleReauthenticationAsync(new Uri("sidey://auth/google"));
        Assert.Equal("/auth/v1/authorize", uri.AbsolutePath);
        Assert.Contains("prompt=select_account", uri.Query);
        Assert.Equal(original, credentials.SessionJson);
        if (matchingUser)
        {
            await auth.CompleteGoogleIdentityLinkAsync("returned-code");
            Assert.Contains(oldUser.ToString(), credentials.SessionJson!);
            Assert.Contains("new-token", credentials.SessionJson!);
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => auth.CompleteGoogleIdentityLinkAsync("returned-code"));
            Assert.Equal(original, credentials.SessionJson);
        }
    }

    private static string SessionJson(Guid userId, string accessToken) => JsonSerializer.Serialize(
        new StoredSupabaseSession(
            accessToken,
            "refresh-token",
            userId,
            DateTimeOffset.UtcNow.AddHours(1)),
        new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private sealed class IdentityLinkHandler(Guid exchangeUserId) : HttpMessageHandler
    {
        public string AccessToken { get; set; } = "new-token";
        public TaskCompletionSource? VerificationStarted { get; set; }
        public bool GoogleIdentity { get; set; } = true;
        public bool FailExchange { get; set; }
        public bool FailVerification { get; set; }
        public int RequestCount { get; private set; }
        public string AuthorizationRequestUri { get; private set; } = string.Empty;
        public string ExchangeBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            Assert.Equal("test-key", Assert.Single(request.Headers.GetValues("apikey")));
            if (request.RequestUri?.AbsolutePath == "/auth/v1/user")
            {
                Assert.NotNull(request.Headers.Authorization?.Parameter);
                if (VerificationStarted is not null)
                {
                    VerificationStarted.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                if (FailVerification)
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                string provider = GoogleIdentity ? "google" : "email";
                return JsonResponse(JsonSerializer.Serialize(new
                {
                    id = exchangeUserId,
                    identities = new[] { new { provider } },
                    user_metadata = new { provider = "google" }
                }));
            }
            if (request.RequestUri?.AbsolutePath == "/auth/v1/user/identities/authorize")
            {
                Assert.Equal("old-token", request.Headers.Authorization?.Parameter);
                AuthorizationRequestUri = request.RequestUri?.AbsoluteUri ?? string.Empty;
                return JsonResponse("""{"url":"https://accounts.google.com/o/oauth2/v2/auth"}""");
            }

            Assert.Equal("/auth/v1/token", request.RequestUri?.AbsolutePath);
            if (FailExchange)
                return new HttpResponseMessage(HttpStatusCode.BadRequest);
            Assert.Null(request.Headers.Authorization);
            ExchangeBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            if (ExchangeBody.Contains("stale-code", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.BadRequest);
            return JsonResponse($$"""
                {
                  "access_token": "{{AccessToken}}",
                  "refresh_token": "new-refresh-token",
                  "expires_in": 3600,
                  "user": { "id": "{{exchangeUserId}}" }
                }
                """);
        }

        private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }

    private sealed class MemoryCredentialStore(string? sessionJson) : ICredentialStore
    {
        public string? SessionJson { get; private set; } = sessionJson;

        public ValueTask<string?> ReadAsync(
            CredentialKey key,
            CancellationToken cancellationToken = default)
        {
            _ = key;
            _ = cancellationToken;
            return ValueTask.FromResult(SessionJson);
        }

        public ValueTask WriteAsync(
            CredentialKey key,
            string value,
            CancellationToken cancellationToken = default)
        {
            _ = key;
            _ = cancellationToken;
            SessionJson = value;
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteAsync(
            CredentialKey key,
            CancellationToken cancellationToken = default)
        {
            _ = key;
            _ = cancellationToken;
            SessionJson = null;
            return ValueTask.CompletedTask;
        }

        public ValueTask<string?> ReadInviteCodeAsync(
            Guid roomId,
            CancellationToken cancellationToken = default)
        {
            _ = roomId;
            _ = cancellationToken;
            return ValueTask.FromResult<string?>(null);
        }

        public ValueTask WriteInviteCodeAsync(
            Guid roomId,
            string inviteCode,
            CancellationToken cancellationToken = default)
        {
            _ = roomId;
            _ = inviteCode;
            _ = cancellationToken;
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteInviteCodeAsync(
            Guid roomId,
            CancellationToken cancellationToken = default)
        {
            _ = roomId;
            _ = cancellationToken;
            return ValueTask.CompletedTask;
        }
    }
}
