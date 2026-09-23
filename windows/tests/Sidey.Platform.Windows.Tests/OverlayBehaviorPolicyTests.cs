using Sidey.Core.Domain;
using Sidey.Overlay;

namespace Sidey.Platform.Windows.Tests;

public sealed class OverlayBehaviorPolicyTests
{
    [Fact]
    public void ExistingPulseDoesNotReplayAfterWorldIsRecreated()
    {
        CharacterPulseEvent pulse = Pulse();
        var guard = new CharacterPulseReplayGuard();

        guard.SeedExisting([pulse]);

        Assert.False(guard.TryAccept(pulse));
        Assert.True(guard.TryAccept(Pulse()));
    }

    [Fact]
    public void ExistingThrowDoesNotReplayAndGuardIsBounded()
    {
        CharacterThrowEvent existing = Throw();
        var guard = new CharacterThrowReplayGuard(capacity: 2);

        guard.SeedExisting([existing]);
        Assert.False(guard.TryAccept(existing));
        CharacterThrowEvent second = Throw();
        CharacterThrowEvent third = Throw();
        Assert.True(guard.TryAccept(second));
        Assert.True(guard.TryAccept(third));
        Assert.True(guard.TryAccept(existing));
    }

    [Fact]
    public void PlacementSessionEntropyChangesInitialPositions()
    {
        var memberId = Guid.Parse("5ec7a319-2dde-48df-af96-e2554fc0cf2a");
        long firstSeed = OverlayPlacementPolicy.CombineSeed(1234, 10);
        long secondSeed = OverlayPlacementPolicy.CombineSeed(1234, 11);

        Assert.NotEqual(
            OverlayPlacementPolicy.Fraction(memberId, firstSeed),
            OverlayPlacementPolicy.Fraction(memberId, secondSeed));
        Assert.NotEqual(
            OverlayPlacementPolicy.Fraction(memberId, firstSeed),
            OverlayPlacementPolicy.Fraction(
                memberId,
                firstSeed,
                OverlayPlacementPolicy.TargetSalt));
    }

    [Theory]
    [InlineData(OverlayEdge.Bottom)]
    [InlineData(OverlayEdge.Top)]
    [InlineData(OverlayEdge.Left)]
    [InlineData(OverlayEdge.Right)]
    public void TextVisualsStayUprightForEveryCharacterEdge(OverlayEdge edge)
    {
        PremultipliedVisual source = Visual(2, 3);
        PremultipliedVisual oriented = PixelVisualOrientation.Apply(source, edge);

        Assert.Same(source, oriented);
        Assert.Equal(2, oriented.Width);
        Assert.Equal(3, oriented.Height);
        Assert.Equal([1, 2, 3, 4, 5, 6], BlueValues(oriented));
    }

    [Theory]
    [InlineData(OverlayEdge.Top)]
    [InlineData(OverlayEdge.Left)]
    [InlineData(OverlayEdge.Right)]
    public void UprightBubblePreservesItsBodyBounds(OverlayEdge edge)
    {
        var source = new PremultipliedVisual(
            new byte[10 * 8 * 4],
            10,
            8,
            BubbleBodyBounds: new PixelVisualBodyBounds(2, 3, 6, 4));

        PremultipliedVisual oriented = PixelVisualOrientation.Apply(source, edge);

        Assert.Equal(
            new PixelVisualBodyBounds(2, 3, 6, 4),
            oriented.BubbleBodyBounds);
    }

    private static CharacterPulseEvent Pulse() => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        Guid.NewGuid());

    private static CharacterThrowEvent Throw() => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        Guid.NewGuid(),
        Guid.NewGuid(),
        "pixel_hamster");

    private static PremultipliedVisual Visual(int width, int height)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int pixel = 0; pixel < width * height; pixel++)
        {
            pixels[pixel * 4] = (byte)(pixel + 1);
            pixels[(pixel * 4) + 3] = 255;
        }
        return new PremultipliedVisual(pixels, width, height);
    }

    private static byte[] BlueValues(PremultipliedVisual visual) =>
        [.. Enumerable.Range(0, visual.Width * visual.Height).Select(index => visual.Pixels[index * 4])];
}
