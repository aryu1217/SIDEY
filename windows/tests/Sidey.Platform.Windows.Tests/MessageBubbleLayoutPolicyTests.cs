using Sidey.Core.Domain;
using Sidey.Core.Overlay;
using Sidey.Overlay;

namespace Sidey.Platform.Windows.Tests;

public sealed class MessageBubbleLayoutPolicyTests
{
    [Theory]
    [InlineData(0d, 4d)]
    [InlineData(250d, 200d)]
    [InlineData(500d, 396d)]
    public void BubbleBodyStaysInsideTheTangentMargins(
        double senderTangent,
        double expectedStart)
    {
        double start = MessageBubbleLayoutPolicy.ClampedTangentStart(
            senderTangent,
            tangentLength: 500d,
            bodyTangentExtent: 100d,
            margin: 4d);

        Assert.Equal(expectedStart, start);
        Assert.InRange(start, 4d, 396d);
        Assert.InRange(start + 100d, 104d, 496d);
    }

    [Theory]
    [InlineData(0d, 10d)]
    [InlineData(50d, 36d)]
    [InlineData(100d, 68d)]
    public void DecoratedBubbleKeepsItsOverflowInsideTheTrack(
        double senderTangent,
        double expectedBodyStart)
    {
        double bodyStart = MessageBubbleLayoutPolicy.ClampedBodyTangentStart(
            senderTangent,
            tangentLength: 100d,
            bodyTangentExtent: 28d,
            leadingOverflow: 6d,
            trailingOverflow: 0d,
            margin: 4d);

        Assert.Equal(expectedBodyStart, bodyStart);
        Assert.True(bodyStart - 6d >= 4d);
        Assert.True(bodyStart + 28d <= 96d);
    }

    [Theory]
    [InlineData(OverlayEdge.Bottom)]
    [InlineData(OverlayEdge.Top)]
    public void HorizontalBubbleTailKeepsItsTipOnTheSender(OverlayEdge edge)
    {
        var body = new RectD(4d, 100d, 100d, 30d);

        MessageBubbleTail tail = MessageBubbleLayoutPolicy.Tail(
            edge,
            body,
            senderWorldTangent: 0d,
            tailHeight: 8d,
            halfBase: 6d,
            baseInset: 10d);

        Assert.Equal(0d, tail.Tip.X);
        Assert.InRange(tail.BaseStart.X, body.MinX, body.MaxX);
        Assert.InRange(tail.BaseEnd.X, body.MinX, body.MaxX);
        Assert.True(edge == OverlayEdge.Bottom
            ? tail.Tip.Y > tail.BaseStart.Y
            : tail.Tip.Y < tail.BaseStart.Y);
    }

    [Theory]
    [InlineData(OverlayEdge.Left)]
    [InlineData(OverlayEdge.Right)]
    public void VerticalBubbleTailKeepsItsTipOnTheSender(OverlayEdge edge)
    {
        var body = new RectD(100d, 4d, 30d, 100d);

        MessageBubbleTail tail = MessageBubbleLayoutPolicy.Tail(
            edge,
            body,
            senderWorldTangent: 0d,
            tailHeight: 8d,
            halfBase: 6d,
            baseInset: 10d);

        Assert.Equal(0d, tail.Tip.Y);
        Assert.InRange(tail.BaseStart.Y, body.MinY, body.MaxY);
        Assert.InRange(tail.BaseEnd.Y, body.MinY, body.MaxY);
        Assert.True(edge == OverlayEdge.Left
            ? tail.Tip.X < tail.BaseStart.X
            : tail.Tip.X > tail.BaseStart.X);
    }

    [Theory]
    [InlineData(OverlayEdge.Bottom, 128d)]
    [InlineData(OverlayEdge.Top, 102d)]
    [InlineData(OverlayEdge.Left, 102d)]
    [InlineData(OverlayEdge.Right, 128d)]
    public void BubbleTailBaseOverlapsTheBodyForASeamlessJunction(
        OverlayEdge edge,
        double expectedNormalCoordinate)
    {
        var body = new RectD(100d, 100d, 30d, 30d);

        MessageBubbleTail tail = MessageBubbleLayoutPolicy.Tail(
            edge,
            body,
            senderWorldTangent: 115d,
            tailHeight: 8d,
            halfBase: 6d,
            baseInset: 10d,
            baseOverlap: 2d);

        double actualNormalCoordinate = edge is OverlayEdge.Top or OverlayEdge.Bottom
            ? tail.BaseStart.Y
            : tail.BaseStart.X;
        Assert.Equal(expectedNormalCoordinate, actualNormalCoordinate);
    }

    [Fact]
    public void TypingAnimationAdvancesEveryPointThreeFiveSeconds()
    {
        Assert.Equal(0, MessageBubbleLayoutPolicy.TypingFrameIndex(0, 30, 3));
        Assert.Equal(0, MessageBubbleLayoutPolicy.TypingFrameIndex(10, 30, 3));
        Assert.Equal(1, MessageBubbleLayoutPolicy.TypingFrameIndex(11, 30, 3));
        Assert.Equal(2, MessageBubbleLayoutPolicy.TypingFrameIndex(21, 30, 3));
        Assert.Equal(0, MessageBubbleLayoutPolicy.TypingFrameIndex(32, 30, 3));
    }

    [Theory]
    [InlineData(100, 100)]
    [InlineData(10, 50)]
    [InlineData(180, 166)]
    public void SideBubbleStackKeepsNewestBubbleNearestTheSenderAndStacksOlderBubbleAbove(
        int desiredNewestTop,
        int expectedNewestTop)
    {
        int[] heightsNewestFirst = [30, 40];

        int newestTop = MessageBubbleLayoutPolicy.ClampedVerticalStackNewestTop(
            desiredNewestTop,
            heightsNewestFirst,
            minimumTop: 4,
            maximumBottom: 196,
            spacing: 6);

        int olderTop = newestTop - 6 - heightsNewestFirst[1];
        Assert.Equal(expectedNewestTop, newestTop);
        Assert.True(olderTop < newestTop);
        Assert.True(olderTop >= 4);
        Assert.True(newestTop + heightsNewestFirst[0] <= 196);
    }
}
