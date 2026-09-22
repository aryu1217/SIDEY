using System.Net;
using System.Text;

namespace Sidey.Platform.Windows.Tests;

public sealed class FirebaseRtdbUrlBuilderTests
{
    [Fact]
    public void DatabasePathsAreEncodedOnceAndAlwaysEndInJson()
    {
        var urls = new FirebaseRtdbUrlBuilder(new Uri("https://sidey.firebaseio.com/"));

        Uri uri = urls.Build("/v2/l/room name/친구", "token+/?", silent: true);

        Assert.Equal(
            "v2/l/room%20name/%EC%B9%9C%EA%B5%AC.json",
            uri.GetComponents(UriComponents.Path, UriFormat.UriEscaped));
        Assert.Equal("auth=token%2B%2F%3F&print=silent", uri.Query.TrimStart('?'));
        Assert.DoesNotContain("token", FirebaseRtdbUrlBuilder.Redact(uri), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("/v2//room")]
    [InlineData("/v2/room/")]
    [InlineData("/v2/invalid.key")]
    [InlineData("/v2/$secret")]
    public void MalformedOrInvalidDatabasePathsAreRejected(string path)
    {
        var urls = new FirebaseRtdbUrlBuilder(new Uri("https://sidey.firebaseio.com/"));

        Assert.ThrowsAny<ArgumentException>(() => urls.Build(path, "token"));
    }

    [Theory]
    [InlineData("http://sidey.firebaseio.com/")]
    [InlineData("https://sidey.firebaseio.com:8443/")]
    [InlineData("https://user@sidey.firebaseio.com/")]
    [InlineData("https://sidey.firebaseio.com/database")]
    [InlineData("https://sidey.firebaseio.com/?auth=token")]
    public void DatabaseOriginMustBeAnHttpsOrigin(string value)
    {
        Assert.Throws<ArgumentException>(() => new FirebaseRtdbUrlBuilder(new Uri(value)));
    }

    [Fact]
    public async Task AllowedRedirectPreservesMethodBodyAndCredentialOnlyAfterValidation()
    {
        var server = new FakeFirebaseServer((requestNumber, _) => requestNumber switch
        {
            1 => Redirect("https://regional.firebasedatabase.app/v2/t/user.json"),
            2 => new HttpResponseMessage(HttpStatusCode.NoContent),
            _ => throw new InvalidOperationException("Unexpected request."),
        });
        var urls = new FirebaseRtdbUrlBuilder(
            new Uri("https://sidey.firebaseio.com/"),
            ["regional.firebasedatabase.app"]);
        await using var client = FirebaseRtdbRestClient.CreateForTesting(
            urls,
            server.SendRawAsync);

        await client.WriteAsync(
            HttpMethod.Put,
            "/v2/t/user",
            "id-token",
            Encoding.UTF8.GetBytes("{\"typing\":true}"));

        Assert.Equal(2, server.Requests.Count);
        Assert.All(server.Requests, request => Assert.Equal(HttpMethod.Put, request.Method));
        Assert.All(server.Requests, request => Assert.Equal("{\"typing\":true}", request.Body));
        Assert.Equal("sidey.firebaseio.com", server.Requests[0].Uri.Host);
        Assert.Equal("regional.firebasedatabase.app", server.Requests[1].Uri.Host);
        Assert.All(server.Requests, request => Assert.Contains("auth=id-token", request.Uri.Query, StringComparison.Ordinal));
        Assert.All(server.Requests, request => Assert.Contains("print=silent", request.Uri.Query, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("https://attacker.example/v2/t/user.json")]
    [InlineData("http://sidey.firebaseio.com/v2/t/user.json")]
    [InlineData("https://user@sidey.firebaseio.com/v2/t/user.json")]
    public async Task UnsafeRedirectIsRejectedBeforeCredentialForwarding(string location)
    {
        var server = new FakeFirebaseServer((_, _) => Redirect(location));
        var urls = new FirebaseRtdbUrlBuilder(new Uri("https://sidey.firebaseio.com/"));
        await using var client = FirebaseRtdbRestClient.CreateForTesting(
            urls,
            server.SendRawAsync);

        FirebaseRtdbRequestException failure = await Assert.ThrowsAsync<FirebaseRtdbRequestException>(
            () => client.WriteAsync(HttpMethod.Delete, "/v2/t/user", "secret-token", null));

        Assert.Equal(FirebaseRtdbFailureKind.RedirectRejected, failure.FailureKind);
        Assert.Single(server.Requests);
        Assert.DoesNotContain("secret-token", failure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RedirectLoopAndAccessDenialDoNotReplayWrites()
    {
        var loopServer = new FakeFirebaseServer((_, request) => Redirect(request.RequestUri!.AbsoluteUri));
        var urls = new FirebaseRtdbUrlBuilder(new Uri("https://sidey.firebaseio.com/"));
        await using var loopClient = FirebaseRtdbRestClient.CreateForTesting(
            urls,
            loopServer.SendRawAsync);

        FirebaseRtdbRequestException loop = await Assert.ThrowsAsync<FirebaseRtdbRequestException>(
            () => loopClient.WriteAsync(HttpMethod.Put, "/v2/t/user", "token", "{}"u8.ToArray()));

        Assert.Equal(FirebaseRtdbFailureKind.RedirectRejected, loop.FailureKind);
        Assert.Single(loopServer.Requests);

        var denialServer = new FakeFirebaseServer((_, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        await using var denialClient = FirebaseRtdbRestClient.CreateForTesting(
            urls,
            denialServer.SendRawAsync);

        FirebaseRtdbRequestException denial = await Assert.ThrowsAsync<FirebaseRtdbRequestException>(
            () => denialClient.WriteAsync(HttpMethod.Put, "/v2/t/user", "token", "{}"u8.ToArray()));

        Assert.Equal(FirebaseRtdbFailureKind.AccessDenied, denial.FailureKind);
        Assert.Single(denialServer.Requests);
    }

    [Fact]
    public async Task DeleteDoesNotUseUnsupportedSilentPrintAndAcceptsNormalSuccess()
    {
        var server = new FakeFirebaseServer((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("null"),
        });
        var urls = new FirebaseRtdbUrlBuilder(new Uri("https://sidey.firebaseio.com/"));
        await using var client = FirebaseRtdbRestClient.CreateForTesting(
            urls,
            server.SendRawAsync);

        await client.WriteAsync(HttpMethod.Delete, "/v2/t/user", "token", null);

        CapturedRequest request = Assert.Single(server.Requests);
        Assert.DoesNotContain("print=silent", request.Uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PutRejectsAnEmptyJsonBodyBeforeSending()
    {
        var server = new FakeFirebaseServer((_, _) => new HttpResponseMessage(HttpStatusCode.NoContent));
        var urls = new FirebaseRtdbUrlBuilder(new Uri("https://sidey.firebaseio.com/"));
        await using var client = FirebaseRtdbRestClient.CreateForTesting(
            urls,
            server.SendRawAsync);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.WriteAsync(HttpMethod.Put, "/v2/t/user", "token", ReadOnlyMemory<byte>.Empty));

        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task TransportExceptionDoesNotRetainCredentializedUri()
    {
        var server = new FakeFirebaseServer((_, request) => throw new HttpRequestException(
            $"failed request {request.RequestUri}"));
        var urls = new FirebaseRtdbUrlBuilder(new Uri("https://sidey.firebaseio.com/"));
        await using var client = FirebaseRtdbRestClient.CreateForTesting(
            urls,
            server.SendRawAsync);

        FirebaseRtdbRequestException failure = await Assert.ThrowsAsync<FirebaseRtdbRequestException>(
            () => client.WriteAsync(HttpMethod.Put, "/v2/t/user", "secret-token", "{}"u8.ToArray()));

        Assert.Equal(FirebaseRtdbFailureKind.Transport, failure.FailureKind);
        Assert.DoesNotContain("secret-token", failure.ToString(), StringComparison.Ordinal);
        Assert.Null(failure.InnerException);
    }

    [Fact]
    public async Task ExcessiveRedirectsStopAtTheBoundedLimit()
    {
        var server = new FakeFirebaseServer((requestNumber, _) =>
            Redirect($"https://sidey.firebaseio.com/v2/redirect-{requestNumber}.json"));
        var urls = new FirebaseRtdbUrlBuilder(new Uri("https://sidey.firebaseio.com/"));
        await using var client = FirebaseRtdbRestClient.CreateForTesting(
            urls,
            server.SendRawAsync);

        FirebaseRtdbRequestException failure = await Assert.ThrowsAsync<FirebaseRtdbRequestException>(
            () => client.WriteAsync(HttpMethod.Put, "/v2/t/user", "token", "{}"u8.ToArray()));

        Assert.Equal(FirebaseRtdbFailureKind.RedirectRejected, failure.FailureKind);
        Assert.Equal(4, server.Requests.Count);
    }

    [Fact]
    public async Task EventStreamUsesSseAcceptHeaderAndConsumesChunkedUtf8()
    {
        const string Payload = "event: put\r\ndata: {\"path\":\"/상태\",\"data\":\"온라인\"}\r\n\r\n";
        var server = new FakeFirebaseServer((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new OneByteReadStream(Encoding.UTF8.GetBytes(Payload))),
        });
        var urls = new FirebaseRtdbUrlBuilder(new Uri("https://sidey.firebaseio.com/"));
        await using var client = FirebaseRtdbRestClient.CreateForTesting(
            urls,
            server.SendRawAsync);
        await using FirebaseRtdbEventStream stream = await client.OpenEventStreamAsync(
            "/v2/l/room",
            "token");

        var events = new List<FirebaseSseEvent>();
        await foreach (FirebaseSseEvent parsed in stream.ReadEventsAsync())
        {
            events.Add(parsed);
        }

        FirebaseSseEvent put = Assert.Single(events);
        Assert.Equal(FirebaseSseEventKind.Put, put.Kind);
        Assert.Equal("/상태", put.GetMutation().Path);
        Assert.Contains("text/event-stream", server.Requests.Single().Accept, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EventStreamReleasesResponseWhenStreamDisposalFails()
    {
        var content = new TrackingContent();
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = content,
        };
        var stream = new ThrowingDisposeStream();
        var eventStream = new FirebaseRtdbEventStream(response, stream);

        await Assert.ThrowsAsync<InvalidOperationException>(() => eventStream.DisposeAsync().AsTask());

        Assert.True(content.IsDisposed);
        await eventStream.DisposeAsync();
    }

    private static HttpResponseMessage Redirect(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
        response.Headers.Location = new Uri(location);
        return response;
    }

    private sealed record CapturedRequest(HttpMethod Method, Uri Uri, string Body, string Accept);

    private sealed class FakeFirebaseServer(
        Func<int, HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        public Task<HttpResponseMessage> SendRawAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            SendAsync(request, cancellationToken);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new CapturedRequest(
                request.Method,
                request.RequestUri!,
                body,
                string.Join(',', request.Headers.Accept.Select(value => value.MediaType))));
            return respond(Requests.Count, request);
        }
    }

    private sealed class OneByteReadStream(byte[] bytes) : Stream
    {
        private int _offset;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => bytes.Length;
        public override long Position { get => _offset; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_offset == bytes.Length || buffer.Length == 0)
            {
                return 0;
            }

            buffer[0] = bytes[_offset++];
            return 1;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Read(buffer.Span));

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class TrackingContent : HttpContent
    {
        public bool IsDisposed { get; private set; }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            Task.CompletedTask;

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return true;
        }

        protected override void Dispose(bool disposing)
        {
            IsDisposed = disposing;
            base.Dispose(disposing);
        }
    }

    private sealed class ThrowingDisposeStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 0;
        public override long Position { get => 0; set => throw new NotSupportedException(); }

        public override ValueTask DisposeAsync() =>
            ValueTask.FromException(new InvalidOperationException("dispose failed"));

        public override int Read(byte[] buffer, int offset, int count) => 0;
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
