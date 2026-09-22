using Sidey.Core.Domain;
using Sidey.Core.Overlay;

namespace Sidey.Core.Tests;

public sealed class CharacterFeedbackTests
{
    [Fact]
    public void HitsAtWindowBoundaryTriggerAndTargetsRemainIndependent()
    {
        double now = 0;
        var state = new CharacterStunState(() => now);
        Guid target = Guid.NewGuid(), other = Guid.NewGuid();
        for (int i = 0; i < 9; i++)
            Assert.False(state.RecordHit(target));
        now = 10;
        Assert.True(state.RecordHit(target));
        Assert.True(state.IsStunned(target));
        Assert.False(state.IsStunned(other));
    }

    [Fact]
    public void ExpiredHitsDoNotCountAndRecoveryHasNoProtectionOrCarryover()
    {
        double now = 0;
        var state = new CharacterStunState(() => now);
        var target = Guid.NewGuid();
        for (int i = 0; i < 9; i++)
            state.RecordHit(target);
        now = 10.001;
        Assert.False(state.RecordHit(target));
        for (int i = 0; i < 9; i++)
            state.RecordHit(target);
        Assert.True(state.IsStunned(target));
        now = 15;
        for (int i = 0; i < 20; i++)
            Assert.False(state.RecordHit(target));
        now = 16.001;
        Assert.False(state.IsStunned(target));
        for (int i = 0; i < 9; i++)
            Assert.False(state.RecordHit(target));
        Assert.True(state.RecordHit(target));
        state.Remove(target);
        Assert.False(state.IsStunned(target));
        for (int i = 0; i < 10; i++)
            state.RecordHit(target);
        state.Reset();
        Assert.False(state.IsStunned(target));
    }

    [Fact]
    public void AudioDropsLateMutedAndSaturatedRequestsWithoutQueueing()
    {
        var gate = new ImpactSoundAdmission();
        Assert.True(gate.Accept(1, 1, 0, true));
        Assert.False(gate.Accept(1.079, 1.079, 1, true));
        Assert.True(gate.Accept(1.081, 1.081, 1, true));
        Assert.False(gate.Accept(2, 2, 4, true));
        Assert.False(gate.Accept(2, 1.499, 0, true));
        Assert.False(gate.Accept(2, 2, 0, false));
        Assert.False(gate.Accept(double.NaN, 2, 0, true));
        gate.Reset();
        Assert.True(gate.Accept(2, 2, 0, true));
    }

    [Fact]
    public void StaticStunPixelsKeepSameLayoutAndAllPixelsStayOnCharacterCanvas()
    {
        StunPixel[] still = [.. CharacterStunPixels.Create(0, false)];
        Assert.Equal(still, CharacterStunPixels.Create(25, false));
        Assert.True(still.Count(p => p.IsStar && !p.IsOutline) >= 42);
        Assert.True(still.Count(p => p.IsOutline) >= 45);
        foreach (IReadOnlyList<StunPixel>? frame in Enumerable.Range(0, 36).Select(i => CharacterStunPixels.Create(i / 30d, true)))
        {
            Assert.True(frame.Count <= CharacterStunPixels.MaximumPixelCount);
            foreach (StunPixel p in frame)
            {
                Assert.InRange(p.X, 0, 23);
                Assert.InRange(p.Y, 0, 23);
            }
        }
        Assert.NotEqual(still, [.. CharacterStunPixels.Create(0.3, true)]);
    }

    [Fact]
    public void SoundVolumePreservesSilenceAndClampsOutOfRangePreferences()
    {
        Assert.Equal(100, AppPreferences.Default.CharacterSoundEffectsVolume);
        Assert.Equal(0, (AppPreferences.Default with { CharacterSoundEffectsVolume = 0 }).Normalize().CharacterSoundEffectsVolume);
        Assert.False((AppPreferences.Default with { CharacterSoundEffectsVolume = 0 }).Normalize().CharacterSoundEffectsEnabled);
        Assert.Equal(0, (AppPreferences.Default with { CharacterSoundEffectsVolume = -1 }).Normalize().CharacterSoundEffectsVolume);
        Assert.Equal(100, (AppPreferences.Default with { CharacterSoundEffectsVolume = 999 }).Normalize().CharacterSoundEffectsVolume);
    }

    [Fact]
    public void SoundMappingUsesTheEquippedObjectOrTheCommonBall()
    {
        Assert.Equal(19, ImpactSoundCatalog.Ids.Distinct().Count());
        Assert.Equal("patch_soft_ball", ImpactSoundCatalog.Resolve("pixel_monkey", null));
        Assert.Equal("banana", ImpactSoundCatalog.Resolve("pixel_hamster", "throwable_banana"));
        Assert.Equal("clam", ImpactSoundCatalog.Resolve("pixel_tree", "throwable_clam"));
        Assert.Equal("throwable_toy_cannon", ImpactSoundCatalog.Resolve("pixel_monkey", "throwable_toy_cannon"));
        Assert.Equal("patch_soft_ball", ImpactSoundCatalog.Resolve("unknown", "invalid"));
        Assert.True(AppPreferences.Default.CharacterSoundEffectsEnabled);
        Assert.False((AppPreferences.Default with { CharacterSoundEffectsEnabled = false }).Normalize().CharacterSoundEffectsEnabled);
    }
}
