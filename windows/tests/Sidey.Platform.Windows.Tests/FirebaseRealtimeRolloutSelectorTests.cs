using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Sidey.Infrastructure.Authentication;
using Sidey.Infrastructure.Configuration;
using Sidey.Infrastructure.Realtime;

namespace Sidey.Platform.Windows.Tests;

public sealed class FirebaseRealtimeRolloutSelectorTests
{
    private static readonly Guid s_userId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
    private static readonly Guid s_sessionId = Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, 0, 4, 6)]
    [InlineData(HttpStatusCode.TooManyRequests, 0, 30, 36)]
    [InlineData(HttpStatusCode.ServiceUnavailable, 2400, 2400, 2880)]
    public async Task ForcedAndParallelSelectionRespectSharedRetryDeadline(
        HttpStatusCode status, int serverSeconds, int minimumSeconds, int maximumSeconds)
    {
        int requests = 0;
        ManualTimeProvider time = new();
        using HttpClient client = new(new DelegateHandler(async (_, _) =>
        {
            if (++requests > 1)
                return SelectionResponse(true, false, 30);
            HttpResponseMessage response = new(status) { Content = JsonContent.Create(new { code = "PGRST003" }) };
            if (serverSeconds > 0)
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(serverSeconds));
            return await Task.FromResult(response);
        }));
        FirebaseRealtimeRolloutSelector selector = new(
            new SupabaseRuntimeConfiguration(new Uri("https://example.supabase.co"), "publishable-test-key"),
            new FixedSessionAccessor(Session()), "2.0.0", client, time);

        FirebaseRealtimeRolloutSelection failed = await selector.SelectAsync();
        Assert.InRange(failed.CacheTtl.TotalSeconds, minimumSeconds, maximumSeconds);
        _ = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => selector.RefreshAsync().AsTask()));
        time.Advance(TimeSpan.FromSeconds(minimumSeconds - 1));
        Assert.False((await selector.RefreshAsync()).Enabled);
        Assert.Equal(1, requests);
        time.Advance(TimeSpan.FromSeconds(maximumSeconds - minimumSeconds + 2));
        Assert.True((await selector.RefreshAsync()).Enabled);
        Assert.Equal(2, requests);
    }

    [Fact]
    public async Task FailedEnabledRefreshPreservesSafeHttpDetailsWithoutErrorBodySecrets()
    {
        int requests = 0;
        using HttpClient client = new(new DelegateHandler(async (_, _) =>
        {
            if (++requests == 1)
            {
                return SelectionResponse(true, false, 30);
            }
            HttpResponseMessage response = new(HttpStatusCode.ServiceUnavailable)
            {
                Content = JsonContent.Create(new { code = "PGRST003", message = "private-server-detail", details = "bearer-secret" }),
            };
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(47));
            return await Task.FromResult(response);
        }));
        FirebaseRealtimeRolloutSelector selector = new(
            new SupabaseRuntimeConfiguration(new Uri("https://example.supabase.co"), "publishable-test-key"),
            new FixedSessionAccessor(Session()), "2.0.0", client);
        _ = await selector.SelectAsync();

        FirebaseRealtimeRolloutException failure = await Assert.ThrowsAsync<FirebaseRealtimeRolloutException>(
            () => selector.RefreshAsync().AsTask());

        Assert.True(failure.FailClosed);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failure.StatusCode);
        Assert.Equal("PGRST003", failure.FailureCode);
        Assert.Equal(TimeSpan.FromSeconds(47), failure.RetryAfter);
        Assert.DoesNotContain("private-server-detail", failure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("bearer-secret", failure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task InitialFailureFallbackRetainsStatusAndDateHeaderButRejectsUnknownErrorCode()
    {
        DateTimeOffset serverNow = new(2026, 9, 30, 0, 0, 0, TimeSpan.Zero);
        using HttpClient client = new(new DelegateHandler(async (_, _) =>
        {
            HttpResponseMessage response = new(HttpStatusCode.TooManyRequests)
            {
                Content = JsonContent.Create(new { code = "private-error-secret", message = "private-message" }),
            };
            response.Headers.Date = serverNow;
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(serverNow.AddSeconds(53));
            return await Task.FromResult(response);
        }));
        FirebaseRealtimeRolloutSelector selector = new(
            new SupabaseRuntimeConfiguration(new Uri("https://example.supabase.co"), "publishable-test-key"),
            new FixedSessionAccessor(Session()), "2.0.0", client);

        FirebaseRealtimeRolloutSelection fallback = await selector.SelectAsync();

        Assert.False(fallback.Enabled);
        FirebaseRealtimeRolloutException failure = Assert.IsType<FirebaseRealtimeRolloutException>(fallback.Failure);
        Assert.Equal(HttpStatusCode.TooManyRequests, failure.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(53), failure.RetryAfter);
        Assert.Equal("http-error", failure.FailureCode);
        Assert.DoesNotContain("private-error-secret", failure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendsExactCapabilityAndCachesEnabledSelectionForServerTtl()
    {
        StoredSupabaseSession session = Session();
        var time = new ManualTimeProvider();
        int requests = 0;
        using var client = new HttpClient(new DelegateHandler(async (request, cancellationToken) =>
        {
            requests++;
            Assert.Equal(
                "https://example.supabase.co/rest/v1/rpc/register_realtime_capability_v2",
                request.RequestUri?.AbsoluteUri);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal(session.AccessToken, request.Headers.Authorization?.Parameter);
            Assert.Equal("publishable-test-key", request.Headers.GetValues("apikey").Single());
            using var body = JsonDocument.Parse(
                await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.Equal(4, body.RootElement.EnumerateObject().Count());
            Assert.Equal("windows", body.RootElement.GetProperty("p_platform").GetString());
            Assert.Equal("2.0.0", body.RootElement.GetProperty("p_app_version").GetString());
            Assert.Equal(2, body.RootElement.GetProperty("p_protocol_version").GetInt32());
            Assert.Equal(
                FirebaseRealtimeRolloutSelector.ContractHash,
                body.RootElement.GetProperty("p_contract_hash").GetString());
            return SelectionResponse(enabled: true, killSwitch: false, ttl: 300);
        }));
        var selector = new FirebaseRealtimeRolloutSelector(
            new SupabaseRuntimeConfiguration(new Uri("https://example.supabase.co"), "publishable-test-key"),
            new FixedSessionAccessor(session),
            "2.0.0",
            client,
            time);

        FirebaseRealtimeRolloutSelection first = await selector.SelectAsync();
        time.Advance(TimeSpan.FromSeconds(299));
        FirebaseRealtimeRolloutSelection cached = await selector.SelectAsync();
        time.Advance(TimeSpan.FromSeconds(1));
        FirebaseRealtimeRolloutSelection refreshed = await selector.SelectAsync();

        Assert.True(first.Enabled);
        Assert.False(first.KillSwitch);
        Assert.Equal(FirebaseRealtimeSelectedTransport.FirebaseV2, first.Transport);
        Assert.Equal(FirebaseRealtimeRolloutSelectionSource.Server, first.Source);
        Assert.Same(first, cached);
        Assert.NotSame(first, refreshed);
        Assert.Equal(2, requests);
    }

    [Fact]
    public async Task ExpiredEnabledSelectionFailsClosedWithoutLeakingSecrets()
    {
        StoredSupabaseSession session = Session();
        var time = new ManualTimeProvider();
        int requests = 0;
        using var client = new HttpClient(new DelegateHandler((request, _) =>
        {
            requests++;
            if (requests == 1)
            {
                return Task.FromResult(SelectionResponse(enabled: true, killSwitch: false, ttl: 30));
            }
            throw new HttpRequestException(
                $"transport-secret {request.Headers.Authorization} {request.RequestUri}");
        }));
        var selector = new FirebaseRealtimeRolloutSelector(
            new SupabaseRuntimeConfiguration(new Uri("https://example.supabase.co"), "publishable-secret-marker"),
            new FixedSessionAccessor(session),
            "2.0.0",
            client,
            time);
        _ = await selector.SelectAsync();
        time.Advance(TimeSpan.FromSeconds(30));

        FirebaseRealtimeRolloutException exception =
            await Assert.ThrowsAsync<FirebaseRealtimeRolloutException>(
                () => selector.SelectAsync().AsTask());

        Assert.True(exception.FailClosed);
        Assert.Equal("Firebase realtime rollout refresh failed closed.", exception.Message);
        Assert.DoesNotContain(session.AccessToken, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("publishable-secret-marker", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("transport-secret", exception.ToString(), StringComparison.Ordinal);
        Assert.Equal(2, requests);
    }

    [Fact]
    public async Task KillSwitchSelectsLegacyAndMalformedRefreshUsesLegacyFailureFallback()
    {
        StoredSupabaseSession session = Session();
        var time = new ManualTimeProvider();
        int requests = 0;
        using var client = new HttpClient(new DelegateHandler((_, _) =>
        {
            requests++;
            return Task.FromResult(requests == 1
                ? SelectionResponse(enabled: false, killSwitch: true, ttl: 30)
                : Json(new
                {
                    enabled = true,
                    protocolVersion = 2,
                    transport = "firebase_v2",
                    contractHash = FirebaseRealtimeRolloutSelector.ContractHash,
                    killSwitch = false,
                    cacheTtlSeconds = 300,
                    failureMode = "fail_closed_if_last_enabled",
                    unexpected = true,
                }));
        }));
        var selector = new FirebaseRealtimeRolloutSelector(
            new SupabaseRuntimeConfiguration(new Uri("https://example.supabase.co"), "publishable-test-key"),
            new FixedSessionAccessor(session),
            "2.0.0",
            client,
            time);

        FirebaseRealtimeRolloutSelection killed = await selector.SelectAsync();
        time.Advance(TimeSpan.FromSeconds(30));
        FirebaseRealtimeRolloutSelection fallback = await selector.SelectAsync();

        Assert.False(killed.Enabled);
        Assert.True(killed.KillSwitch);
        Assert.Equal(FirebaseRealtimeRolloutSelectionSource.Server, killed.Source);
        Assert.False(fallback.Enabled);
        Assert.True(fallback.KillSwitch);
        Assert.Equal(TimeSpan.FromSeconds(30), fallback.CacheTtl);
        Assert.Equal(FirebaseRealtimeRolloutSelectionSource.FailureFallback, fallback.Source);
        Assert.Equal(2, requests);
    }

    [Fact]
    public async Task FailedEnabledRefreshDoesNotReuseStillFreshEnabledCache()
    {
        StoredSupabaseSession session = Session();
        int requests = 0;
        using var client = new HttpClient(new DelegateHandler((_, _) =>
        {
            requests++;
            if (requests == 1)
            {
                return Task.FromResult(SelectionResponse(enabled: true, killSwitch: false, ttl: 300));
            }
            throw new HttpRequestException("offline");
        }));
        var selector = new FirebaseRealtimeRolloutSelector(
            new SupabaseRuntimeConfiguration(new Uri("https://example.supabase.co"), "publishable-test-key"),
            new FixedSessionAccessor(session),
            "2.0.0",
            client);
        _ = await selector.SelectAsync();

        FirebaseRealtimeRolloutException refreshFailure =
            await Assert.ThrowsAsync<FirebaseRealtimeRolloutException>(
                () => selector.RefreshAsync().AsTask());
        FirebaseRealtimeRolloutException cachedSelection =
            await Assert.ThrowsAsync<FirebaseRealtimeRolloutException>(
                () => selector.SelectAsync().AsTask());

        Assert.True(refreshFailure.FailClosed);
        Assert.True(cachedSelection.FailClosed);
        Assert.Equal(2, requests);
    }

    private static HttpResponseMessage SelectionResponse(bool enabled, bool killSwitch, int ttl) => Json(new
    {
        enabled,
        protocolVersion = 2,
        transport = enabled ? "firebase_v2" : "legacy_supabase",
        contractHash = FirebaseRealtimeRolloutSelector.ContractHash,
        killSwitch,
        cacheTtlSeconds = ttl,
        failureMode = "fail_closed_if_last_enabled",
    });

    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(value),
    };

    private static StoredSupabaseSession Session() => new(
        Jwt(new { sub = s_userId, session_id = s_sessionId }),
        "supabase-refresh-token",
        s_userId,
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

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan elapsed) => _timestamp = checked(_timestamp + elapsed.Ticks);
    }

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
    }
}
