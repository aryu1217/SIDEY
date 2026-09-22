using System.Text;

namespace Sidey.Infrastructure.Realtime;

internal sealed class FirebaseSseParser
{
    private const int MaximumLineLength = 256 * 1024;
    private const int MaximumEventDataLength = 256 * 1024;

    private readonly Decoder _decoder = new UTF8Encoding(false, true).GetDecoder();
    private readonly char[] _characters = new char[1024];
    private readonly StringBuilder _line = new();
    private readonly StringBuilder _data = new();
    private string _eventName = string.Empty;
    private bool _skipLineFeed;
    private bool _atBeginning = true;
    private bool _hasDataField;
    private bool _completed;

    public IReadOnlyList<FirebaseSseEvent> Append(ReadOnlySpan<byte> bytes)
    {
        ObjectDisposedException.ThrowIf(_completed, this);
        var events = new List<FirebaseSseEvent>();
        Decode(bytes, flush: false, events);
        return events;
    }

    public IReadOnlyList<FirebaseSseEvent> Complete()
    {
        ObjectDisposedException.ThrowIf(_completed, this);
        _completed = true;
        var events = new List<FirebaseSseEvent>();
        Decode([], flush: true, events);
        // A reconnect can cut an event at any byte. Only a blank line dispatches an SSE
        // event, so an unterminated tail must never mutate the local snapshot.
        _line.Clear();
        _eventName = string.Empty;
        _data.Clear();
        return events;
    }

    private void Decode(ReadOnlySpan<byte> bytes, bool flush, List<FirebaseSseEvent> events)
    {
        do
        {
            _decoder.Convert(
                bytes,
                _characters,
                flush,
                out int bytesUsed,
                out int charactersUsed,
                out bool completed);
            ProcessCharacters(_characters.AsSpan(0, charactersUsed), events);
            bytes = bytes[bytesUsed..];
            if (completed)
            {
                break;
            }

            if (bytesUsed == 0 && charactersUsed == 0)
            {
                throw new InvalidDataException("Firebase SSE decoder made no progress.");
            }
        }
        while (!bytes.IsEmpty || flush);
    }

    private void ProcessCharacters(ReadOnlySpan<char> characters, List<FirebaseSseEvent> events)
    {
        foreach (char character in characters)
        {
            if (_atBeginning)
            {
                _atBeginning = false;
                if (character == '\uFEFF')
                {
                    continue;
                }
            }

            if (_skipLineFeed)
            {
                _skipLineFeed = false;
                if (character == '\n')
                {
                    continue;
                }
            }

            if (character is '\r' or '\n')
            {
                ProcessLine(events);
                _skipLineFeed = character == '\r';
                continue;
            }

            if (_line.Length >= MaximumLineLength)
            {
                throw new InvalidDataException("Firebase SSE line exceeded the supported limit.");
            }

            _line.Append(character);
        }
    }

    private void ProcessLine(List<FirebaseSseEvent> events)
    {
        if (_line.Length == 0)
        {
            Dispatch(events);
            return;
        }

        string line = _line.ToString();
        _line.Clear();
        if (line[0] == ':')
        {
            return;
        }

        int separator = line.IndexOf(':');
        string field = separator < 0 ? line : line[..separator];
        string value = separator < 0 ? string.Empty : line[(separator + 1)..];
        if (value.StartsWith(' '))
        {
            value = value[1..];
        }

        if (field == "event")
        {
            _eventName = value;
        }
        else if (field == "data")
        {
            if (_data.Length + value.Length + 1 > MaximumEventDataLength)
            {
                throw new InvalidDataException("Firebase SSE event data exceeded the supported limit.");
            }

            _hasDataField = true;
            _data.Append(value);
            _data.Append('\n');
        }
    }

    private void Dispatch(List<FirebaseSseEvent> events)
    {
        if (!_hasDataField)
        {
            _eventName = string.Empty;
            _data.Clear();
            return;
        }

        if (_data.Length > 0)
        {
            _data.Length--;
        }

        events.Add(new FirebaseSseEvent(ParseKind(_eventName), _data.ToString()));
        _eventName = string.Empty;
        _data.Clear();
        _hasDataField = false;
    }

    private static FirebaseSseEventKind ParseKind(string eventName) => eventName switch
    {
        "put" => FirebaseSseEventKind.Put,
        "patch" => FirebaseSseEventKind.Patch,
        "keep-alive" => FirebaseSseEventKind.KeepAlive,
        "cancel" => FirebaseSseEventKind.Cancel,
        "auth_revoked" => FirebaseSseEventKind.AuthRevoked,
        _ => FirebaseSseEventKind.Unknown,
    };
}
