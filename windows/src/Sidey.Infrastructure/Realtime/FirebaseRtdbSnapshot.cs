using System.Globalization;
using System.Text.Json.Nodes;

namespace Sidey.Infrastructure.Realtime;

internal sealed class FirebaseRtdbSnapshot
{
    private const int MaximumArrayIndex = 10_000;
    private const int MaximumPathLength = 4_096;
    private const int MaximumPathSegments = 64;
    private static readonly char[] s_invalidKeyCharacters = ['.', '#', '$', '[', ']'];

    private JsonNode? _root;

    public JsonNode? Value => _root?.DeepClone();

    public void Apply(FirebaseRtdbMutation mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        string[] basePath = ParseEventPath(mutation.Path);
        if (mutation.Kind == FirebaseSseEventKind.Put)
        {
            _root = ApplyValue(_root, basePath, 0, mutation.Data);
            return;
        }

        if (mutation.Kind != FirebaseSseEventKind.Patch)
        {
            throw new ArgumentException("Only Firebase put and patch mutations can update a snapshot.", nameof(mutation));
        }

        JsonObject updates = mutation.Data as JsonObject
            ?? throw new InvalidDataException("Firebase patch data must be an object.");
        JsonNode? updatedRoot = _root?.DeepClone();
        foreach ((string relativePath, JsonNode? value) in updates)
        {
            string[] childPath = ParsePatchPath(relativePath);
            updatedRoot = ApplyValue(updatedRoot, [.. basePath, .. childPath], 0, value);
        }
        _root = updatedRoot;
    }

    private static JsonNode? ApplyValue(
        JsonNode? current,
        IReadOnlyList<string> path,
        int index,
        JsonNode? value)
    {
        if (index == path.Count)
        {
            return value?.DeepClone();
        }

        string key = path[index];
        if (current is JsonArray sourceArray
            && int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out int arrayIndex)
            && arrayIndex.ToString(CultureInfo.InvariantCulture) == key)
        {
            if (arrayIndex > MaximumArrayIndex)
            {
                throw new InvalidDataException("Firebase SSE array index exceeded the supported limit.");
            }

            var array = (JsonArray)sourceArray.DeepClone();
            while (array.Count <= arrayIndex)
            {
                array.Add(null);
            }

            array[arrayIndex] = ApplyValue(array[arrayIndex], path, index + 1, value);
            return array;
        }

        JsonObject container = current switch
        {
            JsonObject sourceObject => (JsonObject)sourceObject.DeepClone(),
            JsonArray array => ConvertArrayToObject(array),
            _ => [],
        };
        JsonNode? updated = ApplyValue(container[key], path, index + 1, value);
        if (updated is null)
        {
            container.Remove(key);
        }
        else
        {
            container[key] = updated;
        }

        return container;
    }

    private static JsonObject ConvertArrayToObject(JsonArray array)
    {
        var converted = new JsonObject();
        for (int index = 0; index < array.Count; index++)
        {
            if (array[index] is { } value)
            {
                converted[index.ToString(CultureInfo.InvariantCulture)] = value.DeepClone();
            }
        }

        return converted;
    }

    private static string[] ParseEventPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path == "/")
        {
            return [];
        }

        if (!path.StartsWith('/')
            || path.EndsWith('/')
            || path.Contains("//", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Firebase SSE event path is malformed.");
        }

        return ParseSegments(path[1..]);
    }

    private static string[] ParsePatchPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Length == 0
            || path.StartsWith('/')
            || path.EndsWith('/')
            || path.Contains("//", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Firebase SSE patch path is malformed.");
        }

        return ParseSegments(path);
    }

    private static string[] ParseSegments(string path)
    {
        if (path.Length > MaximumPathLength)
        {
            throw new InvalidDataException("Firebase SSE path exceeded the supported limit.");
        }

        string[] segments = path.Split('/');
        if (segments.Length > MaximumPathSegments
            || segments.Any(segment => segment.Length == 0
                || segment.IndexOfAny(s_invalidKeyCharacters) >= 0
                || segment.Any(char.IsControl)))
        {
            throw new InvalidDataException("Firebase SSE path contains an invalid key.");
        }

        return segments;
    }
}
