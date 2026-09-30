using Sidey.Infrastructure.Authentication;

namespace Sidey.Infrastructure.Realtime;

internal interface IFirebaseRealtimeTransientClient
{
    public Task PublishTypingAsync(
        Guid roomId,
        bool active,
        CancellationToken cancellationToken);

    public Task PublishCharacterPulseAsync(
        Guid roomId,
        CancellationToken cancellationToken);

    public Task PublishCharacterThrowAsync(
        Guid roomId,
        Guid targetUserId,
        string wireCode,
        CancellationToken cancellationToken);
}

internal sealed class FirebaseRealtimeTransientClient : IFirebaseRealtimeTransientClient, IDisposable
{
    private readonly IFirebaseRealtimeCredentialProvider _credentials;
    private readonly Func<Uri, FirebaseRtdbRestClient> _createClient;
    private readonly HttpClient? _httpClient;

    public FirebaseRealtimeTransientClient(
        IFirebaseRealtimeCredentialProvider credentials,
        Func<Uri, FirebaseRtdbRestClient>? createClient = null)
    {
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        if (createClient is null)
        {
            _httpClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
            _createClient = databaseUrl => new FirebaseRtdbRestClient(new FirebaseRtdbUrlBuilder(databaseUrl), _httpClient);
        }
        else
        {
            _createClient = createClient;
        }
    }

    public async Task PublishTypingAsync(
        Guid roomId,
        bool active,
        CancellationToken cancellationToken)
    {
        FirebaseRealtimeCredential credential =
            await _credentials.GetUsableCredentialAsync(cancellationToken).ConfigureAwait(false);
        string path = FirebaseRealtimeProtocol.TypingPath(
            roomId,
            credential.UserId,
            credential.SessionId);
        if (active)
        {
            await WriteAsync(
                credential,
                HttpMethod.Put,
                path,
                FirebaseRealtimeProtocol.CreateServerTimestampBody(),
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await WriteAsync(
                credential,
                HttpMethod.Delete,
                path,
                body: null,
                cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task PublishCharacterPulseAsync(
        Guid roomId,
        CancellationToken cancellationToken)
    {
        FirebaseRealtimeCredential credential =
            await _credentials.GetUsableCredentialAsync(cancellationToken).ConfigureAwait(false);
        await WriteAsync(
            credential,
            HttpMethod.Put,
            FirebaseRealtimeProtocol.CharacterPulsePath(roomId, credential.UserId),
            FirebaseRealtimeProtocol.CreateServerTimestampBody(),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task PublishCharacterThrowAsync(
        Guid roomId,
        Guid targetUserId,
        string wireCode,
        CancellationToken cancellationToken)
    {
        FirebaseRealtimeCredential credential =
            await _credentials.GetUsableCredentialAsync(cancellationToken).ConfigureAwait(false);
        if (wireCode != "0" && !credential.WireItems.Contains(wireCode, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("The equipped throwable is not authorized for Firebase realtime.");
        }
        await WriteAsync(
            credential,
            HttpMethod.Put,
            FirebaseRealtimeProtocol.CharacterThrowPath(roomId, credential.UserId),
            FirebaseRealtimeProtocol.CreateCharacterThrowBody(targetUserId, wireCode),
            cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => _httpClient?.Dispose();

    private async Task WriteAsync(
        FirebaseRealtimeCredential credential,
        HttpMethod method,
        string path,
        ReadOnlyMemory<byte>? body,
        CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            credential.LifetimeToken);
        await using FirebaseRtdbRestClient client = _createClient(credential.DatabaseUrl);
        await client.WriteAsync(
            method,
            path,
            credential.IdToken,
            body,
            linked.Token).ConfigureAwait(false);
    }
}
