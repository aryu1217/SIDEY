using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Sidey.Infrastructure.Authentication;
using Sidey.Infrastructure.Configuration;

namespace Sidey.Infrastructure.Realtime;

internal interface IFirebaseRealtimeRolloutSelector
{
    public ValueTask<FirebaseRealtimeRolloutSelection> SelectAsync(
        CancellationToken cancellationToken = default);

    public ValueTask<FirebaseRealtimeRolloutSelection> RefreshAsync(
        CancellationToken cancellationToken = default) => SelectAsync(cancellationToken);
}

internal enum FirebaseRealtimeSelectedTransport
{
    LegacySupabase,
    FirebaseV2,
}

internal enum FirebaseRealtimeRolloutSelectionSource
{
    Server,
    FailureFallback,
}

internal sealed class FirebaseRealtimeRolloutSelection
{
    public FirebaseRealtimeRolloutSelection(
        FirebaseRealtimeSelectedTransport transport,
        bool killSwitch,
        TimeSpan cacheTtl,
        FirebaseRealtimeRolloutSelectionSource source)
    {
        Transport = transport;
        KillSwitch = killSwitch;
        CacheTtl = cacheTtl;
        Source = source;
    }

    public FirebaseRealtimeSelectedTransport Transport { get; }
    public bool Enabled => Transport == FirebaseRealtimeSelectedTransport.FirebaseV2;
    public bool KillSwitch { get; }
    public TimeSpan CacheTtl { get; }
    public FirebaseRealtimeRolloutSelectionSource Source { get; }

    public override string ToString() =>
        $"{nameof(FirebaseRealtimeRolloutSelection)}(Transport={Transport}, KillSwitch={KillSwitch}, CacheTtl={CacheTtl.TotalSeconds}, Source={Source})";
}

internal sealed class FirebaseRealtimeRolloutException : Exception
{
    public FirebaseRealtimeRolloutException(bool failClosed)
        : base(failClosed
            ? "Firebase realtime rollout refresh failed closed."
            : "Firebase realtime rollout selection failed.")
    {
        FailClosed = failClosed;
    }

    public bool FailClosed { get; }

    public override string ToString() =>
        $"{nameof(FirebaseRealtimeRolloutException)}(FailClosed={FailClosed})";
}

internal sealed class FirebaseRealtimeRolloutSelector : IFirebaseRealtimeRolloutSelector
{
    internal const string ContractHash =
        "3c836b40cfc44437e9d069b84787cd3d8793026ce40de46d82b9ece79127b7e5";

    private const int ProtocolVersion = 2;
    private const int MaximumResponseBytes = 16 * 1024;
    private static readonly TimeSpan s_failureFallbackTtl = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> s_responseProperties =
    [
        "enabled",
        "protocolVersion",
        "transport",
        "contractHash",
        "killSwitch",
        "cacheTtlSeconds",
        "failureMode",
    ];

    private readonly SupabaseRuntimeConfiguration _configuration;
    private readonly IAuthSessionAccessor _sessions;
    private readonly string _appVersion;
    private readonly HttpClient _httpClient;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CacheEntry? _cache;
    private bool _failedClosed;

    public FirebaseRealtimeRolloutSelector(
        SupabaseRuntimeConfiguration configuration,
        IAuthSessionAccessor sessions,
        string appVersion,
        HttpClient httpClient,
        TimeProvider? timeProvider = null)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _appVersion = IsValidAppVersion(appVersion)
            ? appVersion
            : throw new ArgumentException("App version is invalid.", nameof(appVersion));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public ValueTask<FirebaseRealtimeRolloutSelection> SelectAsync(
        CancellationToken cancellationToken = default) =>
        SelectCoreAsync(forceRefresh: false, cancellationToken);

    public ValueTask<FirebaseRealtimeRolloutSelection> RefreshAsync(
        CancellationToken cancellationToken = default) =>
        SelectCoreAsync(forceRefresh: true, cancellationToken);

    private async ValueTask<FirebaseRealtimeRolloutSelection> SelectCoreAsync(
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        StoredSupabaseSession session =
            await _sessions.GetStoredSessionAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new FirebaseRealtimeRolloutException(failClosed: false);
        SessionKey key = ParseSessionKey(session);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_failedClosed)
            {
                forceRefresh = true;
            }

            if (!forceRefresh
                && _cache is { } cached
                && cached.Key == key
                && _timeProvider.GetElapsedTime(cached.Timestamp) < cached.Selection.CacheTtl)
            {
                return cached.Selection;
            }

            try
            {
                FirebaseRealtimeRolloutSelection selected = await RequestSelectionAsync(
                    session,
                    cancellationToken).ConfigureAwait(false);
                _failedClosed = false;
                _cache = new CacheEntry(key, selected, _timeProvider.GetTimestamp());
                return selected;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                if (_failedClosed
                    || _cache is { } previous && previous.Key == key && previous.Selection.Enabled)
                {
                    _failedClosed = true;
                    throw new FirebaseRealtimeRolloutException(failClosed: true);
                }

                var fallback = new FirebaseRealtimeRolloutSelection(
                    FirebaseRealtimeSelectedTransport.LegacySupabase,
                    killSwitch: true,
                    s_failureFallbackTtl,
                    FirebaseRealtimeRolloutSelectionSource.FailureFallback);
                _cache = new CacheEntry(key, fallback, _timeProvider.GetTimestamp());
                return fallback;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public override string ToString() => nameof(FirebaseRealtimeRolloutSelector);

    private async Task<FirebaseRealtimeRolloutSelection> RequestSelectionAsync(
        StoredSupabaseSession session,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(_configuration.Url, "/rest/v1/rpc/register_realtime_capability_v2"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        request.Headers.Add("apikey", _configuration.PublishableKey);
        request.Content = JsonContent.Create(new
        {
            p_platform = "windows",
            p_app_version = _appVersion,
            p_protocol_version = ProtocolVersion,
            p_contract_hash = ContractHash,
        }, options: s_jsonOptions);

        using HttpResponseMessage response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidDataException("Rollout RPC failed.");
        }

        byte[] payload = await ReadBoundedAsync(
            response.Content,
            MaximumResponseBytes,
            cancellationToken).ConfigureAwait(false);
        return ParseSelection(payload);
    }

    private static FirebaseRealtimeRolloutSelection ParseSelection(ReadOnlyMemory<byte> payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 4,
            });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException();
            }

            HashSet<string> found = [];
            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (!s_responseProperties.Contains(property.Name) || !found.Add(property.Name))
                {
                    throw new JsonException();
                }
            }
            if (!s_responseProperties.SetEquals(found))
            {
                throw new JsonException();
            }

            bool enabled = RequireBoolean(root, "enabled");
            bool killSwitch = RequireBoolean(root, "killSwitch");
            string transport = RequireString(root, "transport");
            int cacheTtlSeconds = RequireInteger(root, "cacheTtlSeconds");
            if (RequireInteger(root, "protocolVersion") != ProtocolVersion
                || !StringComparer.Ordinal.Equals(RequireString(root, "contractHash"), ContractHash)
                || !StringComparer.Ordinal.Equals(
                    RequireString(root, "failureMode"),
                    "fail_closed_if_last_enabled")
                || cacheTtlSeconds is < 30 or > 300)
            {
                throw new JsonException();
            }

            FirebaseRealtimeSelectedTransport selectedTransport = transport switch
            {
                "legacy_supabase" => FirebaseRealtimeSelectedTransport.LegacySupabase,
                "firebase_v2" => FirebaseRealtimeSelectedTransport.FirebaseV2,
                _ => throw new JsonException(),
            };
            if (enabled != (selectedTransport == FirebaseRealtimeSelectedTransport.FirebaseV2)
                || (killSwitch && enabled))
            {
                throw new JsonException();
            }

            return new FirebaseRealtimeRolloutSelection(
                selectedTransport,
                killSwitch,
                TimeSpan.FromSeconds(cacheTtlSeconds),
                FirebaseRealtimeRolloutSelectionSource.Server);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            throw new InvalidDataException("Rollout RPC response was invalid.");
        }
    }

    private static SessionKey ParseSessionKey(StoredSupabaseSession session)
    {
        try
        {
            if (string.IsNullOrEmpty(session.AccessToken) || session.AccessToken.Length > 32 * 1024)
            {
                throw new FormatException();
            }
            string[] segments = session.AccessToken.Split('.');
            if (segments.Length != 3 || segments[1].Length == 0)
            {
                throw new FormatException();
            }
            string encoded = segments[1].Replace('-', '+').Replace('_', '/');
            encoded = encoded.PadRight(encoded.Length + ((4 - encoded.Length % 4) % 4), '=');
            using var document = JsonDocument.Parse(Convert.FromBase64String(encoded));
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("sub", out JsonElement subject)
                || !Guid.TryParse(subject.GetString(), out Guid userId)
                || userId != session.UserId
                || !root.TryGetProperty("session_id", out JsonElement sessionClaim)
                || !Guid.TryParse(sessionClaim.GetString(), out Guid sessionId)
                || sessionId == Guid.Empty)
            {
                throw new FormatException();
            }
            return new SessionKey(userId, sessionId);
        }
        catch (Exception exception) when (exception is FormatException or JsonException or InvalidOperationException)
        {
            throw new FirebaseRealtimeRolloutException(failClosed: false);
        }
    }

    private static bool IsValidAppVersion(string? value) =>
        !string.IsNullOrEmpty(value)
        && value.Length <= 64
        && IsAsciiAlphaNumeric(value[0])
        && value.All(character => IsAsciiAlphaNumeric(character) || character is '.' or '_' or '+' or '-');

    private static bool IsAsciiAlphaNumeric(char value) =>
        value is >= '0' and <= '9'
        || value is >= 'A' and <= 'Z'
        || value is >= 'a' and <= 'z';

    private static bool RequireBoolean(JsonElement root, string name)
    {
        JsonElement value = root.GetProperty(name);
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new JsonException(),
        };
    }

    private static int RequireInteger(JsonElement root, string name)
    {
        JsonElement value = root.GetProperty(name);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int result)
            ? result
            : throw new JsonException();
    }

    private static string RequireString(JsonElement root, string name)
    {
        JsonElement value = root.GetProperty(name);
        return value.ValueKind == JsonValueKind.String && value.GetString() is { } result
            ? result
            : throw new JsonException();
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
                throw new InvalidDataException("Rollout RPC response was too large.");
            }
            output.Write(buffer, 0, read);
        }
    }

    private readonly record struct SessionKey(Guid UserId, Guid SessionId);

    private sealed record CacheEntry(
        SessionKey Key,
        FirebaseRealtimeRolloutSelection Selection,
        long Timestamp);
}
