using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sidey.Core.Abstractions;
using Sidey.Infrastructure.Realtime;

namespace Sidey.Infrastructure.Authentication;

internal interface IFirebaseRealtimeCredentialProvider
{
    public ValueTask<FirebaseRealtimeCredential> GetCredentialAsync(
        CancellationToken cancellationToken = default);

    public ValueTask<FirebaseRealtimeCredential> ConvergeAsync(
        string minimumAccessRevision,
        CancellationToken cancellationToken = default) =>
        GetCredentialAsync(cancellationToken);

    public ValueTask ResetAsync(CancellationToken cancellationToken = default);
}

internal sealed class FirebaseRealtimeCredential
{
    public FirebaseRealtimeCredential(
        Guid userId,
        Guid sessionId,
        string idToken,
        Uri databaseUrl,
        long generation,
        CancellationToken lifetimeToken = default)
    {
        UserId = userId;
        SessionId = sessionId;
        IdToken = idToken;
        DatabaseUrl = databaseUrl;
        Generation = generation;
        LifetimeToken = lifetimeToken;
    }

    public Guid UserId { get; }
    public Guid SessionId { get; }
    public string IdToken { get; }
    public Uri DatabaseUrl { get; }
    public long Generation { get; }
    public CancellationToken LifetimeToken { get; }

    public override string ToString() =>
        $"FirebaseRealtimeCredential(UserId={UserId:D}, SessionId={SessionId:D}, Generation={Generation})";
}

internal sealed class FirebaseRealtimeCredentialProvider : IFirebaseRealtimeCredentialProvider
{
    private const int ProtocolVersion = 2;
    private const int MaximumTokenCharacters = 32 * 1024;
    private const int MaximumApiKeyCharacters = 200;
    private const int MaximumResponseBytes = 256 * 1024;
    private const int PersistedVersion = 1;
    private const string FirebaseProjectId = "sidey-realtime";
    private static readonly Uri s_bootstrapEndpoint =
        new("https://asia-southeast1-sidey-realtime.cloudfunctions.net/bootstrapRealtime");
    private static readonly Uri s_databaseUrl =
        new("https://sidey.asia-southeast1.firebasedatabase.app");
    private static readonly Uri s_identityToolkitEndpoint =
        new("https://identitytoolkit.googleapis.com/v1/accounts:signInWithCustomToken");
    private static readonly Uri s_secureTokenEndpoint =
        new("https://securetoken.googleapis.com/v1/token");
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IAuthSessionAccessor _sessions;
    private readonly ICredentialStore _credentials;
    private readonly HttpClient _httpClient;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _lifecycleGate = new();
    private readonly Dictionary<SessionKey, TokenState> _states = [];
    private readonly Dictionary<SessionKey, Task<FirebaseRealtimeCredential>> _inflight = [];
    private readonly Dictionary<SessionKey, long> _inflightGenerations = [];
    private readonly Dictionary<SessionKey, string> _minimumAccessRevisions = [];
    private readonly Dictionary<string, PersistedSession> _persistedSessions = new(StringComparer.Ordinal);
    private CancellationTokenSource _generationCancellation = new();
    private bool _persistedSessionsLoaded;
    private bool _resetInProgress;
    private long _generation = 1;

    public FirebaseRealtimeCredentialProvider(
        IAuthSessionAccessor sessions,
        ICredentialStore credentials,
        HttpClient httpClient,
        TimeProvider? timeProvider = null)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public ValueTask<FirebaseRealtimeCredential> GetCredentialAsync(
        CancellationToken cancellationToken = default)
    {
        (long requestGeneration, CancellationToken generationToken) = CaptureGeneration();
        return GetCredentialAsync(requestGeneration, generationToken, cancellationToken);
    }

    private async ValueTask<FirebaseRealtimeCredential> GetCredentialAsync(
        long requestGeneration,
        CancellationToken generationToken,
        CancellationToken cancellationToken)
    {
        StoredSupabaseSession supabaseSession =
            await _sessions.GetStoredSessionAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("An authenticated Supabase session is required.");
        SessionKey key = ParseSupabaseSessionKey(supabaseSession);
        Task<FirebaseRealtimeCredential> operation;
        PersistedSession? persistedSession = null;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureCurrentGeneration(requestGeneration, generationToken);
            bool requiresRebootstrap = false;
            _minimumAccessRevisions.TryGetValue(key, out string? minimumAccessRevision);
            if (_states.TryGetValue(key, out TokenState? state)
                && state.Generation == requestGeneration)
            {
                TimeSpan tokenAge = _timeProvider.GetElapsedTime(state.IssuedTimestamp);
                TimeSpan bootstrapAge = _timeProvider.GetElapsedTime(
                    state.Bootstrap.ReceivedTimestamp);
                requiresRebootstrap = bootstrapAge >= state.Bootstrap.RefreshAfter;
                requiresRebootstrap |= minimumAccessRevision is not null
                    && StringComparer.Ordinal.Compare(
                        state.Bootstrap.AccessRevision,
                        minimumAccessRevision) < 0;
                if (!requiresRebootstrap && tokenAge < state.RefreshAfter)
                {
                    return state.CreateCredential();
                }
            }

            if (state is null)
            {
                await EnsurePersistedSessionsLoadedWithinGateAsync().ConfigureAwait(false);
                _persistedSessions.TryGetValue(StorageKey(key), out persistedSession);
                if (persistedSession is not null && !persistedSession.IsValidFor(
                    FirebaseProjectId,
                    s_bootstrapEndpoint,
                    s_databaseUrl,
                    persistedSession.FirebaseApiKey ?? string.Empty,
                    key.UserId,
                    key.SessionId))
                {
                    _persistedSessions.Remove(StorageKey(key));
                    persistedSession = null;
                }
            }

            if (!_inflight.TryGetValue(key, out operation!))
            {
                operation = AcquireCredentialAsync(
                    key,
                    supabaseSession,
                    state,
                    persistedSession,
                    requiresRebootstrap,
                    minimumAccessRevision,
                    requestGeneration,
                    generationToken);
                _inflight.Add(key, operation);
                _inflightGenerations.Add(key, requestGeneration);
            }
        }
        finally
        {
            _gate.Release();
        }

        return await operation.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<FirebaseRealtimeCredential> ConvergeAsync(
        string minimumAccessRevision,
        CancellationToken cancellationToken = default)
    {
        _ = FirebaseRealtimeProtocol.CreateBootstrapRequestBody(minimumAccessRevision);
        (long requestGeneration, CancellationToken generationToken) = CaptureGeneration();
        StoredSupabaseSession session =
            await _sessions.GetStoredSessionAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("An authenticated Supabase session is required.");
        EnsureCurrentGeneration(requestGeneration, generationToken);
        SessionKey key = ParseSupabaseSessionKey(session);
        Task<FirebaseRealtimeCredential>? existingOperation = null;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureCurrentGeneration(requestGeneration, generationToken);
            if (_states.TryGetValue(key, out TokenState? state)
                && StringComparer.Ordinal.Compare(
                    state.Bootstrap.AccessRevision,
                    minimumAccessRevision) >= 0)
            {
                return state.CreateCredential();
            }

            if (_minimumAccessRevisions.TryGetValue(key, out string? existingMinimum)
                && StringComparer.Ordinal.Compare(existingMinimum, minimumAccessRevision) > 0)
            {
                minimumAccessRevision = existingMinimum;
            }
            _minimumAccessRevisions[key] = minimumAccessRevision;
            _inflight.TryGetValue(key, out existingOperation);
        }
        finally
        {
            _gate.Release();
        }

        if (existingOperation is not null)
        {
            _ = await existingOperation.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        EnsureCurrentGeneration(requestGeneration, generationToken);
        return await GetCredentialAsync(
            requestGeneration,
            generationToken,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask ResetAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CancellationTokenSource previousCancellation;
        lock (_lifecycleGate)
        {
            if (_resetInProgress)
            {
                throw new OperationCanceledException("Firebase realtime session is being reset.");
            }
            checked
            {
                _generation++;
            }
            _resetInProgress = true;
            previousCancellation = _generationCancellation;
            _generationCancellation = new CancellationTokenSource();
        }
        previousCancellation.Cancel();

        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            _states.Clear();
            _inflight.Clear();
            _inflightGenerations.Clear();
            _minimumAccessRevisions.Clear();
            _persistedSessions.Clear();
            _persistedSessionsLoaded = true;
            await _credentials.DeleteAsync(
                CredentialKey.FirebaseRealtimeSession,
                CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            CancellationTokenSource duringResetCancellation;
            lock (_lifecycleGate)
            {
                checked
                {
                    _generation++;
                }
                duringResetCancellation = _generationCancellation;
                _generationCancellation = new CancellationTokenSource();
                _resetInProgress = false;
            }
            duringResetCancellation.Cancel();
            _gate.Release();
            previousCancellation.Dispose();
            duringResetCancellation.Dispose();
        }
    }

    private async Task<FirebaseRealtimeCredential> AcquireCredentialAsync(
        SessionKey key,
        StoredSupabaseSession supabaseSession,
        TokenState? previousState,
        PersistedSession? persistedSession,
        bool requiresRebootstrap,
        string? minimumAccessRevision,
        long generation,
        CancellationToken generationToken)
    {
        try
        {
            if (previousState is null || requiresRebootstrap)
            {
                BootstrapGrant validated = await BootstrapAsync(
                    supabaseSession,
                    key,
                    minimumAccessRevision,
                    generationToken).ConfigureAwait(false);
                FirebaseTokenGrant grant;
                if (previousState is not null)
                {
                    grant = await ExchangeCustomTokenAsync(
                        validated.Bootstrap.FirebaseApiKey,
                        validated.CustomToken,
                        key,
                        validated.Bootstrap.RolloutLeaseExpiresAt,
                        generationToken).ConfigureAwait(false);
                }
                else if (persistedSession is not null && persistedSession.IsValidFor(
                    FirebaseProjectId,
                    s_bootstrapEndpoint,
                    validated.Bootstrap.DatabaseUrl,
                    validated.Bootstrap.FirebaseApiKey,
                    key.UserId,
                    key.SessionId))
                {
                    try
                    {
                        grant = await RefreshTokenAsync(
                            validated.Bootstrap.FirebaseApiKey,
                            persistedSession.RefreshToken!,
                            key,
                            validated.Bootstrap.RolloutLeaseExpiresAt,
                            generationToken).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (
                        exception is InvalidDataException
                        || exception is HttpRequestException httpException
                            && IsRejectedRefresh(httpException.StatusCode))
                    {
                        await DiscardPersistedSessionAsync(
                            key,
                            generation,
                            generationToken).ConfigureAwait(false);
                        grant = await ExchangeCustomTokenAsync(
                            validated.Bootstrap.FirebaseApiKey,
                            validated.CustomToken,
                            key,
                            validated.Bootstrap.RolloutLeaseExpiresAt,
                            generationToken).ConfigureAwait(false);
                    }
                }
                else
                {
                    grant = await ExchangeCustomTokenAsync(
                        validated.Bootstrap.FirebaseApiKey,
                        validated.CustomToken,
                        key,
                        validated.Bootstrap.RolloutLeaseExpiresAt,
                        generationToken).ConfigureAwait(false);
                }
                return await CommitGrantAsync(
                    key,
                    generation,
                    validated.Bootstrap,
                    grant,
                    generationToken).ConfigureAwait(false);
            }

            FirebaseTokenGrant refreshed = await RefreshTokenAsync(
                previousState.FirebaseApiKey,
                previousState.RefreshToken,
                key,
                previousState.Bootstrap.RolloutLeaseExpiresAt,
                generationToken).ConfigureAwait(false);
            return await CommitGrantAsync(
                key,
                generation,
                previousState.Bootstrap,
                refreshed,
                generationToken).ConfigureAwait(false);
        }
        catch
        {
            await RemoveFailedOperationAsync(key, generation).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<BootstrapGrant> BootstrapAsync(
        StoredSupabaseSession session,
        SessionKey key,
        string? minimumAccessRevision,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, s_bootstrapEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        request.Content = new ByteArrayContent(
            FirebaseRealtimeProtocol.CreateBootstrapRequestBody(minimumAccessRevision));
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        byte[] responseBody = await SendAndReadBytesAsync(
            request,
            "Realtime bootstrap request failed.",
            cancellationToken).ConfigureAwait(false);
        long receivedTimestamp = _timeProvider.GetTimestamp();
        long receivedAtUnixMilliseconds = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        FirebaseRealtimeBootstrapConfiguration response =
            FirebaseRealtimeProtocol.ParseBootstrapResponse(
                responseBody,
                receivedAtUnixMilliseconds,
                minimumAccessRevision);
        ValidateCustomToken(response.CustomToken, key, response.RolloutLeaseExpiresAt);
        long remainingLeaseMilliseconds = checked(
            response.RolloutLeaseExpiresAt - receivedAtUnixMilliseconds);
        long refreshDelayMilliseconds = response.RefreshAfter > receivedAtUnixMilliseconds
            ? response.RefreshAfter - receivedAtUnixMilliseconds
            : Math.Max(1, remainingLeaseMilliseconds / 2);
        return new BootstrapGrant(
            new ValidatedBootstrap(
                response.DatabaseUrl,
                response.FirebaseApiKey,
                response.AccessRevision,
                response.Rooms,
                response.WireItems,
                response.RolloutLeaseExpiresAt,
                receivedTimestamp,
                TimeSpan.FromMilliseconds(refreshDelayMilliseconds)),
            response.CustomToken);
    }

    private async Task<FirebaseTokenGrant> ExchangeCustomTokenAsync(
        string apiKey,
        string customToken,
        SessionKey key,
        long rolloutLeaseExpiresAt,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, WithApiKey(s_identityToolkitEndpoint, apiKey))
        {
            Content = JsonContent.Create(
                new { token = customToken, returnSecureToken = true },
                options: s_jsonOptions),
        };
        CustomTokenExchangeResponse payload =
            await SendAndReadJsonAsync<CustomTokenExchangeResponse>(
                request,
                "Firebase custom-token exchange request failed.",
                cancellationToken).ConfigureAwait(false);
        return ValidateTokenGrant(
            payload.IdToken,
            payload.RefreshToken,
            payload.ExpiresIn,
            payload.LocalId,
            key,
            rolloutLeaseExpiresAt);
    }

    private async Task<FirebaseTokenGrant> RefreshTokenAsync(
        string apiKey,
        string refreshToken,
        SessionKey key,
        long rolloutLeaseExpiresAt,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, WithApiKey(s_secureTokenEndpoint, apiKey))
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
            }),
        };
        SecureTokenResponse payload = await SendAndReadJsonAsync<SecureTokenResponse>(
            request,
            "Firebase token refresh request failed.",
            cancellationToken).ConfigureAwait(false);
        return ValidateTokenGrant(
            payload.IdToken,
            payload.RefreshToken,
            payload.ExpiresIn,
            payload.UserId,
            key,
            rolloutLeaseExpiresAt);
    }

    private async Task<byte[]> SendAndReadBytesAsync(
        HttpRequestMessage request,
        string failureMessage,
        CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(failureMessage, inner: null, response.StatusCode);
            }
            return await ReadBoundedAsync(response.Content, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException exception)
        {
            throw new HttpRequestException(failureMessage, inner: null, exception.StatusCode);
        }
        catch (Exception)
        {
            throw new HttpRequestException(failureMessage);
        }
    }

    private async Task<TResponse> SendAndReadJsonAsync<TResponse>(
        HttpRequestMessage request,
        string failureMessage,
        CancellationToken cancellationToken)
        where TResponse : class
    {
        try
        {
            using HttpResponseMessage response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(failureMessage, inner: null, response.StatusCode);
            }
            byte[] payload = await ReadBoundedAsync(
                response.Content,
                cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<TResponse>(payload, s_jsonOptions)
                ?? throw new InvalidDataException(failureMessage);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException exception)
        {
            throw new HttpRequestException(failureMessage, inner: null, exception.StatusCode);
        }
        catch (Exception)
        {
            throw new HttpRequestException(failureMessage);
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > MaximumResponseBytes)
        {
            throw new InvalidDataException("Firebase response exceeds the maximum size.");
        }
        await using Stream stream = await content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var output = new MemoryStream();
        byte[] buffer = new byte[4096];
        while (true)
        {
            int read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return output.ToArray();
            }
            if (output.Length + read > MaximumResponseBytes)
            {
                throw new InvalidDataException("Firebase response exceeds the maximum size.");
            }
            output.Write(buffer, 0, read);
        }
    }

    private async Task<FirebaseRealtimeCredential> CommitGrantAsync(
        SessionKey key,
        long generation,
        ValidatedBootstrap bootstrap,
        FirebaseTokenGrant grant,
        CancellationToken generationToken)
    {
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            EnsureCurrentGeneration(generation, generationToken);
            EnsureBootstrapCurrent(bootstrap);
            await EnsurePersistedSessionsLoadedWithinGateAsync().ConfigureAwait(false);
            string storageKey = StorageKey(key);
            _persistedSessions.TryGetValue(storageKey, out PersistedSession? previousPersistedSession);
            _persistedSessions[storageKey] = new PersistedSession
            {
                ProjectId = FirebaseProjectId,
                BootstrapEndpoint = s_bootstrapEndpoint.AbsoluteUri,
                UserId = key.UserId,
                SessionId = key.SessionId,
                DatabaseUrl = bootstrap.DatabaseUrl.AbsoluteUri,
                FirebaseApiKey = bootstrap.FirebaseApiKey,
                RefreshToken = grant.RefreshToken,
            };
            try
            {
                await StorePersistedSessionsWithinGateAsync(generationToken).ConfigureAwait(false);
                EnsureCurrentGeneration(generation, generationToken);
                EnsureBootstrapCurrent(bootstrap);
            }
            catch
            {
                if (previousPersistedSession is null)
                {
                    _persistedSessions.Remove(storageKey);
                }
                else
                {
                    _persistedSessions[storageKey] = previousPersistedSession;
                }
                throw;
            }

            var state = new TokenState(
                key.UserId,
                key.SessionId,
                grant.IdToken,
                grant.RefreshToken,
                bootstrap.FirebaseApiKey,
                generation,
                generationToken,
                grant.ReceivedTimestamp,
                RefreshAfter(grant.ExpiresIn),
                bootstrap);
            _states[key] = state;
            if (_minimumAccessRevisions.TryGetValue(key, out string? minimumAccessRevision)
                && StringComparer.Ordinal.Compare(
                    bootstrap.AccessRevision,
                    minimumAccessRevision) >= 0)
            {
                _minimumAccessRevisions.Remove(key);
            }
            RemoveInflightWithinGate(key, generation);
            return state.CreateCredential();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RemoveFailedOperationAsync(SessionKey key, long generation)
    {
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            RemoveInflightWithinGate(key, generation);
            if (generation == CurrentGeneration())
            {
                _states.Remove(key);
                _persistedSessions.Remove(StorageKey(key));
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task DiscardPersistedSessionAsync(
        SessionKey key,
        long generation,
        CancellationToken generationToken)
    {
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            EnsureCurrentGeneration(generation, generationToken);
            if (_persistedSessions.Remove(StorageKey(key)))
            {
                await StorePersistedSessionsWithinGateAsync(generationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsurePersistedSessionsLoadedWithinGateAsync()
    {
        if (_persistedSessionsLoaded)
        {
            return;
        }

        string? json = await _credentials.ReadAsync(
            CredentialKey.FirebaseRealtimeSession,
            CancellationToken.None).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                PersistedEnvelope? envelope = JsonSerializer.Deserialize<PersistedEnvelope>(json, s_jsonOptions);
                if (envelope?.Version != PersistedVersion || envelope.Sessions is null)
                {
                    throw new JsonException("Unsupported persisted Firebase session payload.");
                }
                foreach ((string storageKey, PersistedSession session) in envelope.Sessions)
                {
                    if (string.IsNullOrWhiteSpace(storageKey) || !session.IsValid())
                    {
                        throw new JsonException("Invalid persisted Firebase session payload.");
                    }
                    _persistedSessions[storageKey] = session;
                }
            }
            catch (JsonException)
            {
                _persistedSessions.Clear();
            }
        }
        _persistedSessionsLoaded = true;
    }

    private ValueTask StorePersistedSessionsWithinGateAsync(CancellationToken cancellationToken)
    {
        string json = JsonSerializer.Serialize(new PersistedEnvelope
        {
            Version = PersistedVersion,
            Sessions = new Dictionary<string, PersistedSession>(
                _persistedSessions,
                StringComparer.Ordinal),
        }, s_jsonOptions);
        return _credentials.WriteAsync(
            CredentialKey.FirebaseRealtimeSession,
            json,
            cancellationToken);
    }

    private FirebaseTokenGrant ValidateTokenGrant(
        string? idToken,
        string? refreshToken,
        string? expiresIn,
        string? userId,
        SessionKey key,
        long rolloutLeaseExpiresAt)
    {
        if (string.IsNullOrWhiteSpace(idToken)
            || idToken.Length > MaximumTokenCharacters
            || string.IsNullOrWhiteSpace(refreshToken)
            || refreshToken.Length > MaximumTokenCharacters
            || !Guid.TryParse(userId, out Guid parsedUserId)
            || parsedUserId != key.UserId
            || !int.TryParse(expiresIn, NumberStyles.None, CultureInfo.InvariantCulture, out int seconds)
            || seconds is <= 0 or > 86_400)
        {
            throw new InvalidDataException("Firebase token response was invalid.");
        }
        ValidateFirebaseIdToken(idToken, key, rolloutLeaseExpiresAt);
        return new FirebaseTokenGrant(
            idToken,
            refreshToken,
            TimeSpan.FromSeconds(seconds),
            _timeProvider.GetTimestamp());
    }

    private static SessionKey ParseSupabaseSessionKey(StoredSupabaseSession session)
    {
        using JsonDocument payload = ParseJwtPayload(session.AccessToken, "Supabase access token");
        JsonElement root = payload.RootElement;
        if (!root.TryGetProperty("sub", out JsonElement subject)
            || !Guid.TryParse(subject.GetString(), out Guid subjectId)
            || subjectId != session.UserId
            || !root.TryGetProperty("session_id", out JsonElement sessionClaim)
            || !Guid.TryParse(sessionClaim.GetString(), out Guid sessionId)
            || sessionId == Guid.Empty)
        {
            throw new InvalidDataException("Supabase session claims were invalid.");
        }
        return new SessionKey(session.UserId, sessionId);
    }

    private static void ValidateCustomToken(
        string customToken,
        SessionKey key,
        long rolloutLeaseExpiresAt)
    {
        using JsonDocument payload = ParseJwtPayload(customToken, "Firebase custom token");
        JsonElement root = payload.RootElement;
        if (!root.TryGetProperty("uid", out JsonElement uid)
            || !Guid.TryParse(uid.GetString(), out Guid userId)
            || userId != key.UserId
            || !root.TryGetProperty("claims", out JsonElement claims)
            || !HasFirebaseSessionClaims(claims, key, rolloutLeaseExpiresAt))
        {
            throw new InvalidDataException("Firebase custom token claims were invalid.");
        }
    }

    private static void ValidateFirebaseIdToken(
        string idToken,
        SessionKey key,
        long rolloutLeaseExpiresAt)
    {
        using JsonDocument payload = ParseJwtPayload(idToken, "Firebase ID token");
        JsonElement root = payload.RootElement;
        string? subject = root.TryGetProperty("user_id", out JsonElement userIdClaim)
            ? userIdClaim.GetString()
            : root.TryGetProperty("sub", out JsonElement subjectClaim)
                ? subjectClaim.GetString()
                : null;
        if (!Guid.TryParse(subject, out Guid userId)
            || userId != key.UserId
            || !HasFirebaseSessionClaims(root, key, rolloutLeaseExpiresAt))
        {
            throw new InvalidDataException("Firebase ID token claims were invalid.");
        }
    }

    private static bool HasFirebaseSessionClaims(
        JsonElement claims,
        SessionKey key,
        long rolloutLeaseExpiresAt) =>
        claims.TryGetProperty("sideyProtocol", out JsonElement protocol)
        && protocol.TryGetInt32(out int protocolVersion)
        && protocolVersion == ProtocolVersion
        && claims.TryGetProperty("sideySessionId", out JsonElement session)
        && Guid.TryParse(session.GetString(), out Guid sessionId)
        && sessionId == key.SessionId
        && claims.TryGetProperty("sideyRolloutUntil", out JsonElement rolloutLease)
        && rolloutLease.TryGetInt64(out long tokenRolloutLeaseExpiresAt)
        && tokenRolloutLeaseExpiresAt == rolloutLeaseExpiresAt;

    private static JsonDocument ParseJwtPayload(string token, string tokenKind)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > MaximumTokenCharacters)
        {
            throw new InvalidDataException($"{tokenKind} was invalid.");
        }
        string[] segments = token.Split('.');
        if (segments.Length != 3 || segments[1].Length == 0)
        {
            throw new InvalidDataException($"{tokenKind} was invalid.");
        }
        try
        {
            string payload = segments[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');
            return JsonDocument.Parse(Convert.FromBase64String(payload));
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            throw new InvalidDataException($"{tokenKind} was invalid.", exception);
        }
    }

    private static Uri WithApiKey(Uri endpoint, string apiKey)
    {
        var builder = new UriBuilder(endpoint) { Query = $"key={Uri.EscapeDataString(apiKey)}" };
        return builder.Uri;
    }

    private static TimeSpan RefreshAfter(TimeSpan lifetime) =>
        TimeSpan.FromTicks(checked(lifetime.Ticks * 4 / 5));

    private void EnsureBootstrapCurrent(ValidatedBootstrap bootstrap)
    {
        if (_timeProvider.GetElapsedTime(bootstrap.ReceivedTimestamp) >= bootstrap.RefreshAfter)
        {
            throw new InvalidDataException("Realtime bootstrap lease must be renewed.");
        }
    }

    private static bool IsRejectedRefresh(HttpStatusCode? statusCode) =>
        statusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;

    private (long Generation, CancellationToken Token) CaptureGeneration()
    {
        lock (_lifecycleGate)
        {
            if (_resetInProgress)
            {
                throw new OperationCanceledException("Firebase realtime session is being reset.");
            }
            return (_generation, _generationCancellation.Token);
        }
    }

    private long CurrentGeneration()
    {
        lock (_lifecycleGate)
        {
            return _generation;
        }
    }

    private void EnsureCurrentGeneration(long generation, CancellationToken generationToken)
    {
        generationToken.ThrowIfCancellationRequested();
        if (generation != CurrentGeneration())
        {
            throw new OperationCanceledException("Firebase realtime session was reset.");
        }
    }

    private string StorageKey(SessionKey key) =>
        $"{FirebaseProjectId}:{key.UserId:D}:{key.SessionId:D}";

    private void RemoveInflightWithinGate(SessionKey key, long generation)
    {
        if (_inflightGenerations.TryGetValue(key, out long activeGeneration)
            && activeGeneration == generation)
        {
            _inflight.Remove(key);
            _inflightGenerations.Remove(key);
        }
    }

    private readonly record struct SessionKey(Guid UserId, Guid SessionId);

    private sealed record FirebaseTokenGrant(
        string IdToken,
        string RefreshToken,
        TimeSpan ExpiresIn,
        long ReceivedTimestamp)
    {
        public override string ToString() => nameof(FirebaseTokenGrant);
    }

    private sealed record ValidatedBootstrap(
        Uri DatabaseUrl,
        string FirebaseApiKey,
        string AccessRevision,
        IReadOnlyList<Guid> Rooms,
        IReadOnlyList<string> WireItems,
        long RolloutLeaseExpiresAt,
        long ReceivedTimestamp,
        TimeSpan RefreshAfter)
    {
        public override string ToString() =>
            $"{nameof(ValidatedBootstrap)}(DatabaseUrl={DatabaseUrl})";
    }

    private sealed record BootstrapGrant(
        ValidatedBootstrap Bootstrap,
        string CustomToken)
    {
        public override string ToString() => nameof(BootstrapGrant);
    }

    private sealed record TokenState(
        Guid UserId,
        Guid SessionId,
        string IdToken,
        string RefreshToken,
        string FirebaseApiKey,
        long Generation,
        CancellationToken LifetimeToken,
        long IssuedTimestamp,
        TimeSpan RefreshAfter,
        ValidatedBootstrap Bootstrap)
    {
        public FirebaseRealtimeCredential CreateCredential() => new(
            UserId,
            SessionId,
            IdToken,
            Bootstrap.DatabaseUrl,
            Generation,
            LifetimeToken);

        public override string ToString() =>
            $"{nameof(TokenState)}(UserId={UserId:D}, SessionId={SessionId:D})";
    }

    private sealed class PersistedEnvelope
    {
        public int Version { get; init; }
        public Dictionary<string, PersistedSession>? Sessions { get; init; }
    }

    private sealed class PersistedSession
    {
        public string? ProjectId { get; init; }
        public string? BootstrapEndpoint { get; init; }
        public Guid UserId { get; init; }
        public Guid SessionId { get; init; }
        public string? DatabaseUrl { get; init; }
        public string? FirebaseApiKey { get; init; }
        public string? RefreshToken { get; init; }

        public bool IsValid() =>
            !string.IsNullOrWhiteSpace(ProjectId)
            && Uri.TryCreate(BootstrapEndpoint, UriKind.Absolute, out _)
            && UserId != Guid.Empty
            && SessionId != Guid.Empty
            && Uri.TryCreate(DatabaseUrl, UriKind.Absolute, out _)
            && !string.IsNullOrWhiteSpace(FirebaseApiKey)
            && !string.IsNullOrWhiteSpace(RefreshToken);

        public bool IsValidFor(
            string projectId,
            Uri bootstrapEndpoint,
            Uri databaseUrl,
            string firebaseApiKey,
            Guid userId,
            Guid sessionId) =>
            IsValid()
            && StringComparer.Ordinal.Equals(ProjectId, projectId)
            && StringComparer.Ordinal.Equals(BootstrapEndpoint, bootstrapEndpoint.AbsoluteUri)
            && StringComparer.Ordinal.Equals(DatabaseUrl, databaseUrl.AbsoluteUri)
            && StringComparer.Ordinal.Equals(FirebaseApiKey, firebaseApiKey)
            && UserId == userId
            && SessionId == sessionId
            && FirebaseApiKey!.Length <= MaximumApiKeyCharacters
            && RefreshToken!.Length <= MaximumTokenCharacters;

        public override string ToString() =>
            $"PersistedFirebaseSession(ProjectId={ProjectId}, UserId={UserId:D}, SessionId={SessionId:D})";
    }

    private sealed record CustomTokenExchangeResponse(
        [property: JsonPropertyName("idToken")] string? IdToken,
        [property: JsonPropertyName("refreshToken")] string? RefreshToken,
        [property: JsonPropertyName("expiresIn")] string? ExpiresIn,
        [property: JsonPropertyName("localId")] string? LocalId)
    {
        public override string ToString() => nameof(CustomTokenExchangeResponse);
    }

    private sealed record SecureTokenResponse(
        [property: JsonPropertyName("id_token")] string? IdToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("expires_in")] string? ExpiresIn,
        [property: JsonPropertyName("user_id")] string? UserId)
    {
        public override string ToString() => nameof(SecureTokenResponse);
    }
}
