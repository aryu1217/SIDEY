using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sidey.Core.Abstractions;
using Sidey.Core.Localization;

namespace Sidey.Infrastructure.Authentication;

internal interface IAuthSessionAccessor
{
    public ValueTask<StoredSupabaseSession?> GetStoredSessionAsync(CancellationToken cancellationToken = default);
}

internal sealed record StoredSupabaseSession(
    string AccessToken,
    string RefreshToken,
    Guid UserId,
    DateTimeOffset ExpiresAt);

public sealed class SupabaseAnonymousAuthService : IAuthService, IAuthSessionAccessor, IDisposable
{
    private static readonly JsonSerializerOptions s_serializerOptions = new(JsonSerializerDefaults.Web);

    private readonly SupabaseRuntimeConfiguration _configuration;
    private readonly ICredentialStore _credentials;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private PendingIdentityLink? _pendingIdentityLink;

    public SupabaseAnonymousAuthService(
        SupabaseRuntimeConfiguration configuration,
        ICredentialStore credentials,
        HttpClient? httpClient = null)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _httpClient = httpClient ?? new HttpClient();
        _ownsHttpClient = httpClient is null;
    }

    public async Task<AuthSession?> RestoreSessionAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            StoredSupabaseSession? stored = await RestoreStoredSessionWithinGateAsync(cancellationToken)
                .ConfigureAwait(false);
            return stored is null ? null : DomainSession(stored);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AuthSession> CreateAnonymousSessionAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await ReadStoredSessionAsync(cancellationToken).ConfigureAwait(false) is not null)
            {
                throw new InvalidOperationException(
                    I18n.Get("auth.cannotReplaceStoredSession"));
            }

            StoredSupabaseSession created = await RequestSessionAsync(
                HttpMethod.Post,
                "/auth/v1/signup",
                new { data = new { } },
                cancellationToken).ConfigureAwait(false);
            await StoreAsync(created, cancellationToken).ConfigureAwait(false);
            return DomainSession(created);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            StoredSupabaseSession? stored = await ReadStoredSessionAsync(cancellationToken).ConfigureAwait(false);
            if (stored is not null)
            {
                try
                {
                    using HttpRequestMessage request = CreateRequest(
                        HttpMethod.Post,
                        "/auth/v1/logout?scope=local",
                        stored.AccessToken);
                    using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken)
                        .ConfigureAwait(false);
                    _ = response.IsSuccessStatusCode;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // Local sign-out must still complete when the network is unavailable.
                }
            }

            try
            {
                await _credentials.DeleteAsync(
                    CredentialKey.FirebaseRealtimeSession,
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await _credentials.DeleteAsync(CredentialKey.SupabaseSession, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> HasGoogleIdentityAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            StoredSupabaseSession? session = await RestoreStoredSessionWithinGateAsync(cancellationToken).ConfigureAwait(false);
            return session is not null && await VerifyGoogleIdentityAsync(session, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task<bool> VerifyGoogleIdentityAsync(StoredSupabaseSession session, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = CreateRequest(HttpMethod.Get, "/auth/v1/user", session.AccessToken);
        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        AuthUser user = await response.Content.ReadFromJsonAsync<AuthUser>(s_serializerOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException(I18n.Get("auth.emptyResponse"));
        if (user.Id != session.UserId)
            throw new InvalidOperationException(I18n.Get("auth.identityChanged"));
        return user.Identities?.Any(identity => StringComparer.Ordinal.Equals(identity.Provider, "google")) == true;
    }

    public Task<Uri> BeginGoogleSignInAsync(Uri redirectUri, CancellationToken cancellationToken = default) =>
        BeginGoogleSignInCoreAsync(redirectUri, preserveStoredUser: false, cancellationToken);

    public Task<Uri> BeginGoogleReauthenticationAsync(Uri redirectUri, CancellationToken cancellationToken = default) =>
        BeginGoogleSignInCoreAsync(redirectUri, preserveStoredUser: true, cancellationToken);

    private async Task<Uri> BeginGoogleSignInCoreAsync(Uri redirectUri, bool preserveStoredUser, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(redirectUri);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Existing installations must link, never replace their user and room ownership.
            StoredSupabaseSession? stored = await ReadStoredSessionAsync(cancellationToken).ConfigureAwait(false);
            if (!preserveStoredUser && stored is not null)
                throw new InvalidOperationException(I18n.Get("auth.cannotReplaceStoredSession"));
            if (preserveStoredUser && stored is null)
                throw new InvalidOperationException(I18n.Get("auth.sessionMissing"));
            string verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
            string challenge = Base64Url(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(verifier)));
            _pendingIdentityLink = new PendingIdentityLink(stored?.UserId, verifier, DateTimeOffset.UtcNow.AddMinutes(10));
            return new Uri(_configuration.Url, "/auth/v1/authorize?provider=google"
                + "&prompt=select_account"
                + $"&redirect_to={Uri.EscapeDataString(redirectUri.AbsoluteUri)}"
                + $"&code_challenge={Uri.EscapeDataString(challenge)}&code_challenge_method=s256");
        }
        finally { _gate.Release(); }
    }

    public async Task CancelGoogleAuthenticationAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        { _pendingIdentityLink = null; }
        finally { _gate.Release(); }
    }

    public async Task<Uri> BeginGoogleIdentityLinkAsync(
        Uri redirectUri,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(redirectUri);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            StoredSupabaseSession session =
                await RestoreStoredSessionWithinGateAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException(I18n.Get("auth.sessionMissing"));
            string verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
            string challenge = Base64Url(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(verifier)));
            string query = string.Join('&',
                "provider=google",
                "prompt=select_account",
                $"redirect_to={Uri.EscapeDataString(redirectUri.AbsoluteUri)}",
                $"code_challenge={Uri.EscapeDataString(challenge)}",
                "code_challenge_method=s256",
                "skip_http_redirect=true");
            using HttpRequestMessage request = CreateRequest(
                HttpMethod.Get,
                $"/auth/v1/user/identities/authorize?{query}",
                session.AccessToken);
            using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            IdentityLinkEnvelope envelope =
                await response.Content.ReadFromJsonAsync<IdentityLinkEnvelope>(
                    s_serializerOptions,
                    cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException(I18n.Get("auth.emptyResponse"));
            if (!Uri.TryCreate(envelope.Url, UriKind.Absolute, out Uri? authorizationUri)
                || authorizationUri.Scheme != Uri.UriSchemeHttps)
            {
                throw new InvalidDataException(I18n.Get("auth.emptyResponse"));
            }

            _pendingIdentityLink = new PendingIdentityLink(
                session.UserId,
                verifier,
                DateTimeOffset.UtcNow.AddMinutes(10));
            return authorizationUri;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CompleteGoogleIdentityLinkAsync(
        string authCode,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(authCode))
        {
            throw new ArgumentException("OAuth code is required.", nameof(authCode));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            PendingIdentityLink pending = _pendingIdentityLink
                ?? throw new InvalidOperationException(I18n.Get("auth.identityLinkExpired"));
            if (pending.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                throw new InvalidOperationException(I18n.Get("auth.identityLinkExpired"));
            }

            StoredSupabaseSession linked = await RequestSessionAsync(
                HttpMethod.Post,
                "/auth/v1/token?grant_type=pkce",
                new { auth_code = authCode, code_verifier = pending.CodeVerifier },
                cancellationToken).ConfigureAwait(false);
            if (pending.UserId is { } expectedUserId && linked.UserId != expectedUserId)
            {
                throw new InvalidOperationException(I18n.Get("auth.identityChanged"));
            }

            if (!await VerifyGoogleIdentityAsync(linked, cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException(I18n.Get("auth.googleRequired"));
            cancellationToken.ThrowIfCancellationRequested();
            await StoreAsync(linked, cancellationToken).ConfigureAwait(false);
            _pendingIdentityLink = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes)
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');

    async ValueTask<StoredSupabaseSession?> IAuthSessionAccessor.GetStoredSessionAsync(
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RestoreStoredSessionWithinGateAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _gate.Dispose();
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private async Task<StoredSupabaseSession> RequestSessionAsync(
        HttpMethod method,
        string relativePath,
        object body,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = CreateRequest(method, relativePath);
        request.Content = JsonContent.Create(body, options: s_serializerOptions);
        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                I18n.Format("auth.requestFailed", (int)response.StatusCode),
                inner: null,
                response.StatusCode);
        }

        AuthEnvelope envelope = await response.Content.ReadFromJsonAsync<AuthEnvelope>(
            s_serializerOptions,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException(I18n.Get("auth.emptyResponse"));
        if (string.IsNullOrWhiteSpace(envelope.AccessToken)
            || string.IsNullOrWhiteSpace(envelope.RefreshToken)
            || envelope.User?.Id is not { } userId)
        {
            throw new InvalidDataException(I18n.Get("auth.missingSessionValues"));
        }

        return new StoredSupabaseSession(
            envelope.AccessToken,
            envelope.RefreshToken,
            userId,
            DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, envelope.ExpiresIn)));
    }

    private HttpRequestMessage CreateRequest(
        HttpMethod method,
        string relativePath,
        string? accessToken = null)
    {
        var request = new HttpRequestMessage(method, new Uri(_configuration.Url, relativePath));
        request.Headers.Add("apikey", _configuration.PublishableKey);
        if (!string.IsNullOrEmpty(accessToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        return request;
    }

    private async ValueTask<StoredSupabaseSession?> ReadStoredSessionAsync(
        CancellationToken cancellationToken)
    {
        string? value = await _credentials.ReadAsync(CredentialKey.SupabaseSession, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<StoredSupabaseSession>(value, s_serializerOptions)
                ?? throw new JsonException("Session payload was null.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(I18n.Get("auth.invalidStoredSession"), exception);
        }
    }

    private async Task<StoredSupabaseSession?> RestoreStoredSessionWithinGateAsync(
        CancellationToken cancellationToken)
    {
        StoredSupabaseSession? stored = await ReadStoredSessionAsync(cancellationToken).ConfigureAwait(false);
        if (stored is null || stored.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1))
        {
            return stored;
        }

        StoredSupabaseSession refreshed = await RequestSessionAsync(
            HttpMethod.Post,
            "/auth/v1/token?grant_type=refresh_token",
            new { refresh_token = stored.RefreshToken },
            cancellationToken).ConfigureAwait(false);
        if (refreshed.UserId != stored.UserId)
            throw new InvalidOperationException(I18n.Get("auth.identityChanged"));
        await StoreAsync(refreshed, cancellationToken).ConfigureAwait(false);
        return refreshed;
    }

    private async ValueTask StoreAsync(
        StoredSupabaseSession session,
        CancellationToken cancellationToken)
    {
        string value = JsonSerializer.Serialize(session, s_serializerOptions);
        await _credentials.WriteAsync(CredentialKey.SupabaseSession, value, cancellationToken)
            .ConfigureAwait(false);
    }

    private static AuthSession DomainSession(StoredSupabaseSession session) =>
        new(session.UserId, session.ExpiresAt);

    private sealed record AuthEnvelope(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn,
        [property: JsonPropertyName("user")] AuthUser? User);

    private sealed record AuthUser(
        [property: JsonPropertyName("id")] Guid Id,
        [property: JsonPropertyName("identities")] AuthIdentity[]? Identities);
    private sealed record AuthIdentity([property: JsonPropertyName("provider")] string? Provider);

    private sealed record IdentityLinkEnvelope(string? Url);
    private sealed record PendingIdentityLink(
        Guid? UserId,
        string CodeVerifier,
        DateTimeOffset ExpiresAt);
}
