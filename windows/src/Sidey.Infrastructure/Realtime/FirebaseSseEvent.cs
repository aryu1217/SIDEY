using System.Text.Json.Nodes;

namespace Sidey.Infrastructure.Realtime;

internal enum FirebaseSseEventKind
{
    Put,
    Patch,
    KeepAlive,
    Cancel,
    AuthRevoked,
    Unknown,
}

internal sealed record FirebaseSseEvent(FirebaseSseEventKind Kind, string Data)
{
    public bool IsTerminal => Kind is FirebaseSseEventKind.Cancel or FirebaseSseEventKind.AuthRevoked;

    public FirebaseRtdbMutation GetMutation()
    {
        if (Kind is not FirebaseSseEventKind.Put and not FirebaseSseEventKind.Patch)
        {
            throw new InvalidOperationException("Only Firebase put and patch events contain mutations.");
        }

        JsonObject envelope = JsonNode.Parse(Data) as JsonObject
            ?? throw new InvalidDataException("Firebase SSE mutation envelope must be an object.");
        string path = envelope["path"]?.GetValue<string>()
            ?? throw new InvalidDataException("Firebase SSE mutation path is missing.");
        if (!envelope.TryGetPropertyValue("data", out JsonNode? data))
        {
            throw new InvalidDataException("Firebase SSE mutation data is missing.");
        }

        return new FirebaseRtdbMutation(Kind, path, data?.DeepClone());
    }

    public override string ToString() => $"FirebaseSseEvent {{ Kind = {Kind}, Data = <redacted> }}";
}

internal sealed record FirebaseRtdbMutation(
    FirebaseSseEventKind Kind,
    string Path,
    JsonNode? Data)
{
    public override string ToString() =>
        $"FirebaseRtdbMutation {{ Kind = {Kind}, Path = {Path}, Data = <redacted> }}";
}
