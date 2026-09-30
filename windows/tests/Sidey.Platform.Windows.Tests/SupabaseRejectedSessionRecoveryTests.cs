using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Sidey.Core.Abstractions;
using Sidey.Core.Domain;
using Sidey.Core.Realtime;
using Sidey.Infrastructure.Authentication;
using Sidey.Infrastructure.Backend;
using Sidey.Infrastructure.Configuration;
using Sidey.Infrastructure.Realtime;

namespace Sidey.Platform.Windows.Tests;

public sealed class SupabaseRejectedSessionRecoveryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnauthorizedReadRefreshesOnceAndDoesNotLoopOnRepeatedRejection(bool rejectAgain)
    {
        var user = Guid.NewGuid();
        var store = new SessionStore(user);
        var handler = new ReadRefreshHandler(user, rejectAgain);
        using var http = new HttpClient(handler);
        using var auth = new SupabaseAnonymousAuthService(Configuration(), store, http);
        await using var gateway = new SupabaseBackendGateway(Configuration(), auth, store, http, new IdleTransport());
        if (rejectAgain)
        {
            RealtimeRestRequestException failure = await Assert.ThrowsAsync<RealtimeRestRequestException>(() => gateway.FetchRecentMessagesAsync(Guid.NewGuid()));
            Assert.Equal(HttpStatusCode.Unauthorized, failure.StatusCode);
            Assert.False(RealtimeRestRetryPolicy.IsTransient(failure));
        }
        else
            Assert.Empty(await gateway.FetchRecentMessagesAsync(Guid.NewGuid()));
        Assert.Equal(1, handler.Refreshes);
        Assert.Equal(2, handler.Reads);
    }

    [Fact]
    public async Task RateLimitedReadDoesNotWaitForErrorBodyAndPreservesRetryAfter()
    {
        var store = new SessionStore(Guid.NewGuid());
        using var http = new HttpClient(new RateLimitedReadHandler());
        using var auth = new SupabaseAnonymousAuthService(Configuration(), store, http);
        await using var gateway = new SupabaseBackendGateway(Configuration(), auth, store, http, new IdleTransport());
        RealtimeRestRequestException failure = await Assert.ThrowsAsync<RealtimeRestRequestException>(() =>
            gateway.FetchRecentMessagesAsync(Guid.NewGuid()).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(TimeSpan.FromSeconds(120), failure.RetryAfter);
    }

    [Fact]
    public async Task ConcurrentRejectedTokenRecoveryRefreshesOnceAndPreservesIdentity()
    {
        var user = Guid.NewGuid();
        var store = new SessionStore(user);
        var handler = new RefreshHandler(user) { Block = true };
        using var http = new HttpClient(handler);
        using var auth = new SupabaseAnonymousAuthService(Configuration(), store, http);
        IAuthSessionAccessor sessions = auth;
        Task[] callers = [.. Enumerable.Range(0, 20)
            .Select(_ => sessions.RecoverRejectedSessionAsync("old-token", CancellationToken.None).AsTask())];
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, handler.Requests);
        handler.Release.TrySetResult();
        await Task.WhenAll(callers).WaitAsync(TimeSpan.FromSeconds(5));
        StoredSupabaseSession session = Assert.IsType<StoredSupabaseSession>(await sessions.GetStoredSessionAsync());
        Assert.Equal("new-token", session.AccessToken);
        Assert.Equal(user, session.UserId);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task RefreshWithDifferentIdentityDoesNotReplaceStoredSession()
    {
        var user = Guid.NewGuid();
        var store = new SessionStore(user);
        var handler = new RefreshHandler(Guid.NewGuid());
        using var http = new HttpClient(handler);
        using var auth = new SupabaseAnonymousAuthService(Configuration(), store, http);
        IAuthSessionAccessor sessions = auth;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            sessions.RecoverRejectedSessionAsync("old-token", CancellationToken.None).AsTask());
        StoredSupabaseSession session = Assert.IsType<StoredSupabaseSession>(await sessions.GetStoredSessionAsync());
        Assert.Equal(user, session.UserId);
        Assert.Equal("old-token", session.AccessToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransientRefreshFailurePreservesRetryAfterForDirtyRoomRecovery(bool unreadableErrorBody)
    {
        var store = new SessionStore(Guid.NewGuid());
        var handler = new RefreshHandler(Guid.NewGuid()) { Reject = true, UnreadableErrorBody = unreadableErrorBody };
        using var http = new HttpClient(handler);
        using var auth = new SupabaseAnonymousAuthService(Configuration(), store, http);
        RealtimeRestRequestException failure = await Assert.ThrowsAsync<RealtimeRestRequestException>(() =>
            ((IAuthSessionAccessor)auth).RecoverRejectedSessionAsync("old-token", CancellationToken.None).AsTask());
        Assert.True(RealtimeRestRetryPolicy.IsTransient(failure));
        Assert.Equal(TimeSpan.FromSeconds(120), failure.RetryAfter);
        Assert.True(RealtimeRestRetryPolicy.Delay(1, failure) >= TimeSpan.FromSeconds(120));
    }

    private static SupabaseRuntimeConfiguration Configuration() => new(new Uri("https://test.supabase.co"), "test-key");

    private sealed class ReadRefreshHandler(Guid user, bool rejectAgain) : HttpMessageHandler
    {
        public int Refreshes { get; private set; }
        public int Reads { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.StartsWith("/auth/", StringComparison.Ordinal))
            {
                Refreshes++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { access_token = "new-token", refresh_token = "new-refresh", expires_in = 3600, user = new { id = user } }),
                });
            }
            Reads++;
            Assert.Equal(Reads == 1 ? "old-token" : "new-token", request.Headers.Authorization!.Parameter);
            return Task.FromResult(new HttpResponseMessage(Reads == 1 || rejectAgain ? HttpStatusCode.Unauthorized : HttpStatusCode.OK)
            {
                Content = JsonContent.Create(Array.Empty<object>()),
            });
        }
    }

    private sealed class RateLimitedReadHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new UnreadableErrorContent() };
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
            return Task.FromResult(response);
        }
    }

    private sealed class UnreadableErrorContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
            throw new InvalidOperationException("Error-body buffering must not precede HTTP failure classification.");
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    private sealed class IdleTransport : IRealtimeTransport
    {
        public RealtimeConnectionStatus ConnectionStatus => RealtimeConnectionStatus.Disconnected;
        public bool IsRecoveryPaused => false;
        public IAsyncEnumerable<BackendEvent> ReadEventsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SynchronizeAsync(IReadOnlyDictionary<Guid, long> roomEpochs, Guid? activeRoomId, PresenceState localPresence, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task PublishPresenceAsync(Guid roomId, PresenceState state, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<bool> RunWhileConnectedAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken) => Task.FromResult(false);
        public void RequestReconnect(bool userInitiated = false) { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RefreshHandler(Guid user) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public bool Block { get; init; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Reject { get; init; }
        public bool UnreadableErrorBody { get; init; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            Started.TrySetResult();
            if (Block)
                await Release.Task.WaitAsync(cancellationToken);
            Assert.Equal("?grant_type=refresh_token", request.RequestUri!.Query);
            var response = new HttpResponseMessage(Reject ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            {
                Content = UnreadableErrorBody ? new UnreadableErrorContent() : JsonContent.Create(new { access_token = "new-token", refresh_token = "new-refresh", expires_in = 3600, user = new { id = user } }),
            };
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
            return response;
        }
    }

    private sealed class SessionStore(Guid user) : ICredentialStore
    {
        private string _value = JsonSerializer.Serialize(new StoredSupabaseSession("old-token", "old-refresh", user, DateTimeOffset.UtcNow.AddHours(1)), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        public ValueTask<string?> ReadAsync(CredentialKey key, CancellationToken cancellationToken = default) => ValueTask.FromResult<string?>(key == CredentialKey.SupabaseSession ? _value : null);
        public ValueTask WriteAsync(CredentialKey key, string value, CancellationToken cancellationToken = default) { _value = value; return ValueTask.CompletedTask; }
        public ValueTask DeleteAsync(CredentialKey key, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask<string?> ReadInviteCodeAsync(Guid roomId, CancellationToken cancellationToken = default) => ValueTask.FromResult<string?>(null);
        public ValueTask WriteInviteCodeAsync(Guid roomId, string inviteCode, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask DeleteInviteCodeAsync(Guid roomId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
