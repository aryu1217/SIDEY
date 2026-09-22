using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;

namespace Sidey.Infrastructure.Realtime;

internal sealed class FirebaseRtdbRestClient : IAsyncDisposable
{
    private const int MaximumRedirects = 3;

    private readonly FirebaseRtdbUrlBuilder _urls;
    private readonly HttpClient? _httpClient;
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _sendAsync;

    public FirebaseRtdbRestClient(FirebaseRtdbUrlBuilder urls)
    {
        _urls = urls ?? throw new ArgumentNullException(nameof(urls));
        _httpClient = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
        });
        _sendAsync = (request, cancellationToken) => _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
    }

    private FirebaseRtdbRestClient(
        FirebaseRtdbUrlBuilder urls,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> testSender)
    {
        _urls = urls ?? throw new ArgumentNullException(nameof(urls));
        _sendAsync = testSender ?? throw new ArgumentNullException(nameof(testSender));
    }

    internal static FirebaseRtdbRestClient CreateForTesting(
        FirebaseRtdbUrlBuilder urls,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> testSender) =>
        new(urls, testSender);

    public async Task WriteAsync(
        HttpMethod method,
        string databasePath,
        string idToken,
        ReadOnlyMemory<byte>? jsonBody,
        CancellationToken cancellationToken = default)
    {
        if (method != HttpMethod.Put && method != HttpMethod.Delete)
        {
            throw new ArgumentException("Firebase transient writes support only PUT and DELETE.", nameof(method));
        }

        if ((method == HttpMethod.Put && (!jsonBody.HasValue || jsonBody.Value.IsEmpty))
            || (method == HttpMethod.Delete && jsonBody.HasValue))
        {
            throw new ArgumentException("Firebase PUT requires JSON and DELETE must not include a body.", nameof(jsonBody));
        }

        byte[]? body = jsonBody?.ToArray();
        using HttpResponseMessage response = await SendFollowingRedirectsAsync(
            _urls.Build(databasePath, idToken, silent: method == HttpMethod.Put),
            target => CreateWriteRequest(method, target, body),
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
    }

    public async Task<FirebaseRtdbEventStream> OpenEventStreamAsync(
        string databasePath,
        string idToken,
        CancellationToken cancellationToken = default)
    {
        HttpResponseMessage response = await SendFollowingRedirectsAsync(
            _urls.Build(databasePath, idToken),
            CreateEventStreamRequest,
            cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureSuccess(response);
            Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return new FirebaseRtdbEventStream(response, stream);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            response.Dispose();
            throw;
        }
        catch (FirebaseRtdbRequestException)
        {
            response.Dispose();
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException
            or OperationCanceledException)
        {
            response.Dispose();
            throw new FirebaseRtdbRequestException(
                FirebaseRtdbFailureKind.Transport,
                statusCode: null,
                "Firebase RTDB event stream failed to open.");
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        _httpClient?.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task<HttpResponseMessage> SendFollowingRedirectsAsync(
        Uri initialTarget,
        Func<Uri, HttpRequestMessage> createRequest,
        CancellationToken cancellationToken)
    {
        Uri target = initialTarget;
        var visited = new HashSet<string>(StringComparer.Ordinal)
        {
            FirebaseRtdbUrlBuilder.RedirectIdentity(target),
        };

        for (int redirectCount = 0; ; redirectCount++)
        {
            using HttpRequestMessage request = createRequest(target);
            HttpResponseMessage response;
            try
            {
                response = await _sendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException
                or OperationCanceledException)
            {
                // HttpClient exception messages can include the request URI. Do not retain the
                // original exception because Firebase credentials are carried in its query.
                throw new FirebaseRtdbRequestException(
                    FirebaseRtdbFailureKind.Transport,
                    statusCode: null,
                    "Firebase RTDB transport failed.");
            }

            if (response.StatusCode != HttpStatusCode.TemporaryRedirect)
            {
                return response;
            }

            try
            {
                if (redirectCount >= MaximumRedirects || response.Headers.Location is null)
                {
                    throw new FirebaseRtdbRequestException(
                        FirebaseRtdbFailureKind.RedirectRejected,
                        response.StatusCode,
                        "Firebase redirect limit was exceeded or Location was missing.");
                }

                Uri validated = _urls.ValidateRedirect(target, response.Headers.Location, visited);
                var credentialized = new UriBuilder(validated)
                {
                    Query = target.Query.TrimStart('?'),
                    Fragment = string.Empty,
                };
                target = credentialized.Uri;
                if (!visited.Add(FirebaseRtdbUrlBuilder.RedirectIdentity(target)))
                {
                    throw new FirebaseRtdbRequestException(
                        FirebaseRtdbFailureKind.RedirectRejected,
                        response.StatusCode,
                        "Firebase redirect loop detected.");
                }
            }
            catch (FirebaseRtdbRequestException)
            {
                throw;
            }
            catch (Exception exception) when (exception is InvalidDataException or UriFormatException)
            {
                throw new FirebaseRtdbRequestException(
                    FirebaseRtdbFailureKind.RedirectRejected,
                    response.StatusCode,
                    "Firebase redirect target was rejected.",
                    exception);
            }
            finally
            {
                response.Dispose();
            }
        }
    }

    private static HttpRequestMessage CreateWriteRequest(HttpMethod method, Uri target, byte[]? body)
    {
        var request = new HttpRequestMessage(method, target);
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
            {
                CharSet = "utf-8",
            };
        }

        return request;
    }

    private static HttpRequestMessage CreateEventStreamRequest(Uri target)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, target);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        return request;
    }

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        FirebaseRtdbFailureKind kind = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => FirebaseRtdbFailureKind.AccessDenied,
            HttpStatusCode.TooManyRequests => FirebaseRtdbFailureKind.RateLimited,
            >= HttpStatusCode.InternalServerError => FirebaseRtdbFailureKind.Server,
            _ => FirebaseRtdbFailureKind.Protocol,
        };
        throw new FirebaseRtdbRequestException(
            kind,
            response.StatusCode,
            $"Firebase RTDB request failed with status {(int)response.StatusCode}.");
    }
}

internal sealed class FirebaseRtdbEventStream(
    HttpResponseMessage response,
    Stream stream) : IAsyncDisposable
{
    private readonly FirebaseSseParser _parser = new();
    private int _disposed;

    public async IAsyncEnumerable<FirebaseSseEvent> ReadEventsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        byte[] buffer = new byte[4096];
        while (true)
        {
            int count;
            try
            {
                count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException
                or OperationCanceledException)
            {
                throw new FirebaseRtdbRequestException(
                    FirebaseRtdbFailureKind.Transport,
                    statusCode: null,
                    "Firebase RTDB event stream read failed.");
            }

            if (count == 0)
            {
                foreach (FirebaseSseEvent finalEvent in _parser.Complete())
                {
                    yield return finalEvent;
                }

                yield break;
            }

            foreach (FirebaseSseEvent parsed in _parser.Append(buffer.AsSpan(0, count)))
            {
                yield return parsed;
                if (parsed.IsTerminal)
                {
                    yield break;
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            response.Dispose();
        }
    }
}
