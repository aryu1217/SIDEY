using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Sidey.Core.Abstractions;

namespace Sidey.Platform.Windows.Tests;

public sealed class FirebaseRealtimeCredentialProviderTests
{
    private static readonly Guid s_userId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
    private static readonly Guid s_roomId = Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");
    private static readonly DateTimeOffset s_initialTime =
        DateTimeOffset.Parse("2026-01-01T00:00:00Z");

    [Fact]
    public async Task BootstrapStoresRotatedRefreshTokenBeforePublishingAndUsesReceiptDeadline()
    {
        var sessionId = Guid.NewGuid();
        StoredSupabaseSession session = SupabaseSession(s_userId, sessionId, "supabase-a");
        var store = new MemoryCredentialStore { BlockWrites = true };
        var handler = new FirebaseProtocolHandler();
        handler.AddSession(session, "id-a-0", "refresh-a-0", expiresIn: 100);
        using var client = new HttpClient(handler);
        var time = new ManualTimeProvider();
        var provider = new FirebaseRealtimeCredentialProvider(
            new FixedSessionAccessor(session), store, client, time);

        Task<FirebaseRealtimeCredential> pending = provider.GetCredentialAsync().AsTask();
        await store.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(pending.IsCompleted);
        Assert.Null(store.Value(CredentialKey.FirebaseRealtimeSession));

        time.Advance(TimeSpan.FromSeconds(80));
        store.AllowWrite.TrySetResult();
        FirebaseRealtimeCredential credential = await pending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(s_userId, credential.UserId);
        Assert.Equal(sessionId, credential.SessionId);
        Assert.Equal("https://sidey.asia-southeast1.firebasedatabase.app/", credential.DatabaseUrl.AbsoluteUri);
        Assert.Equal(1, credential.Generation);
        Assert.DoesNotContain(credential.IdToken, credential.ToString(), StringComparison.Ordinal);
        Assert.Equal(
            ["bootstrap", "exchange"],
            [.. handler.RequestKinds]);
        string persisted = Assert.IsType<string>(store.Value(CredentialKey.FirebaseRealtimeSession));
        Assert.Contains("sidey-realtime", persisted, StringComparison.Ordinal);
        Assert.Contains("bootstrapRealtime", persisted, StringComparison.Ordinal);
        Assert.Contains(sessionId.ToString(), persisted, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("refresh-a-0", persisted, StringComparison.Ordinal);

        FirebaseRealtimeCredential refreshed = await provider.GetCredentialAsync();

        Assert.NotEqual(credential.IdToken, refreshed.IdToken);
        Assert.Equal(1, handler.RefreshRequests);
    }

    [Fact]
    public async Task MonotonicDeadlineCoalescesConcurrentRefreshAndUsesLatestRefreshToken()
    {
        var sessionId = Guid.NewGuid();
        StoredSupabaseSession session = SupabaseSession(s_userId, sessionId, "supabase-a");
        var store = new MemoryCredentialStore();
        var time = new ManualTimeProvider();
        var handler = new FirebaseProtocolHandler { BlockRefresh = true };
        handler.AddSession(session, "id-a-0", "refresh-a-0", expiresIn: 100);
        using var client = new HttpClient(handler);
        var provider = new FirebaseRealtimeCredentialProvider(
            new FixedSessionAccessor(session), store, client, time);
        FirebaseRealtimeCredential initial = await provider.GetCredentialAsync();

        time.SetUtcNow(time.GetUtcNow().AddDays(30));
        time.Advance(TimeSpan.FromSeconds(79));
        FirebaseRealtimeCredential beforeDeadline = await provider.GetCredentialAsync();
        Assert.Equal(initial.IdToken, beforeDeadline.IdToken);
        Assert.Equal(0, handler.RefreshRequests);

        time.SetUtcNow(time.GetUtcNow().AddDays(-60));
        time.Advance(TimeSpan.FromSeconds(1));
        Task<FirebaseRealtimeCredential>[] callers = [.. Enumerable.Range(0, 8)
            .Select(_ => provider.GetCredentialAsync().AsTask())];
        await handler.RefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, handler.RefreshRequests);

        handler.AllowRefresh.TrySetResult();
        FirebaseRealtimeCredential[] refreshed = await Task.WhenAll(callers).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.All(refreshed, item => Assert.Equal(handler.IdToken(sessionId, 1), item.IdToken));
        Assert.Equal(1, handler.RefreshRequests);
        string persisted = Assert.IsType<string>(store.Value(CredentialKey.FirebaseRealtimeSession));
        Assert.Contains("refresh-a-1", persisted, StringComparison.Ordinal);
        Assert.DoesNotContain("refresh-a-0", persisted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BootstrapRefreshDeadlineUsesReceiptTimestampAndExchangesRenewedLease()
    {
        var sessionId = Guid.NewGuid();
        StoredSupabaseSession session = SupabaseSession(s_userId, sessionId, "supabase-a");
        var store = new MemoryCredentialStore();
        var time = new ManualTimeProvider();
        var handler = new FirebaseProtocolHandler();
        handler.AddSession(session, "id-a-0", "refresh-a-0", expiresIn: 3_600);
        using var client = new HttpClient(handler);
        var provider = new FirebaseRealtimeCredentialProvider(
            new FixedSessionAccessor(session), store, client, time);
        FirebaseRealtimeCredential initial = await provider.GetCredentialAsync();

        time.Advance(TimeSpan.FromSeconds(269));
        FirebaseRealtimeCredential beforeDeadline = await provider.GetCredentialAsync();
        Assert.Equal(initial.IdToken, beforeDeadline.IdToken);

        time.Advance(TimeSpan.FromSeconds(1));
        time.SetUtcNow(s_initialTime.AddSeconds(270));
        handler.RolloutLeaseExpiresAt = s_initialTime.AddSeconds(570).ToUnixTimeMilliseconds();
        FirebaseRealtimeCredential renewed = await provider.GetCredentialAsync();

        Assert.NotEqual(initial.IdToken, renewed.IdToken);
        Assert.Equal(
            ["bootstrap", "exchange", "bootstrap", "exchange"],
            [.. handler.RequestKinds]);
        Assert.Equal(0, handler.RefreshRequests);
    }

    [Fact]
    public async Task ThirtySecondLeaseRenewsAtHalfOfRemainingMonotonicLease()
    {
        var sessionId = Guid.NewGuid();
        StoredSupabaseSession session = SupabaseSession(s_userId, sessionId, "supabase-a");
        var time = new ManualTimeProvider();
        var handler = new FirebaseProtocolHandler
        {
            RolloutLeaseExpiresAt = s_initialTime.AddSeconds(30).ToUnixTimeMilliseconds(),
        };
        handler.AddSession(session, "id-a-0", "refresh-a-0", expiresIn: 3_600);
        using var client = new HttpClient(handler);
        var provider = new FirebaseRealtimeCredentialProvider(
            new FixedSessionAccessor(session), new MemoryCredentialStore(), client, time);
        FirebaseRealtimeCredential initial = await provider.GetCredentialAsync();

        time.Advance(TimeSpan.FromSeconds(14));
        Assert.Equal(initial.IdToken, (await provider.GetCredentialAsync()).IdToken);

        time.Advance(TimeSpan.FromSeconds(1));
        time.SetUtcNow(s_initialTime.AddSeconds(15));
        handler.RolloutLeaseExpiresAt = s_initialTime.AddSeconds(45).ToUnixTimeMilliseconds();
        FirebaseRealtimeCredential renewed = await provider.GetCredentialAsync();

        Assert.NotEqual(initial.IdToken, renewed.IdToken);
        Assert.Equal(
            ["bootstrap", "exchange", "bootstrap", "exchange"],
            [.. handler.RequestKinds]);
    }

    [Fact]
    public async Task RestartBootstrapsThenRefreshesTheExactPersistedSession()
    {
        var sessionId = Guid.NewGuid();
        StoredSupabaseSession session = SupabaseSession(s_userId, sessionId, "supabase-a");
        var store = new MemoryCredentialStore();
        var handler = new FirebaseProtocolHandler();
        handler.AddSession(session, "id-a-0", "refresh-a-0", expiresIn: 100);
        using var client = new HttpClient(handler);
        var firstProvider = new FirebaseRealtimeCredentialProvider(
            new FixedSessionAccessor(session), store, client, new ManualTimeProvider());
        _ = await firstProvider.GetCredentialAsync();

        var restartedProvider = new FirebaseRealtimeCredentialProvider(
            new FixedSessionAccessor(session), store, client, new ManualTimeProvider());
        FirebaseRealtimeCredential restored = await restartedProvider.GetCredentialAsync();

        Assert.Equal(sessionId, restored.SessionId);
        Assert.Equal(handler.IdToken(sessionId, 1), restored.IdToken);
        Assert.Equal(["bootstrap", "exchange", "bootstrap", "refresh"], [.. handler.RequestKinds]);
        string persisted = Assert.IsType<string>(store.Value(CredentialKey.FirebaseRealtimeSession));
        Assert.Contains("refresh-a-1", persisted, StringComparison.Ordinal);
        Assert.DoesNotContain("refresh-a-0", persisted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidPersistedSessionFallsBackToBootstrapCustomTokenExchange()
    {
        var sessionId = Guid.NewGuid();
        StoredSupabaseSession session = SupabaseSession(s_userId, sessionId, "supabase-a");
        var store = new MemoryCredentialStore();
        var handler = new FirebaseProtocolHandler();
        handler.AddSession(session, "id-a-0", "refresh-a-0", expiresIn: 100);
        using var client = new HttpClient(handler);
        var firstProvider = new FirebaseRealtimeCredentialProvider(
            new FixedSessionAccessor(session), store, client, new ManualTimeProvider());
        _ = await firstProvider.GetCredentialAsync();
        string persisted = Assert.IsType<string>(store.Value(CredentialKey.FirebaseRealtimeSession));
        store.Set(
            CredentialKey.FirebaseRealtimeSession,
            persisted.Replace(
                "asia-southeast1-sidey-realtime.cloudfunctions.net",
                "invalid.example",
                StringComparison.Ordinal));

        var restartedProvider = new FirebaseRealtimeCredentialProvider(
            new FixedSessionAccessor(session), store, client, new ManualTimeProvider());
        FirebaseRealtimeCredential credential = await restartedProvider.GetCredentialAsync();

        Assert.Equal(sessionId, credential.SessionId);
        Assert.Equal(["bootstrap", "exchange", "bootstrap", "exchange"], [.. handler.RequestKinds]);
        Assert.Equal(0, handler.RefreshRequests);
    }

    [Fact]
    public async Task MalformedPersistedSessionIsReplacedAfterBootstrapExchange()
    {
        var sessionId = Guid.NewGuid();
        StoredSupabaseSession session = SupabaseSession(s_userId, sessionId, "supabase-a");
        var store = new MemoryCredentialStore();
        store.Set(CredentialKey.FirebaseRealtimeSession, "{");
        var handler = new FirebaseProtocolHandler();
        handler.AddSession(session, "id-a-0", "refresh-a-0", expiresIn: 100);
        using var client = new HttpClient(handler);
        var provider = new FirebaseRealtimeCredentialProvider(
            new FixedSessionAccessor(session), store, client, new ManualTimeProvider());

        FirebaseRealtimeCredential credential = await provider.GetCredentialAsync();

        Assert.Equal(sessionId, credential.SessionId);
        Assert.Equal(["bootstrap", "exchange"], [.. handler.RequestKinds]);
        string persisted = Assert.IsType<string>(store.Value(CredentialKey.FirebaseRealtimeSession));
        Assert.Contains("refresh-a-0", persisted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectedPersistedRefreshTokenIsDiscardedAndExchangedOnce()
    {
        var sessionId = Guid.NewGuid();
        StoredSupabaseSession session = SupabaseSession(s_userId, sessionId, "supabase-a");
        var store = new MemoryCredentialStore();
        var handler = new FirebaseProtocolHandler();
        handler.AddSession(session, "id-a-0", "refresh-a-0", expiresIn: 100);
        using var client = new HttpClient(handler);
        var firstProvider = new FirebaseRealtimeCredentialProvider(
            new FixedSessionAccessor(session), store, client, new ManualTimeProvider());
        _ = await firstProvider.GetCredentialAsync();
        handler.RejectNextRefresh = true;

        var restartedProvider = new FirebaseRealtimeCredentialProvider(
            new FixedSessionAccessor(session), store, client, new ManualTimeProvider());
        FirebaseRealtimeCredential recovered = await restartedProvider.GetCredentialAsync();

        Assert.Equal(handler.IdToken(sessionId, 0), recovered.IdToken);
        Assert.Equal(
            ["bootstrap", "exchange", "bootstrap", "refresh", "exchange"],
            [.. handler.RequestKinds]);
        string persisted = Assert.IsType<string>(store.Value(CredentialKey.FirebaseRealtimeSession));
        Assert.Contains("refresh-a-0", persisted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HttpFailureUsesFixedMessageWithoutRequestSecrets()
    {
        var sessionId = Guid.NewGuid();
        StoredSupabaseSession session = SupabaseSession(s_userId, sessionId, "supabase-secret-marker");
        using var client = new HttpClient(new SecretLeakingHandler());
        var provider = new FirebaseRealtimeCredentialProvider(
            new FixedSessionAccessor(session),
            new MemoryCredentialStore(),
            client,
            new ManualTimeProvider());

        HttpRequestException exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => provider.GetCredentialAsync().AsTask());

        Assert.Equal("Realtime bootstrap request failed.", exception.Message);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain(session.AccessToken, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("transport-secret", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResetWhileSessionReadIsPendingInvalidatesTheRequestBeforeNetworkUse()
    {
        var sessionId = Guid.NewGuid();
        StoredSupabaseSession session = SupabaseSession(s_userId, sessionId, "supabase-a");
        var accessor = new DelayedSessionAccessor(session);
        var handler = new FirebaseProtocolHandler();
        handler.AddSession(session, "id-a-0", "refresh-a-0", expiresIn: 100);
        using var client = new HttpClient(handler);
        var provider = new FirebaseRealtimeCredentialProvider(
            accessor, new MemoryCredentialStore(), client, new ManualTimeProvider());
        Task<FirebaseRealtimeCredential> pending = provider.GetCredentialAsync().AsTask();
        await accessor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await provider.ResetAsync();
        accessor.Allow.TrySetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Empty(handler.RequestKinds);
    }

    [Fact]
    public async Task ResetWhileConvergenceSessionReadIsPendingCannotStartANewGeneration()
    {
        var sessionId = Guid.NewGuid();
        StoredSupabaseSession session = SupabaseSession(s_userId, sessionId, "supabase-a");
        var accessor = new DelayedSessionAccessor(session);
        var handler = new FirebaseProtocolHandler();
        handler.AddSession(session, "id-a-0", "refresh-a-0", expiresIn: 100);
        using var client = new HttpClient(handler);
        var provider = new FirebaseRealtimeCredentialProvider(
            accessor, new MemoryCredentialStore(), client, new ManualTimeProvider());
        Task<FirebaseRealtimeCredential> pending = provider.ConvergeAsync(
            "00000000000000000002").AsTask();
        await accessor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await provider.ResetAsync();
        accessor.Allow.TrySetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Empty(handler.RequestKinds);
    }

    [Fact]
    public async Task RequestStartedWhileResetIsDeletingCannotRestoreCredential()
    {
        var sessionId = Guid.NewGuid();
        StoredSupabaseSession session = SupabaseSession(s_userId, sessionId, "supabase-a");
        var store = new MemoryCredentialStore { BlockDeletes = true };
        var handler = new FirebaseProtocolHandler();
        handler.AddSession(session, "id-a-0", "refresh-a-0", expiresIn: 100);
        using var client = new HttpClient(handler);
        var provider = new FirebaseRealtimeCredentialProvider(
            new FixedSessionAccessor(session), store, client, new ManualTimeProvider());
        Task reset = provider.ResetAsync().AsTask();
        await store.DeleteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.GetCredentialAsync().AsTask());
        store.AllowDelete.TrySetResult();
        await reset.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Null(store.Value(CredentialKey.FirebaseRealtimeSession));
        Assert.Empty(handler.RequestKinds);
    }

    [Fact]
    public async Task SameUserConcurrentSessionsRemainIsolatedWhenResponsesCompleteInReverseOrder()
    {
        var sessionA = Guid.NewGuid();
        var sessionB = Guid.NewGuid();
        StoredSupabaseSession supabaseA = SupabaseSession(s_userId, sessionA, "supabase-a");
        StoredSupabaseSession supabaseB = SupabaseSession(s_userId, sessionB, "supabase-b");
        var store = new MemoryCredentialStore();
        var handler = new FirebaseProtocolHandler { BlockExchanges = true };
        handler.AddSession(supabaseA, "id-a-0", "refresh-a-0", expiresIn: 100);
        handler.AddSession(supabaseB, "id-b-0", "refresh-b-0", expiresIn: 100);
        using var client = new HttpClient(handler);
        var provider = new FirebaseRealtimeCredentialProvider(
            new QueueSessionAccessor(supabaseA, supabaseB),
            store,
            client,
            new ManualTimeProvider());

        Task<FirebaseRealtimeCredential> requestA = provider.GetCredentialAsync().AsTask();
        Task<FirebaseRealtimeCredential> requestB = provider.GetCredentialAsync().AsTask();
        await handler.ExchangeStarted(sessionA).WaitAsync(TimeSpan.FromSeconds(5));
        await handler.ExchangeStarted(sessionB).WaitAsync(TimeSpan.FromSeconds(5));

        handler.ReleaseExchange(sessionB);
        FirebaseRealtimeCredential credentialB = await requestB.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(sessionB, credentialB.SessionId);
        Assert.False(requestA.IsCompleted);

        handler.ReleaseExchange(sessionA);
        FirebaseRealtimeCredential credentialA = await requestA.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(sessionA, credentialA.SessionId);
        Assert.NotEqual(credentialA.IdToken, credentialB.IdToken);
        string persisted = Assert.IsType<string>(store.Value(CredentialKey.FirebaseRealtimeSession));
        Assert.Contains(sessionA.ToString(), persisted, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(sessionB.ToString(), persisted, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("refresh-a-0", persisted, StringComparison.Ordinal);
        Assert.Contains("refresh-b-0", persisted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResetDiscardsLateExchangeAndCannotResurrectDeletedCredential()
    {
        var sessionId = Guid.NewGuid();
        StoredSupabaseSession session = SupabaseSession(s_userId, sessionId, "supabase-a");
        var store = new MemoryCredentialStore();
        var handler = new FirebaseProtocolHandler { BlockExchanges = true };
        handler.AddSession(session, "id-a-0", "refresh-a-0", expiresIn: 100);
        using var client = new HttpClient(handler);
        var provider = new FirebaseRealtimeCredentialProvider(
            new FixedSessionAccessor(session), store, client, new ManualTimeProvider());

        Task<FirebaseRealtimeCredential> pending = provider.GetCredentialAsync().AsTask();
        await handler.ExchangeStarted(sessionId).WaitAsync(TimeSpan.FromSeconds(5));

        await provider.ResetAsync();
        handler.ReleaseExchange(sessionId);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Null(store.Value(CredentialKey.FirebaseRealtimeSession));
        Assert.Equal(0, store.WritesAfterLastDelete);
    }

    private static StoredSupabaseSession SupabaseSession(Guid userId, Guid sessionId, string marker) => new(
        Jwt(new { sub = userId, session_id = sessionId, marker }),
        $"supabase-refresh-{marker}",
        userId,
        DateTimeOffset.UtcNow.AddHours(1));

    private static string Jwt(object payload)
    {
        static string Encode(byte[] value) => Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return $"{Encode(Encoding.UTF8.GetBytes("{\"alg\":\"none\"}"))}."
            + $"{Encode(JsonSerializer.SerializeToUtf8Bytes(payload))}.signature";
    }

    private sealed class FixedSessionAccessor(StoredSupabaseSession session) : IAuthSessionAccessor
    {
        public ValueTask<StoredSupabaseSession?> GetStoredSessionAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<StoredSupabaseSession?>(session);
        }
    }

    private sealed class QueueSessionAccessor(params StoredSupabaseSession[] sessions) : IAuthSessionAccessor
    {
        private readonly ConcurrentQueue<StoredSupabaseSession> _sessions = new(sessions);

        public ValueTask<StoredSupabaseSession?> GetStoredSessionAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<StoredSupabaseSession?>(
                _sessions.TryDequeue(out StoredSupabaseSession? session)
                    ? session
                    : throw new InvalidOperationException("No queued test session."));
        }
    }

    private sealed class DelayedSessionAccessor(StoredSupabaseSession session) : IAuthSessionAccessor
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Allow { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<StoredSupabaseSession?> GetStoredSessionAsync(
            CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Allow.Task.WaitAsync(cancellationToken);
            return session;
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = s_initialTime;
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan elapsed) => _timestamp = checked(_timestamp + elapsed.Ticks);
        public void SetUtcNow(DateTimeOffset value) => _utcNow = value;
    }

    private sealed class MemoryCredentialStore : ICredentialStore
    {
        private readonly Dictionary<CredentialKey, string> _values = [];
        private int _deleteSequence;

        public bool BlockWrites { get; init; }
        public bool BlockDeletes { get; init; }
        public TaskCompletionSource WriteStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowWrite { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DeleteStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowDelete { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int WritesAfterLastDelete { get; private set; }

        public string? Value(CredentialKey key) => _values.GetValueOrDefault(key);

        public void Set(CredentialKey key, string value) => _values[key] = value;

        public ValueTask<string?> ReadAsync(
            CredentialKey key,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_values.GetValueOrDefault(key));

        public async ValueTask WriteAsync(
            CredentialKey key,
            string value,
            CancellationToken cancellationToken = default)
        {
            WriteStarted.TrySetResult();
            if (BlockWrites)
            {
                await AllowWrite.Task.WaitAsync(cancellationToken);
            }
            _values[key] = value;
            if (_deleteSequence != 0)
            {
                WritesAfterLastDelete++;
            }
        }

        public async ValueTask DeleteAsync(
            CredentialKey key,
            CancellationToken cancellationToken = default)
        {
            DeleteStarted.TrySetResult();
            if (BlockDeletes)
            {
                await AllowDelete.Task.WaitAsync(cancellationToken);
            }
            _values.Remove(key);
            _deleteSequence++;
            WritesAfterLastDelete = 0;
        }

        public ValueTask<string?> ReadInviteCodeAsync(
            Guid roomId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<string?>(null);

        public ValueTask WriteInviteCodeAsync(
            Guid roomId,
            string inviteCode,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask DeleteInviteCodeAsync(
            Guid roomId,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }

    private sealed class SecretLeakingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new HttpRequestException(
                $"transport-secret {request.RequestUri} {request.Headers.Authorization}");
    }

    private sealed class FirebaseProtocolHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, SessionFixture> _bySupabaseToken = new(StringComparer.Ordinal);
        private readonly Dictionary<Guid, SessionFixture> _bySession = [];

        public bool BlockRefresh { get; init; }
        public bool BlockExchanges { get; init; }
        public bool RejectNextRefresh { get; set; }
        public long RolloutLeaseExpiresAt { get; set; } =
            s_initialTime.AddMinutes(5).ToUnixTimeMilliseconds();
        public TaskCompletionSource RefreshStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowRefresh { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<string> RequestKinds { get; } = [];
        public int RefreshRequests { get; private set; }

        public void AddSession(
            StoredSupabaseSession session,
            string idMarker,
            string refreshToken,
            int expiresIn)
        {
            Guid sessionId = SessionId(session.AccessToken);
            var fixture = new SessionFixture(
                session.UserId,
                sessionId,
                idMarker,
                refreshToken,
                expiresIn);
            _bySupabaseToken.Add(session.AccessToken, fixture);
            _bySession.Add(sessionId, fixture);
        }

        public Task ExchangeStarted(Guid sessionId) => _bySession[sessionId].ExchangeStarted.Task;
        public void ReleaseExchange(Guid sessionId) => _bySession[sessionId].AllowExchange.TrySetResult();
        public string IdToken(Guid sessionId, int generation) =>
            _bySession[sessionId].IdToken(generation, RolloutLeaseExpiresAt);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsoluteUri ==
                "https://asia-southeast1-sidey-realtime.cloudfunctions.net/bootstrapRealtime")
            {
                RequestKinds.Enqueue("bootstrap");
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal(
                    "{}",
                    await request.Content!.ReadAsStringAsync(cancellationToken));
                Assert.Equal("application/json", request.Content.Headers.ContentType?.MediaType);
                Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
                string accessToken = Assert.IsType<string>(request.Headers.Authorization?.Parameter);
                SessionFixture fixture = _bySupabaseToken[accessToken];
                string customToken = Jwt(new
                {
                    uid = fixture.UserId,
                    claims = new
                    {
                        sideyProtocol = 2,
                        sideySessionId = fixture.SessionId,
                        sideyRolloutUntil = RolloutLeaseExpiresAt,
                    },
                });
                return Json(new
                {
                    accessRevision = "00000000000000000042",
                    authTokenLifetimeSeconds = 3600,
                    customToken,
                    databaseURL = "https://sidey.asia-southeast1.firebasedatabase.app",
                    firebaseApiKey = "test-public-api-key-1234567890",
                    permissionSync = "event-driven",
                    protocolVersion = 2,
                    refreshAfter = RolloutLeaseExpiresAt - 30_000,
                    rolloutLeaseExpiresAt = RolloutLeaseExpiresAt,
                    rooms = new[] { s_roomId },
                    wireItems = new[] { "0" },
                });
            }

            if (request.RequestUri?.AbsolutePath == "/v1/accounts:signInWithCustomToken")
            {
                RequestKinds.Enqueue("exchange");
                Assert.Equal("test-public-api-key-1234567890", QueryApiKey(request.RequestUri));
                using var body = JsonDocument.Parse(
                    await request.Content!.ReadAsStringAsync(cancellationToken));
                Assert.True(body.RootElement.GetProperty("returnSecureToken").GetBoolean());
                Guid sessionId = CustomTokenSessionId(body.RootElement.GetProperty("token").GetString()!);
                SessionFixture fixture = _bySession[sessionId];
                fixture.ExchangeStarted.TrySetResult();
                if (BlockExchanges)
                {
                    await fixture.AllowExchange.Task;
                }
                return Json(new
                {
                    idToken = fixture.IdToken(0, RolloutLeaseExpiresAt),
                    refreshToken = fixture.RefreshToken,
                    expiresIn = fixture.ExpiresIn.ToString(),
                    localId = fixture.UserId,
                });
            }

            if (request.RequestUri?.AbsolutePath == "/v1/token")
            {
                RequestKinds.Enqueue("refresh");
                RefreshRequests++;
                RefreshStarted.TrySetResult();
                if (RejectNextRefresh)
                {
                    RejectNextRefresh = false;
                    return new HttpResponseMessage(HttpStatusCode.BadRequest);
                }
                string form = await request.Content!.ReadAsStringAsync(cancellationToken);
                var fields = form.Split('&')
                    .Select(field => field.Split('=', 2))
                    .ToDictionary(
                        pair => Uri.UnescapeDataString(pair[0]),
                        pair => Uri.UnescapeDataString(pair[1]));
                Assert.Equal("refresh_token", fields["grant_type"]);
                SessionFixture fixture = _bySession.Values.Single(item => item.RefreshToken == fields["refresh_token"]);
                if (BlockRefresh)
                {
                    await AllowRefresh.Task.WaitAsync(cancellationToken);
                }
                fixture.RefreshToken = fixture.RefreshToken.Replace("-0", "-1", StringComparison.Ordinal);
                return Json(new
                {
                    id_token = fixture.IdToken(1, RolloutLeaseExpiresAt),
                    refresh_token = fixture.RefreshToken,
                    expires_in = fixture.ExpiresIn.ToString(),
                    user_id = fixture.UserId,
                });
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(value),
        };

        private static string QueryApiKey(Uri uri) => uri.Query[1..]
            .Split('&')
            .Select(item => item.Split('=', 2))
            .Single(pair => pair[0] == "key")[1];

        private static Guid SessionId(string jwt)
        {
            using JsonDocument payload = JwtPayload(jwt);
            return payload.RootElement.GetProperty("session_id").GetGuid();
        }

        private static Guid CustomTokenSessionId(string jwt)
        {
            using JsonDocument payload = JwtPayload(jwt);
            return payload.RootElement.GetProperty("claims").GetProperty("sideySessionId").GetGuid();
        }

        private static JsonDocument JwtPayload(string jwt)
        {
            string segment = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
            segment = segment.PadRight(segment.Length + ((4 - segment.Length % 4) % 4), '=');
            return JsonDocument.Parse(Convert.FromBase64String(segment));
        }

        private sealed class SessionFixture(
            Guid userId,
            Guid sessionId,
            string idMarker,
            string refreshToken,
            int expiresIn)
        {
            public Guid UserId { get; } = userId;
            public Guid SessionId { get; } = sessionId;
            public string IdMarker { get; } = idMarker;
            public string RefreshToken { get; set; } = refreshToken;
            public int ExpiresIn { get; } = expiresIn;
            public TaskCompletionSource ExchangeStarted { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource AllowExchange { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            public string IdToken(int generation, long rolloutLeaseExpiresAt) => Jwt(new
            {
                user_id = UserId,
                sub = UserId,
                sideyProtocol = 2,
                sideySessionId = SessionId,
                sideyRolloutUntil = rolloutLeaseExpiresAt,
                marker = $"{IdMarker}-{generation}",
            });
        }
    }
}
