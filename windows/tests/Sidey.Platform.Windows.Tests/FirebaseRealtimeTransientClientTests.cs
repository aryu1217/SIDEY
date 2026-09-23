using System.Net;
using Sidey.Infrastructure.Authentication;
using Sidey.Infrastructure.Realtime;

namespace Sidey.Platform.Windows.Tests;

public sealed class FirebaseRealtimeTransientClientTests
{
    private static readonly Guid s_roomId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid s_userId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid s_sessionId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly Guid s_targetUserId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");

    [Fact]
    public async Task PublishesTypingPulseAndThrowToCompactFirebasePathsOnly()
    {
        List<CapturedWrite> writes = [];
        var credential = new FirebaseRealtimeCredential(
            s_userId,
            s_sessionId,
            "secret-token",
            new Uri("https://sidey.asia-southeast1.firebasedatabase.app"),
            generation: 1,
            wireItems: ["18"]);
        var client = new FirebaseRealtimeTransientClient(
            new FakeCredentialProvider(credential),
            databaseUrl => FirebaseRtdbRestClient.CreateForTesting(
                new FirebaseRtdbUrlBuilder(databaseUrl),
                async (request, cancellationToken) =>
                {
                    string? body = request.Content is null
                        ? null
                        : await request.Content.ReadAsStringAsync(cancellationToken);
                    writes.Add(new CapturedWrite(
                        request.Method,
                        request.RequestUri!.AbsolutePath,
                        body));
                    return new HttpResponseMessage(HttpStatusCode.OK);
                }));

        await client.PublishTypingAsync(s_roomId, active: true, CancellationToken.None);
        await client.PublishTypingAsync(s_roomId, active: false, CancellationToken.None);
        await client.PublishCharacterPulseAsync(s_roomId, CancellationToken.None);
        await client.PublishCharacterThrowAsync(
            s_roomId,
            s_targetUserId,
            "18",
            CancellationToken.None);

        Assert.Collection(
            writes,
            write => AssertWrite(write, HttpMethod.Put,
                $"/v2/l/{s_roomId:D}/t/{s_userId:D}/{s_sessionId:D}.json",
                "{\".sv\":\"timestamp\"}"),
            write => AssertWrite(write, HttpMethod.Delete,
                $"/v2/l/{s_roomId:D}/t/{s_userId:D}/{s_sessionId:D}.json",
                expectedBody: null),
            write => AssertWrite(write, HttpMethod.Put,
                $"/v2/l/{s_roomId:D}/c/{s_userId:D}.json",
                "{\".sv\":\"timestamp\"}"),
            write =>
            {
                Assert.Equal(HttpMethod.Put, write.Method);
                Assert.Equal($"/v2/l/{s_roomId:D}/x/{s_userId:D}.json", write.Path);
                Assert.Contains($"\"u\":\"{s_targetUserId:D}\"", write.Body, StringComparison.Ordinal);
                Assert.Contains("\"k\":\"18\"", write.Body, StringComparison.Ordinal);
                Assert.Contains("\".sv\":\"timestamp\"", write.Body, StringComparison.Ordinal);
            });
    }

    [Fact]
    public async Task UnauthorizedThrowableFailsBeforeFirebaseWrite()
    {
        int writeCount = 0;
        var credential = new FirebaseRealtimeCredential(
            s_userId,
            s_sessionId,
            "secret-token",
            new Uri("https://sidey.asia-southeast1.firebasedatabase.app"),
            generation: 1,
            wireItems: []);
        var client = new FirebaseRealtimeTransientClient(
            new FakeCredentialProvider(credential),
            databaseUrl => FirebaseRtdbRestClient.CreateForTesting(
                new FirebaseRtdbUrlBuilder(databaseUrl),
                (_, _) =>
                {
                    writeCount++;
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
                }));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.PublishCharacterThrowAsync(
                s_roomId,
                s_targetUserId,
                "18",
                CancellationToken.None));
        Assert.Equal(0, writeCount);
    }

    private static void AssertWrite(
        CapturedWrite write,
        HttpMethod method,
        string path,
        string? expectedBody)
    {
        Assert.Equal(method, write.Method);
        Assert.Equal(path, write.Path);
        Assert.Equal(expectedBody, write.Body);
    }

    private sealed record CapturedWrite(HttpMethod Method, string Path, string? Body);

    private sealed class FakeCredentialProvider(FirebaseRealtimeCredential credential)
        : IFirebaseRealtimeCredentialProvider
    {
        public ValueTask<FirebaseRealtimeCredential> GetCredentialAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(credential);

        public ValueTask ResetAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }
}
