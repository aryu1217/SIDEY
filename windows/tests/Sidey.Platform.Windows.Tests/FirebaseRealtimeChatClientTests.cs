using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Sidey.Infrastructure.Authentication;
using Sidey.Infrastructure.Realtime;

namespace Sidey.Platform.Windows.Tests;

public sealed class FirebaseRealtimeChatClientTests
{
    private static readonly Guid s_userId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
    private static readonly Guid s_sessionId = Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");
    private static readonly Guid s_roomId = Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc");
    private static readonly Guid s_messageId = Guid.Parse("dddddddd-dddd-4ddd-8ddd-dddddddddddd");

    [Fact]
    public async Task SendsExactCallableEnvelopeAndParsesStrictSuccessResult()
    {
        const string IdToken = "firebase-id-token-secret";
        int requests = 0;
        using var client = new HttpClient(new DelegateHandler(async (request, cancellationToken) =>
        {
            requests++;
            Assert.Equal(
                "https://asia-southeast1-sidey-realtime.cloudfunctions.net/sendRealtimeChat",
                request.RequestUri?.AbsoluteUri);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal(IdToken, request.Headers.Authorization?.Parameter);
            Assert.Equal("application/json", request.Content?.Headers.ContentType?.MediaType);
            using var body = JsonDocument.Parse(
                await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.Single(body.RootElement.EnumerateObject());
            JsonElement data = body.RootElement.GetProperty("data");
            Assert.Equal(3, data.EnumerateObject().Count());
            Assert.Equal("hello", data.GetProperty("b").GetString());
            Assert.Equal(s_messageId, data.GetProperty("i").GetGuid());
            Assert.Equal(s_roomId, data.GetProperty("r").GetGuid());
            return Json(HttpStatusCode.OK, new
            {
                result = new
                {
                    b = "hello",
                    i = s_messageId,
                    k = "2",
                    n = 42,
                    t = 1_790_000_000_123,
                },
            });
        }));
        var chat = new FirebaseRealtimeChatClient(
            new FixedCredentialProvider(Credential(IdToken)),
            client);

        FirebaseRealtimeChatResult result = await chat.SendAsync(
            s_messageId,
            s_roomId,
            "  hello  ");

        Assert.Equal(s_messageId, result.MessageId);
        Assert.Equal(s_roomId, result.RoomId);
        Assert.Equal(s_userId, result.SenderId);
        Assert.Equal("hello", result.Body);
        Assert.Equal(42, result.Sequence);
        Assert.Equal(1_790_000_000_123, result.TimestampMilliseconds);
        Assert.Equal("2", result.BubbleWireCode);
        Assert.DoesNotContain(result.Body, result.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, requests);
    }

    [Theory]
    [InlineData("UNAUTHENTICATED", HttpStatusCode.Unauthorized, "unauthenticated")]
    [InlineData("PERMISSION_DENIED", HttpStatusCode.Forbidden, "permission-denied")]
    [InlineData("RESOURCE_EXHAUSTED", HttpStatusCode.TooManyRequests, "resource-exhausted")]
    [InlineData("FAILED_PRECONDITION", HttpStatusCode.BadRequest, "failed-precondition")]
    [InlineData("INVALID_ARGUMENT", HttpStatusCode.BadRequest, "invalid-argument")]
    public async Task CallableKnownRejectIsClassifiedAsNonCommit(
        string status,
        HttpStatusCode httpStatus,
        string expectedCode)
    {
        int requests = 0;
        using var client = new HttpClient(new DelegateHandler((_, _) =>
        {
            requests++;
            return Task.FromResult(Json(httpStatus, new
            {
                error = new
                {
                    status,
                    message = "server-message-secret-body",
                    details = new { body = "secret-chat-body" },
                },
            }));
        }));
        var chat = new FirebaseRealtimeChatClient(
            new FixedCredentialProvider(Credential("firebase-id-token-secret")),
            client);

        FirebaseRealtimeChatException exception =
            await Assert.ThrowsAsync<FirebaseRealtimeChatException>(
                () => chat.SendAsync(s_messageId, s_roomId, "hello").AsTask());

        Assert.Equal(expectedCode, exception.Code);
        Assert.Equal(FirebaseRealtimeChatFailureClassification.KnownNonCommit, exception.Classification);
        Assert.Equal(httpStatus, exception.StatusCode);
        Assert.DoesNotContain("server-message-secret-body", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret-chat-body", exception.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, requests);
    }

    [Theory]
    [InlineData("DEADLINE_EXCEEDED", "deadline-exceeded")]
    [InlineData("INTERNAL", "internal")]
    [InlineData("UNAVAILABLE", "unavailable")]
    [InlineData("UNKNOWN_FUTURE_CODE", "unavailable")]
    public async Task CallableAmbiguousErrorIsNeverRetried(string status, string expectedCode)
    {
        int requests = 0;
        using var client = new HttpClient(new DelegateHandler((_, _) =>
        {
            requests++;
            return Task.FromResult(Json(HttpStatusCode.ServiceUnavailable, new
            {
                error = new { status, message = "secret-chat-body" },
            }));
        }));
        var chat = new FirebaseRealtimeChatClient(
            new FixedCredentialProvider(Credential("firebase-id-token-secret")),
            client);

        FirebaseRealtimeChatException exception =
            await Assert.ThrowsAsync<FirebaseRealtimeChatException>(
                () => chat.SendAsync(s_messageId, s_roomId, "hello").AsTask());

        Assert.Equal(expectedCode, exception.Code);
        Assert.Equal(FirebaseRealtimeChatFailureClassification.CommitAmbiguous, exception.Classification);
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task TransportFailureIsAmbiguousSecretSafeAndNeverRetried()
    {
        const string IdToken = "firebase-id-token-secret";
        int requests = 0;
        using var client = new HttpClient(new DelegateHandler((request, _) =>
        {
            requests++;
            throw new HttpRequestException(
                $"transport-secret {request.Headers.Authorization} secret-chat-body");
        }));
        var chat = new FirebaseRealtimeChatClient(
            new FixedCredentialProvider(Credential(IdToken)),
            client);

        FirebaseRealtimeChatException exception =
            await Assert.ThrowsAsync<FirebaseRealtimeChatException>(
                () => chat.SendAsync(s_messageId, s_roomId, "secret-chat-body").AsTask());

        Assert.Equal("transport-error", exception.Code);
        Assert.Equal(FirebaseRealtimeChatFailureClassification.CommitAmbiguous, exception.Classification);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain(IdToken, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret-chat-body", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("transport-secret", exception.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task MismatchedSuccessEchoIsAmbiguousAndNeverRetried()
    {
        int requests = 0;
        using var client = new HttpClient(new DelegateHandler((_, _) =>
        {
            requests++;
            return Task.FromResult(Json(HttpStatusCode.OK, new
            {
                result = new
                {
                    b = "different-body",
                    i = s_messageId,
                    n = 1,
                    t = 1_790_000_000_123,
                },
            }));
        }));
        var chat = new FirebaseRealtimeChatClient(
            new FixedCredentialProvider(Credential("firebase-id-token-secret")),
            client);

        FirebaseRealtimeChatException exception =
            await Assert.ThrowsAsync<FirebaseRealtimeChatException>(
                () => chat.SendAsync(s_messageId, s_roomId, "hello").AsTask());

        Assert.Equal("transport-error", exception.Code);
        Assert.Equal(FirebaseRealtimeChatFailureClassification.CommitAmbiguous, exception.Classification);
        Assert.Equal(1, requests);
    }

    private static FirebaseRealtimeCredential Credential(string idToken) => new(
        s_userId,
        s_sessionId,
        idToken,
        new Uri("https://sidey.asia-southeast1.firebasedatabase.app"),
        generation: 1);

    private static HttpResponseMessage Json(HttpStatusCode status, object value) => new(status)
    {
        Content = JsonContent.Create(value),
    };

    private sealed class FixedCredentialProvider(FirebaseRealtimeCredential credential)
        : IFirebaseRealtimeCredentialProvider
    {
        public ValueTask<FirebaseRealtimeCredential> GetCredentialAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(credential);
        }

        public ValueTask ResetAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
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
