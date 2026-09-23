using System.Diagnostics;
using Sidey.Core.Domain;
using Sidey.Core.Overlay;
using Sidey.Platform.Windows;

namespace Sidey.Overlay.Rendering;

internal readonly record struct CharacterHotspotFrame(Guid UserId, NativePixelRect Bounds);

internal sealed class LayeredPixelWorldRenderer : IDisposable
{
    private const int FramesPerSecond = 30;
    private const double FixedDeltaTime = 1d / FramesPerSecond;
    private const double EntranceFadeDurationSeconds = 0.24d;
    private const double ExitFadeDurationSeconds = 0.24d;
    private const double EdgeInsetAnimationSpeedDipPerSecond = 72d;
    private const double DozeRestingOpacity = 0.55d;
    private const double DozeFloatingDistanceDip = 3d;
    private const int MaximumActiveProjectiles = 32;
    private const double ThrowActionSeconds = 0.4d;
    private const double ThrowReleaseSeconds = 0.2d;
    private const double HitActionSeconds = 0.44d;
    private const double AmbientSparkleCycleSeconds = 1.2d;
    private const double AmbientSparkleDurationSeconds = 1.05d;
    private const double SparklePulseDurationSeconds = 0.78d;
    private static readonly IReadOnlyList<RectD> s_noAvoidanceRects = [];

    private readonly Lock _gate = new();
    private readonly CharacterStunState _stun = new();
    private bool _treeMovementPaused;

    private readonly List<(int X, int Y, double Elapsed)> _stunDraws = new(12);
    private readonly Func<bool> _animationsEnabled;
    private readonly Action<string, long>? _impact;
    private readonly Queue<(string Id, long Time)> _impactNotifications = new();
    private Guid _selfId;
    private readonly Lock _selfGate = new();
    public bool IsSelfStunned { get { lock (_selfGate) return _stun.IsStunned(_selfId); } }

    public void ResetFeedback()
    {
        lock (_gate)
        {
            _stun.Reset();
            _projectiles.Clear();
            _impactNotifications.Clear();
        }
    }
    private readonly NativePixelRect _activityBounds;
    private readonly NativePixelRect _renderBounds;
    private readonly int _hotspotPixelSize;
    private readonly int _integerScale;
    private readonly int _dozeFloatingDistancePixels;
    private readonly int _bubbleBodySpacingPixels;
    private readonly int _bubbleTailHeightPixels;
    private readonly int _bubbleTailHalfBasePixels;
    private readonly int _bubbleTailBaseInsetPixels;
    private readonly int _bubbleTailBodyOverlapPixels;
    private readonly int _bubbleCharacterGapPixels;
    private readonly double _bubbleTangentMarginPixels;
    private readonly double _dpiScale;
    private readonly OverlayEdge _edge;
    private readonly Action<NativePixelRect?, IReadOnlyList<CharacterHotspotFrame>> _hotspotsMoved;
    private readonly Action<Exception> _renderingFailed;
    private readonly Action<int>? _messageBubblesPresented;
    private readonly Action<double, double, long, long>? _performanceSampled;
    private readonly ValidationMetricsCollector? _metrics;
    private readonly Random _random;
    private readonly long _initialPositionSeed;
    private readonly EdgeTrackGeometry _geometry;
    private readonly PixelCharacterFrameCache _frameCache;
    private readonly CharacterThrowFrameCache _throwFrameCache;
    private readonly PixelTextVisualCache _textVisuals;
    private readonly NativeLayeredBitmap _surface;
    private readonly List<WorldNode> _nodes = [];
    private readonly List<PixelMovementAgent> _agents = [];
    private readonly Dictionary<Guid, WorldNode> _nodeById = [];
    private readonly HashSet<Guid> _stoppedIds = [];
    private readonly HashSet<Guid> _incomingMemberIds = [];
    private readonly Dictionary<Guid, long> _pulseStartedAt = [];
    private readonly Dictionary<Guid, long> _dozeStartedAt = [];
    private readonly Dictionary<Guid, List<ActiveBubble>> _bubblesBySender = [];
    private readonly List<Guid> _expiredBubbleSenders = [];
    private readonly HashSet<Guid> _drawnBubbleIds = [];
    private readonly HashSet<Guid> _presentedBubbleIds = [];
    private readonly List<MessageBubbleTrackBounds> _bubbleTrackBounds = [];
    private readonly List<CharacterHotspotFrame> _targetHotspots = new(11);
    private readonly PixelMovementScratch _movementScratch = new();
    private readonly MessageBubbleCollisionScratch _bubbleCollisionScratch = new();
    private readonly CharacterPulseReplayGuard _pulseReplayGuard = new();
    private readonly CharacterThrowReplayGuard _throwReplayGuard = new();
    private readonly List<ActiveProjectile> _projectiles = new(MaximumActiveProjectiles);
    private readonly Dictionary<Guid, long> _throwStartedAt = [];
    private readonly Dictionary<Guid, long> _hitStartedAt = [];
    private readonly List<Guid> _expiredHitIds = new(12);
    private readonly Timer _timer;
    private double _hotspotTrackingElapsed = double.PositiveInfinity;
    private long _tick;
    private long _performanceWindowStarted = Stopwatch.GetTimestamp();
    private long _performanceFrameCount;
    private long _performanceSkippedTicks;
    private double _performanceTotalMilliseconds;
    private double _performanceMaximumMilliseconds;
    private int _tickRunning;
    private int _presentedFrameCount;
    private int _fadeOutStartedFrame = -1;
    private TaskCompletionSource<bool>? _fadeOutCompletion;
    private double _edgeInsetPixels;
    private int _targetEdgeInsetPixels;
    private bool _faulted;
    private bool _disposed;
    private Guid? _roomId;

    internal LayeredPixelWorldRenderer(
        nint windowHandle,
        NativePixelRect activityBounds,
        NativePixelRect renderBounds,
        uint dpi,
        OverlayEdge edge,
        WorldSnapshot initialSnapshot,
        Action<NativePixelRect?, IReadOnlyList<CharacterHotspotFrame>> hotspotsMoved,
        Action<Exception> renderingFailed,
        Action<int>? messageBubblesPresented,
        Action<double, double, long, long>? performanceSampled,
        IReadOnlySet<string>? cachedCharacterIds = null,
        ValidationMetricsCollector? metrics = null,
        int initialEdgeInsetPixels = 0,
        Func<bool>? animationsEnabled = null,
        Action<string, long>? impact = null)
    {
        if (!activityBounds.IsValid || !renderBounds.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(activityBounds));
        }

        _activityBounds = activityBounds;
        _ = CharacterStunPixels.Create(0, false);
        _animationsEnabled = animationsEnabled ?? (() => true);
        _impact = impact;
        _renderBounds = renderBounds;
        _hotspotsMoved = hotspotsMoved ?? throw new ArgumentNullException(nameof(hotspotsMoved));
        _renderingFailed = renderingFailed ?? throw new ArgumentNullException(nameof(renderingFailed));
        _messageBubblesPresented = messageBubblesPresented;
        _performanceSampled = performanceSampled;
        _metrics = metrics;
        _edge = edge;
        _edgeInsetPixels = ClampEdgeInset(initialEdgeInsetPixels);
        _targetEdgeInsetPixels = (int)_edgeInsetPixels;
        _integerScale = PixelScalePolicy.IntegerScale(dpi);
        _dpiScale = Math.Max(1d, dpi / 96d);
        _dozeFloatingDistancePixels = Math.Max(1, DipToPixels(DozeFloatingDistanceDip, dpi));
        _bubbleBodySpacingPixels = Math.Max(1, DipToPixels(MessageBubbleLayoutPolicy.BodySpacingDip, dpi));
        _bubbleTailHeightPixels = Math.Max(1, DipToPixels(MessageBubbleLayoutPolicy.TailHeightDip, dpi));
        _bubbleTailHalfBasePixels = Math.Max(1, DipToPixels(MessageBubbleLayoutPolicy.TailHalfBaseDip, dpi));
        _bubbleTailBaseInsetPixels = Math.Max(1, DipToPixels(MessageBubbleLayoutPolicy.TailBaseInsetDip, dpi));
        _bubbleTailBodyOverlapPixels = Math.Max(1, DipToPixels(MessageBubbleLayoutPolicy.TailBodyOverlapDip, dpi));
        _bubbleCharacterGapPixels = Math.Max(1, DipToPixels(MessageBubbleLayoutPolicy.CharacterGapDip, dpi));
        _bubbleTangentMarginPixels = MessageBubbleLayoutPolicy.TangentMarginDip * _dpiScale;
        _hotspotPixelSize = Math.Max(1, DipToPixels(52, dpi));
        _initialPositionSeed = OverlayPlacementPolicy.CreateSessionSeed(
            initialSnapshot.InstallationSeed);
        _random = new Random(OverlayPlacementPolicy.RandomSeed(_initialPositionSeed));
        _geometry = new EdgeTrackGeometry(
            new RectD(0, 0, activityBounds.Width, activityBounds.Height),
            edge,
            Math.Max(24 * _integerScale, _hotspotPixelSize));
        string assetRoot = CharacterAssetPathResolver.Resolve();
        PixelCharacterFrameCache? frameCache = null;
        CharacterThrowFrameCache? throwFrameCache = null;
        PixelTextVisualCache? textVisuals = null;
        NativeLayeredBitmap? surface = null;
        try
        {
            IReadOnlySet<string> initialCharacterIds = cachedCharacterIds
                ?? initialSnapshot.Members
                    .Select(member => PixelCharacterCatalog.NormalizeId(member.CharacterId))
                    .ToHashSet(StringComparer.Ordinal);
            frameCache = new PixelCharacterFrameCache(
                assetRoot,
                _integerScale,
                edge,
                initialCharacterIds);
            string throwableAssetRoot = Path.Combine(
                Directory.GetParent(assetRoot)?.FullName ?? assetRoot,
                "Throwables");
            string bubbleAssetRoot = Path.Combine(
                Directory.GetParent(assetRoot)?.FullName ?? assetRoot,
                "Bubbles");
            throwFrameCache = new CharacterThrowFrameCache(
                assetRoot,
                throwableAssetRoot,
                _integerScale,
                edge);
            double tangentLengthDip = (edge is OverlayEdge.Bottom or OverlayEdge.Top
                ? activityBounds.Width
                : activityBounds.Height) / _dpiScale;
            float bubbleMaximumWidthDip = (float)Math.Min(
                220d,
                Math.Max(24d, tangentLengthDip - 16d));
            textVisuals = new PixelTextVisualCache(
                dpi,
                edge,
                bubbleMaximumWidthDip,
                bubbleAssetRoot);
            surface = new NativeLayeredBitmap(
                windowHandle,
                renderBounds.Width,
                renderBounds.Height);
            _frameCache = frameCache;
            _throwFrameCache = throwFrameCache;
            _textVisuals = textVisuals;
            _surface = surface;
            _pulseReplayGuard.SeedExisting(initialSnapshot.Pulses);
            _throwReplayGuard.SeedExisting(initialSnapshot.Throws);
            ApplySnapshotWithinGate(initialSnapshot with { Pulses = [], Throws = [] });
            RenderFrame();
            _timer = new Timer(
                static state => ((LayeredPixelWorldRenderer)state!).TickSafely(),
                this,
                TimeSpan.FromSeconds(FixedDeltaTime),
                TimeSpan.FromSeconds(FixedDeltaTime));
        }
        catch
        {
            surface?.Dispose();
            textVisuals?.Dispose();
            frameCache?.Dispose();
            throwFrameCache?.Dispose();
            throw;
        }
    }

    public void ApplySnapshot(WorldSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ApplySnapshotWithinGate(snapshot);
        }
    }

    public void SetEdgeInset(int edgeInsetPixels)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _targetEdgeInsetPixels = ClampEdgeInset(edgeInsetPixels);
        }
    }

    public Task FadeOutAsync(CancellationToken cancellationToken = default)
    {
        Task completion;
        lock (_gate)
        {
            if (_disposed || _faulted || !_animationsEnabled())
            {
                return Task.CompletedTask;
            }

            if (_fadeOutStartedFrame < 0)
            {
                _fadeOutStartedFrame = _presentedFrameCount;
                _fadeOutCompletion = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }
            completion = _fadeOutCompletion!.Task;
        }

        return cancellationToken.CanBeCanceled
            ? completion.WaitAsync(cancellationToken)
            : completion;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _fadeOutCompletion?.TrySetResult(false);
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _timer.Dispose();
            _surface.Dispose();
            _textVisuals.Dispose();
            _frameCache.Dispose();
            _throwFrameCache.Dispose();
        }
    }

    private void TickSafely()
    {
        if (Interlocked.Exchange(ref _tickRunning, 1) != 0)
        {
            Interlocked.Increment(ref _performanceSkippedTicks);
            return;
        }

        long started = Stopwatch.GetTimestamp();
        try
        {
            Tick();
            while (true)
            {
                (string Id, long Time) next;
                lock (_gate)
                {
                    if (!_impactNotifications.TryDequeue(out next))
                        break;
                }
                _impact?.Invoke(next.Id, next.Time);
            }
        }
        catch (Exception exception)
        {
            lock (_gate)
            {
                if (_disposed || _faulted)
                {
                    return;
                }

                _faulted = true;
                _fadeOutCompletion?.TrySetResult(false);
                _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            }

            try
            {
                _renderingFailed(exception);
            }
            catch (Exception reportingException)
            {
                Trace.TraceError(
                    "SIDEY renderer failure callback also failed. Renderer: {0}; callback: {1}",
                    exception,
                    reportingException);
            }
        }
        finally
        {
            TimeSpan elapsed = Stopwatch.GetElapsedTime(started);
            try
            {
                _metrics?.RecordFrame(elapsed);
                RecordRuntimePerformance(elapsed);
            }
            catch (Exception exception)
            {
                Trace.TraceError("SIDEY validation metrics sampling failed: {0}", exception);
            }
            finally
            {
                Volatile.Write(ref _tickRunning, 0);
            }
        }
    }

    private void RecordRuntimePerformance(TimeSpan frameTime)
    {
        _performanceFrameCount++;
        _performanceTotalMilliseconds += frameTime.TotalMilliseconds;
        _performanceMaximumMilliseconds = Math.Max(
            _performanceMaximumMilliseconds,
            frameTime.TotalMilliseconds);
        if (_performanceSampled is null
            || Stopwatch.GetElapsedTime(_performanceWindowStarted) < TimeSpan.FromMinutes(1))
        {
            return;
        }

        long frameCount = _performanceFrameCount;
        double averageMilliseconds = frameCount == 0
            ? 0
            : _performanceTotalMilliseconds / frameCount;
        long skippedTicks = Interlocked.Exchange(ref _performanceSkippedTicks, 0);
        _performanceSampled(
            averageMilliseconds,
            _performanceMaximumMilliseconds,
            frameCount,
            skippedTicks);
        _performanceWindowStarted = Stopwatch.GetTimestamp();
        _performanceFrameCount = 0;
        _performanceTotalMilliseconds = 0;
        _performanceMaximumMilliseconds = 0;
    }

    private void Tick()
    {
        lock (_gate)
        {
            if (_disposed || _faulted)
            {
                return;
            }

            AnimateEdgeInset();
            ExpireHitAnimations();
            RefreshStoppedIds();

            _expiredBubbleSenders.Clear();
            DateTimeOffset now = DateTimeOffset.UtcNow;
            foreach (KeyValuePair<Guid, List<ActiveBubble>> senderBubbles in _bubblesBySender)
            {
                for (int index = senderBubbles.Value.Count - 1; index >= 0; index--)
                {
                    if (senderBubbles.Value[index].ExpiresAt <= now)
                    {
                        senderBubbles.Value.RemoveAt(index);
                    }
                }
                if (senderBubbles.Value.Count == 0)
                {
                    _expiredBubbleSenders.Add(senderBubbles.Key);
                }
            }
            foreach (Guid senderId in _expiredBubbleSenders)
            {
                _bubblesBySender.Remove(senderId);
            }
            _bubbleTrackBounds.Clear();
            foreach (KeyValuePair<Guid, List<ActiveBubble>> senderBubbles in _bubblesBySender)
            {
                if (!_nodeById.TryGetValue(senderBubbles.Key, out WorldNode? node))
                {
                    continue;
                }
                PixelMemberVisuals visuals = _textVisuals.Get(senderBubbles.Key);
                if (!TryGetBubbleTangentRange(
                    node.Agent.TrackPosition,
                    senderBubbles.Value,
                    visuals,
                    out double lower,
                    out double upper))
                {
                    continue;
                }
                _bubbleTrackBounds.Add(new MessageBubbleTrackBounds(
                    senderBubbles.Key,
                    lower,
                    upper));
            }
            IReadOnlySet<Guid> separatedIds = MessageBubbleCollisionResolver.Apply(
                _agents,
                _bubbleTrackBounds,
                FixedDeltaTime,
                _geometry,
                _bubbleCollisionScratch, _stoppedIds, _dpiScale);
            PixelMovementSimulation.Step(
                _agents, FixedDeltaTime, _geometry, s_noAvoidanceRects,
                _stoppedIds, _movementScratch, _dpiScale, separatedIds);
            PixelRoamingPolicy.UpdateAfterMovement(_agents, _stoppedIds, _geometry, _random, _dpiScale);
            _tick++;
            _hotspotTrackingElapsed = Math.Min(1d, _hotspotTrackingElapsed + FixedDeltaTime);
            RenderFrame();
        }
    }

    private void RenderFrame()
    {
        Span<byte> destinationPixels = _surface.Pixels;
        destinationPixels.Clear();
        _stunDraws.Clear();
        _drawnBubbleIds.Clear();
        NativePixelRect? currentUserHotspot = null;
        _targetHotspots.Clear();
        UpdateProjectiles();
        foreach (WorldNode node in _nodes)
        {
            CachedCharacterFrames cached = _frameCache.Get(node.Member.CharacterId);
            bool moving = PixelRoamingPolicy.IsWalking(node.Agent, _stoppedIds.Contains(node.Member.Id), _dpiScale);
            double? stunElapsed = _stun.Elapsed(node.Member.Id);
            int? actionFrame = stunElapsed is not null ? null : ActionFrame(node.Member.Id);
            int frame = FrameIndex(node.Member.Presence, moving, cached.Definition.Frames);
            if (stunElapsed is { } asleep)
            {
                Range range = cached.Definition.Frames.Offline;
                frame = range.Start.Value + (_animationsEnabled() ? (int)(asleep / 1.2) % (range.End.Value - range.Start.Value) : 0);
            }
            double? pulseElapsed = PulseElapsed(node.Member.Id);
            double pulseScale = stunElapsed is not null ? 1 : PulseScale(node.Member.Id);
            (double X, double Y) foot = FootPoint(node.Agent.TrackPosition);
            (int X, int Y) baseDestination = DestinationForFoot(
                foot,
                cached.PixelSize,
                cached.Definition.FootBaselinePixel * _integerScale,
                cached.OnlineContentBounds,
                1d,
                _edge);
            (int X, int Y) destination = DestinationForFoot(
                foot,
                cached.PixelSize,
                cached.Definition.FootBaselinePixel * _integerScale,
                cached.OnlineContentBounds,
                pulseScale,
                _edge);
            if (actionFrame is null
                && cached.Definition.MirrorsToMovementDirection
                && Math.Abs(node.Agent.Velocity) > 2d * _dpiScale)
            {
                node.FacingLeft = ShouldMirrorForVelocity(node.Agent.Velocity);
            }
            bool flipped = cached.Definition.MirrorsToMovementDirection && node.FacingLeft;
            Composite(
                destinationPixels,
                actionFrame is { } activeAction
                    ? _throwFrameCache.ActionFrame(node.Member.CharacterId, activeAction, flipped)
                    : cached.Frame(frame, flipped),
                cached.PixelSize,
                destination.X - _renderBounds.X,
                destination.Y - _renderBounds.Y,
                pulseScale,
                node.Member.Presence == PresenceState.Offline ? 0.75d : 1d,
                desaturate: node.Member.Presence == PresenceState.Offline);
            if (stunElapsed is null && ActiveCannonProjectile(node.Member.Id) is { } cannon)
            {
                bool emitterMirrored = ShouldMirrorEmitter(cannon);
                (double X, double Y) emitterCenter = CannonEmitterLayout.Center(
                    CharacterCenterPoint(node), emitterMirrored, _edge, _integerScale / 2d);
                int emitterFrame = Math.Min(
                    CharacterThrowFrameCache.EmitterFrameCount - 1,
                    (int)(Stopwatch.GetElapsedTime(cannon.StartedAt).TotalSeconds
                        / (ThrowActionSeconds / CharacterThrowFrameCache.EmitterFrameCount)));
                Composite(
                    destinationPixels,
                    _throwFrameCache.CannonEmitterFrame(
                        emitterFrame,
                        emitterMirrored),
                    cached.PixelSize,
                    (int)Math.Round(emitterCenter.X - (cached.PixelSize / 2d)) - _renderBounds.X,
                    (int)Math.Round(emitterCenter.Y - (cached.PixelSize / 2d)) - _renderBounds.Y,
                    1d,
                    1d);
            }
            if (stunElapsed is null && cached.Definition.VisualEffect == PixelCharacterVisualEffect.StarlightSparkles)
            {
                DrawStarlightSparkles(destinationPixels, node, pulseElapsed);
            }
            if (stunElapsed is { } effectElapsed)
                _stunDraws.Add((baseDestination.X, baseDestination.Y, effectElapsed));

            if (node.Member.IsCurrentUser)
            {
                currentUserHotspot = HotspotBounds(baseDestination, cached.PixelSize, foot);
            }
            else if (_targetHotspots.Count < NativeOverlayWindowThread.MaximumTargetHotspots)
            {
                _targetHotspots.Add(new CharacterHotspotFrame(
                    node.Member.Id,
                    HotspotBounds(baseDestination, cached.PixelSize, foot)));
            }

            PixelMemberVisuals visuals = _textVisuals.Get(node.Member.Id);
            (int X, int Y) nameplate = PlaceNameplate(foot, visuals.Nameplate);
            CompositeVisual(destinationPixels, visuals.Nameplate, nameplate.X, nameplate.Y);
            if (_bubblesBySender.TryGetValue(node.Member.Id, out List<ActiveBubble>? activeBubbles)
                && activeBubbles.Count > 0)
            {
                RenderMessageBubbles(
                    destinationPixels,
                    node.Agent.TrackPosition,
                    activeBubbles,
                    visuals,
                    visuals.Nameplate,
                    nameplate);
            }
            else if (visuals.TypingFrames.Count > 0)
            {
                RenderTypingBubble(
                    destinationPixels,
                    node.Agent.TrackPosition,
                    visuals,
                    visuals.Nameplate,
                    nameplate);
            }
            if (stunElapsed is null && visuals.Doze is { } doze)
            {
                (int X, int Y) dozePosition = PlaceDoze(baseDestination, cached.PixelSize, doze);
                long startedAt = _dozeStartedAt.GetValueOrDefault(node.Member.Id, _tick);
                double progress = DozeAnimationProgress(_tick - startedAt);
                (int X, int Y) animatedPosition = FloatDozeTowardInterior(
                    dozePosition,
                    (int)Math.Round(progress * _dozeFloatingDistancePixels));
                CompositeVisual(
                    destinationPixels,
                    doze,
                    animatedPosition.X,
                    animatedPosition.Y,
                    DozeRestingOpacity + (progress * (1d - DozeRestingOpacity)));
            }
        }

        // Draw after every nameplate, including neighboring characters' labels.
        foreach ((int X, int Y, double Elapsed) effect in _stunDraws)
            DrawStun(destinationPixels, effect.X, effect.Y, effect.Elapsed);
        RenderProjectiles(destinationPixels);

        byte opacity = _fadeOutStartedFrame < 0
            ? EntranceOpacity(_presentedFrameCount, _animationsEnabled())
            : ExitOpacity(_presentedFrameCount - _fadeOutStartedFrame, _animationsEnabled());
        _surface.Present(_renderBounds.X, _renderBounds.Y, opacity);
        _presentedFrameCount++;
        if (_fadeOutStartedFrame >= 0 && opacity == 0)
        {
            _fadeOutCompletion?.TrySetResult(true);
        }
        ReportPresentedMessageBubbles();
        if (_hotspotTrackingElapsed >= HotspotTrackingPolicy.MinimumUpdateInterval.TotalSeconds)
        {
            _hotspotsMoved(currentUserHotspot, _targetHotspots);
            _hotspotTrackingElapsed = 0d;
        }
    }

    private void RenderMessageBubbles(
        Span<byte> destination,
        double senderTangent,
        IReadOnlyList<ActiveBubble> bubbles,
        PixelMemberVisuals visuals,
        PremultipliedVisual nameplate,
        (int X, int Y) nameplatePosition)
    {
        if (_edge is OverlayEdge.Left or OverlayEdge.Right)
        {
            RenderSideMessageBubbles(
                destination,
                senderTangent,
                bubbles,
                visuals,
                nameplate,
                nameplatePosition);
            return;
        }

        int normalDistance = _bubbleCharacterGapPixels + _bubbleTailHeightPixels;
        for (int index = bubbles.Count - 1; index >= 0; index--)
        {
            ActiveBubble activeBubble = bubbles[index];
            if (!visuals.MessageBubbles.TryGetValue(activeBubble.MessageId, out PremultipliedVisual? bubble))
            {
                continue;
            }

            (int X, int Y) position = PlaceBubbleBody(
                senderTangent,
                bubble,
                normalDistance,
                nameplate,
                nameplatePosition);
            CompositeVisual(destination, bubble, position.X, position.Y);
            if (index == bubbles.Count - 1)
            {
                CompositeBubbleTail(destination, position, bubble, senderTangent);
            }
            _drawnBubbleIds.Add(activeBubble.MessageId);
            normalDistance += BubbleNormalExtent(bubble) + _bubbleBodySpacingPixels;
        }
    }

    private void RenderSideMessageBubbles(
        Span<byte> destination,
        double senderTangent,
        IReadOnlyList<ActiveBubble> bubbles,
        PixelMemberVisuals visuals,
        PremultipliedVisual nameplate,
        (int X, int Y) nameplatePosition)
    {
        Span<int> heightsNewestFirst = stackalloc int[ActiveBubbleLedger.MaximumVisiblePerSender];
        int visibleCount = 0;
        PremultipliedVisual? newestBubble = null;
        for (int index = bubbles.Count - 1; index >= 0; index--)
        {
            if (!visuals.MessageBubbles.TryGetValue(bubbles[index].MessageId, out PremultipliedVisual? bubble))
            {
                continue;
            }

            newestBubble ??= bubble;
            heightsNewestFirst[visibleCount++] = bubble.Height;
        }

        if (newestBubble is null || visibleCount == 0)
        {
            return;
        }

        int normalDistance = _bubbleCharacterGapPixels + _bubbleTailHeightPixels;
        (int X, int Y) desiredNewestPosition = PlaceBubbleBody(
            senderTangent,
            newestBubble,
            normalDistance,
            nameplate,
            nameplatePosition);
        int tangentMargin = (int)Math.Ceiling(_bubbleTangentMarginPixels);
        int newestTop = MessageBubbleLayoutPolicy.ClampedVerticalStackNewestTop(
            desiredNewestPosition.Y,
            heightsNewestFirst[..visibleCount],
            _activityBounds.Y + tangentMargin,
            _activityBounds.Y + _activityBounds.Height - tangentMargin,
            _bubbleBodySpacingPixels);

        int currentTop = newestTop;
        int renderedCount = 0;
        for (int index = bubbles.Count - 1; index >= 0; index--)
        {
            ActiveBubble activeBubble = bubbles[index];
            if (!visuals.MessageBubbles.TryGetValue(activeBubble.MessageId, out PremultipliedVisual? bubble))
            {
                continue;
            }

            if (renderedCount > 0)
            {
                currentTop -= bubble.Height + _bubbleBodySpacingPixels;
            }

            (int x, _) = PlaceBubbleBody(
                senderTangent,
                bubble,
                normalDistance,
                nameplate,
                nameplatePosition);
            (int X, int Y) position = (x, currentTop);
            CompositeVisual(destination, bubble, position.X, position.Y);
            if (renderedCount == 0)
            {
                CompositeBubbleTail(destination, position, bubble, senderTangent);
            }
            _drawnBubbleIds.Add(activeBubble.MessageId);
            renderedCount++;
        }
    }

    private void RenderTypingBubble(
        Span<byte> destination,
        double senderTangent,
        PixelMemberVisuals visuals,
        PremultipliedVisual nameplate,
        (int X, int Y) nameplatePosition)
    {
        int frameIndex = MessageBubbleLayoutPolicy.TypingFrameIndex(
            _tick,
            FramesPerSecond,
            visuals.TypingFrames.Count);
        PremultipliedVisual bubble = visuals.TypingFrames[frameIndex];
        (int X, int Y) position = PlaceBubbleBody(
            senderTangent,
            bubble,
            _bubbleCharacterGapPixels + _bubbleTailHeightPixels,
            nameplate,
            nameplatePosition);
        CompositeVisual(destination, bubble, position.X, position.Y);
        CompositeBubbleTail(destination, position, bubble, senderTangent);
    }

    private bool TryGetBubbleTangentRange(
        double senderTangent,
        IReadOnlyList<ActiveBubble> bubbles,
        PixelMemberVisuals visuals,
        out double lower,
        out double upper)
    {
        if (_edge is OverlayEdge.Left or OverlayEdge.Right)
        {
            Span<int> heightsNewestFirst = stackalloc int[ActiveBubbleLedger.MaximumVisiblePerSender];
            int visibleCount = 0;
            PremultipliedVisual? newestBubble = null;
            for (int index = bubbles.Count - 1; index >= 0; index--)
            {
                if (!visuals.MessageBubbles.TryGetValue(bubbles[index].MessageId, out PremultipliedVisual? bubble))
                {
                    continue;
                }

                newestBubble ??= bubble;
                heightsNewestFirst[visibleCount++] = bubble.Height;
            }

            if (newestBubble is null || visibleCount == 0)
            {
                lower = double.PositiveInfinity;
                upper = double.NegativeInfinity;
                return false;
            }

            int tangentMargin = (int)Math.Ceiling(_bubbleTangentMarginPixels);
            int desiredNewestTop = (int)Math.Round(ClampedBubbleVisualTangentStart(senderTangent, newestBubble));
            int newestTop = MessageBubbleLayoutPolicy.ClampedVerticalStackNewestTop(
                desiredNewestTop,
                heightsNewestFirst[..visibleCount],
                tangentMargin,
                (int)Math.Floor(_geometry.TangentLength - _bubbleTangentMarginPixels),
                _bubbleBodySpacingPixels);
            int totalHeight = 0;
            for (int index = 0; index < visibleCount; index++)
            {
                totalHeight += heightsNewestFirst[index];
            }
            totalHeight += _bubbleBodySpacingPixels * (visibleCount - 1);

            lower = newestTop - (totalHeight - heightsNewestFirst[0]);
            upper = lower + totalHeight;
            return true;
        }

        lower = double.PositiveInfinity;
        upper = double.NegativeInfinity;
        foreach (ActiveBubble activeBubble in bubbles)
        {
            if (!visuals.MessageBubbles.TryGetValue(activeBubble.MessageId, out PremultipliedVisual? bubble))
            {
                continue;
            }

            double start = ClampedBubbleVisualTangentStart(senderTangent, bubble);
            int extent = BubbleTangentExtent(bubble);
            lower = Math.Min(lower, start);
            upper = Math.Max(upper, start + extent);
        }

        return double.IsFinite(lower) && double.IsFinite(upper);
    }

    private void ReportPresentedMessageBubbles()
    {
        int newlyPresented = 0;
        foreach (Guid bubbleId in _drawnBubbleIds)
        {
            if (!_presentedBubbleIds.Contains(bubbleId))
            {
                newlyPresented++;
            }
        }

        _presentedBubbleIds.Clear();
        _presentedBubbleIds.UnionWith(_drawnBubbleIds);
        if (newlyPresented > 0)
        {
            _messageBubblesPresented?.Invoke(newlyPresented);
        }
    }

    private void ApplySnapshotWithinGate(WorldSnapshot snapshot)
    {
        _treeMovementPaused = snapshot.TreeMovementPaused;
        lock (_selfGate)
            _selfId = snapshot.Members.FirstOrDefault(member => member.IsCurrentUser)?.Id ?? Guid.Empty;
        if (_roomId != snapshot.RoomId)
        {
            _stun.Reset();
            _impactNotifications.Clear();
            _roomId = snapshot.RoomId;
            _projectiles.Clear();
            _throwStartedAt.Clear();
            _hitStartedAt.Clear();
        }
        _frameCache.RetainCharacters(snapshot.Members.Select(member => member.CharacterId));
        _incomingMemberIds.Clear();
        foreach (PixelWorldMember member in snapshot.Members)
        {
            _incomingMemberIds.Add(member.Id);
        }
        for (int index = _nodes.Count - 1; index >= 0; index--)
        {
            WorldNode node = _nodes[index];
            if (_incomingMemberIds.Contains(node.Member.Id))
            {
                continue;
            }

            _nodes.RemoveAt(index);
            _stun.Remove(node.Member.Id);
            _agents.Remove(node.Agent);
            _nodeById.Remove(node.Member.Id);
            _stoppedIds.Remove(node.Member.Id);
            _pulseStartedAt.Remove(node.Member.Id);
            _dozeStartedAt.Remove(node.Member.Id);
        }

        foreach (PixelWorldMember member in snapshot.Members)
        {
            if (_nodeById.TryGetValue(member.Id, out WorldNode? existing))
            {
                bool wasAway = existing.Member.Presence == PresenceState.Away;
                existing.Member = member with
                {
                    CharacterId = PixelCharacterCatalog.NormalizeId(member.CharacterId),
                };
                bool isAway = existing.Member.Presence == PresenceState.Away;
                if (!wasAway && isAway)
                {
                    _dozeStartedAt[member.Id] = _tick;
                }
                else if (!isAway)
                {
                    _dozeStartedAt.Remove(member.Id);
                }
            }
            else
            {
                double fraction = OverlayPlacementPolicy.Fraction(member.Id, _initialPositionSeed);
                double targetFraction = OverlayPlacementPolicy.Fraction(
                    member.Id,
                    _initialPositionSeed,
                    OverlayPlacementPolicy.TargetSalt);
                double position = _geometry.TrackLowerBound
                    + (fraction * (_geometry.TrackUpperBound - _geometry.TrackLowerBound));
                double target = _geometry.Clamp(
                    _geometry.TrackLowerBound
                    + (targetFraction * (_geometry.TrackUpperBound - _geometry.TrackLowerBound)));
                var agent = new PixelMovementAgent(member.Id, position, target,
                    idleRemaining: _random.NextDouble() * 1.5d);
                var node = new WorldNode(
                    member with { CharacterId = PixelCharacterCatalog.NormalizeId(member.CharacterId) },
                    agent);
                _nodeById.Add(member.Id, node);
                _nodes.Add(node);
                _agents.Add(agent);
                if (node.Member.Presence == PresenceState.Away)
                {
                    _dozeStartedAt[node.Member.Id] = _tick;
                }
            }
        }

        RefreshStoppedIds();

        _bubblesBySender.Clear();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (ActiveBubble bubble in snapshot.Bubbles)
        {
            if (bubble.ExpiresAt <= now)
            {
                continue;
            }

            if (!_bubblesBySender.TryGetValue(bubble.SenderId, out List<ActiveBubble>? senderBubbles))
            {
                senderBubbles = [];
                _bubblesBySender.Add(bubble.SenderId, senderBubbles);
            }
            senderBubbles.Add(bubble);
        }
        foreach (List<ActiveBubble> senderBubbles in _bubblesBySender.Values)
        {
            senderBubbles.Sort(CompareBubbles);
            if (senderBubbles.Count > ActiveBubbleLedger.MaximumVisiblePerSender)
            {
                senderBubbles.RemoveRange(
                    0,
                    senderBubbles.Count - ActiveBubbleLedger.MaximumVisiblePerSender);
            }
        }

        foreach (CharacterPulseEvent pulse in snapshot.Pulses)
        {
            if (!_pulseReplayGuard.TryAccept(pulse) || _stun.IsStunned(pulse.UserId))
            {
                continue;
            }

            _pulseStartedAt[pulse.UserId] = Stopwatch.GetTimestamp();
        }

        foreach (CharacterThrowEvent characterThrow in snapshot.Throws)
        {
            if (characterThrow.RoomId != snapshot.RoomId
                || characterThrow.ActorUserId == characterThrow.TargetUserId
                || !_nodeById.ContainsKey(characterThrow.ActorUserId)
                || !_nodeById.ContainsKey(characterThrow.TargetUserId)
                || !_throwReplayGuard.TryAccept(characterThrow)
                || _stun.IsStunned(characterThrow.ActorUserId))
            {
                continue;
            }

            if (_projectiles.Count == MaximumActiveProjectiles)
            {
                _projectiles.RemoveAt(0);
            }
            long started = Stopwatch.GetTimestamp();
            _throwStartedAt[characterThrow.ActorUserId] = started;
            var trajectory = new CharacterThrowTrajectory(
                CharacterCenterPoint(_nodeById[characterThrow.ActorUserId]),
                CharacterCenterPoint(_nodeById[characterThrow.TargetUserId]),
                _dpiScale);
            _projectiles.Add(new ActiveProjectile(characterThrow, started, trajectory));
        }

        _textVisuals.Update(snapshot);
    }

    private void RefreshStoppedIds()
    {
        _stoppedIds.Clear();
        foreach (WorldNode node in _nodes)
        {
            if (node.Member.Presence is PresenceState.Away or PresenceState.Offline or PresenceState.Reconnecting
                || _hitStartedAt.ContainsKey(node.Member.Id) || _stun.IsStunned(node.Member.Id)
                || PixelMovementPolicy.IsTreePaused(node.Member, _treeMovementPaused))
            {
                _stoppedIds.Add(node.Member.Id);
            }
        }
    }

    private void ExpireHitAnimations()
    {
        _expiredHitIds.Clear();
        foreach (KeyValuePair<Guid, long> hit in _hitStartedAt)
        {
            if (Stopwatch.GetElapsedTime(hit.Value).TotalSeconds >= HitActionSeconds)
            {
                _expiredHitIds.Add(hit.Key);
            }
        }
        foreach (Guid userId in _expiredHitIds)
        {
            _hitStartedAt.Remove(userId);
        }
    }

    private int? ActionFrame(Guid userId)
    {
        if (_hitStartedAt.TryGetValue(userId, out long hitStarted))
        {
            double elapsed = Stopwatch.GetElapsedTime(hitStarted).TotalSeconds;
            return 4 + Math.Min(3, (int)(elapsed / (HitActionSeconds / 4d)));
        }
        if (_throwStartedAt.TryGetValue(userId, out long throwStarted))
        {
            double elapsed = Stopwatch.GetElapsedTime(throwStarted).TotalSeconds;
            if (elapsed < ThrowActionSeconds)
            {
                return Math.Min(3, (int)(elapsed / (ThrowActionSeconds / 4d)));
            }
            _throwStartedAt.Remove(userId);
        }
        return null;
    }

    private ActiveProjectile? ActiveCannonProjectile(Guid actorUserId)
    {
        for (int index = _projectiles.Count - 1; index >= 0; index--)
        {
            ActiveProjectile projectile = _projectiles[index];
            if (projectile.Event.ActorUserId == actorUserId
                && StringComparer.Ordinal.Equals(
                    CosmeticCatalog.NormalizeThrowableId(projectile.Event.ThrowableId),
                    "throwable_toy_cannon")
                && Stopwatch.GetElapsedTime(projectile.StartedAt).TotalSeconds < ThrowActionSeconds)
            {
                return projectile;
            }
        }
        return null;
    }

    private void UpdateProjectiles()
    {
        for (int index = _projectiles.Count - 1; index >= 0; index--)
        {
            ActiveProjectile projectile = _projectiles[index];
            if (!_nodeById.ContainsKey(projectile.Event.ActorUserId))
            {
                _projectiles.RemoveAt(index);
                continue;
            }

            if (!_nodeById.TryGetValue(projectile.Event.TargetUserId, out WorldNode? target))
            {
                if (projectile.Start is not null && projectile.End is not null)
                {
                    projectile.ImpactStartedAt ??= Stopwatch.GetTimestamp();
                    if (Stopwatch.GetElapsedTime(projectile.ImpactStartedAt.Value).TotalSeconds >= CharacterImpactTiming.Duration(projectile.Event.ThrowableId))
                    {
                        _projectiles.RemoveAt(index);
                    }
                }
                else
                {
                    _projectiles.RemoveAt(index);
                }
                continue;
            }

            double elapsed = Stopwatch.GetElapsedTime(projectile.StartedAt).TotalSeconds;
            if (elapsed < ThrowReleaseSeconds)
            {
                continue;
            }
            projectile.Start ??= projectile.Trajectory.Start;
            projectile.End = CharacterCenterPoint(target);
            if (projectile.ImpactStartedAt is null
                && elapsed - ThrowReleaseSeconds >= projectile.Trajectory.DurationSeconds)
            {
                projectile.ImpactStartedAt = Stopwatch.GetTimestamp();
                if (elapsed - ThrowReleaseSeconds - projectile.Trajectory.DurationSeconds <= 0.5)
                    _impactNotifications.Enqueue((ImpactSoundCatalog.Resolve(projectile.Event.SourceCharacterId, projectile.Event.ThrowableId), projectile.ImpactStartedAt.Value));
                if (_stun.RecordHit(target.Member.Id))
                {
                    _pulseStartedAt.Remove(target.Member.Id);
                    _throwStartedAt.Remove(target.Member.Id);
                    _hitStartedAt.Remove(target.Member.Id);
                    target.Agent.Velocity = 0;
                }
                if (!_stun.IsStunned(target.Member.Id))
                    _hitStartedAt[target.Member.Id] = projectile.ImpactStartedAt.Value;
            }
            if (projectile.ImpactStartedAt is { } impactStarted
                && Stopwatch.GetElapsedTime(impactStarted).TotalSeconds >= CharacterImpactTiming.Duration(projectile.Event.ThrowableId))
            {
                _projectiles.RemoveAt(index);
            }
        }
    }

    private void RenderProjectiles(Span<byte> destination)
    {
        foreach (ActiveProjectile projectile in _projectiles)
        {
            if (projectile.Start is null || projectile.End is not { } end)
            {
                continue;
            }

            int frame;
            (double X, double Y) point;
            double renderScale;
            if (projectile.ImpactStartedAt is { } impactStarted)
            {
                double elapsed = Stopwatch.GetElapsedTime(impactStarted).TotalSeconds;
                frame = 8 + CharacterImpactTiming.Frame(projectile.Event.ThrowableId, elapsed);
                point = ImpactPoint(end);
                renderScale = 1.5d;
            }
            else
            {
                double elapsed = Stopwatch.GetElapsedTime(projectile.StartedAt).TotalSeconds - ThrowReleaseSeconds;
                point = projectile.Trajectory.PointAt(end, elapsed, _edge);
                frame = (int)(Math.Max(0d, elapsed) / 0.083d) % 8;
                renderScale = 1d;
            }

            int size = _throwFrameCache.ObjectPixelSize;
            int renderedSize = (int)Math.Round(size * renderScale);
            CompositeRectangle(
                destination,
                _throwFrameCache.ObjectFrame(
                    projectile.Event.SourceCharacterId,
                    projectile.Event.ThrowableId,
                    frame),
                size,
                size,
                (int)Math.Round(point.X - (renderedSize / 2d)) - _renderBounds.X,
                (int)Math.Round(point.Y - (renderedSize / 2d)) - _renderBounds.Y,
                renderScale,
                1d,
                desaturate: false);
        }
    }

    private (double X, double Y) BodyPoint(WorldNode node)
    {
        (double X, double Y) foot = FootPoint(node.Agent.TrackPosition);
        double inward = 12d * _integerScale;
        return _edge switch
        {
            OverlayEdge.Bottom => (foot.X, foot.Y - inward),
            OverlayEdge.Top => (foot.X, foot.Y + inward),
            OverlayEdge.Left => (foot.X + inward, foot.Y),
            OverlayEdge.Right => (foot.X - inward, foot.Y),
            _ => throw new ArgumentOutOfRangeException(),
        };
    }

    private (double X, double Y) CharacterCenterPoint(WorldNode node)
    {
        CachedCharacterFrames cached = _frameCache.Get(node.Member.CharacterId);
        return CharacterCenterPoint(
            FootPoint(node.Agent.TrackPosition), cached.PixelSize,
            cached.Definition.FootBaselinePixel * _integerScale,
            cached.OnlineContentBounds, _edge);
    }

    internal static (double X, double Y) CharacterCenterPoint(
        (double X, double Y) foot,
        int pixelSize,
        int baselinePixels,
        PixelContentBounds content,
        OverlayEdge edge)
    {
        // Use the unpulsed sprite placement, not its foot or sparkle anchor.
        (int X, int Y) destination = DestinationForFoot(foot, pixelSize, baselinePixels, content, 1d, edge);
        return (destination.X + (pixelSize / 2d), destination.Y + (pixelSize / 2d));
    }

    private void DrawStun(Span<byte> pixels, int worldX, int worldY, double elapsed)
    {
        foreach (StunPixel p in CharacterStunPixels.Create(elapsed, _animationsEnabled()))
        {
            (int x, int y) = _edge switch
            {
                OverlayEdge.Top => (23 - p.X, 23 - p.Y),
                OverlayEdge.Left => (23 - p.Y, p.X),
                OverlayEdge.Right => (p.Y, 23 - p.X),
                _ => (p.X, p.Y),
            };
            for (int dy = 0; dy < _integerScale; dy++)
                for (int dx = 0; dx < _integerScale; dx++)
                {
                    int px = worldX - _renderBounds.X + x * _integerScale + dx;
                    int py = worldY - _renderBounds.Y + y * _integerScale + dy;
                    if (px < 0 || py < 0 || px >= _renderBounds.Width || py >= _renderBounds.Height)
                        continue;
                    int index = (py * _renderBounds.Width + px) * 4;
                    pixels[index] = p.IsOutline ? (byte)12 : p.IsStar ? (byte)72 : (byte)104;
                    pixels[index + 1] = p.IsOutline ? (byte)82 : p.IsStar ? (byte)224 : (byte)175;
                    pixels[index + 2] = p.IsOutline ? (byte)130 : (byte)255;
                    pixels[index + 3] = 255;
                }
        }
    }

    private int FrameIndex(
        PresenceState presence,
        bool moving,
        PixelCharacterFrameContract frames)
    {
        Range range = presence switch
        {
            PresenceState.Away => frames.Doze,
            PresenceState.Offline => frames.Offline,
            _ when moving => frames.Walk,
            _ => frames.Idle,
        };
        int start = range.Start.Value;
        int count = range.End.Value - start;
        long divisor = moving ? 4L : 18L;
        return start + (int)((_tick / divisor) % count);
    }

    private double PulseScale(Guid userId)
    {
        if (!_pulseStartedAt.TryGetValue(userId, out long started))
        {
            return 1d;
        }

        double elapsed = Stopwatch.GetElapsedTime(started).TotalSeconds;
        if (elapsed <= 0.2d)
        {
            return 1d + (6d * elapsed / 0.2d);
        }

        if (elapsed <= 0.8d)
        {
            return 7d - (6d * (elapsed - 0.2d) / 0.6d);
        }

        _pulseStartedAt.Remove(userId);
        return 1d;
    }

    private double? PulseElapsed(Guid userId) =>
        _pulseStartedAt.TryGetValue(userId, out long started)
            ? Stopwatch.GetElapsedTime(started).TotalSeconds
            : null;

    private void DrawStarlightSparkles(
        Span<byte> destination,
        WorldNode node,
        double? pulseElapsed)
    {
        (double X, double Y) center = BodyPoint(node);
        (double X, double Y) ambientOrigin = CharacterCenterPoint(node);
        if (node.Member.Presence is PresenceState.Online or PresenceState.Typing)
        {
            double seedOffset = PositiveUnit(node.Member.Id.GetHashCode()) * 0.4d;
            double ambientElapsed = (_tick * FixedDeltaTime) + seedOffset;
            for (int index = 0; index < 5; index++)
            {
                (double Tangent, double Normal, double Radius, double Opacity) particle = StarlightSparkleLayout.Ambient(ambientElapsed, index, node.Member.Id.GetHashCode());
                (double X, double Y) point = StarlightSparkleLayout.Point(ambientOrigin,
                    particle.Tangent * _dpiScale, particle.Normal * _dpiScale, _edge);
                DrawSparkle(
                    destination,
                    point.X,
                    point.Y,
                    particle.Radius * _dpiScale,
                    particle.Opacity,
                    SparkleColor(index));
            }
        }

        if (pulseElapsed is not { } elapsed || elapsed < 0d || elapsed > SparklePulseDurationSeconds)
        {
            return;
        }

        double pulseProgress = elapsed / SparklePulseDurationSeconds;
        double pulseOpacity = 1d - (pulseProgress * pulseProgress);
        if (elapsed <= 0.32d)
        {
            double flashProgress = elapsed / 0.32d;
            DrawSparkle(
                destination,
                center.X,
                center.Y,
                34d * _dpiScale * (0.25d + (0.75d * flashProgress)),
                Math.Sin(Math.PI * flashProgress) * 0.75d,
                SparkleColor(2));
        }

        DrawSparkleWave(destination, node.Member.Id, center, 18, 126d, 168d, 8d, 13d,
            pulseProgress, pulseOpacity, angleOffset: 0d);
        double innerProgress = Math.Clamp((elapsed - 0.06d) / 0.72d, 0d, 1d);
        DrawSparkleWave(destination, node.Member.Id, center, 24, 82d, 132d, 4.5d, 8d,
            innerProgress, 1d - (innerProgress * innerProgress), angleOffset: 0.13d);
    }

    private void DrawSparkleWave(
        Span<byte> destination,
        Guid userId,
        (double X, double Y) center,
        int count,
        double minimumDistanceDip,
        double maximumDistanceDip,
        double minimumRadiusDip,
        double maximumRadiusDip,
        double progress,
        double opacity,
        double angleOffset)
    {
        if (progress <= 0d || opacity <= 0d)
        {
            return;
        }

        int seed = userId.GetHashCode();
        for (int index = 0; index < count; index++)
        {
            double jitter = PositiveUnit(seed ^ (index * 6151));
            double angle = ((Math.PI * 2d * index) / count) + angleOffset + ((jitter - 0.5d) * 0.18d);
            double maximumDistance = minimumDistanceDip
                + ((maximumDistanceDip - minimumDistanceDip) * PositiveUnit(seed ^ (index * 3253) ^ 0x24B7));
            double distance = maximumDistance * EaseOutCubic(progress) * _dpiScale;
            double radius = (minimumRadiusDip
                + ((maximumRadiusDip - minimumRadiusDip) * PositiveUnit(seed ^ (index * 1879))))
                * _dpiScale * (1d - (0.45d * progress));
            DrawSparkle(
                destination,
                center.X + (Math.Cos(angle) * distance),
                center.Y + (Math.Sin(angle) * distance),
                radius,
                opacity,
                SparkleColor(index));
        }
    }

    private void DrawSparkle(
        Span<byte> destination,
        double worldCenterX,
        double worldCenterY,
        double radius,
        double opacity,
        (byte Red, byte Green, byte Blue) color)
    {
        int centerX = (int)Math.Round(worldCenterX) - _renderBounds.X;
        int centerY = (int)Math.Round(worldCenterY) - _renderBounds.Y;
        int extent = Math.Max(1, (int)Math.Ceiling(radius));
        byte alpha = (byte)Math.Clamp((int)Math.Round(255d * opacity), 0, 255);
        for (int y = -extent; y <= extent; y++)
        {
            int destinationY = centerY + y;
            if (destinationY < 0 || destinationY >= _renderBounds.Height)
            {
                continue;
            }

            for (int x = -extent; x <= extent; x++)
            {
                int destinationX = centerX + x;
                if (destinationX < 0 || destinationX >= _renderBounds.Width)
                {
                    continue;
                }

                double normalizedX = Math.Abs(x) / Math.Max(1d, radius);
                double normalizedY = Math.Abs(y) / Math.Max(1d, radius);
                if (normalizedX + normalizedY > 1d
                    || (normalizedX > 0.24d && normalizedY > 0.24d))
                {
                    continue;
                }

                int pixel = ((destinationY * _renderBounds.Width) + destinationX) * 4;
                int inverseAlpha = 255 - alpha;
                destination[pixel] = Blend((byte)(color.Blue * alpha / 255), destination[pixel], inverseAlpha);
                destination[pixel + 1] = Blend((byte)(color.Green * alpha / 255), destination[pixel + 1], inverseAlpha);
                destination[pixel + 2] = Blend((byte)(color.Red * alpha / 255), destination[pixel + 2], inverseAlpha);
                destination[pixel + 3] = Blend(alpha, destination[pixel + 3], inverseAlpha);
            }
        }
    }

    private static double EaseOutCubic(double value) => 1d - Math.Pow(1d - value, 3d);

    private static double PositiveUnit(int value) => StarlightSparkleLayout.Unit(value);

    private static (byte Red, byte Green, byte Blue) SparkleColor(int index) => (index % 3) switch
    {
        0 => (120, 194, 173),
        1 => (168, 135, 214),
        _ => (245, 186, 56),
    };

    private (double X, double Y) FootPoint(double tangent) => _edge switch
    {
        OverlayEdge.Bottom => (
            _activityBounds.X + tangent,
            _activityBounds.Y + _activityBounds.Height - _edgeInsetPixels),
        OverlayEdge.Top => (
            _activityBounds.X + tangent,
            _activityBounds.Y + _edgeInsetPixels),
        OverlayEdge.Left => (
            _activityBounds.X + _edgeInsetPixels,
            _activityBounds.Y + tangent),
        OverlayEdge.Right => (
            _activityBounds.X + _activityBounds.Width - _edgeInsetPixels,
            _activityBounds.Y + tangent),
        _ => throw new ArgumentOutOfRangeException(),
    };

    private int ClampEdgeInset(int edgeInsetPixels)
    {
        int depth = _edge is OverlayEdge.Bottom or OverlayEdge.Top
            ? _activityBounds.Height
            : _activityBounds.Width;
        return Math.Clamp(edgeInsetPixels, 0, Math.Max(0, depth - 1));
    }

    private void AnimateEdgeInset()
    {
        _edgeInsetPixels = NextEdgeInset(
            _edgeInsetPixels,
            _targetEdgeInsetPixels,
            _integerScale,
            _animationsEnabled());
    }

    internal static byte EntranceOpacity(int presentedFrameCount, bool animationsEnabled)
    {
        if (!animationsEnabled)
        {
            return byte.MaxValue;
        }

        double progress = Math.Clamp(
            presentedFrameCount * FixedDeltaTime / EntranceFadeDurationSeconds,
            0d,
            1d);
        double eased = 1d - Math.Pow(1d - progress, 3d);
        return (byte)Math.Round(byte.MaxValue * eased, MidpointRounding.AwayFromZero);
    }

    internal static byte ExitOpacity(int presentedFrameCount, bool animationsEnabled)
    {
        if (!animationsEnabled)
        {
            return 0;
        }

        double progress = Math.Clamp(
            presentedFrameCount * FixedDeltaTime / ExitFadeDurationSeconds,
            0d,
            1d);
        double remaining = 1d - progress;
        double eased = remaining * remaining * (3d - (2d * remaining));
        return (byte)Math.Round(byte.MaxValue * eased, MidpointRounding.AwayFromZero);
    }

    internal static double NextEdgeInset(
        double current,
        int target,
        int integerScale,
        bool animationsEnabled)
    {
        if (!animationsEnabled)
        {
            return target;
        }

        double distance = target - current;
        double maximumStep = EdgeInsetAnimationSpeedDipPerSecond
            * (integerScale / 2d)
            * FixedDeltaTime;
        if (Math.Abs(distance) <= 1d)
        {
            return target;
        }

        double easedStep = Math.Clamp(Math.Abs(distance) * 0.24d, 1d, maximumStep);
        return current + Math.CopySign(easedStep, distance);
    }

    private static (int X, int Y) DestinationForFoot(
        (double X, double Y) foot,
        int pixelSize,
        int baselinePixels,
        PixelContentBounds content,
        double pulseScale,
        OverlayEdge edge)
    {
        double scaledSize = pixelSize * pulseScale;
        double scaledBaseline = baselinePixels * pulseScale;
        double x = edge switch
        {
            OverlayEdge.Left => foot.X - (content.MinX * pulseScale),
            OverlayEdge.Right => foot.X - (content.MaxX * pulseScale),
            _ => foot.X - (scaledSize / 2d),
        };
        double y = edge switch
        {
            OverlayEdge.Top => foot.Y - scaledBaseline,
            OverlayEdge.Bottom => foot.Y - (scaledSize - scaledBaseline),
            _ => foot.Y - (scaledSize / 2d),
        };
        return (
            (int)Math.Round(x, MidpointRounding.AwayFromZero),
            (int)Math.Round(y, MidpointRounding.AwayFromZero));
    }

    private NativePixelRect HotspotBounds(
        (int X, int Y) sprite,
        int spriteSize,
        (double X, double Y) foot)
    {
        int centeredX = sprite.X + ((spriteSize - _hotspotPixelSize) / 2);
        int centeredY = sprite.Y + ((spriteSize - _hotspotPixelSize) / 2);
        int footX = (int)Math.Round(foot.X, MidpointRounding.AwayFromZero);
        int footY = (int)Math.Round(foot.Y, MidpointRounding.AwayFromZero);
        return _edge switch
        {
            OverlayEdge.Bottom => new NativePixelRect(
                centeredX,
                Math.Min(centeredY, footY - _hotspotPixelSize),
                _hotspotPixelSize,
                _hotspotPixelSize),
            OverlayEdge.Top => new NativePixelRect(
                centeredX,
                Math.Max(centeredY, footY),
                _hotspotPixelSize,
                _hotspotPixelSize),
            OverlayEdge.Left => new NativePixelRect(
                Math.Max(centeredX, footX),
                centeredY,
                _hotspotPixelSize,
                _hotspotPixelSize),
            OverlayEdge.Right => new NativePixelRect(
                Math.Min(centeredX, footX - _hotspotPixelSize),
                centeredY,
                _hotspotPixelSize,
                _hotspotPixelSize),
            _ => throw new ArgumentOutOfRangeException(),
        };
    }

    private void Composite(
        Span<byte> destination,
        ReadOnlySpan<byte> source,
        int sourceSize,
        int destinationX,
        int destinationY,
        double scale,
        double opacity,
        bool desaturate = false)
    {
        CompositeRectangle(
            destination,
            source,
            sourceSize,
            sourceSize,
            destinationX,
            destinationY,
            scale,
            opacity,
            desaturate);
    }

    private void CompositeBubbleTail(
        Span<byte> destination,
        (int X, int Y) visualPosition,
        PremultipliedVisual visual,
        double senderTangent)
    {
        PixelVisualBodyBounds localBody = BubbleBodyBounds(visual);
        var bodyBounds = new RectD(
            visualPosition.X + localBody.X,
            visualPosition.Y + localBody.Y,
            localBody.Width,
            localBody.Height);
        MessageBubbleTail tail = MessageBubbleLayoutPolicy.Tail(
            _edge,
            bodyBounds,
            TangentWorldOrigin() + senderTangent,
            _bubbleTailHeightPixels,
            _bubbleTailHalfBasePixels,
            _bubbleTailBaseInsetPixels,
            baseOverlap: _bubbleTailBodyOverlapPixels);
        MessageBubbleTailRasterizer.Composite(
            destination,
            _renderBounds,
            tail,
            bodyBounds,
            _dpiScale,
            visual.BubblePalette);
    }

    private void CompositeVisual(
        Span<byte> destination,
        PremultipliedVisual visual,
        int destinationX,
        int destinationY,
        double opacity = 1d) =>
        CompositeRectangle(
            destination,
            visual.Pixels,
            visual.Width,
            visual.Height,
            destinationX - _renderBounds.X,
            destinationY - _renderBounds.Y,
            1d,
            opacity,
            desaturate: false);

    private void CompositeRectangle(
        Span<byte> destination,
        ReadOnlySpan<byte> source,
        int sourceWidth,
        int sourceHeight,
        int destinationX,
        int destinationY,
        double scale,
        double opacity,
        bool desaturate)
    {
        int destinationWidth = Math.Max(1, (int)Math.Round(sourceWidth * scale));
        int destinationHeight = Math.Max(1, (int)Math.Round(sourceHeight * scale));
        bool useDirectColors = opacity >= 0.999d && !desaturate;
        for (int y = 0; y < destinationHeight; y++)
        {
            int worldY = destinationY + y;
            if (worldY < 0 || worldY >= _renderBounds.Height)
            {
                continue;
            }

            int sourceY = Math.Min(sourceHeight - 1, (int)(y / scale));
            for (int x = 0; x < destinationWidth; x++)
            {
                int worldX = destinationX + x;
                if (worldX < 0 || worldX >= _renderBounds.Width)
                {
                    continue;
                }

                int sourceX = Math.Min(sourceWidth - 1, (int)(x / scale));
                int sourceIndex = ((sourceY * sourceWidth) + sourceX) * 4;
                byte sourceAlpha = useDirectColors
                    ? source[sourceIndex + 3]
                    : (byte)Math.Round(source[sourceIndex + 3] * opacity);
                if (sourceAlpha == 0)
                {
                    continue;
                }

                int destinationIndex = ((worldY * _renderBounds.Width) + worldX) * 4;
                int inverseAlpha = 255 - sourceAlpha;
                byte sourceBlue = source[sourceIndex];
                byte sourceGreen = source[sourceIndex + 1];
                byte sourceRed = source[sourceIndex + 2];
                if (desaturate)
                {
                    byte gray = (byte)((sourceBlue * 11 + sourceGreen * 59 + sourceRed * 30) / 100);
                    sourceBlue = (byte)((sourceBlue * 42 + gray * 58) / 100);
                    sourceGreen = (byte)((sourceGreen * 42 + gray * 58) / 100);
                    sourceRed = (byte)((sourceRed * 42 + gray * 58) / 100);
                }

                if (!useDirectColors)
                {
                    sourceBlue = (byte)Math.Round(sourceBlue * opacity);
                    sourceGreen = (byte)Math.Round(sourceGreen * opacity);
                    sourceRed = (byte)Math.Round(sourceRed * opacity);
                }

                destination[destinationIndex] = Blend(
                    sourceBlue,
                    destination[destinationIndex],
                    inverseAlpha);
                destination[destinationIndex + 1] = Blend(
                    sourceGreen,
                    destination[destinationIndex + 1],
                    inverseAlpha);
                destination[destinationIndex + 2] = Blend(
                    sourceRed,
                    destination[destinationIndex + 2],
                    inverseAlpha);
                destination[destinationIndex + 3] = Blend(
                    sourceAlpha,
                    destination[destinationIndex + 3],
                    inverseAlpha);
            }
        }
    }

    private (int X, int Y) PlaceNameplate(
        (double X, double Y) foot, PremultipliedVisual visual)
    {
        (double X, double Y) position = CharacterNameplateLayout.Position(foot, visual.Width, visual.Height, _integerScale, _edge);
        return ((int)Math.Round(position.X, MidpointRounding.AwayFromZero),
            (int)Math.Round(position.Y, MidpointRounding.AwayFromZero));
    }

    private (int X, int Y) PlaceBubbleBody(
        double senderTangent,
        PremultipliedVisual visual,
        int normalDistance,
        PremultipliedVisual nameplate,
        (int X, int Y) nameplatePosition)
    {
        PixelVisualBodyBounds body = BubbleBodyBounds(visual);
        double visualTangentStart = ClampedBubbleVisualTangentStart(senderTangent, visual);
        int worldVisualTangentStart = (int)Math.Round(visualTangentStart) + TangentWorldOrigin();
        return _edge switch
        {
            OverlayEdge.Bottom => (
                worldVisualTangentStart,
                nameplatePosition.Y - normalDistance - body.Height - body.Y),
            OverlayEdge.Top => (
                worldVisualTangentStart,
                nameplatePosition.Y + nameplate.Height + normalDistance - body.Y),
            OverlayEdge.Left => (
                nameplatePosition.X + nameplate.Width + normalDistance - body.X,
                worldVisualTangentStart),
            OverlayEdge.Right => (
                nameplatePosition.X - normalDistance - body.Width - body.X,
                worldVisualTangentStart),
            _ => throw new ArgumentOutOfRangeException(),
        };
    }

    private (double X, double Y) ImpactPoint((double X, double Y) center)
    {
        double inward = 10d * _dpiScale;
        return _edge switch
        {
            OverlayEdge.Bottom => (center.X, center.Y - inward),
            OverlayEdge.Top => (center.X, center.Y + inward),
            OverlayEdge.Left => (center.X + inward, center.Y),
            OverlayEdge.Right => (center.X - inward, center.Y),
            _ => throw new ArgumentOutOfRangeException(),
        };
    }

    private bool ShouldMirrorForVelocity(double velocity) => _edge switch
    {
        OverlayEdge.Bottom or OverlayEdge.Right => velocity < 0d,
        OverlayEdge.Top or OverlayEdge.Left => velocity > 0d,
        _ => throw new ArgumentOutOfRangeException(),
    };

    private bool ShouldMirrorEmitter(ActiveProjectile projectile)
    {
        if (!_nodeById.TryGetValue(projectile.Event.ActorUserId, out WorldNode? actor)
            || !_nodeById.TryGetValue(projectile.Event.TargetUserId, out WorldNode? target))
        {
            return false;
        }

        double actorPosition = actor.Agent.TrackPosition;
        double targetPosition = target.Agent.TrackPosition;
        return CannonEmitterLayout.ShouldMirror(actorPosition, targetPosition, _edge);
    }

    private double ClampedBubbleVisualTangentStart(
        double senderTangent,
        PremultipliedVisual visual)
    {
        PixelVisualBodyBounds body = BubbleBodyBounds(visual);
        int bodyExtent = _edge is OverlayEdge.Bottom or OverlayEdge.Top
            ? body.Width
            : body.Height;
        int bodyStart = _edge is OverlayEdge.Bottom or OverlayEdge.Top
            ? body.X
            : body.Y;
        int visualExtent = BubbleTangentExtent(visual);
        int trailingOverflow = visualExtent - bodyStart - bodyExtent;
        double clampedBodyStart = MessageBubbleLayoutPolicy.ClampedBodyTangentStart(
            senderTangent,
            _geometry.TangentLength,
            bodyExtent,
            bodyStart,
            trailingOverflow,
            _bubbleTangentMarginPixels);
        return clampedBodyStart - bodyStart;
    }

    private int TangentWorldOrigin() => _edge is OverlayEdge.Bottom or OverlayEdge.Top
        ? _activityBounds.X
        : _activityBounds.Y;

    private int BubbleTangentExtent(PremultipliedVisual visual) =>
        _edge is OverlayEdge.Bottom or OverlayEdge.Top ? visual.Width : visual.Height;

    private int BubbleNormalExtent(PremultipliedVisual visual) =>
        _edge is OverlayEdge.Bottom or OverlayEdge.Top
            ? BubbleBodyBounds(visual).Height
            : BubbleBodyBounds(visual).Width;

    private static PixelVisualBodyBounds BubbleBodyBounds(PremultipliedVisual visual) =>
        visual.BubbleBodyBounds ?? new PixelVisualBodyBounds(0, 0, visual.Width, visual.Height);

    private (int X, int Y) PlaceDoze(
        (int X, int Y) sprite,
        int spriteSize,
        PremultipliedVisual visual) => _edge switch
        {
            OverlayEdge.Bottom => (sprite.X + spriteSize - (visual.Width / 3), sprite.Y - (visual.Height / 2)),
            OverlayEdge.Top => (sprite.X - (visual.Width / 2), sprite.Y + spriteSize - (visual.Height / 2)),
            OverlayEdge.Left => (sprite.X + spriteSize - (visual.Width / 2), sprite.Y + spriteSize - (visual.Height / 3)),
            OverlayEdge.Right => (sprite.X - (visual.Width / 2), sprite.Y - (visual.Height / 3)),
            _ => throw new ArgumentOutOfRangeException(),
        };

    private static double DozeAnimationProgress(long tick)
    {
        long phase = tick % FramesPerSecond;
        return phase <= FramesPerSecond / 2
            ? phase / (FramesPerSecond / 2d)
            : (FramesPerSecond - phase) / (FramesPerSecond / 2d);
    }

    private (int X, int Y) FloatDozeTowardInterior((int X, int Y) position, int distance) => _edge switch
    {
        OverlayEdge.Bottom => (position.X, position.Y - distance),
        OverlayEdge.Top => (position.X, position.Y + distance),
        OverlayEdge.Left => (position.X + distance, position.Y),
        OverlayEdge.Right => (position.X - distance, position.Y),
        _ => throw new ArgumentOutOfRangeException(),
    };

    private static byte Blend(byte source, byte destination, int inverseAlpha) =>
        (byte)Math.Min(255, source + ((destination * inverseAlpha + 127) / 255));

    private static int DipToPixels(double value, uint dpi) =>
        (int)Math.Round(value * dpi / 96d, MidpointRounding.AwayFromZero);

    private static int CompareBubbles(ActiveBubble left, ActiveBubble right)
    {
        int dateComparison = left.ExpiresAt.CompareTo(right.ExpiresAt);
        return dateComparison != 0
            ? dateComparison
            : StringComparer.Ordinal.Compare(
                left.MessageId.ToString("D"),
                right.MessageId.ToString("D"));
    }

    private sealed class WorldNode(PixelWorldMember member, PixelMovementAgent agent)
    {
        public PixelWorldMember Member { get; set; } = member;
        public PixelMovementAgent Agent { get; } = agent;
        public bool FacingLeft { get; set; }
    }

    private sealed class ActiveProjectile(
        CharacterThrowEvent @event, long startedAt, CharacterThrowTrajectory trajectory)
    {
        public CharacterThrowEvent Event { get; } = @event;
        public long StartedAt { get; } = startedAt;
        public CharacterThrowTrajectory Trajectory { get; } = trajectory;
        public (double X, double Y)? Start { get; set; }
        public (double X, double Y)? End { get; set; }
        public long? ImpactStartedAt { get; set; }
    }
}
