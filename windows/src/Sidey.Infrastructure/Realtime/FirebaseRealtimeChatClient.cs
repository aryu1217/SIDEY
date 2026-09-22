using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Sidey.Core.Domain;
using Sidey.Infrastructure.Authentication;

namespace Sidey.Infrastructure.Realtime;

internal interface IFirebaseRealtimeChatClient
{
    public ValueTask<FirebaseRealtimeChatResult> SendAsync(
        Guid messageId,
        Guid roomId,
        string body,
        CancellationToken cancellationToken = default);
}

internal enum FirebaseRealtimeChatFailureClassification
{
    KnownNonCommit,
    CommitAmbiguous,
}

internal sealed class FirebaseRealtimeChatException : Exception
{
    public FirebaseRealtimeChatException(
        string code,
        FirebaseRealtimeChatFailureClassification classification,
        HttpStatusCode? statusCode = null)
        : base(classification == FirebaseRealtimeChatFailureClassification.KnownNonCommit
            ? "Firebase realtime chat was rejected before commit."
            : "Firebase realtime chat outcome is ambiguous.")
    {
        Code = code;
        Classification = classification;
        StatusCode = statusCode;
    }

    public string Code { get; }
    public FirebaseRealtimeChatFailureClassification Classification { get; }
    public HttpStatusCode? StatusCode { get; }

    public override string ToString() =>
        $"{nameof(FirebaseRealtimeChatException)}(Code={Code}, Classification={Classification}, StatusCode={(StatusCode is null ? "<none>" : ((int)StatusCode).ToString())})";
}

internal sealed class FirebaseRealtimeChatResult
{
    public FirebaseRealtimeChatResult(
        Guid messageId,
        Guid roomId,
        Guid senderId,
        string body,
        long timestampMilliseconds,
        long sequence,
        string? bubbleWireCode)
    {
        MessageId = messageId;
        RoomId = roomId;
        SenderId = senderId;
        Body = body;
        TimestampMilliseconds = timestampMilliseconds;
        Sequence = sequence;
        BubbleWireCode = bubbleWireCode;
    }

    public Guid MessageId { get; }
    public Guid RoomId { get; }
    public Guid SenderId { get; }
    public string Body { get; }
    public long TimestampMilliseconds { get; }
    public long Sequence { get; }
    public string? BubbleWireCode { get; }

    public override string ToString() =>
        $"{nameof(FirebaseRealtimeChatResult)}(MessageId={MessageId:D}, RoomId={RoomId:D}, SenderId={SenderId:D}, Body=<redacted>, TimestampMilliseconds={TimestampMilliseconds}, Sequence={Sequence}, BubbleWireCode={BubbleWireCode ?? "<none>"})";
}

internal sealed class FirebaseRealtimeChatClient : IFirebaseRealtimeChatClient
{
    private const int MaximumResponseBytes = 64 * 1024;
    private const long MaximumSafeInteger = 9_007_199_254_740_991;
    private static readonly Uri s_endpoint =
        new("https://asia-southeast1-sidey-realtime.cloudfunctions.net/sendRealtimeChat");
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> s_rootSuccessProperties = ["result"];
    private static readonly HashSet<string> s_resultProperties = ["b", "i", "k", "n", "t"];
    private static readonly HashSet<string> s_resultRequiredProperties = ["b", "i", "n", "t"];
    private static readonly HashSet<string> s_knownNonCommitCodes =
    [
        "unauthenticated",
        "permission-denied",
        "resource-exhausted",
        "failed-precondition",
        "invalid-argument",
    ];
    private static readonly HashSet<string> s_commitAmbiguousCodes =
    [
        "deadline-exceeded",
        "internal",
        "unavailable",
    ];

    private readonly IFirebaseRealtimeCredentialProvider _credentials;
    private readonly HttpClient _httpClient;

    public FirebaseRealtimeChatClient(
        IFirebaseRealtimeCredentialProvider credentials,
        HttpClient httpClient)
    {
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async ValueTask<FirebaseRealtimeChatResult> SendAsync(
        Guid messageId,
        Guid roomId,
        string body,
        CancellationToken cancellationToken = default)
    {
        if (messageId == Guid.Empty)
        {
            throw new ArgumentException("Message identifier must not be empty.", nameof(messageId));
        }
        if (roomId == Guid.Empty)
        {
            throw new ArgumentException("Room identifier must not be empty.", nameof(roomId));
        }
        string normalized = MessageValidator.Normalize(body);
        if (!MessageValidator.IsValid(normalized))
        {
            throw new ArgumentException("Message body is invalid.", nameof(body));
        }

        FirebaseRealtimeCredential credential =
            await _credentials.GetCredentialAsync(cancellationToken).ConfigureAwait(false);
        credential.LifetimeToken.ThrowIfCancellationRequested();
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            credential.LifetimeToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, s_endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.IdToken);
        request.Content = JsonContent.Create(new
        {
            data = new
            {
                b = normalized,
                i = messageId,
                r = roomId,
            },
        }, options: s_jsonOptions);

        try
        {
            using HttpResponseMessage response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                linkedCancellation.Token).ConfigureAwait(false);
            byte[] payload = await ReadBoundedAsync(
                response.Content,
                MaximumResponseBytes,
                linkedCancellation.Token).ConfigureAwait(false);
            return ParseResponse(
                payload,
                response.StatusCode,
                credential,
                messageId,
                roomId,
                normalized);
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested || credential.LifetimeToken.IsCancellationRequested)
        {
            throw;
        }
        catch (FirebaseRealtimeChatException)
        {
            throw;
        }
        catch
        {
            throw Ambiguous("transport-error");
        }
    }

    public override string ToString() => nameof(FirebaseRealtimeChatClient);

    private static FirebaseRealtimeChatResult ParseResponse(
        ReadOnlyMemory<byte> payload,
        HttpStatusCode statusCode,
        FirebaseRealtimeCredential credential,
        Guid expectedMessageId,
        Guid roomId,
        string expectedBody)
    {
        try
        {
            using var document = JsonDocument.Parse(payload, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8,
            });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw Ambiguous("transport-error", statusCode);
            }

            if (TryGetError(root, out JsonElement error))
            {
                throw CallableError(error, statusCode);
            }
            if (statusCode != HttpStatusCode.OK)
            {
                throw Ambiguous("transport-error", statusCode);
            }

            ValidateProperties(root, s_rootSuccessProperties, s_rootSuccessProperties);
            JsonElement result = root.GetProperty("result");
            if (result.ValueKind != JsonValueKind.Object)
            {
                throw Ambiguous("transport-error", statusCode);
            }
            ValidateProperties(result, s_resultProperties, s_resultRequiredProperties);

            Guid messageId = RequireGuid(result, "i");
            string body = RequireString(result, "b");
            long timestamp = RequirePositiveSafeInteger(result, "t");
            long sequence = RequirePositiveSafeInteger(result, "n");
            string? bubbleWireCode = result.TryGetProperty("k", out JsonElement bubble)
                ? RequireWireCode(bubble)
                : null;
            if (messageId != expectedMessageId || !StringComparer.Ordinal.Equals(body, expectedBody))
            {
                throw Ambiguous("transport-error", statusCode);
            }

            return new FirebaseRealtimeChatResult(
                messageId,
                roomId,
                credential.UserId,
                body,
                timestamp,
                sequence,
                bubbleWireCode);
        }
        catch (FirebaseRealtimeChatException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            throw Ambiguous("transport-error", statusCode);
        }
    }

    private static FirebaseRealtimeChatException CallableError(
        JsonElement error,
        HttpStatusCode statusCode)
    {
        HashSet<string> found = [];
        foreach (JsonProperty property in error.EnumerateObject())
        {
            if (property.Name is not ("status" or "message" or "details")
                || !found.Add(property.Name))
            {
                return Ambiguous("internal", statusCode);
            }
        }
        if (error.ValueKind != JsonValueKind.Object
            || !error.TryGetProperty("status", out JsonElement status)
            || status.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(status.GetString()))
        {
            return Ambiguous("internal", statusCode);
        }

        string code = status.GetString()!
            .ToLowerInvariant()
            .Replace('_', '-');
        if (s_knownNonCommitCodes.Contains(code))
        {
            return new FirebaseRealtimeChatException(
                code,
                FirebaseRealtimeChatFailureClassification.KnownNonCommit,
                statusCode);
        }
        return Ambiguous(s_commitAmbiguousCodes.Contains(code) ? code : "unavailable", statusCode);
    }

    private static bool TryGetError(JsonElement root, out JsonElement error)
    {
        error = default;
        int properties = 0;
        foreach (JsonProperty property in root.EnumerateObject())
        {
            properties++;
            if (property.Name == "error")
            {
                if (error.ValueKind != JsonValueKind.Undefined)
                {
                    throw new JsonException();
                }
                error = property.Value;
            }
        }
        if (error.ValueKind == JsonValueKind.Undefined)
        {
            return false;
        }
        if (properties != 1)
        {
            throw new JsonException();
        }
        return true;
    }

    private static FirebaseRealtimeChatException Ambiguous(
        string code,
        HttpStatusCode? statusCode = null) =>
        new(code, FirebaseRealtimeChatFailureClassification.CommitAmbiguous, statusCode);

    private static void ValidateProperties(
        JsonElement value,
        IReadOnlySet<string> allowed,
        IReadOnlySet<string> required)
    {
        HashSet<string> found = [];
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (!allowed.Contains(property.Name) || !found.Add(property.Name))
            {
                throw new JsonException();
            }
        }
        if (!required.IsSubsetOf(found))
        {
            throw new JsonException();
        }
    }

    private static Guid RequireGuid(JsonElement value, string name)
    {
        JsonElement property = value.GetProperty(name);
        if (property.ValueKind != JsonValueKind.String
            || !Guid.TryParseExact(property.GetString(), "D", out Guid result)
            || result == Guid.Empty)
        {
            throw new JsonException();
        }
        return result;
    }

    private static string RequireString(JsonElement value, string name)
    {
        JsonElement property = value.GetProperty(name);
        if (property.ValueKind != JsonValueKind.String || property.GetString() is not { } result)
        {
            throw new JsonException();
        }
        return result;
    }

    private static long RequirePositiveSafeInteger(JsonElement value, string name)
    {
        JsonElement property = value.GetProperty(name);
        if (property.ValueKind != JsonValueKind.Number
            || !property.TryGetInt64(out long result)
            || result is < 1 or > MaximumSafeInteger)
        {
            throw new JsonException();
        }
        return result;
    }

    private static string RequireWireCode(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String
            || value.GetString() is not { } result
            || result.Length is < 1 or > 6
            || result[0] is < '1' or > '9'
            || result.Any(character => character is < '0' or > '9'))
        {
            throw new JsonException();
        }
        return result;
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        await using Stream stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        byte[] buffer = new byte[4096];
        while (true)
        {
            int read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return output.ToArray();
            }
            if (output.Length + read > maximumBytes)
            {
                throw Ambiguous("transport-error");
            }
            output.Write(buffer, 0, read);
        }
    }
}
