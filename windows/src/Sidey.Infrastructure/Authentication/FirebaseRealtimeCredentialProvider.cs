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

    public ValueTask<FirebaseRealtimeCredential> GetUsableCredentialAsync(
        CancellationToken cancellationToken = default) => GetCredentialAsync(cancellationToken);

    public ValueTask<FirebaseRealtimeCredential> ConvergeAsync(
        string minimumAccessRevision,
        CancellationToken cancellationToken = default) =>
        GetCredentialAsync(cancellationToken);

    public ValueTask RecoverRejectedCredentialAsync(
        FirebaseRealtimeCredential rejected,
        CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    public event Action<FirebaseRealtimeCredential>? CredentialRejected
    {
        add { }
        remove { }
    }

    public ValueTask ResetAsync(CancellationToken cancellationToken = default);
}

internal sealed class FirebaseRealtimeCredentialStageException(
    string stage,
    InvalidDataException innerException) : Exception(
        "Firebase realtime credential payload is invalid.",
        innerException)
{
    public string Stage { get; } = stage;
}

internal class FirebaseRealtimeHttpException(
    string message,
    HttpStatusCode? statusCode,
    string failureCode = "transport-error") : HttpRequestException(message, inner: null, statusCode)
{
    public string FailureCode { get; } = failureCode;
}

internal sealed class FirebaseRealtimeRetryException(
    string message,
    HttpStatusCode? statusCode,
    TimeSpan retryAfter,
    string failureCode = "transport-error") : FirebaseRealtimeHttpException(message, statusCode, failureCode)
{
    public TimeSpan RetryAfter { get; } = retryAfter;
    public TimeSpan? ServerRetryAfter { get; init; }
}

internal sealed class FirebaseRealtimeCredential
{
    public FirebaseRealtimeCredential(
        Guid userId,
        Guid sessionId,
        string idToken,
        Uri databaseUrl,
        long generation,
        CancellationToken lifetimeToken = default,
        IReadOnlyList<string>? wireItems = null,
        TimeSpan? refreshAfter = null,
        TimeSpan? expiresAfter = null,
        long authVersion = 0)
    {
        UserId = userId;
        SessionId = sessionId;
        IdToken = idToken;
        DatabaseUrl = databaseUrl;
        Generation = generation;
        AuthVersion = authVersion;
        LifetimeToken = lifetimeToken;
        WireItems = wireItems ?? [];
        RefreshAfter = refreshAfter ?? TimeSpan.FromSeconds(275);
        ExpiresAfter = expiresAfter ?? TimeSpan.FromSeconds(300);
    }

    public Guid UserId { get; }
    public Guid SessionId { get; }
    public string IdToken { get; }
    public Uri DatabaseUrl { get; }
    public long Generation { get; }
    public long AuthVersion { get; }
    public CancellationToken LifetimeToken { get; }
    public IReadOnlyList<string> WireItems { get; }
    public TimeSpan RefreshAfter { get; }
    public TimeSpan ExpiresAfter { get; }

    public override string ToString() =>
        $"FirebaseRealtimeCredential(UserId={UserId:D}, SessionId={SessionId:D}, Generation={Generation})";
}

internal sealed class FirebaseRealtimeCredentialProvider : IFirebaseRealtimeCredentialProvider
{
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
    private readonly Func<double> _retryJitter;
    private readonly TimeSpan _acquisitionTimeout;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _lifecycleGate = new();
    private readonly Dictionary<SessionKey, TokenState> _states = [];
    private readonly Dictionary<SessionKey, RetryCooldown> _retryCooldowns = [];
    private readonly Dictionary<SessionKey, RetryState> _retryStates = [];
    private readonly Dictionary<SessionKey, long> _rejectedAuthVersions = [];
    private readonly Dictionary<SessionKey, Task<FirebaseRealtimeCredential>> _inflight = [];
    private readonly Dictionary<SessionKey, long> _inflightGenerations = [];
    private readonly Dictionary<SessionKey, string> _minimumAccessRevisions = [];
    private readonly Dictionary<string, PersistedSession> _persistedSessions = new(StringComparer.Ordinal);
    private CancellationTokenSource _generationCancellation = new();
    private bool _persistedSessionsLoaded;
    private bool _resetInProgress;
    private long _generation = 1;
    private long _authVersion;

    public event Action<FirebaseRealtimeCredential>? CredentialRejected;

    public FirebaseRealtimeCredentialProvider(
        IAuthSessionAccessor sessions,
        ICredentialStore credentials,
        HttpClient httpClient,
        TimeProvider? timeProvider = null,
        Func<double>? retryJitter = null,
        TimeSpan? acquisitionTimeout = null)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _retryJitter = retryJitter ?? (() => Random.Shared.NextDouble());
        _acquisitionTimeout = acquisitionTimeout ?? TimeSpan.FromSeconds(20);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_acquisitionTimeout, TimeSpan.Zero);
    }

    public ValueTask<FirebaseRealtimeCredential> GetCredentialAsync(
        CancellationToken cancellationToken = default)
    {
        (long requestGeneration, CancellationToken generationToken) = CaptureGeneration();
        return GetCredentialAsync(requestGeneration, generationToken, cancellationToken);
    }

    public ValueTask<FirebaseRealtimeCredential> GetUsableCredentialAsync(
        CancellationToken cancellationToken = default)
    {
        (long requestGeneration, CancellationToken generationToken) = CaptureGeneration();
        return GetCredentialAsync(requestGeneration, generationToken, cancellationToken, allowUsableCached: true);
    }

    private async ValueTask<FirebaseRealtimeCredential> GetCredentialAsync(
        long requestGeneration,
        CancellationToken generationToken,
        CancellationToken cancellationToken,
        bool allowUsableCached = false)
    {
        StoredSupabaseSession supabaseSession =
            await _sessions.GetStoredSessionAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("An authenticated Supabase session is required.");
        SessionKey key = ValidateAtStage(
            "supabase-session",
            () => ParseSupabaseSessionKey(supabaseSession));
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
                bool rejected = _rejectedAuthVersions.TryGetValue(key, out long rejectedVersion)
                    && rejectedVersion == state.AuthVersion;
                requiresRebootstrap = rejected || bootstrapAge >= state.Bootstrap.RefreshAfter;
                requiresRebootstrap |= minimumAccessRevision is not null
                    && StringComparer.Ordinal.Compare(
                        state.Bootstrap.AccessRevision,
                        minimumAccessRevision) < 0;
                bool meetsAccessRevision = minimumAccessRevision is null
                    || StringComparer.Ordinal.Compare(state.Bootstrap.AccessRevision, minimumAccessRevision) >= 0;
                if ((!requiresRebootstrap && tokenAge < state.RefreshAfter)
                    || (!rejected && allowUsableCached && meetsAccessRevision
                        && tokenAge < state.ExpiresAfter && bootstrapAge < state.Bootstrap.ExpiresAfter))
                {
                    return state.CreateCredential(_timeProvider);
                }
            }

            if (_retryCooldowns.TryGetValue(key, out RetryCooldown? cooldown))
            {
                TimeSpan remaining = cooldown.Delay - _timeProvider.GetElapsedTime(cooldown.StartedTimestamp);
                if (remaining > TimeSpan.Zero)
                {
                    throw new FirebaseRealtimeRetryException(cooldown.Message, cooldown.StatusCode, remaining, cooldown.FailureCode)
                    {
                        ServerRetryAfter = cooldown.ServerRetryAfter,
                    };
                }
                _retryCooldowns.Remove(key);
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
        SessionKey key = ValidateAtStage(
            "supabase-session",
            () => ParseSupabaseSessionKey(session));
        Task<FirebaseRealtimeCredential>? existingOperation = null;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureCurrentGeneration(requestGeneration, generationToken);
            if (_states.TryGetValue(key, out TokenState? state)
                && state.Generation == requestGeneration
                && (!_rejectedAuthVersions.TryGetValue(key, out long rejectedVersion)
                    || rejectedVersion != state.AuthVersion)
                && state.CreateCredential(_timeProvider).ExpiresAfter > TimeSpan.Zero
                && StringComparer.Ordinal.Compare(
                    state.Bootstrap.AccessRevision,
                    minimumAccessRevision) >= 0)
            {
                return state.CreateCredential(_timeProvider);
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

    public async ValueTask RecoverRejectedCredentialAsync(
        FirebaseRealtimeCredential rejected,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rejected);
        (long requestGeneration, CancellationToken generationToken) = CaptureGeneration();
        if (rejected.Generation != requestGeneration)
        {
            return;
        }
        SessionKey key = new(rejected.UserId, rejected.SessionId);
        bool firstRejection;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureCurrentGeneration(requestGeneration, generationToken);
            if (!_states.TryGetValue(key, out TokenState? state)
                || state.Generation != rejected.Generation
                || state.AuthVersion != rejected.AuthVersion)
            {
                return;
            }
            firstRejection = !_rejectedAuthVersions.TryGetValue(key, out long previousRejected)
                || previousRejected != rejected.AuthVersion;
            _rejectedAuthVersions[key] = rejected.AuthVersion;
        }
        finally
        {
            _gate.Release();
        }
        if (!firstRejection)
        {
            return;
        }
        CredentialRejected?.Invoke(rejected);
        _ = await GetCredentialAsync(requestGeneration, generationToken, cancellationToken).ConfigureAwait(false);
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
            _retryCooldowns.Clear();
            _retryStates.Clear();
            _rejectedAuthVersions.Clear();
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
        using var deadline = new CancellationTokenSource(_acquisitionTimeout, _timeProvider);
        using var acquisition = CancellationTokenSource.CreateLinkedTokenSource(generationToken, deadline.Token);
        try
        {
            return await AcquireCredentialWithinDeadlineAsync(
                key, supabaseSession, previousState, persistedSession, requiresRebootstrap,
                minimumAccessRevision, generation, acquisition.Token, generationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !generationToken.IsCancellationRequested)
        {
            var timeout = new FirebaseRealtimeRetryException("Firebase credential acquisition timed out.", null, TimeSpan.Zero, "timeout");
            throw await RemoveFailedOperationAsync(key, generation, timeout).ConfigureAwait(false) ?? timeout;
        }
    }

    private async Task<FirebaseRealtimeCredential> AcquireCredentialWithinDeadlineAsync(
        SessionKey key,
        StoredSupabaseSession supabaseSession,
        TokenState? previousState,
        PersistedSession? persistedSession,
        bool requiresRebootstrap,
        string? minimumAccessRevision,
        long generation,
        CancellationToken generationToken,
        CancellationToken lifetimeToken)
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
                        || exception is FirebaseRealtimeCredentialStageException
                        {
                            Stage: "firebase-token",
                        }
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
                    generationToken,
                    lifetimeToken).ConfigureAwait(false);
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
                generationToken,
                lifetimeToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !generationToken.IsCancellationRequested)
        {
            EnsureCurrentGeneration(generation, lifetimeToken);
            FirebaseRealtimeRetryException? adjusted = await RemoveFailedOperationAsync(key, generation, exception)
                .ConfigureAwait(false);
            if (adjusted is not null)
            {
                throw adjusted;
            }
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
        BoundedHttpResponse bootstrapResponse = await SendAndReadBytesAsync(
            request,
            "Realtime bootstrap request failed.",
            cancellationToken).ConfigureAwait(false);
        long receivedTimestamp = _timeProvider.GetTimestamp();
        long receivedAtUnixMilliseconds = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        long serverNowUnixMilliseconds = bootstrapResponse.ServerDateUnixMilliseconds
            ?? receivedAtUnixMilliseconds;
        FirebaseRealtimeBootstrapConfiguration response = ValidateAtStage(
            "bootstrap-response",
            () => FirebaseRealtimeProtocol.ParseBootstrapResponse(
                bootstrapResponse.Payload,
                serverNowUnixMilliseconds,
                minimumAccessRevision));
        long remainingLeaseMilliseconds = checked(
            response.RolloutLeaseExpiresAt - serverNowUnixMilliseconds);
        long refreshDelayMilliseconds = response.RefreshAfter > serverNowUnixMilliseconds
            ? Math.Min(
                response.RefreshAfter - serverNowUnixMilliseconds,
                remainingLeaseMilliseconds - 1)
            : Math.Max(1, remainingLeaseMilliseconds / 2);
        // Schedule locally before the server's final renewal window so transient retries
        // have time to recover. Keep the validated wire deadline unchanged.
        long refreshLeadMilliseconds = 60_000 + (long)(Random.Shared.NextDouble() * 30_000);
        long localRefreshMilliseconds = Math.Max(1, remainingLeaseMilliseconds > refreshLeadMilliseconds
            ? remainingLeaseMilliseconds - refreshLeadMilliseconds
            : remainingLeaseMilliseconds / 2);
        refreshDelayMilliseconds = Math.Min(refreshDelayMilliseconds, localRefreshMilliseconds);
        return new BootstrapGrant(
            new ValidatedBootstrap(
                response.DatabaseUrl,
                response.FirebaseApiKey,
                response.AccessRevision,
                response.Rooms,
                response.WireItems,
                response.RolloutLeaseExpiresAt,
                receivedTimestamp,
                TimeSpan.FromMilliseconds(refreshDelayMilliseconds),
                TimeSpan.FromMilliseconds(remainingLeaseMilliseconds)),
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
        return ValidateAtStage(
            "firebase-token",
            () => ValidateTokenGrant(
                payload.IdToken,
                payload.RefreshToken,
                payload.ExpiresIn,
                key,
                rolloutLeaseExpiresAt));
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
        return ValidateAtStage(
            "firebase-token",
            () =>
            {
                if (!Guid.TryParse(payload.UserId, out Guid userId) || userId != key.UserId)
                {
                    throw new InvalidDataException("Firebase refresh response identity was invalid.");
                }
                return ValidateTokenGrant(
                    payload.IdToken,
                    payload.RefreshToken,
                    payload.ExpiresIn,
                    key,
                    rolloutLeaseExpiresAt);
            });
    }

    private async Task<BoundedHttpResponse> SendAndReadBytesAsync(
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
                throw await CreateHttpFailureAsync(response, failureMessage, cancellationToken).ConfigureAwait(false);
            }
            byte[] payload = await ReadBoundedAsync(response.Content, cancellationToken)
                .ConfigureAwait(false);
            return new BoundedHttpResponse(
                payload,
                response.Headers.Date?.ToUnixTimeMilliseconds());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (FirebaseRealtimeRetryException)
        {
            throw;
        }
        catch (FirebaseRealtimeHttpException)
        {
            throw;
        }
        catch (HttpRequestException exception)
        {
            throw exception.StatusCode is null
                ? new FirebaseRealtimeRetryException(failureMessage, null, TimeSpan.Zero)
                : new HttpRequestException(failureMessage, inner: null, exception.StatusCode);
        }
        catch (OperationCanceledException)
        {
            throw new FirebaseRealtimeRetryException(failureMessage, null, TimeSpan.Zero);
        }
        catch (IOException)
        {
            throw new FirebaseRealtimeRetryException(failureMessage, null, TimeSpan.Zero);
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
                throw await CreateHttpFailureAsync(response, failureMessage, cancellationToken).ConfigureAwait(false);
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
        catch (FirebaseRealtimeRetryException)
        {
            throw;
        }
        catch (FirebaseRealtimeHttpException)
        {
            throw;
        }
        catch (HttpRequestException exception)
        {
            throw exception.StatusCode is null
                ? new FirebaseRealtimeRetryException(failureMessage, null, TimeSpan.Zero)
                : new HttpRequestException(failureMessage, inner: null, exception.StatusCode);
        }
        catch (OperationCanceledException)
        {
            throw new FirebaseRealtimeRetryException(failureMessage, null, TimeSpan.Zero);
        }
        catch (IOException)
        {
            throw new FirebaseRealtimeRetryException(failureMessage, null, TimeSpan.Zero);
        }
        catch (Exception)
        {
            throw new HttpRequestException(failureMessage);
        }
    }

    private async Task<HttpRequestException> CreateHttpFailureAsync(
        HttpResponseMessage response, string message, CancellationToken cancellationToken)
    {
        string failureCode = "transport-error";
        try
        {
            byte[] payload = await ReadBoundedAsync(response.Content, cancellationToken).ConfigureAwait(false);
            JsonDocument document;
            document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 8 });
            using (document)
            {
                if (document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("error", out JsonElement error)
                    && error.ValueKind == JsonValueKind.String)
                {
                    failureCode = error.GetString() switch
                    {
                        "authentication_required" => "authentication_required",
                        "invalid_argument" => "invalid_argument",
                        "realtime_grant_not_converged" => "realtime_grant_not_converged",
                        "realtime_rollout_disabled" => "realtime_rollout_disabled",
                        "realtime_bootstrap_unavailable" => "realtime_bootstrap_unavailable",
                        "realtime_bootstrap_rate_limited" => "realtime_bootstrap_rate_limited",
                        _ => "transport-error",
                    };
                }
            }
        }
        catch (Exception)
        {
            // Retain the HTTP status and retry headers when optional error-body reading fails.
        }
        if (response.StatusCode != HttpStatusCode.TooManyRequests && (int)response.StatusCode < 500)
        {
            return new FirebaseRealtimeHttpException(message, response.StatusCode, failureCode);
        }
        RetryConditionHeaderValue? retry = response.Headers.RetryAfter;
        TimeSpan delay = retry?.Delta
            ?? (retry?.Date is DateTimeOffset retryAt
                ? retryAt - (response.Headers.Date ?? _timeProvider.GetUtcNow())
                : TimeSpan.Zero);
        delay = TimeSpan.FromTicks(Math.Max(delay.Ticks, 0));
        return new FirebaseRealtimeRetryException(message, response.StatusCode, delay, failureCode)
        {
            ServerRetryAfter = retry is not null ? delay : null,
        };
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
        CancellationToken generationToken,
        CancellationToken lifetimeToken)
    {
        await _gate.WaitAsync(generationToken).ConfigureAwait(false);
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
                checked(++_authVersion),
                lifetimeToken,
                grant.ReceivedTimestamp,
                RefreshAfter(grant.ExpiresIn),
                grant.ExpiresIn,
                bootstrap);
            _states[key] = state;
            _rejectedAuthVersions.Remove(key);
            _retryCooldowns.Remove(key);
            _retryStates.TryGetValue(key, out RetryState? retryState);
            _retryStates[key] = new RetryState(retryState?.Failures ?? 0, _timeProvider.GetTimestamp());
            if (_minimumAccessRevisions.TryGetValue(key, out string? minimumAccessRevision)
                && StringComparer.Ordinal.Compare(
                    bootstrap.AccessRevision,
                    minimumAccessRevision) >= 0)
            {
                _minimumAccessRevisions.Remove(key);
            }
            RemoveInflightWithinGate(key, generation);
            return state.CreateCredential(_timeProvider);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<FirebaseRealtimeRetryException?> RemoveFailedOperationAsync(
        SessionKey key, long generation, Exception exception)
    {
        FirebaseRealtimeRetryException? adjusted = null;
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            RemoveInflightWithinGate(key, generation);
            if (generation == CurrentGeneration())
            {
                if (exception is FirebaseRealtimeRetryException retry)
                {
                    _retryStates.TryGetValue(key, out RetryState? previousRetry);
                    int failures = previousRetry?.Failures ?? 0;
                    if (previousRetry?.LastSuccessTimestamp is long success
                        && _timeProvider.GetElapsedTime(success) >= FirebaseRealtimeRetryPolicy.StableConnection)
                    {
                        failures = 0;
                    }
                    failures = Math.Min(failures + 1, 17);
                    TimeSpan delay = FirebaseRealtimeRetryPolicy.CalculateDelay(
                        failures, retry.StatusCode, retry.RetryAfter, _retryJitter());
                    _retryStates[key] = new RetryState(failures, null);
                    _retryCooldowns[key] = new RetryCooldown(
                        _timeProvider.GetTimestamp(), delay, retry.Message, retry.StatusCode, retry.FailureCode, retry.ServerRetryAfter);
                    adjusted = new FirebaseRealtimeRetryException(retry.Message, retry.StatusCode, delay, retry.FailureCode)
                    {
                        ServerRetryAfter = retry.ServerRetryAfter,
                    };
                }
                else if (exception is not OperationCanceledException)
                {
                    _states.Remove(key);
                    _persistedSessions.Remove(StorageKey(key));
                    _retryCooldowns.Remove(key);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
        return adjusted;
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
        SessionKey key,
        long rolloutLeaseExpiresAt)
    {
        if (string.IsNullOrWhiteSpace(idToken)
            || idToken.Length > MaximumTokenCharacters
            || string.IsNullOrWhiteSpace(refreshToken)
            || refreshToken.Length > MaximumTokenCharacters
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
        claims.TryGetProperty("sideySessionId", out JsonElement session)
        && Guid.TryParse(session.GetString(), out Guid sessionId)
        && sessionId == key.SessionId
        && claims.TryGetProperty("sideyRolloutUntil", out JsonElement rolloutLease)
        && rolloutLease.TryGetInt64(out long tokenRolloutLeaseExpiresAt)
        && tokenRolloutLeaseExpiresAt == rolloutLeaseExpiresAt;

    private static T ValidateAtStage<T>(string stage, Func<T> validation)
    {
        try
        {
            return validation();
        }
        catch (FirebaseRealtimeCredentialStageException)
        {
            throw;
        }
        catch (InvalidDataException exception)
        {
            throw new FirebaseRealtimeCredentialStageException(stage, exception);
        }
    }

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
        if (_timeProvider.GetElapsedTime(bootstrap.ReceivedTimestamp) >= bootstrap.ExpiresAfter)
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

    private sealed record BoundedHttpResponse(
        byte[] Payload,
        long? ServerDateUnixMilliseconds);

    private sealed record ValidatedBootstrap(
        Uri DatabaseUrl,
        string FirebaseApiKey,
        string AccessRevision,
        IReadOnlyList<Guid> Rooms,
        IReadOnlyList<string> WireItems,
        long RolloutLeaseExpiresAt,
        long ReceivedTimestamp,
        TimeSpan RefreshAfter,
        TimeSpan ExpiresAfter)
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

    private sealed record RetryState(int Failures, long? LastSuccessTimestamp);

    private sealed record RetryCooldown(
        long StartedTimestamp,
        TimeSpan Delay,
        string Message,
        HttpStatusCode? StatusCode,
        string FailureCode,
        TimeSpan? ServerRetryAfter);

    private sealed record TokenState(
        Guid UserId,
        Guid SessionId,
        string IdToken,
        string RefreshToken,
        string FirebaseApiKey,
        long Generation,
        long AuthVersion,
        CancellationToken LifetimeToken,
        long IssuedTimestamp,
        TimeSpan RefreshAfter,
        TimeSpan ExpiresAfter,
        ValidatedBootstrap Bootstrap)
    {
        public FirebaseRealtimeCredential CreateCredential(TimeProvider timeProvider)
        {
            TimeSpan tokenAge = timeProvider.GetElapsedTime(IssuedTimestamp);
            TimeSpan bootstrapAge = timeProvider.GetElapsedTime(Bootstrap.ReceivedTimestamp);
            return new FirebaseRealtimeCredential(
                UserId,
                SessionId,
                IdToken,
                Bootstrap.DatabaseUrl,
                Generation,
                LifetimeToken,
                Bootstrap.WireItems,
                Remaining(RefreshAfter - tokenAge, Bootstrap.RefreshAfter - bootstrapAge),
                Remaining(ExpiresAfter - tokenAge, Bootstrap.ExpiresAfter - bootstrapAge),
                AuthVersion);
        }

        private static TimeSpan Remaining(TimeSpan token, TimeSpan bootstrap) =>
            TimeSpan.FromTicks(Math.Max(0, Math.Min(token.Ticks, bootstrap.Ticks)));

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
        [property: JsonPropertyName("expiresIn")] string? ExpiresIn)
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
