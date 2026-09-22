using System.ComponentModel;
using Sidey.Core.Abstractions;
using Sidey.Infrastructure.Authentication;

namespace Sidey.Platform.Windows.Tests;

public sealed class WindowsCredentialStoreTests
{
    private const string TestPrefix = "SIDEY-Test/";
    private const string RootTarget = TestPrefix + nameof(CredentialKey.SupabaseSession);
    private const string FirebaseRootTarget = TestPrefix + nameof(CredentialKey.FirebaseRealtimeSession);

    [Fact]
    public async Task ExistingSingleCredentialSessionIsReadWithoutMigration()
    {
        var backend = new FakeCredentialManager();
        backend.Values[RootTarget] = "existing-session";

        Assert.Equal("existing-session", await backend.CreateStore().ReadAsync(CredentialKey.SupabaseSession));
        Assert.Single(backend.Values);
        Assert.Empty(backend.Writes);
    }

    [Fact]
    public async Task LargeUnicodeSessionSurvivesNewStoreAndReplacementWithoutExceedingNativeBlobLimit()
    {
        var backend = new FakeCredentialManager();
        string session = new string('한', 1199) + "🐹" + new string('x', 7000);
        WindowsCredentialStore store = backend.CreateStore();

        await store.WriteAsync(CredentialKey.SupabaseSession, session);

        Assert.Equal(session, await backend.CreateStore().ReadAsync(CredentialKey.SupabaseSession));
        Assert.All(backend.Values.Values, value => Assert.True(value.Length * sizeof(char) <= 2560));
        Assert.Equal(RootTarget, backend.Writes[^1]);

        await store.WriteAsync(CredentialKey.SupabaseSession, "refreshed-short-session");

        Assert.Equal("refreshed-short-session", await backend.CreateStore().ReadAsync(CredentialKey.SupabaseSession));
        Assert.Single(backend.Values);
    }

    [Fact]
    public async Task FirebaseSessionUsesIndependentAtomicChunkGeneration()
    {
        var backend = new FakeCredentialManager();
        WindowsCredentialStore store = backend.CreateStore();
        string supabase = new('s', 5000);
        string firebase = new('f', 7000);
        await store.WriteAsync(CredentialKey.SupabaseSession, supabase);

        await store.WriteAsync(CredentialKey.FirebaseRealtimeSession, firebase);

        Assert.Equal(supabase, await store.ReadAsync(CredentialKey.SupabaseSession));
        Assert.Equal(firebase, await store.ReadAsync(CredentialKey.FirebaseRealtimeSession));
        Assert.Contains(RootTarget, backend.Values.Keys);
        Assert.Contains(FirebaseRootTarget, backend.Values.Keys);
        Assert.All(backend.Values.Values, value => Assert.True(value.Length * sizeof(char) <= 2560));

        await store.DeleteAsync(CredentialKey.FirebaseRealtimeSession);

        Assert.Null(await store.ReadAsync(CredentialKey.FirebaseRealtimeSession));
        Assert.Equal(supabase, await store.ReadAsync(CredentialKey.SupabaseSession));
        Assert.DoesNotContain(backend.Values.Keys, key =>
            key.StartsWith(FirebaseRootTarget, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedChunkOrRootWritePreservesPreviouslyCommittedSession(bool failRoot)
    {
        var backend = new FakeCredentialManager();
        WindowsCredentialStore store = backend.CreateStore();
        string previous = new string('p', 6000);
        await store.WriteAsync(CredentialKey.SupabaseSession, previous);
        KeyValuePair<string, string>[] saved = [.. backend.Values.OrderBy(pair => pair.Key)];
        int chunksWritten = 0;
        backend.BeforeWrite = target =>
        {
            if ((failRoot && target == RootTarget) || (!failRoot && target != RootTarget && ++chunksWritten == 2))
            {
                throw new Win32Exception(5);
            }
        };

        await Assert.ThrowsAsync<Win32Exception>(() =>
            store.WriteAsync(CredentialKey.SupabaseSession, new string('n', 7000)).AsTask());

        Assert.Equal(previous, await backend.CreateStore().ReadAsync(CredentialKey.SupabaseSession));
        Assert.Equal(saved, backend.Values.OrderBy(pair => pair.Key));
    }

    [Fact]
    public async Task FailedMigrationPreservesLegacySession()
    {
        var backend = new FakeCredentialManager();
        backend.Values[RootTarget] = "legacy-session";
        backend.BeforeWrite = target =>
        {
            if (target == RootTarget)
            {
                throw new Win32Exception(5);
            }
        };

        await Assert.ThrowsAsync<Win32Exception>(() =>
            backend.CreateStore().WriteAsync(CredentialKey.SupabaseSession, new string('x', 5000)).AsTask());

        Assert.Equal("legacy-session", await backend.CreateStore().ReadAsync(CredentialKey.SupabaseSession));
        Assert.Single(backend.Values);
    }

    [Fact]
    public async Task CancellationDuringChunkWritesPreservesSessionAndRemovesUncommittedChunks()
    {
        var backend = new FakeCredentialManager();
        backend.Values[RootTarget] = "previous-session";
        using var cancellation = new CancellationTokenSource();
        int writes = 0;
        backend.BeforeWrite = _ =>
        {
            if (++writes == 2)
            {
                cancellation.Cancel();
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            backend.CreateStore().WriteAsync(CredentialKey.SupabaseSession, new string('x', 5000), cancellation.Token).AsTask());

        Assert.Equal("previous-session", await backend.CreateStore().ReadAsync(CredentialKey.SupabaseSession));
        Assert.Single(backend.Values);
    }

    [Fact]
    public async Task CleanupFailureDoesNotInvalidateNewSessionOrResurrectItAfterLogout()
    {
        var backend = new FakeCredentialManager();
        WindowsCredentialStore store = backend.CreateStore();
        await store.WriteAsync(CredentialKey.SupabaseSession, new string('p', 6000));
        backend.BeforeDelete = target =>
        {
            if (target != RootTarget)
            {
                throw new Win32Exception(5);
            }
        };

        string replacement = new string('n', 7000);
        await store.WriteAsync(CredentialKey.SupabaseSession, replacement);
        Assert.Equal(replacement, await backend.CreateStore().ReadAsync(CredentialKey.SupabaseSession));

        await store.DeleteAsync(CredentialKey.SupabaseSession);

        Assert.Null(await backend.CreateStore().ReadAsync(CredentialKey.SupabaseSession));
        Assert.NotEmpty(backend.Values);
    }

    [Fact]
    public async Task FailedRootDeletionLeavesEntireSessionReadable()
    {
        var backend = new FakeCredentialManager();
        WindowsCredentialStore store = backend.CreateStore();
        string session = new string('p', 6000);
        await store.WriteAsync(CredentialKey.SupabaseSession, session);
        backend.BeforeDelete = target =>
        {
            if (target == RootTarget)
            {
                throw new Win32Exception(5);
            }
        };

        await Assert.ThrowsAsync<Win32Exception>(() => store.DeleteAsync(CredentialKey.SupabaseSession).AsTask());

        Assert.Equal(session, await backend.CreateStore().ReadAsync(CredentialKey.SupabaseSession));
    }

    [Theory]
    [InlineData("SIDEY:CHUNKED-SESSION:1:bad:3")]
    [InlineData("SIDEY:CHUNKED-SESSION:1:0123456789abcdef0123456789abcdef:0")]
    [InlineData("SIDEY:CHUNKED-SESSION:1:0123456789abcdef0123456789abcdef:2048")]
    public async Task MalformedManifestFailsInsteadOfLookingLikeMissingCredentials(string manifest)
    {
        var backend = new FakeCredentialManager();
        backend.Values[RootTarget] = manifest;

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            backend.CreateStore().ReadAsync(CredentialKey.SupabaseSession).AsTask());
    }

    [Fact]
    public async Task MissingChunkFailsInsteadOfReturningPartialOrMissingSession()
    {
        var backend = new FakeCredentialManager();
        WindowsCredentialStore store = backend.CreateStore();
        await store.WriteAsync(CredentialKey.SupabaseSession, new string('p', 5000));
        backend.Values.Remove(backend.Values.Keys.First(key => key != RootTarget));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            backend.CreateStore().ReadAsync(CredentialKey.SupabaseSession).AsTask());
    }

    [Fact]
    public async Task SessionDeletionRemovesAllChunksAndLeavesInviteCodesIntact()
    {
        var backend = new FakeCredentialManager();
        WindowsCredentialStore store = backend.CreateStore();
        var room = Guid.NewGuid();
        await store.WriteInviteCodeAsync(room, "invite-code");
        await store.WriteAsync(CredentialKey.SupabaseSession, new string('p', 6000));

        await store.DeleteAsync(CredentialKey.SupabaseSession);
        await store.DeleteAsync(CredentialKey.SupabaseSession);

        Assert.Null(await store.ReadAsync(CredentialKey.SupabaseSession));
        Assert.Equal("invite-code", await store.ReadInviteCodeAsync(room));
        Assert.Single(backend.Values);
    }

    [Fact]
    public async Task AnotherStoreCannotReadAnUncommittedGeneration()
    {
        var backend = new FakeCredentialManager();
        backend.Values[RootTarget] = "previous-session";
        var writingChunk = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.BeforeWriteAsync = async target =>
        {
            if (target != RootTarget)
            {
                writingChunk.TrySetResult();
                await continueWrite.Task;
            }
        };

        string session = new string('n', 5000);
        Task write = backend.CreateStore().WriteAsync(CredentialKey.SupabaseSession, session).AsTask();
        Task<string?>? read = null;
        try
        {
            await writingChunk.Task.WaitAsync(TimeSpan.FromSeconds(10));
            read = backend.CreateStore().ReadAsync(CredentialKey.SupabaseSession).AsTask();
            Assert.False(read.IsCompleted);
            Assert.Equal("previous-session", backend.Values[RootTarget]);
        }
        finally
        {
            continueWrite.TrySetResult();
            await write.WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.Equal(session, await read!.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task NativeCredentialManagerRoundTripUsesOnlyIsolatedTestNamespace()
    {
        string prefix = $"SIDEY-Diagnostic/CredentialStore/{Guid.NewGuid():N}/";
        var store = new WindowsCredentialStore(prefix);
        string session = new string('한', 1199) + "🐹" + new string('x', 7000);
        try
        {
            await store.WriteAsync(CredentialKey.SupabaseSession, session);
            Assert.Equal(session, await new WindowsCredentialStore(prefix).ReadAsync(CredentialKey.SupabaseSession));
            await store.WriteAsync(CredentialKey.SupabaseSession, new string('r', 4000));
            Assert.Equal(new string('r', 4000), await new WindowsCredentialStore(prefix).ReadAsync(CredentialKey.SupabaseSession));
        }
        finally
        {
            await store.DeleteAsync(CredentialKey.SupabaseSession);
        }

        Assert.Null(await new WindowsCredentialStore(prefix).ReadAsync(CredentialKey.SupabaseSession));
    }

    private sealed class FakeCredentialManager
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);
        public List<string> Writes { get; } = [];
        public Action<string>? BeforeWrite { get; set; }
        public Func<string, ValueTask>? BeforeWriteAsync { get; set; }
        public Action<string>? BeforeDelete { get; set; }

        public WindowsCredentialStore CreateStore() => new(TestPrefix, Read, Write, Delete);

        private ValueTask<string?> Read(string target, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Values.GetValueOrDefault(target));
        }

        private async ValueTask Write(string target, string value, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BeforeWrite?.Invoke(target);
            if (BeforeWriteAsync is not null)
            {
                await BeforeWriteAsync(target);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (value.Length * sizeof(char) > 2560)
            {
                throw new Win32Exception(1783);
            }

            Writes.Add(target);
            Values[target] = value;
        }

        private ValueTask Delete(string target, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BeforeDelete?.Invoke(target);
            Values.Remove(target);
            return ValueTask.CompletedTask;
        }
    }
}
