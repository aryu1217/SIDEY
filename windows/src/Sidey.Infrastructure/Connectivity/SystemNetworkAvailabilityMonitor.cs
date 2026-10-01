using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Windows.Networking.Connectivity;

namespace Sidey.Infrastructure.Connectivity;

internal interface INetworkAvailabilityMonitor : IDisposable
{
    public bool IsAvailable { get; }

    public event Action<bool>? AvailabilityChanged;
    public event Action? PathChanged;

    public void Start();
    public void Refresh();
}

internal sealed class SystemNetworkAvailabilityMonitor : INetworkAvailabilityMonitor
{
    private readonly Lock _refreshGate = new();
    private readonly Func<bool> _readAvailability;
    private int _available;
    private int _started;

    public SystemNetworkAvailabilityMonitor(Func<bool>? readAvailability = null)
    {
        _readAvailability = readAvailability ?? ReadInternetAvailability;
        _available = _readAvailability() ? 1 : 0;
    }

    public bool IsAvailable => Volatile.Read(ref _available) != 0;

    public event Action<bool>? AvailabilityChanged;
    public event Action? PathChanged;

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        NetworkInformation.NetworkStatusChanged += OnNetworkStatusChanged;
        Refresh();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _started, 0) == 0)
        {
            return;
        }

        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        NetworkInformation.NetworkStatusChanged -= OnNetworkStatusChanged;
    }

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs args)
    {
        _ = sender;
        _ = args;
        Refresh();
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs args) => Refresh(notifyPathChange: true);

    private void OnNetworkStatusChanged(object sender) => Refresh(notifyPathChange: true);

    public void Refresh() => Refresh(notifyPathChange: false);

    internal void Refresh(bool notifyPathChange)
    {
        lock (_refreshGate)
        {
            if (Volatile.Read(ref _started) == 0)
                return;
            bool wasAvailable = IsAvailable;
            Update(_readAvailability());
            if (notifyPathChange && wasAvailable && IsAvailable)
                PathChanged?.Invoke();
        }
    }

    internal static bool CanAttemptConnection(NetworkConnectivityLevel? level) =>
        level is null or NetworkConnectivityLevel.LocalAccess or NetworkConnectivityLevel.InternetAccess
            or NetworkConnectivityLevel.ConstrainedInternetAccess;

    private static bool ReadInternetAvailability()
    {
        try
        {
            // NCSI is a hint: an unknown profile after resume is not proof that SIDEY is unreachable.
            return CanAttemptConnection(NetworkInformation.GetInternetConnectionProfile()?.GetNetworkConnectivityLevel());
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException)
        {
            // If Windows cannot query connectivity, require a routed adapter as a fallback.
            try
            {
                return NetworkInterface.GetAllNetworkInterfaces().Any(adapter =>
                    adapter.OperationalStatus == OperationalStatus.Up
                    && adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback
                    && adapter.GetIPProperties().GatewayAddresses.Any());
            }
            catch (NetworkInformationException) { return true; } // An unavailable query is not proof of disconnection.
        }
    }

    private void Update(bool isAvailable)
    {
        int next = isAvailable ? 1 : 0;
        if (Interlocked.Exchange(ref _available, next) == next)
        {
            return;
        }

        AvailabilityChanged?.Invoke(isAvailable);
    }
}
