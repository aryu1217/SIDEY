using Sidey.Core.Domain;
using Sidey.Core.Overlay;

namespace Sidey.Core.Tests;

public sealed class CharacterDragPhysicsTests
{
    [Fact]
    public void ScreenCoordinatesRoundTripOnEveryEdgeOfNegativeOriginMonitor()
    {
        var monitor = new RectD(-1920, 0, 1920, 1080);
        foreach (OverlayEdge edge in Enum.GetValues<OverlayEdge>())
        {
            RectD activity = edge switch
            {
                OverlayEdge.Bottom => new RectD(-1500, 840, 1000, 240),
                OverlayEdge.Top => new RectD(-1500, 0, 1000, 240),
                OverlayEdge.Left => new RectD(-1920, 200, 240, 600),
                _ => new RectD(-240, 200, 240, 600),
            };
            PointD pointer = edge is OverlayEdge.Bottom or OverlayEdge.Top
                ? new PointD(-1000, 100)
                : new PointD(-120, 500);
            if (edge == OverlayEdge.Bottom)
                pointer = new PointD(-1000, 900);
            if (edge == OverlayEdge.Left)
                pointer = new PointD(-1800, 500);
            CharacterDragPoint local = CharacterDragCoordinates.FromScreen(
                pointer, activity, monitor, edge, edgeInset: 8);
            PointD recovered = CharacterDragCoordinates.ToScreen(local, activity, edge, edgeInset: 8);

            Assert.Equal(pointer, recovered);
            Assert.InRange(CharacterDragCoordinates.MaximumDepth(activity, monitor, edge, 8), 1, 1920);
        }
    }

    [Fact]
    public void MovingPinKeepsCharacterInsideTrackAndRopeLimit()
    {
        var drag = new CharacterDragPhysics(new CharacterDragPoint(250, 0));
        for (int index = 0; index < 240; index++)
        {
            drag.MovePin(new CharacterDragPoint(index % 2 == 0 ? -200 : 1200, 800));
            drag.Step(1d / 30d, 26, 974, 900, reduceMotion: false);
            CharacterDragPoint pin = Assert.IsType<CharacterDragPoint>(drag.Pin(26, 974, 900));
            double distance = Math.Sqrt(
                Math.Pow(drag.Position.Tangent - pin.Tangent, 2)
                + Math.Pow(drag.Position.Depth - pin.Depth, 2));
            Assert.InRange(distance, 0, CharacterDragPhysics.MaximumRopeLength + 0.001d);
            Assert.InRange(drag.Position.Tangent, 26, 974);
            Assert.InRange(drag.Position.Depth, 0, 840);
            Assert.True(double.IsFinite(drag.Velocity.Tangent));
            Assert.True(double.IsFinite(drag.Velocity.Depth));
        }
    }

    [Fact]
    public void ReleaseIncludesLastPointerMovementAndEventuallyLands()
    {
        var drag = new CharacterDragPhysics(new CharacterDragPoint(250, 0));
        drag.MovePin(new CharacterDragPoint(280, 180));
        for (int index = 0; index < 20; index++)
        {
            drag.Step(1d / 120d, 26, 974, 900, reduceMotion: false);
        }
        drag.MovePin(new CharacterDragPoint(380, 180));
        drag.Release(26, 974, 900, reduceMotion: false);

        Assert.Null(drag.Pointer);
        Assert.True(drag.Velocity.Tangent > 0);
        for (int index = 0; index < 600 && !drag.Landed; index++)
        {
            drag.Step(1d / 120d, 26, 974, 900, reduceMotion: false);
        }
        Assert.True(drag.Landed);
        Assert.Equal(0d, drag.Position.Depth);
        Assert.InRange(drag.Position.Tangent, 26, 974);
    }

    [Fact]
    public void ReducedMotionSuppressesSwingAndLandingBounce()
    {
        var drag = new CharacterDragPhysics(new CharacterDragPoint(250, 0));
        drag.MovePin(new CharacterDragPoint(400, 400));
        drag.Step(1d / 30d, 26, 974, 900, reduceMotion: true);

        Assert.Equal(400d, drag.Position.Tangent);
        Assert.Equal(0d, drag.Rotation);
        drag.Release(26, 974, 900, reduceMotion: true);
        for (int index = 0; index < 600 && !drag.Landed; index++)
        {
            drag.Step(1d / 120d, 26, 974, 900, reduceMotion: true);
        }
        Assert.True(drag.Landed);
        Assert.Equal(0d, drag.Position.Depth);
        Assert.Equal(0d, drag.Velocity.Depth);
    }
}
