namespace Sidey.Presentation.Services;

public enum ComposerVisibilityAction
{
    None,
    Show,
    Hide,
}

public sealed class CharacterClickComposerState
{
    private bool? _visibilityBeforeClick;

    public ComposerVisibilityAction HandleClick(int clickCount, bool isVisible)
    {
        switch (clickCount)
        {
            case 1:
                _visibilityBeforeClick = isVisible;
                return isVisible ? ComposerVisibilityAction.Hide : ComposerVisibilityAction.Show;
            case 2:
                bool? previousVisibility = _visibilityBeforeClick;
                _visibilityBeforeClick = null;
                if (previousVisibility is null || previousVisibility.Value == isVisible)
                {
                    return ComposerVisibilityAction.None;
                }
                return previousVisibility.Value
                    ? ComposerVisibilityAction.Show
                    : ComposerVisibilityAction.Hide;
            default:
                _visibilityBeforeClick = null;
                return ComposerVisibilityAction.None;
        }
    }

    public void CompleteSingleClick(bool isVisible)
    {
        if (_visibilityBeforeClick == isVisible)
        {
            _visibilityBeforeClick = null;
        }
    }

    public void Reset() => _visibilityBeforeClick = null;
}
