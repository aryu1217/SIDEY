using Sidey.Core.Domain;

namespace Sidey.Core.Overlay;

/// <summary>Local edge coordinates: tangent follows the activity track; depth points into the screen.</summary>
public readonly record struct CharacterDragPoint(double Tangent, double Depth);

public static class CharacterDragCoordinates
{
    public static CharacterDragPoint FromScreen(
        PointD pointer, RectD activity, RectD monitor, OverlayEdge edge, double edgeInset)
    {
        double x = Math.Clamp(pointer.X, monitor.MinX, monitor.MaxX - 1d);
        double y = Math.Clamp(pointer.Y, monitor.MinY, monitor.MaxY - 1d);
        return edge switch
        {
            OverlayEdge.Bottom => new CharacterDragPoint(x - activity.MinX, activity.MaxY - edgeInset - y),
            OverlayEdge.Top => new CharacterDragPoint(x - activity.MinX, y - activity.MinY - edgeInset),
            OverlayEdge.Left => new CharacterDragPoint(y - activity.MinY, x - activity.MinX - edgeInset),
            OverlayEdge.Right => new CharacterDragPoint(y - activity.MinY, activity.MaxX - edgeInset - x),
            _ => throw new ArgumentOutOfRangeException(nameof(edge)),
        };
    }

    public static PointD ToScreen(CharacterDragPoint point, RectD activity, OverlayEdge edge, double edgeInset) =>
        edge switch
        {
            OverlayEdge.Bottom => new PointD(activity.MinX + point.Tangent, activity.MaxY - edgeInset - point.Depth),
            OverlayEdge.Top => new PointD(activity.MinX + point.Tangent, activity.MinY + edgeInset + point.Depth),
            OverlayEdge.Left => new PointD(activity.MinX + edgeInset + point.Depth, activity.MinY + point.Tangent),
            OverlayEdge.Right => new PointD(activity.MaxX - edgeInset - point.Depth, activity.MinY + point.Tangent),
            _ => throw new ArgumentOutOfRangeException(nameof(edge)),
        };

    public static double MaximumDepth(RectD activity, RectD monitor, OverlayEdge edge, double edgeInset) =>
        Math.Max(0d, edge switch
        {
            OverlayEdge.Bottom => activity.MaxY - edgeInset - monitor.MinY,
            OverlayEdge.Top => monitor.MaxY - activity.MinY - edgeInset,
            OverlayEdge.Left => monitor.MaxX - activity.MinX - edgeInset,
            OverlayEdge.Right => activity.MaxX - edgeInset - monitor.MinX,
            _ => throw new ArgumentOutOfRangeException(nameof(edge)),
        });
}

/// <summary>Bounded, fixed-step motion for a character suspended from a local pointer.</summary>
public sealed class CharacterDragPhysics(CharacterDragPoint position)
{
    public const double MaximumRopeLength = 48d;
    private const double StepSeconds = 1d / 120d;
    private const double MaximumFrameSeconds = 0.1d;
    private double _accumulator;
    private double _flightSeconds;
    private CharacterDragPoint? _lastSimulatedPointer;

    public CharacterDragPoint Position { get; private set; } = position;
    public CharacterDragPoint Velocity { get; private set; }
    public CharacterDragPoint? Pointer { get; private set; }
    public double Rotation { get; private set; }
    public bool Landed { get; private set; }

    public void MovePin(CharacterDragPoint pointer) => Pointer = pointer;

    public void Release(double tangentLower, double tangentUpper, double depth, bool reduceMotion)
    {
        if (Pointer is not null && Pointer != _lastSimulatedPointer)
        {
            Step(StepSeconds, tangentLower, tangentUpper, depth, reduceMotion);
        }
        Pointer = null;
    }

    public CharacterDragPoint? Pin(double tangentLower, double tangentUpper, double depth) =>
        Pointer is { } pointer
            ? new CharacterDragPoint(
                Math.Clamp(pointer.Tangent, tangentLower, tangentUpper),
                Math.Clamp(pointer.Depth, 0d, Math.Max(0d, depth - 22d)))
            : null;

    public void Step(double deltaTime, double tangentLower, double tangentUpper, double depth, bool reduceMotion)
    {
        if (Landed || tangentUpper < tangentLower || !double.IsFinite(depth))
        {
            return;
        }

        _accumulator = Math.Min(MaximumFrameSeconds,
            _accumulator + Math.Clamp(double.IsFinite(deltaTime) ? deltaTime : 0d, 0d, MaximumFrameSeconds));
        int steps = 0;
        while (_accumulator >= StepSeconds && steps++ < 12)
        {
            _accumulator -= StepSeconds;
            double ceiling = Math.Max(0d, depth - 60d);
            CharacterDragPoint previous = Position;
            _lastSimulatedPointer = Pointer;
            CharacterDragPoint? pin = Pin(tangentLower, tangentUpper, depth);
            if (pin is { } stillPin && reduceMotion)
            {
                Position = new CharacterDragPoint(stillPin.Tangent,
                    Math.Clamp(stillPin.Depth - MaximumRopeLength, 0d, ceiling));
                Velocity = default;
                Rotation = 0d;
                continue;
            }

            double vx = Velocity.Tangent * Math.Exp(-(pin is null ? 2.8d : 0.75d) * StepSeconds);
            double vy = (Velocity.Depth - 1100d * StepSeconds)
                * Math.Exp(-(pin is null ? 0d : 0.75d) * StepSeconds);
            vx = Math.Clamp(vx, -650d, 650d);
            vy = Math.Clamp(vy, -850d, 850d);
            if (pin is null)
            {
                _flightSeconds += StepSeconds;
                Rotation *= Math.Exp(-7d * StepSeconds);
            }
            double x = Position.Tangent + vx * StepSeconds;
            double y = Position.Depth + vy * StepSeconds;
            if (pin is { } anchor)
            {
                double dx = x - anchor.Tangent;
                double dy = y - anchor.Depth;
                double distance = Math.Sqrt(dx * dx + dy * dy);
                if (distance > MaximumRopeLength)
                {
                    double ratio = MaximumRopeLength / distance;
                    x = anchor.Tangent + dx * ratio;
                    y = anchor.Depth + dy * ratio;
                }
                Rotation = Math.Clamp(
                    -Math.Atan2(anchor.Tangent - x, Math.Max(1d, anchor.Depth - y)),
                    -0.9d, 0.9d);
            }
            if (x < tangentLower || x > tangentUpper)
            {
                x = Math.Clamp(x, tangentLower, tangentUpper);
                vx *= -0.15d;
            }
            if (y > ceiling)
            {
                y = ceiling;
                vy = Math.Min(0d, vy);
            }
            if (pin is { } constrainedPin)
            {
                y = Math.Clamp(y,
                    Math.Min(ceiling, Math.Max(0d, constrainedPin.Depth - MaximumRopeLength)), ceiling);
                double vertical = y - constrainedPin.Depth;
                double horizontal = Math.Sqrt(Math.Max(0d,
                    MaximumRopeLength * MaximumRopeLength - vertical * vertical));
                x = Math.Clamp(x,
                    Math.Max(tangentLower, constrainedPin.Tangent - horizontal),
                    Math.Min(tangentUpper, constrainedPin.Tangent + horizontal));
                vx = Math.Clamp((x - previous.Tangent) / StepSeconds, -650d, 650d);
                vy = Math.Clamp((y - previous.Depth) / StepSeconds, -850d, 850d);
            }
            if (y <= 0d)
            {
                y = 0d;
                if (pin is null)
                {
                    if (reduceMotion || Math.Abs(vy) < 95d || _flightSeconds > 3d)
                    {
                        vx = 0d;
                        vy = 0d;
                        Rotation = 0d;
                        Landed = true;
                    }
                    else
                    {
                        vy = Math.Min(160d, Math.Abs(vy) * 0.24d);
                        vx *= 0.6d;
                    }
                }
                else
                {
                    vy = Math.Max(0d, vy);
                }
            }
            Position = new CharacterDragPoint(x, y);
            Velocity = new CharacterDragPoint(vx, vy);
        }
    }
}
