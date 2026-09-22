namespace Sidey.Infrastructure.Realtime;

internal sealed class FirebaseRtdbUrlBuilder
{
    private static readonly char[] s_invalidKeyCharacters = ['.', '#', '$', '[', ']'];

    private readonly Uri _databaseRoot;
    private readonly IReadOnlySet<string> _allowedHosts;

    public FirebaseRtdbUrlBuilder(Uri databaseRoot, IEnumerable<string>? redirectHosts = null)
    {
        ArgumentNullException.ThrowIfNull(databaseRoot);
        if (databaseRoot.Scheme != Uri.UriSchemeHttps
            || !databaseRoot.IsDefaultPort
            || string.IsNullOrWhiteSpace(databaseRoot.Host)
            || !string.IsNullOrEmpty(databaseRoot.UserInfo)
            || !string.IsNullOrEmpty(databaseRoot.Query)
            || !string.IsNullOrEmpty(databaseRoot.Fragment)
            || databaseRoot.AbsolutePath != "/")
        {
            throw new ArgumentException("Firebase database URL must be an HTTPS origin.", nameof(databaseRoot));
        }

        _databaseRoot = databaseRoot;
        var allowedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            databaseRoot.IdnHost,
        };
        if (redirectHosts is not null)
        {
            foreach (string host in redirectHosts)
            {
                if (string.IsNullOrWhiteSpace(host)
                    || !StringComparer.Ordinal.Equals(host, host.Trim())
                    || host.Contains('/')
                    || host.Contains(':')
                    || Uri.CheckHostName(host) != UriHostNameType.Dns)
                {
                    throw new ArgumentException("Firebase redirect allowlist contains an invalid host.", nameof(redirectHosts));
                }

                allowedHosts.Add(new UriBuilder(Uri.UriSchemeHttps, host).Uri.IdnHost);
            }
        }

        _allowedHosts = allowedHosts;
    }

    public string DatabaseHost => _databaseRoot.IdnHost;

    public Uri Build(string databasePath, string idToken, bool silent = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(idToken);

        string relativePath = databasePath.StartsWith('/') ? databasePath[1..] : databasePath;
        if (relativePath.Length == 0
            || relativePath.EndsWith('/')
            || relativePath.Contains("//", StringComparison.Ordinal))
        {
            throw new ArgumentException("Firebase database path is malformed.", nameof(databasePath));
        }

        string[] segments = relativePath.Split('/');
        foreach (string segment in segments)
        {
            if (segment.Length == 0
                || segment is "." or ".."
                || segment.IndexOfAny(s_invalidKeyCharacters) >= 0
                || segment.Any(char.IsControl))
            {
                throw new ArgumentException("Firebase database path contains an invalid key.", nameof(databasePath));
            }
        }

        string encodedPath = string.Join('/', segments.Select(Uri.EscapeDataString));
        var builder = new UriBuilder(_databaseRoot)
        {
            Path = $"/{encodedPath}.json",
            Query = silent
                ? $"auth={Uri.EscapeDataString(idToken)}&print=silent"
                : $"auth={Uri.EscapeDataString(idToken)}",
        };
        return builder.Uri;
    }

    public Uri ValidateRedirect(Uri currentRequest, Uri location, IReadOnlySet<string> visitedHostsAndPaths)
    {
        ArgumentNullException.ThrowIfNull(currentRequest);
        ArgumentNullException.ThrowIfNull(location);
        ArgumentNullException.ThrowIfNull(visitedHostsAndPaths);

        Uri target = location.IsAbsoluteUri ? location : new Uri(currentRequest, location);
        if (target.Scheme != Uri.UriSchemeHttps
            || !target.IsDefaultPort
            || !string.IsNullOrEmpty(target.UserInfo)
            || !_allowedHosts.Contains(target.IdnHost)
            || !target.AbsolutePath.EndsWith(".json", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Firebase redirect target is not allowed.");
        }

        string identity = RedirectIdentity(target);
        if (visitedHostsAndPaths.Contains(identity))
        {
            throw new InvalidDataException("Firebase redirect loop detected.");
        }

        return target;
    }

    public static string Redact(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        var builder = new UriBuilder(uri)
        {
            Query = string.Empty,
            Fragment = string.Empty,
        };
        return builder.Uri.GetLeftPart(UriPartial.Path);
    }

    public static string RedirectIdentity(Uri uri) =>
        $"{uri.Scheme.ToLowerInvariant()}://{uri.IdnHost.ToLowerInvariant()}:{uri.Port}{uri.AbsolutePath}";
}
