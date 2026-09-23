using System.Diagnostics;
using System.Globalization;
using Sidey.Core.Abstractions;
using Sidey.Core.Domain;
using Sidey.Core.Overlay;
using Sidey.Platform.Windows;

namespace Sidey.Overlay.Rendering;

public sealed record NativePixelWorldSessionOptions(
    IReadOnlySet<string>? ValidationCharacterIds = null,
    bool CollectValidationMetrics = false,
    string? ValidationMetricsPath = null,
    Action<int>? MessageBubblesPresented = null,
    Action<string>? Diagnostic = null,
    Action<string, Exception>? DiagnosticFailure = null,
    Action<double, double, long, long>? RendererPerformanceSampled = null,
    Func<bool>? AnimationsEnabled = null,
    Action<string, long>? CharacterImpact = null,
    Action<Guid?>? TreeMovementToggleRequested = null);

public sealed class NativePixelWorldSession : IOverlayHost, IDisposable
{
    public bool IsSelfStunned => _renderer.IsSelfStunned;
    private static readonly TimeSpan s_throwTargetingDuration = TimeSpan.FromSeconds(10);

    private readonly Lock _throwGate = new();
    private readonly CharacterRightClickState _rightClicks = new();
    private Action<Guid?>? _requestTreeMovementToggle;
    private Guid? _rightClickRoomId;
    private string? _selfCharacterId;
    private static double NowSeconds => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;

    private readonly NativeOverlayWindowThread _windows;
    private readonly LayeredPixelWorldRenderer _renderer;
    private readonly ValidationMetricsCollector? _metrics;
    private readonly WindowsMonitorInfo _monitor;
    private readonly OverlayEdge _edge;
    private readonly Timer _taskbarTimer;
    private readonly Timer _throwTargetingTimer;
    private readonly Action<Guid> _requestThrow;
    private readonly Action<string>? _diagnostic;
    private readonly Action<string, Exception>? _diagnosticFailure;
    private readonly Guid?[] _targetUserIds = new Guid?[NativeOverlayWindowThread.MaximumTargetHotspots];
    private readonly NativePixelRect[] _targetBounds = new NativePixelRect[NativeOverlayWindowThread.MaximumTargetHotspots];
    private readonly NativePixelRect[] _appliedTargetBounds = new NativePixelRect[NativeOverlayWindowThread.MaximumTargetHotspots];
    private readonly bool[] _targetWindowsShown = new bool[NativeOverlayWindowThread.MaximumTargetHotspots];
    private int _topmostRefreshCountdown = 20;
    private nint _yieldedShellSurface;
    private Guid? _roomId;
    private bool _requiresRightClickToThrow;
    private bool _realtimeConnected;
    private bool _selfHotspotAvailable;
    private NativePixelRect _lastSelfHotspot = new(0, 0, 1, 1);
    private bool _selfWindowShown = true;
    private bool _throwTargetingActive;
    private bool _disposed;

    private NativePixelWorldSession(
        NativeOverlayWindowThread windows,
        LayeredPixelWorldRenderer renderer,
        ValidationMetricsCollector? metrics,
        WindowsMonitorInfo monitor,
        OverlayEdge edge,
        Guid? roomId,
        Action<Guid> requestThrow,
        bool requiresRightClickToThrow,
        bool realtimeConnected,
        Action<string>? diagnostic,
        Action<string, Exception>? diagnosticFailure)
    {
        _windows = windows;
        _renderer = renderer;
        _metrics = metrics;
        _monitor = monitor;
        _edge = edge;
        _roomId = roomId;
        _requestThrow = requestThrow;
        _diagnostic = diagnostic;
        _diagnosticFailure = diagnosticFailure;
        _requiresRightClickToThrow = requiresRightClickToThrow;
        _realtimeConnected = realtimeConnected;
        _taskbarTimer = new Timer(
            static state => ((NativePixelWorldSession)state!).RefreshTaskbarInset(),
            this,
            TimeSpan.FromMilliseconds(50),
            TimeSpan.FromMilliseconds(50));
        _throwTargetingTimer = new Timer(
            static state => ((NativePixelWorldSession)state!).ExpireThrowTargeting(),
            this,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
    }

    public bool IsVisible { get; private set; } = true;

    public string? ValidationMetricsPath => _metrics?.OutputPath;

    public ValidationMetricsSummary? ValidationMetricsSummary => _metrics?.Snapshot();

    public static NativePixelWorldSession Start(
        OverlayRegionPreference preference,
        WorldSnapshot initialSnapshot,
        Action<int> characterClicked,
        Action requestPulse,
        Action<Guid> requestThrow,
        bool requiresRightClickToThrow,
        bool realtimeConnected,
        Action<Exception> renderingFailed,
        NativePixelWorldSessionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(initialSnapshot);
        ArgumentNullException.ThrowIfNull(characterClicked);
        ArgumentNullException.ThrowIfNull(requestPulse);
        ArgumentNullException.ThrowIfNull(requestThrow);
        ArgumentNullException.ThrowIfNull(renderingFailed);
        options ??= new NativePixelWorldSessionOptions();

        var normalizedValidationIds = options.ValidationCharacterIds?
            .Select(PixelCharacterCatalog.NormalizeId)
            .ToHashSet(StringComparer.Ordinal);
        if (normalizedValidationIds is { Count: 0 })
        {
            throw new ArgumentException("ValidationCharacterIds cannot be empty.", nameof(options));
        }

        if (normalizedValidationIds is not null
            && initialSnapshot.Members.Any(member => !normalizedValidationIds.Contains(
                PixelCharacterCatalog.NormalizeId(member.CharacterId))))
        {
            throw new ArgumentException(
                "The validation snapshot contains a character outside ValidationCharacterIds.",
                nameof(initialSnapshot));
        }

        WindowsMonitorInfo monitor = WindowsMonitorService.Select(preference.MonitorIdentifier);
        IReadOnlyList<WindowsMonitorInfo> monitors = WindowsMonitorService.GetAll();
        int monitorIndex = Math.Max(
            0,
            monitors.ToList().FindIndex(candidate => candidate.Identifier == monitor.Identifier));
        options.Diagnostic?.Invoke(
            $"overlay-environment monitor={monitorIndex + 1} dpi={monitor.Dpi} "
            + $"scale={(monitor.Dpi / 96d).ToString("F2", CultureInfo.InvariantCulture)}");
        int initialTaskbarInset = WindowsTaskbarService.VisibleInset(
            monitor.MonitorPixels,
            monitor.MonitorPixels,
            preference.Edge);
        WindowsOverlayRegionFrames frames = WindowsOverlayRegionLayout.Frames(
            monitor.MonitorPixels,
            monitor.Dpi,
            preference);
        int hotspotSize = Math.Max(
            1,
            (int)Math.Round(52d * monitor.Dpi / 96d, MidpointRounding.AwayFromZero));
        NativePixelRect initialHotspot = InitialHotspot(
            frames.ActivityFrame,
            preference.Edge,
            hotspotSize,
            initialTaskbarInset);
        ValidationMetricsCollector? metrics = options.CollectValidationMetrics
            ? new ValidationMetricsCollector(
                normalizedValidationIds?.ToArray() ?? [.. PixelCharacterCatalog.All.Select(item => item.Id)],
                options.ValidationMetricsPath)
            : null;

        NativeOverlayWindowThread? windows = null;
        LayeredPixelWorldRenderer? renderer = null;
        NativePixelWorldSession? session = null;
        windows = NativeOverlayWindowThread.Start(
            frames.RenderFrame,
            initialHotspot,
            handle =>
            {
                renderer = new LayeredPixelWorldRenderer(
                    handle,
                    frames.ActivityFrame,
                    frames.RenderFrame,
                    monitor.Dpi,
                    preference.Edge,
                    initialSnapshot,
                    (self, targets) => session?.UpdateHotspots(self, targets),
                    renderingFailed,
                    options.MessageBubblesPresented,
                    options.RendererPerformanceSampled,
                    normalizedValidationIds,
                    metrics,
                    initialTaskbarInset,
                    options.AnimationsEnabled,
                    options.CharacterImpact);
                return renderer;
            },
            () => characterClicked(1),
            () =>
            {
                characterClicked(2);
                if (session?.IsSelfStunned != true)
                {
                    requestPulse();
                }
            },
            isDoubleClick => session?.HandleRightClick(isDoubleClick),
            index => session?.ActivateTarget(index));
        session = new NativePixelWorldSession(
            windows,
            renderer ?? throw new InvalidOperationException("SIDEY renderer did not initialize."),
            metrics,
            monitor,
            preference.Edge,
            initialSnapshot.RoomId,
            requestThrow,
            requiresRightClickToThrow,
            realtimeConnected,
            options.Diagnostic,
            options.DiagnosticFailure);
        session._requestTreeMovementToggle = options.TreeMovementToggleRequested;
        session._selfCharacterId = initialSnapshot.Members.FirstOrDefault(member => member.IsCurrentUser)?.CharacterId;
        options.Diagnostic?.Invoke("overlay-window-created result=success");
        return session;
    }

    public void Apply(WorldSnapshot snapshot)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string? selfCharacterId = snapshot.Members.FirstOrDefault(member => member.IsCurrentUser)?.CharacterId;
        lock (_throwGate)
        {
            if (_roomId != snapshot.RoomId || _selfCharacterId != selfCharacterId)
            {
                _rightClicks.Cancel();
            }
            _selfCharacterId = selfCharacterId;
        }
        if (_roomId != snapshot.RoomId)
        {
            lock (_throwGate)
            {
                _roomId = snapshot.RoomId;
                CancelThrowTargetingWithinGate();
                ClearTargetsWithinGate();
            }
        }
        _renderer.ApplySnapshot(snapshot);
    }

    public ValueTask SetVisibleAsync(bool visible, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        _windows.SetVisible(visible);
        IsVisible = visible;
        if (visible && !_selfHotspotAvailable)
        {
            _windows.SetHotspotBounds(_lastSelfHotspot, visible: false);
        }
        _selfWindowShown = visible && _selfHotspotAvailable;
        if (!visible)
        {
            _renderer.ResetFeedback();
            lock (_throwGate)
            {
                CancelThrowTargetingWithinGate();
                _windows.HideTargetHotspots();
                Array.Clear(_targetWindowsShown);
            }
        }
        else
        {
            lock (_throwGate)
            {
                RefreshTargetVisibilityWithinGate();
            }
        }
        return ValueTask.CompletedTask;
    }

    public async Task FadeOutAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsVisible)
        {
            return;
        }

        IsVisible = false;
        _windows.SetHotspotBounds(_lastSelfHotspot, visible: false);
        _selfWindowShown = false;
        _renderer.ResetFeedback();
        lock (_throwGate)
        {
            CancelThrowTargetingWithinGate();
            _windows.HideTargetHotspots();
            Array.Clear(_targetWindowsShown);
        }
        await _renderer.FadeOutAsync(cancellationToken).ConfigureAwait(false);
        if (_disposed)
        {
            return;
        }
        try
        {
            _windows.SetVisible(false);
        }
        catch (ObjectDisposedException) when (_disposed)
        {
        }
    }

    public void ConfigureThrowInteraction(bool requiresRightClickToThrow, bool realtimeConnected)
    {
        if (_realtimeConnected != realtimeConnected)
            _renderer.ResetFeedback();
        lock (_throwGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            bool changed = _requiresRightClickToThrow != requiresRightClickToThrow
                || _realtimeConnected != realtimeConnected;
            _requiresRightClickToThrow = requiresRightClickToThrow;
            _realtimeConnected = realtimeConnected;
            if (changed)
            {
                CancelThrowTargetingWithinGate();
            }
            RefreshTargetVisibilityWithinGate();
        }
    }

    public Task<string?> ExportValidationMetricsAsync(CancellationToken cancellationToken = default) =>
        _metrics is null
            ? Task.FromResult<string?>(null)
            : ExportAsync(_metrics, cancellationToken);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _taskbarTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _taskbarTimer.Dispose();
        _throwTargetingTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _throwTargetingTimer.Dispose();
        _windows.Dispose();
        _diagnostic?.Invoke("overlay-stopped");
        if (_metrics is not null)
        {
            try
            {
                _metrics.Export();
            }
            catch (Exception exception)
            {
                Trace.TraceError("SIDEY validation metrics export failed: {0}", exception);
            }
        }
    }

    private static async Task<string?> ExportAsync(
        ValidationMetricsCollector metrics,
        CancellationToken cancellationToken) =>
        await metrics.ExportAsync(cancellationToken).ConfigureAwait(false);

    private void HandleRightClick(bool isDoubleClick)
    {
        bool activate;
        lock (_throwGate)
        {
            if (_disposed || !IsVisible || !_selfHotspotAvailable)
            {
                return;
            }
            _rightClickRoomId = _roomId;
            activate = _rightClicks.Press(isDoubleClick, NowSeconds, NativeOverlayWindow.DoubleClickIntervalSeconds);
        }
        if (activate)
        {
            ActivateThrowTargeting();
        }
    }

    private void DispatchPendingRightClick()
    {
        Guid? roomId;
        lock (_throwGate)
        {
            if (_disposed || !IsVisible || !_rightClicks.TakeSingle(NowSeconds) || _selfCharacterId != "pixel_tree")
            {
                return;
            }
            roomId = _rightClickRoomId;
        }
        _requestTreeMovementToggle?.Invoke(roomId);
    }

    private void ActivateThrowTargeting()
    {
        lock (_throwGate)
        {
            if (_disposed || !_requiresRightClickToThrow || !_realtimeConnected
                || !IsVisible || !_selfHotspotAvailable || IsSelfStunned)
            {
                return;
            }

            _throwTargetingActive = true;
            _throwTargetingTimer.Change(s_throwTargetingDuration, Timeout.InfiniteTimeSpan);
            RefreshTargetVisibilityWithinGate();
        }
    }

    private void ExpireThrowTargeting()
    {
        lock (_throwGate)
        {
            if (_disposed)
            {
                return;
            }

            _throwTargetingActive = false;
            RefreshTargetVisibilityWithinGate();
        }
    }

    private void ActivateTarget(int index)
    {
        Guid? targetUserId;
        lock (_throwGate)
        {
            if (_disposed || !TargetsEnabledWithinGate()
                || index < 0 || index >= _targetUserIds.Length)
            {
                return;
            }

            targetUserId = _targetUserIds[index];
        }

        if (targetUserId is { } userId)
        {
            _requestThrow(userId);
        }
    }

    private void UpdateHotspots(
        NativePixelRect? self,
        IReadOnlyList<CharacterHotspotFrame> targets)
    {
        lock (_throwGate)
        {
            if (_disposed)
            {
                return;
            }

            _selfHotspotAvailable = self is not null;
            if (IsSelfStunned)
                CancelThrowTargetingWithinGate();
            if (self is { } selfBounds)
            {
                if (!_selfWindowShown || MovedAtLeastOneDip(_lastSelfHotspot, selfBounds))
                {
                    _windows.SetHotspotBounds(selfBounds, IsVisible);
                    _lastSelfHotspot = selfBounds;
                }
                _selfWindowShown = IsVisible;
            }
            else
            {
                if (_selfWindowShown)
                {
                    _windows.SetHotspotBounds(_lastSelfHotspot, visible: false);
                    _selfWindowShown = false;
                }
                CancelThrowTargetingWithinGate();
            }

            int count = Math.Min(targets.Count, _targetUserIds.Length);
            for (int index = 0; index < count; index++)
            {
                _targetUserIds[index] = targets[index].UserId;
                _targetBounds[index] = targets[index].Bounds;
            }
            for (int index = count; index < _targetUserIds.Length; index++)
            {
                _targetUserIds[index] = null;
            }
            RefreshTargetVisibilityWithinGate();
        }
    }

    private bool TargetsEnabledWithinGate() =>
        IsVisible
        && _realtimeConnected
        && _selfHotspotAvailable
        && !IsSelfStunned
        && (!_requiresRightClickToThrow || _throwTargetingActive);

    private void RefreshTargetVisibilityWithinGate()
    {
        bool enabled = TargetsEnabledWithinGate();
        for (int index = 0; index < _targetUserIds.Length; index++)
        {
            bool visible = enabled && _targetUserIds[index] is not null;
            if (visible)
            {
                if (!_targetWindowsShown[index]
                    || MovedAtLeastOneDip(_appliedTargetBounds[index], _targetBounds[index]))
                {
                    _windows.SetTargetHotspotBounds(index, _targetBounds[index], visible: true);
                    _appliedTargetBounds[index] = _targetBounds[index];
                }
                _targetWindowsShown[index] = true;
            }
            else if (_targetWindowsShown[index])
            {
                _windows.SetTargetHotspotBounds(
                    index,
                    _targetBounds[index].IsValid ? _targetBounds[index] : new NativePixelRect(0, 0, 1, 1),
                    visible: false);
                _targetWindowsShown[index] = false;
            }
        }
    }

    private void CancelThrowTargetingWithinGate()
    {
        _rightClicks.Cancel();
        _throwTargetingActive = false;
        _throwTargetingTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    private void ClearTargetsWithinGate()
    {
        Array.Clear(_targetUserIds);
        Array.Clear(_targetWindowsShown);
        _windows.HideTargetHotspots();
    }

    private bool MovedAtLeastOneDip(NativePixelRect previous, NativePixelRect current)
    {
        if (!previous.IsValid)
        {
            return true;
        }
        double minimumPixels = Math.Max(1d, _monitor.Dpi / 96d);
        double previousCenterX = previous.X + (previous.Width / 2d);
        double previousCenterY = previous.Y + (previous.Height / 2d);
        double currentCenterX = current.X + (current.Width / 2d);
        double currentCenterY = current.Y + (current.Height / 2d);
        return Math.Abs(currentCenterX - previousCenterX) >= minimumPixels
            || Math.Abs(currentCenterY - previousCenterY) >= minimumPixels;
    }

    private void RefreshTaskbarInset()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            DispatchPendingRightClick();
            WindowsTaskbarPresentation taskbar = WindowsTaskbarService.VisiblePresentation(
                _monitor.MonitorPixels,
                _monitor.MonitorPixels,
                _edge);
            _renderer.SetEdgeInset(taskbar.EdgeInset);
            nint shellSurface = taskbar.RevealedAutoHideWindow != nint.Zero
                ? taskbar.RevealedAutoHideWindow
                : WindowsShellSurfaceDetector.ForegroundSurface(_monitor.MonitorPixels);
            if (IsVisible && shellSurface != nint.Zero)
            {
                if (shellSurface != _yieldedShellSurface
                    || Interlocked.Decrement(ref _topmostRefreshCountdown) <= 0)
                {
                    bool stateChanged = _yieldedShellSurface != shellSurface;
                    Interlocked.Exchange(ref _topmostRefreshCountdown, 20);
                    _windows.YieldBehind(shellSurface);
                    _yieldedShellSurface = shellSurface;
                    if (stateChanged)
                    {
                        _diagnostic?.Invoke("overlay-z-order mode=behind-shell");
                    }
                }
            }
            else if (IsVisible
                && (_yieldedShellSurface != nint.Zero
                    || Interlocked.Decrement(ref _topmostRefreshCountdown) <= 0))
            {
                bool stateChanged = _yieldedShellSurface != nint.Zero;
                Interlocked.Exchange(ref _topmostRefreshCountdown, 20);
                _windows.EnsureTopmost();
                _yieldedShellSurface = nint.Zero;
                if (stateChanged)
                {
                    _diagnostic?.Invoke("overlay-z-order mode=topmost");
                }
            }
        }
        catch (ObjectDisposedException) when (_disposed)
        {
        }
        catch (Exception exception)
        {
            Trace.TraceError("SIDEY taskbar tracking failed: {0}", exception);
            _diagnosticFailure?.Invoke("overlay-z-order", exception);
        }
    }

    private static NativePixelRect InitialHotspot(
        NativePixelRect activity,
        OverlayEdge edge,
        int hotspotSize,
        int edgeInset)
    {
        double tangent = edge is OverlayEdge.Bottom or OverlayEdge.Top
            ? activity.Width / 2d
            : activity.Height / 2d;
        (double X, double Y) foot = edge switch
        {
            OverlayEdge.Bottom => (
                X: activity.X + tangent,
                Y: (double)(activity.Y + activity.Height - edgeInset)),
            OverlayEdge.Top => (
                X: activity.X + tangent,
                Y: (double)(activity.Y + edgeInset)),
            OverlayEdge.Left => (
                X: (double)(activity.X + edgeInset),
                Y: activity.Y + tangent),
            OverlayEdge.Right => (
                X: (double)(activity.X + activity.Width - edgeInset),
                Y: activity.Y + tangent),
            _ => throw new ArgumentOutOfRangeException(nameof(edge)),
        };
        int tangentOrigin = edge is OverlayEdge.Bottom or OverlayEdge.Top
            ? (int)Math.Round(foot.X - (hotspotSize / 2d), MidpointRounding.AwayFromZero)
            : (int)Math.Round(foot.Y - (hotspotSize / 2d), MidpointRounding.AwayFromZero);
        return edge switch
        {
            OverlayEdge.Bottom => new NativePixelRect(
                tangentOrigin,
                (int)Math.Round(foot.Y, MidpointRounding.AwayFromZero) - hotspotSize,
                hotspotSize,
                hotspotSize),
            OverlayEdge.Top => new NativePixelRect(
                tangentOrigin,
                (int)Math.Round(foot.Y, MidpointRounding.AwayFromZero),
                hotspotSize,
                hotspotSize),
            OverlayEdge.Left => new NativePixelRect(
                (int)Math.Round(foot.X, MidpointRounding.AwayFromZero),
                tangentOrigin,
                hotspotSize,
                hotspotSize),
            OverlayEdge.Right => new NativePixelRect(
                (int)Math.Round(foot.X, MidpointRounding.AwayFromZero) - hotspotSize,
                tangentOrigin,
                hotspotSize,
                hotspotSize),
            _ => throw new ArgumentOutOfRangeException(nameof(edge)),
        };
    }
}

public static class PixelWorldPreview
{
    public static WorldSnapshot Create(
        IReadOnlyList<string>? characterIds = null,
        long installationSeed = 0x51DE7,
        OverlayEdge edge = OverlayEdge.Bottom)
    {
        IReadOnlyList<string> ids = characterIds ?? [.. PixelCharacterCatalog.All.Select(character => character.Id)];
        var roomId = Guid.Parse("51de7000-0000-0000-0000-000000000100");
        PixelWorldMember[] members = [.. ids.Select((characterId, index) => new PixelWorldMember(
            StableMemberId(index),
            PixelCharacterCatalog.Get(characterId).DisplayName,
            PixelCharacterCatalog.NormalizeId(characterId),
            PresenceState.Online,
            IsTyping: false,
            IsCurrentUser: index == 0))];
        return new WorldSnapshot(
            roomId,
            members,
            [],
            [],
            [],
            edge,
            installationSeed);
    }

    private static Guid StableMemberId(int index) =>
        Guid.Parse($"51de7000-0000-0000-0000-{index + 1:D12}");
}
