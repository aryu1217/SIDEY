using Sidey.Core.Domain;
using Sidey.Core.Overlay;

namespace Sidey.Overlay.Layout;

internal readonly record struct MessageBubbleTail(
    PointD BaseStart,
    PointD Tip,
    PointD BaseEnd);

internal static class MessageBubbleLayoutPolicy
{
    internal const double TangentMarginDip = 4d;
    internal const double BodySpacingDip = 6d;
    internal const double TailHeightDip = 8d;
    internal const double TailHalfBaseDip = 6d;
    internal const double TailBaseInsetDip = 10d;
    internal const double TailBodyOverlapDip = 2d;
    internal const double CharacterGapDip = 4d;
    internal const double TypingFrameIntervalSeconds = 0.35d;

    internal static double ClampedTangentStart(
        double senderTangent,
        double tangentLength,
        double bodyTangentExtent,
        double margin)
    {
        double halfExtent = bodyTangentExtent / 2d;
        double minimumCenter = halfExtent + margin;
        double maximumCenter = Math.Max(minimumCenter, tangentLength - halfExtent - margin);
        double center = Math.Clamp(senderTangent, minimumCenter, maximumCenter);
        return center - halfExtent;
    }

    internal static double ClampedBodyTangentStart(
        double senderTangent,
        double tangentLength,
        double bodyTangentExtent,
        double leadingOverflow,
        double trailingOverflow,
        double margin)
    {
        double halfExtent = bodyTangentExtent / 2d;
        double minimumCenter = halfExtent + margin + Math.Max(0d, leadingOverflow);
        double maximumCenter = Math.Max(
            minimumCenter,
            tangentLength - halfExtent - margin - Math.Max(0d, trailingOverflow));
        double center = Math.Clamp(senderTangent, minimumCenter, maximumCenter);
        return center - halfExtent;
    }

    internal static int TypingFrameIndex(long tick, int framesPerSecond, int frameCount)
    {
        if (framesPerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(framesPerSecond));
        }
        if (frameCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(frameCount));
        }

        if (tick < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tick));
        }

        const int IntervalHundredths = 35;
        long elapsedIntervals = (tick * 100) / (framesPerSecond * IntervalHundredths);
        return (int)(elapsedIntervals % frameCount);
    }

    internal static int ClampedVerticalStackNewestTop(
        int desiredNewestTop,
        ReadOnlySpan<int> visualHeightsNewestFirst,
        int minimumTop,
        int maximumBottom,
        int spacing)
    {
        if (visualHeightsNewestFirst.IsEmpty)
        {
            throw new ArgumentException("At least one bubble height is required.", nameof(visualHeightsNewestFirst));
        }
        if (maximumBottom < minimumTop)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBottom));
        }
        if (spacing < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(spacing));
        }

        int totalHeight = 0;
        foreach (int height in visualHeightsNewestFirst)
        {
            if (height <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(visualHeightsNewestFirst));
            }

            totalHeight = checked(totalHeight + height);
        }

        totalHeight = checked(totalHeight + (spacing * (visualHeightsNewestFirst.Length - 1)));
        int olderStackExtent = totalHeight - visualHeightsNewestFirst[0];
        int desiredStackTop = desiredNewestTop - olderStackExtent;
        int maximumStackTop = Math.Max(minimumTop, maximumBottom - totalHeight);
        int stackTop = Math.Clamp(desiredStackTop, minimumTop, maximumStackTop);
        return checked(stackTop + olderStackExtent);
    }

    internal static MessageBubbleTail Tail(
        OverlayEdge edge,
        RectD body,
        double senderWorldTangent,
        double tailHeight,
        double halfBase,
        double baseInset,
        double baseOverlap = 0d)
    {
        return edge switch
        {
            OverlayEdge.Bottom => HorizontalTail(
                body,
                senderWorldTangent,
                body.MaxY - baseOverlap,
                body.MaxY + tailHeight,
                halfBase,
                baseInset),
            OverlayEdge.Top => HorizontalTail(
                body,
                senderWorldTangent,
                body.MinY + baseOverlap,
                body.MinY - tailHeight,
                halfBase,
                baseInset),
            OverlayEdge.Left => VerticalTail(
                body,
                senderWorldTangent,
                body.MinX + baseOverlap,
                body.MinX - tailHeight,
                halfBase,
                baseInset),
            OverlayEdge.Right => VerticalTail(
                body,
                senderWorldTangent,
                body.MaxX - baseOverlap,
                body.MaxX + tailHeight,
                halfBase,
                baseInset),
            _ => throw new ArgumentOutOfRangeException(nameof(edge)),
        };
    }

    private static MessageBubbleTail HorizontalTail(
        RectD body,
        double senderWorldTangent,
        double baseY,
        double tipY,
        double halfBase,
        double baseInset)
    {
        double baseCenter = Math.Clamp(
            senderWorldTangent,
            body.MinX + baseInset,
            body.MaxX - baseInset);
        return new MessageBubbleTail(
            new PointD(baseCenter - halfBase, baseY),
            new PointD(senderWorldTangent, tipY),
            new PointD(baseCenter + halfBase, baseY));
    }

    private static MessageBubbleTail VerticalTail(
        RectD body,
        double senderWorldTangent,
        double baseX,
        double tipX,
        double halfBase,
        double baseInset)
    {
        double baseCenter = Math.Clamp(
            senderWorldTangent,
            body.MinY + baseInset,
            body.MaxY - baseInset);
        return new MessageBubbleTail(
            new PointD(baseX, baseCenter - halfBase),
            new PointD(tipX, senderWorldTangent),
            new PointD(baseX, baseCenter + halfBase));
    }
}
