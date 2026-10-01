using System.Runtime.ExceptionServices;

namespace Sidey.Platform.Windows.Overlay;

public sealed class NativeOverlayWindowThread : IDisposable
{
    public const int MaximumTargetHotspots = 11;
    private static readonly TimeSpan s_startupTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan s_shutdownTimeout = TimeSpan.FromSeconds(5);

    private readonly NativePixelRect _initialWorldBounds;
    private readonly NativePixelRect _initialHotspotBounds;
    private readonly ManualResetEventSlim _started = new(false);
    private readonly Thread _thread;
    private readonly Func<nint, IDisposable?>? _initializeWorld;
    private readonly Action? _hotspotActivated;
    private readonly Action? _hotspotDoubleClicked;
    private readonly Action<bool>? _hotspotRightClicked;
    private readonly Action<int>? _targetHotspotActivated;
    private readonly Action<NativeDragPointerEvent>? _hotspotDragged;
    private readonly Action<int, NativeDragPointerEvent>? _targetHotspotDragged;
    private NativeOverlayWindow? _worldWindow;
    private NativeOverlayWindow? _hotspotWindow;
    private readonly NativeOverlayWindow?[] _targetHotspotWindows = new NativeOverlayWindow?[MaximumTargetHotspots];
    private readonly bool[] _targetHotspotVisible = new bool[MaximumTargetHotspots];
    private bool _windowsVisible = true;
    private IDisposable? _threadResource;
    private ExceptionDispatchInfo? _startupFailure;
    private ExceptionDispatchInfo? _shutdownFailure;
    private bool _disposed;

    private NativeOverlayWindowThread(
        NativePixelRect initialWorldBounds,
        NativePixelRect initialHotspotBounds,
        Func<nint, IDisposable?>? initializeWorld,
        Action? hotspotActivated,
        Action? hotspotDoubleClicked,
        Action<bool>? hotspotRightClicked,
        Action<int>? targetHotspotActivated,
        Action<NativeDragPointerEvent>? hotspotDragged,
        Action<int, NativeDragPointerEvent>? targetHotspotDragged)
    {
        _initialWorldBounds = initialWorldBounds;
        _initialHotspotBounds = initialHotspotBounds;
        _initializeWorld = initializeWorld;
        _hotspotActivated = hotspotActivated;
        _hotspotDoubleClicked = hotspotDoubleClicked;
        _hotspotRightClicked = hotspotRightClicked;
        _targetHotspotActivated = targetHotspotActivated;
        _hotspotDragged = hotspotDragged;
        _targetHotspotDragged = targetHotspotDragged;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "SIDEY Overlay",
        };
        _thread.SetApartmentState(ApartmentState.STA);
    }

    public nint WorldWindowHandle => RequiredWorldWindow.Handle;
    public nint HotspotWindowHandle => RequiredHotspotWindow.Handle;

    public static NativeOverlayWindowThread Start(
        NativePixelRect initialWorldBounds,
        NativePixelRect initialHotspotBounds,
        Func<nint, IDisposable?>? initializeWorld = null,
        Action? hotspotActivated = null,
        Action? hotspotDoubleClicked = null,
        Action<bool>? hotspotRightClicked = null,
        Action<int>? targetHotspotActivated = null,
        Action<NativeDragPointerEvent>? hotspotDragged = null,
        Action<int, NativeDragPointerEvent>? targetHotspotDragged = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The native overlay thread requires Windows.");
        }

        var owner = new NativeOverlayWindowThread(
            initialWorldBounds,
            initialHotspotBounds,
            initializeWorld,
            hotspotActivated,
            hotspotDoubleClicked,
            hotspotRightClicked,
            targetHotspotActivated,
            hotspotDragged,
            targetHotspotDragged);
        owner._thread.Start();
        if (!owner._started.Wait(s_startupTimeout))
        {
            throw new TimeoutException("Timed out while creating SIDEY overlay windows.");
        }

        if (owner._startupFailure is { } startupFailure)
        {
            owner.Dispose();
            startupFailure.Throw();
        }

        return owner;
    }

    public void SetWorldBounds(NativePixelRect bounds, bool visible = true) =>
        RequiredWorldWindow.SetBounds(bounds, visible);

    public void SetHotspotBounds(NativePixelRect bounds, bool visible = true) =>
        RequiredHotspotWindow.SetBounds(bounds, visible);

    public void SetTargetHotspotBounds(int index, NativePixelRect bounds, bool visible)
    {
        if (index < 0 || index >= MaximumTargetHotspots)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        _targetHotspotVisible[index] = visible;
        RequiredTargetHotspot(index).SetBounds(bounds, visible && _windowsVisible);
    }

    public void HideTargetHotspots()
    {
        for (int index = 0; index < MaximumTargetHotspots; index++)
        {
            _targetHotspotVisible[index] = false;
            RequiredTargetHotspot(index).SetVisible(false);
        }
    }

    public void SetVisible(bool visible)
    {
        _windowsVisible = visible;
        RequiredWorldWindow.SetVisible(visible);
        RequiredHotspotWindow.SetVisible(visible);
        for (int index = 0; index < MaximumTargetHotspots; index++)
        {
            RequiredTargetHotspot(index).SetVisible(visible && _targetHotspotVisible[index]);
        }
    }

    public void EnsureTopmost()
    {
        RequiredWorldWindow.EnsureTopmost();
        RequiredHotspotWindow.EnsureTopmost();
        foreach (NativeOverlayWindow? hotspot in _targetHotspotWindows)
        {
            hotspot?.EnsureTopmost();
        }
    }

    public void YieldBehind(nint window)
    {
        RequiredWorldWindow.YieldBehind(window);
        RequiredHotspotWindow.YieldBehind(window);
        foreach (NativeOverlayWindow? hotspot in _targetHotspotWindows)
        {
            hotspot?.YieldBehind(window);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            Interlocked.Exchange(ref _threadResource, null)?.Dispose();
        }
        catch (Exception exception)
        {
            _shutdownFailure = ExceptionDispatchInfo.Capture(exception);
        }

        _worldWindow?.Dispose();
        _hotspotWindow?.Dispose();
        foreach (NativeOverlayWindow? hotspot in _targetHotspotWindows)
        {
            hotspot?.Dispose();
        }
        if (_thread.IsAlive && !_thread.Join(s_shutdownTimeout))
        {
            throw new TimeoutException("SIDEY overlay thread did not shut down within five seconds.");
        }

        _started.Dispose();
        _shutdownFailure?.Throw();
    }

    private NativeOverlayWindow RequiredWorldWindow =>
        _worldWindow ?? throw new InvalidOperationException("World overlay window is not available.");

    private NativeOverlayWindow RequiredHotspotWindow =>
        _hotspotWindow ?? throw new InvalidOperationException("Hotspot window is not available.");

    private NativeOverlayWindow RequiredTargetHotspot(int index) =>
        _targetHotspotWindows[index]
        ?? throw new InvalidOperationException("Target hotspot window is not available.");

    private void Run()
    {
        try
        {
            _worldWindow = NativeOverlayWindow.Create(NativeOverlayWindowRole.World, _initialWorldBounds);
            _hotspotWindow = NativeOverlayWindow.Create(
                NativeOverlayWindowRole.Hotspot,
                _initialHotspotBounds,
                _hotspotActivated,
                _hotspotDoubleClicked,
                _hotspotRightClicked,
                _hotspotDragged);
            for (int index = 0; index < MaximumTargetHotspots; index++)
            {
                int capturedIndex = index;
                _targetHotspotWindows[index] = NativeOverlayWindow.Create(
                    NativeOverlayWindowRole.Hotspot,
                    _initialHotspotBounds,
                    () => _targetHotspotActivated?.Invoke(capturedIndex),
                    dragPointer: pointer => _targetHotspotDragged?.Invoke(capturedIndex, pointer));
                _targetHotspotWindows[index]!.SetVisible(false);
            }
            _threadResource = _initializeWorld?.Invoke(_worldWindow.Handle);
            _started.Set();
            NativeMessageLoop.Run();
        }
        catch (Exception exception)
        {
            _startupFailure = ExceptionDispatchInfo.Capture(exception);
            _started.Set();
        }
        finally
        {
            try
            {
                Interlocked.Exchange(ref _threadResource, null)?.Dispose();
            }
            catch (Exception exception)
            {
                _shutdownFailure ??= ExceptionDispatchInfo.Capture(exception);
            }

            _worldWindow?.Dispose();
            _hotspotWindow?.Dispose();
            foreach (NativeOverlayWindow? hotspot in _targetHotspotWindows)
            {
                hotspot?.Dispose();
            }
        }
    }
}
