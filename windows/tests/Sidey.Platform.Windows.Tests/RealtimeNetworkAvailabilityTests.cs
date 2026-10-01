using Sidey.Core.Abstractions;
using Sidey.Core.Domain;
using Sidey.Infrastructure;
using Windows.Networking.Connectivity;

namespace Sidey.Platform.Windows.Tests;

public sealed class RealtimeNetworkAvailabilityTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData(NetworkConnectivityLevel.None, false)]
    [InlineData(NetworkConnectivityLevel.LocalAccess, true)]
    [InlineData(NetworkConnectivityLevel.ConstrainedInternetAccess, true)]
    [InlineData(NetworkConnectivityLevel.InternetAccess, true)]
    public void OnlyDefinitePathLossBlocksServiceAttempts(NetworkConnectivityLevel? level, bool expected) =>
        Assert.Equal(expected, SystemNetworkAvailabilityMonitor.CanAttemptConnection(level));

    [Fact]
    public void PathChangesCanRequestRecoveryWithoutAnOfflineOnlineTransition()
    {
        using var monitor = new SystemNetworkAvailabilityMonitor(() => true);
        int hints = 0;
        monitor.PathChanged += () => hints++;
        monitor.Start();
        monitor.Refresh();
        Assert.Equal(0, hints);
        monitor.Refresh(notifyPathChange: true);
        Assert.Equal(1, hints);
        monitor.Dispose();
        monitor.Refresh(notifyPathChange: true);
        Assert.Equal(1, hints);
    }

    [Fact]
    public void MonitorRefreshEmitsOnlyChangedConnectivityAndStopsAfterDisposal()
    {
        bool available = true;
        using var monitor = new SystemNetworkAvailabilityMonitor(() => available);
        var changes = new List<bool>();
        monitor.AvailabilityChanged += changes.Add;
        monitor.Start();
        available = false;
        monitor.Refresh();
        monitor.Refresh();
        Assert.False(monitor.IsAvailable);
        available = true;
        monitor.Refresh();
        Assert.True(monitor.IsAvailable);
        monitor.Dispose();
        available = false;
        monitor.Refresh();
        Assert.Equal(new[] { false, true }, changes);
    }

    [Fact]
    public async Task NetworkIndicatorCannotPreventAnActualConnectionAttempt()
    {
        var network = new FakeNetworkAvailabilityMonitor(isAvailable: false);
        var sessions = new CountingSessionAccessor();
        await using (var transport = new SupabaseRealtimeTransport(
            new SupabaseRuntimeConfiguration(new Uri("http://localhost"), "test-key"),
            sessions,
            network))
        {
            await transport.SynchronizeAsync(
                new Dictionary<Guid, long>(),
                activeRoomId: null,
                PresenceState.Online,
                CancellationToken.None);

            Assert.True(sessions.ReadCount > 0);
            Assert.Equal(RealtimeConnectionStatus.Disconnected, transport.ConnectionStatus);
            Assert.True(network.Started);
        }

        Assert.True(network.Disposed);
    }

    private sealed class FakeNetworkAvailabilityMonitor(bool isAvailable)
        : INetworkAvailabilityMonitor
    {
        public bool IsAvailable { get; } = isAvailable;
        public bool Started { get; private set; }
        public bool Disposed { get; private set; }

        public event Action<bool>? AvailabilityChanged
        {
            add { }
            remove { }
        }

        public void Start() => Started = true;
        public event Action? PathChanged { add { } remove { } }
        public void Refresh() { }

        public void Dispose()
        {
            Disposed = true;
        }
    }

    private sealed class CountingSessionAccessor : IAuthSessionAccessor
    {
        public int ReadCount { get; private set; }

        public ValueTask<StoredSupabaseSession?> GetStoredSessionAsync(
            CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;
            ReadCount++;
            return ValueTask.FromResult<StoredSupabaseSession?>(null);
        }
    }
}
