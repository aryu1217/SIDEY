using Sidey.Presentation.Services;

namespace Sidey.Presentation.Tests;

public sealed class CharacterClickComposerStateTests
{
    [Theory]
    [InlineData(false, ComposerVisibilityAction.Show, true, ComposerVisibilityAction.Hide)]
    [InlineData(true, ComposerVisibilityAction.Hide, false, ComposerVisibilityAction.Show)]
    public void DoubleClickRestoresVisibilityFromBeforeTheImmediateSingleClick(
        bool initiallyVisible,
        ComposerVisibilityAction singleAction,
        bool visibilityAfterSingle,
        ComposerVisibilityAction doubleAction)
    {
        var state = new CharacterClickComposerState();

        Assert.Equal(singleAction, state.HandleClick(1, initiallyVisible));
        state.CompleteSingleClick(visibilityAfterSingle);

        Assert.Equal(doubleAction, state.HandleClick(2, visibilityAfterSingle));
        Assert.Equal(ComposerVisibilityAction.None, state.HandleClick(2, initiallyVisible));
    }

    [Fact]
    public void FailedSingleClickAndExplicitResetLeaveDoubleClickWithNothingToRestore()
    {
        var state = new CharacterClickComposerState();

        Assert.Equal(ComposerVisibilityAction.Show, state.HandleClick(1, isVisible: false));
        state.CompleteSingleClick(isVisible: false);
        Assert.Equal(ComposerVisibilityAction.None, state.HandleClick(2, isVisible: false));

        Assert.Equal(ComposerVisibilityAction.Hide, state.HandleClick(1, isVisible: true));
        state.Reset();
        Assert.Equal(ComposerVisibilityAction.None, state.HandleClick(2, isVisible: false));
    }
}
