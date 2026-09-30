using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sidey.Platform.Windows.Deployment;

public sealed partial class WindowsUpdateService
{
    private const int MaximumManifestBytes = 4 * 1024 * 1024;
    private const long MaximumPayloadBytes = 2L * 1024 * 1024 * 1024;
    private const int MaximumCompressedFileBytes = 128 * 1024 * 1024;

    private sealed record IncrementalMetadataDto(
        [property: JsonPropertyName("manifest_url")] string? ManifestUrl,
        [property: JsonPropertyName("sha256")] string? Sha256);

    private sealed record IncrementalArtifact(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("sha256")] string Sha256,
        [property: JsonPropertyName("size")] long Size);

    private sealed record IncrementalFile(
        [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("sha256")] string Sha256,
        [property: JsonPropertyName("size")] long Size,
        [property: JsonPropertyName("offset")] long Offset,
        [property: JsonPropertyName("length")] long Length);

    private sealed record IncrementalManifest(
        [property: JsonPropertyName("schema")] int Schema,
        [property: JsonPropertyName("version")] string Version,
        [property: JsonPropertyName("updater")] IncrementalArtifact Updater,
        [property: JsonPropertyName("payload")] IncrementalArtifact Payload,
        [property: JsonPropertyName("files")] IncrementalFile[] Files);

    private static WindowsIncrementalUpdate? ReadIncrementalMetadata(IncrementalMetadataDto? metadata, Uri installer)
    {
        if (metadata is null || !IsDigest(metadata.Sha256)
            || !Uri.TryCreate(metadata.ManifestUrl, UriKind.Absolute, out Uri? uri)
            || uri != new Uri(installer.AbsoluteUri.Replace("-Setup.exe", "-Update.json", StringComparison.Ordinal)))
        {
            // Optional newer protocol must never prevent legacy full updates.
            return null;
        }
        return new WindowsIncrementalUpdate(uri, metadata.Sha256!.ToLowerInvariant());
    }

    private string IncrementalDirectory(WindowsUpdateManifest update) =>
        Path.Combine(Path.GetDirectoryName(InstallerPath(update.UpdateVersion))!, "incremental");

    private static bool IsDigest(string? value) => value is not null && Sha256Pattern().IsMatch(value);

    private static bool IsSafePayloadPath(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 240 || path.Contains('\\')
            || path.Any(c => c < 32 || "<>:\"|?*".Contains(c)))
        {
            return false;
        }
        foreach (string part in path.Split('/'))
        {
            string stem = part.Split('.')[0].ToUpperInvariant();
            if (part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ')
                || stem is "CON" or "PRN" or "AUX" or "NUL" or "CLOCK$" or "CONIN$" or "CONOUT$"
                || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal)
                    || stem.StartsWith("LPT", StringComparison.Ordinal)) && "123456789¹²³".Contains(stem[3])))
            {
                return false;
            }
        }
        return true;
    }

    private IncrementalManifest ParseIncrementalManifest(byte[] bytes, WindowsUpdateManifest update)
    {
        if (update.Incremental is null || bytes.Length > MaximumManifestBytes
            || !StringComparer.OrdinalIgnoreCase.Equals(
                Convert.ToHexStringLower(SHA256.HashData(bytes)), update.Incremental.Sha256))
        {
            throw new InvalidDataException("Incremental manifest checksum mismatch.");
        }
        IncrementalManifest manifest = JsonSerializer.Deserialize<IncrementalManifest>(bytes)
            ?? throw new InvalidDataException("Missing incremental manifest.");
        string releaseVersion = ResolveReleaseVersion(update.ProductVersion, update.UpdateVersion);
        string prefix = $"SIDEY-Windows-{_architecture}-v{releaseVersion}";
        if (manifest.Schema != 1 || manifest.Version != releaseVersion
            || manifest.Updater is null || manifest.Payload is null
            || manifest.Updater.Name != prefix + "-Update.exe" || manifest.Payload.Name != prefix + "-Payload.bin"
            || !IsDigest(manifest.Updater.Sha256) || !IsDigest(manifest.Payload.Sha256)
            || manifest.Updater.Size <= 0 || manifest.Updater.Size > MaximumCompressedFileBytes
            || manifest.Payload.Size <= 0 || manifest.Payload.Size > MaximumPayloadBytes
            || manifest.Files is null || manifest.Files.Length is 0 or > 10000)
        {
            throw new InvalidDataException("Invalid incremental artifacts.");
        }
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalSize = 0;
        long rangeEnd = 0;
        foreach (IncrementalFile file in manifest.Files.OrderBy(f => f?.Offset))
        {
            if (file is null || !IsSafePayloadPath(file.Path) || !paths.Add(file.Path)
                || file.Path.Equals("Uninstall.exe", StringComparison.OrdinalIgnoreCase)
                || !IsDigest(file.Sha256) || file.Size < 0 || file.Size > MaximumPayloadBytes
                || file.Offset < rangeEnd || file.Length <= 0 || file.Length > MaximumCompressedFileBytes
                || file.Offset > manifest.Payload.Size - file.Length)
            {
                throw new InvalidDataException("Invalid incremental file.");
            }
            totalSize += file.Size;
            if (totalSize > MaximumPayloadBytes)
            {
                throw new InvalidDataException("Incremental payload exceeds size limit.");
            }
            rangeEnd = file.Offset + file.Length;
        }
        foreach (string path in paths)
        {
            int slash = path.LastIndexOf('/');
            while (slash > 0)
            {
                if (paths.Contains(path[..slash]))
                {
                    throw new InvalidDataException("Incremental file conflicts with directory.");
                }
                slash = path.LastIndexOf('/', slash - 1);
            }
        }
        if (!paths.Contains("SIDEY.exe") || !paths.Contains("Runtime/SIDEY.Host.exe")
            || !paths.Contains("Runtime/SIDEY.UninstallHelper.exe"))
        {
            throw new InvalidDataException("Incremental payload lacks application entry points.");
        }
        return manifest;
    }

    private static void RequireSafeDirectory(string path)
    {
        for (DirectoryInfo? directory = new(path); directory is not null; directory = directory.Parent)
        {
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Update path contains a link.");
            }
        }
    }

    private static void RequireSafeCacheFile(string path)
    {
        RequireSafeDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Update cache file contains a link.");
        }
    }

    private int CleanupIncrementalCache(string updateDirectory, string version)
    {
        string directory = Path.Combine(updateDirectory, "incremental");
        if (!Directory.Exists(directory))
        {
            return 0;
        }
        RequireSafeDirectory(directory);
        int removed = 0;
        string files = Path.Combine(directory, "files");
        if (Directory.Exists(files))
        {
            RequireSafeDirectory(files);
            foreach (string file in Directory.GetFiles(files))
            {
                string name = Path.GetFileName(file);
                string suffix = name.EndsWith(".bin.download", StringComparison.Ordinal) ? ".bin.download" : ".bin";
                if (name.EndsWith(suffix, StringComparison.Ordinal) && IsDigest(name[..^suffix.Length]))
                {
                    removed += DeleteOwnedCacheFile(file);
                }
            }
            try
            { Directory.Delete(files, recursive: false); }
            catch (IOException) { }
        }
        foreach (string file in Directory.GetFiles(directory))
        {
            string name = Path.GetFileName(file);
            if (name is "manifest.json" || name == $"SIDEY-Windows-{_architecture}-v{version}-Update.exe"
                || name == $"SIDEY-Windows-{_architecture}-v{version}-Update.exe.download")
            {
                removed += DeleteOwnedCacheFile(file);
            }
        }
        try
        { Directory.Delete(directory, recursive: false); }
        catch (IOException) { }
        return removed;
    }

    private static int DeleteOwnedCacheFile(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
            {
                File.Delete(path);
                return 1;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return 0;
    }

    private static async Task<bool> MatchesFileAsync(string path, long size, string digest, CancellationToken token)
    {
        try
        {
            RequireSafeDirectory(Path.GetDirectoryName(path)!);
            if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                return false;
            }
            await using FileStream stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return stream.Length == size && StringComparer.OrdinalIgnoreCase.Equals(
                Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false)), digest);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static async Task CopyBoundedAsync(Stream source, Stream destination, long limit, CancellationToken token)
    {
        byte[] buffer = new byte[81920];
        long copied = 0;
        int count;
        while ((count = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            copied += count;
            if (copied > limit)
            {
                throw new InvalidDataException("Update download exceeds declared size.");
            }
            await destination.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
        }
    }

    private async Task<byte[]> DownloadManifestAsync(WindowsUpdateManifest update, CancellationToken token)
    {
        using HttpResponseMessage response = await _httpClient.GetAsync(update.Incremental!.ManifestUri,
            HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using Stream source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var destination = new MemoryStream();
        await CopyBoundedAsync(source, destination, MaximumManifestBytes, token).ConfigureAwait(false);
        return destination.ToArray();
    }

    private async Task<string?> TryPrepareIncrementalAsync(WindowsUpdateManifest update, CancellationToken token, IProgress<int>? progress)
    {
        try
        {
            string directory = IncrementalDirectory(update);
            RequireSafeDirectory(directory);
            if (await IsIncrementalReadyAsync(update, token).ConfigureAwait(false))
            {
                progress?.Report(100);
                return Path.Combine(directory,
                    $"SIDEY-Windows-{_architecture}-v{ResolveReleaseVersion(update.ProductVersion, update.UpdateVersion)}-Update.exe");
            }
            Directory.CreateDirectory(directory);
            string manifestPath = Path.Combine(directory, "manifest.json");
            RequireSafeCacheFile(manifestPath);
            byte[] bytes = File.Exists(manifestPath) && new FileInfo(manifestPath).Length <= MaximumManifestBytes
                && (File.GetAttributes(manifestPath) & FileAttributes.ReparsePoint) == 0
                ? await File.ReadAllBytesAsync(manifestPath, token).ConfigureAwait(false)
                : await DownloadManifestAsync(update, token).ConfigureAwait(false);
            IncrementalManifest manifest;
            try
            { manifest = ParseIncrementalManifest(bytes, update); }
            catch (InvalidDataException)
            {
                bytes = await DownloadManifestAsync(update, token).ConfigureAwait(false);
                manifest = ParseIncrementalManifest(bytes, update);
            }
            string filesDirectory = Path.Combine(directory, "files");
            RequireSafeDirectory(filesDirectory);
            Directory.CreateDirectory(filesDirectory);
            await File.WriteAllBytesAsync(manifestPath, bytes, token).ConfigureAwait(false);
            var missing = new List<IncrementalFile>();
            foreach (IncrementalFile file in manifest.Files)
            {
                token.ThrowIfCancellationRequested();
                string cachePath = Path.Combine(filesDirectory, file.Sha256.ToLowerInvariant() + ".bin");
                if (await MatchesFileAsync(cachePath, file.Size, file.Sha256, token).ConfigureAwait(false))
                {
                    continue;
                }
                string installed = Path.Combine(_installationDirectory!, file.Path.Replace('/', Path.DirectorySeparatorChar));
                if (await MatchesFileAsync(installed, file.Size, file.Sha256, token).ConfigureAwait(false))
                {
                    string partial = cachePath + ".download";
                    RequireSafeCacheFile(partial);
                    await using (FileStream source = File.Open(installed, FileMode.Open, FileAccess.Read, FileShare.Read))
                    await using (FileStream destination = File.Open(partial, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        await CopyBoundedAsync(source, destination, file.Size, token).ConfigureAwait(false);
                    }
                    if (await MatchesFileAsync(partial, file.Size, file.Sha256, token).ConfigureAwait(false))
                    {
                        File.Move(partial, cachePath, overwrite: true);
                        continue;
                    }
                }
                missing.Add(file);
            }
            // Avoid downloading ranges when full Setup is already the cheaper transfer.
            long transferSize = missing.Sum(f => f.Length) + manifest.Updater.Size;
            using (var request = new HttpRequestMessage(HttpMethod.Head, update.InstallerUri))
            using (HttpResponseMessage response = await _httpClient.SendAsync(request, token).ConfigureAwait(false))
            {
                if (response.IsSuccessStatusCode && response.Content.Headers.ContentLength is > 0
                    && transferSize >= response.Content.Headers.ContentLength.Value)
                {
                    return null;
                }
            }
            long transferred = 0;
            progress?.Report(0);
            Uri payloadUri = new(update.Incremental!.ManifestUri, manifest.Payload.Name);
            foreach (IncrementalFile file in missing)
            {
                string path = Path.Combine(filesDirectory, file.Sha256.ToLowerInvariant() + ".bin");
                if (!await MatchesFileAsync(path, file.Size, file.Sha256, token).ConfigureAwait(false))
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, payloadUri);
                    request.Headers.Range = new RangeHeaderValue(file.Offset, file.Offset + file.Length - 1);
                    using HttpResponseMessage response = await _httpClient.SendAsync(request,
                        HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                    ContentRangeHeaderValue? range = response.Content.Headers.ContentRange;
                    if (response.StatusCode != HttpStatusCode.PartialContent || range?.Unit != "bytes"
                        || range.From != file.Offset || range.To != file.Offset + file.Length - 1
                        || range.Length != manifest.Payload.Size)
                    {
                        throw new InvalidDataException("Update host does not support the requested file range.");
                    }
                    await using Stream source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                    using var compressed = new MemoryStream();
                    await CopyBoundedAsync(source, compressed, file.Length, token).ConfigureAwait(false);
                    if (compressed.Length != file.Length)
                    {
                        throw new InvalidDataException("Incomplete incremental range.");
                    }
                    compressed.Position = 0;
                    string partial = path + ".download";
                    RequireSafeCacheFile(partial);
                    await using (var gzip = new GZipStream(compressed, CompressionMode.Decompress))
                    await using (FileStream destination = File.Open(partial, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        await CopyBoundedAsync(gzip, destination, file.Size, token).ConfigureAwait(false);
                    }
                    if (!await MatchesFileAsync(partial, file.Size, file.Sha256, token).ConfigureAwait(false))
                    {
                        throw new InvalidDataException("Incremental file checksum mismatch.");
                    }
                    File.Move(partial, path, overwrite: true);
                }
                transferred += file.Length;
                progress?.Report((int)(transferred * 100 / transferSize));
            }
            string updaterPath = Path.Combine(directory, manifest.Updater.Name);
            if (!await MatchesFileAsync(updaterPath, manifest.Updater.Size, manifest.Updater.Sha256, token).ConfigureAwait(false))
            {
                using HttpResponseMessage response = await _httpClient.GetAsync(new Uri(update.Incremental.ManifestUri, manifest.Updater.Name),
                    HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                await using Stream source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                string partial = updaterPath + ".download";
                RequireSafeCacheFile(partial);
                await using (FileStream destination = File.Open(partial, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await CopyBoundedAsync(source, destination, manifest.Updater.Size, token).ConfigureAwait(false);
                }
                if (!await MatchesFileAsync(partial, manifest.Updater.Size, manifest.Updater.Sha256, token).ConfigureAwait(false))
                {
                    throw new InvalidDataException("Incremental updater checksum mismatch.");
                }
                File.Move(partial, updaterPath, overwrite: true);
            }
            progress?.Report(100);
            return updaterPath;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or HttpRequestException or JsonException or InvalidDataException || (exception is OperationCanceledException && !token.IsCancellationRequested))
        {
            token.ThrowIfCancellationRequested();
            // The complete Setup remains authoritative and is verified independently.
            return null;
        }
    }

    private async Task<bool> IsIncrementalReadyAsync(WindowsUpdateManifest update, CancellationToken token)
    {
        if (update.Incremental is null || _installationDirectory is null)
        {
            return false;
        }
        try
        {
            string directory = IncrementalDirectory(update);
            RequireSafeDirectory(directory);
            string path = Path.Combine(directory, "manifest.json");
            if (!File.Exists(path) || new FileInfo(path).Length > MaximumManifestBytes
                || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                return false;
            }
            IncrementalManifest manifest = ParseIncrementalManifest(await File.ReadAllBytesAsync(path, token).ConfigureAwait(false), update);
            if (!await MatchesFileAsync(Path.Combine(directory, manifest.Updater.Name), manifest.Updater.Size, manifest.Updater.Sha256, token)
                .ConfigureAwait(false))
            {
                return false;
            }
            foreach (IncrementalFile file in manifest.Files)
            {
                if (!await MatchesFileAsync(Path.Combine(directory, "files", file.Sha256.ToLowerInvariant() + ".bin"),
                    file.Size, file.Sha256, token).ConfigureAwait(false))
                {
                    return false;
                }
            }
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return false;
        }
    }

    public static string InstallerArguments(string installerPath, bool silent = false)
    {
        string arguments = silent ? "/S /RESTARTSIDEY" : string.Empty;
        if (installerPath.EndsWith("-Update.exe", StringComparison.OrdinalIgnoreCase))
        {
            string cache = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(installerPath))!, "files");
            if (cache.Contains('"'))
            {
                throw new InvalidDataException("Invalid update cache path.");
            }
            arguments += $" /UPDATECACHE=\"{cache}\"";
        }
        return arguments.Trim();
    }
}
