using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using Sidey.Core.Abstractions;

namespace Sidey.Infrastructure.Authentication;

public sealed class WindowsCredentialStore : ICredentialStore
{
    private const string Prefix = "SIDEY/";
    private const string ManifestPrefix = "SIDEY:CHUNKED-SESSION:1:";
    private const int ChunkCharacters = 1200;
    private const int MaximumChunks = 1024;
    private static readonly SemaphoreSlim s_sessionGate = new(1, 1);
    private readonly string _prefix;
    private readonly Func<string, CancellationToken, ValueTask<string?>> _read;
    private readonly Func<string, string, CancellationToken, ValueTask> _write;
    private readonly Func<string, CancellationToken, ValueTask> _delete;

    public WindowsCredentialStore() : this(Prefix)
    {
    }

    internal WindowsCredentialStore(string prefix) : this(prefix, ReadTargetAsync, WriteTargetAsync, DeleteTargetAsync)
    {
    }

    internal WindowsCredentialStore(
        string prefix,
        Func<string, CancellationToken, ValueTask<string?>> read,
        Func<string, string, CancellationToken, ValueTask> write,
        Func<string, CancellationToken, ValueTask> delete)
    {
        _prefix = prefix;
        _read = read;
        _write = write;
        _delete = delete;
    }

    public ValueTask<string?> ReadAsync(CredentialKey key, CancellationToken cancellationToken = default) =>
        ReadSessionAsync(SessionTarget(key), cancellationToken);

    public ValueTask WriteAsync(CredentialKey key, string value, CancellationToken cancellationToken = default) =>
        WriteSessionAsync(SessionTarget(key), value, cancellationToken);

    public ValueTask DeleteAsync(CredentialKey key, CancellationToken cancellationToken = default) =>
        DeleteSessionAsync(SessionTarget(key), cancellationToken);

    public ValueTask<string?> ReadInviteCodeAsync(Guid roomId, CancellationToken cancellationToken = default) =>
        _read(InviteTarget(roomId), cancellationToken);

    public ValueTask WriteInviteCodeAsync(Guid roomId, string inviteCode, CancellationToken cancellationToken = default) =>
        _write(InviteTarget(roomId), inviteCode, cancellationToken);

    public ValueTask DeleteInviteCodeAsync(Guid roomId, CancellationToken cancellationToken = default) =>
        _delete(InviteTarget(roomId), cancellationToken);

    private async ValueTask<string?> ReadSessionAsync(
        string target,
        CancellationToken cancellationToken)
    {
        await s_sessionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string? root = await _read(target, cancellationToken).ConfigureAwait(false);
            SessionManifest? manifest = ParseManifest(root);
            if (manifest is null)
            {
                return root;
            }

            string[] chunks = new string[manifest.Count];
            for (int index = 0; index < chunks.Length; index++)
            {
                chunks[index] = await _read(ChunkTarget(target, manifest, index), cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException("Stored session is incomplete.");
            }

            return string.Concat(chunks);
        }
        finally
        {
            s_sessionGate.Release();
        }
    }

    private async ValueTask WriteSessionAsync(
        string target,
        string value,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(value);
        int count = checked((int)(((long)value.Length + ChunkCharacters - 1) / ChunkCharacters));
        if (count > MaximumChunks)
        {
            throw new ArgumentException("Session exceeds the supported credential size.", nameof(value));
        }

        await s_sessionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SessionManifest? previous = ParseManifest(await _read(target, cancellationToken).ConfigureAwait(false));
            SessionManifest? next = value.Length > ChunkCharacters ? new(Guid.NewGuid(), count) : null;
            try
            {
                if (next is not null)
                {
                    for (int index = 0; index < next.Count; index++)
                    {
                        int offset = index * ChunkCharacters;
                        string chunk = value.Substring(offset, Math.Min(ChunkCharacters, value.Length - offset));
                        await _write(ChunkTarget(target, next, index), chunk, cancellationToken).ConfigureAwait(false);
                    }
                }

                // Publish the root only after every new chunk has been persisted.
                // Failed writes leave the previous root and its generation readable.
                string root = next is null
                    ? value
                    : $"{ManifestPrefix}{next.Generation:N}:{next.Count.ToString(CultureInfo.InvariantCulture)}";
                await _write(target, root, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await CleanupChunksAsync(target, next).ConfigureAwait(false);
                throw;
            }

            await CleanupChunksAsync(target, previous).ConfigureAwait(false);
        }
        finally
        {
            s_sessionGate.Release();
        }
    }

    private async ValueTask DeleteSessionAsync(
        string target,
        CancellationToken cancellationToken)
    {
        await s_sessionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SessionManifest? previous = ParseManifest(await _read(target, cancellationToken).ConfigureAwait(false));
            // Remove the root first so incomplete cleanup cannot revive the session.
            await _delete(target, cancellationToken).ConfigureAwait(false);
            await CleanupChunksAsync(target, previous).ConfigureAwait(false);
        }
        finally
        {
            s_sessionGate.Release();
        }
    }

    private async ValueTask CleanupChunksAsync(string target, SessionManifest? manifest)
    {
        if (manifest is null)
        {
            return;
        }

        for (int index = 0; index < manifest.Count; index++)
        {
            try
            {
                await _delete(ChunkTarget(target, manifest, index), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Win32Exception)
            {
                // Session reads ignore orphaned chunks without a root. Cleanup must not
                // turn a committed sign-in/sign-out into a reported authentication failure.
            }
        }
    }

    private static SessionManifest? ParseManifest(string? value)
    {
        if (value is null || !value.StartsWith(ManifestPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        string[] parts = value[ManifestPrefix.Length..].Split(':');
        if (parts.Length != 2 || !Guid.TryParseExact(parts[0], "N", out Guid generation)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int count)
            || count is < 1 or > MaximumChunks)
        {
            throw new InvalidDataException("Stored session manifest is invalid.");
        }

        return new SessionManifest(generation, count);
    }

    private string SessionTarget(CredentialKey key) => _prefix + key;

    private static string ChunkTarget(string target, SessionManifest manifest, int index) =>
        $"{target}/{manifest.Generation:N}/{index.ToString(CultureInfo.InvariantCulture)}";

    private sealed record SessionManifest(Guid Generation, int Count);

    private static ValueTask<string?> ReadTargetAsync(string target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureWindows();
        if (!NativeMethods.CredRead(target, CredentialType.Generic, 0, out nint pointer))
        {
            int error = Marshal.GetLastPInvokeError();
            return error == NativeMethods.ErrorNotFound
                ? ValueTask.FromResult<string?>(null)
                : ValueTask.FromException<string?>(new Win32Exception(error, "Credential Manager read failed."));
        }

        try
        {
            NativeCredential credential = Marshal.PtrToStructure<NativeCredential>(pointer);
            if (credential.CredentialBlob == nint.Zero || credential.CredentialBlobSize == 0)
            {
                return ValueTask.FromResult<string?>(string.Empty);
            }

            return ValueTask.FromResult<string?>(Marshal.PtrToStringUni(
                credential.CredentialBlob,
                checked((int)credential.CredentialBlobSize / sizeof(char))));
        }
        finally
        {
            NativeMethods.CredFree(pointer);
        }
    }

    private static ValueTask WriteTargetAsync(
        string target,
        string value,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(value);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureWindows();
        uint bytes = checked((uint)(value.Length * sizeof(char)));
        nint pointer = Marshal.StringToCoTaskMemUni(value);
        try
        {
            var credential = new NativeCredential
            {
                Type = CredentialType.Generic,
                TargetName = target,
                CredentialBlobSize = bytes,
                CredentialBlob = pointer,
                Persist = CredentialPersistence.LocalMachine,
                UserName = "SIDEY",
            };
            if (!NativeMethods.CredWrite(ref credential, 0))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Credential Manager write failed.");
            }

            return ValueTask.CompletedTask;
        }
        finally
        {
            Marshal.ZeroFreeCoTaskMemUnicode(pointer);
        }
    }

    private static ValueTask DeleteTargetAsync(string target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureWindows();
        if (!NativeMethods.CredDelete(target, CredentialType.Generic, 0))
        {
            int error = Marshal.GetLastPInvokeError();
            if (error != NativeMethods.ErrorNotFound)
            {
                return ValueTask.FromException(new Win32Exception(
                    error,
                    "Credential Manager delete failed."));
            }
        }

        return ValueTask.CompletedTask;
    }

    private string InviteTarget(Guid roomId) => $"{_prefix}Invite/{roomId:D}";

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows Credential Manager is required.");
        }
    }

    private enum CredentialType : uint
    {
        Generic = 1,
    }

    private enum CredentialPersistence : uint
    {
        LocalMachine = 2,
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public CredentialType Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public nint CredentialBlob;
        public CredentialPersistence Persist;
        public uint AttributeCount;
        public nint Attributes;
        public string? TargetAlias;
        public string UserName;
    }

    private static class NativeMethods
    {
        public const int ErrorNotFound = 1168;
        [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CredRead(
            string target,
            CredentialType type,
            uint reservedFlag,
            out nint credential);

        [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CredWrite(ref NativeCredential credential, uint flags);

        [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CredDelete(string target, CredentialType type, uint flags);

        [DllImport("advapi32.dll")]
        public static extern void CredFree(nint buffer);
    }
}
