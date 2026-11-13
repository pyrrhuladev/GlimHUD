namespace GlimHUD.Services;

/// <summary>Separates user hide/show actions from automatic foreground visibility.</summary>
internal sealed class OverlayVisibilityPolicy
{
    private bool _manuallyHidden;
    private bool _manualShow;
    private bool? _lastGameActive;
    private bool? _lastAutomaticMode;

    public bool Evaluate(bool automaticMode, bool gameActive, bool ownFocus, bool settingsOpen)
    {
        if (_lastAutomaticMode is { } previousMode && previousMode != automaticMode)
        {
            _manuallyHidden = false;
            _manualShow = false;
        }
        _lastAutomaticMode = automaticMode;
        if (!ownFocus)
        {
            if (_lastGameActive is { } previousGame && previousGame != gameActive) _manualShow = false;
            _lastGameActive = gameActive;
        }
        return !_manuallyHidden && (!automaticMode || _manualShow || settingsOpen
            || (ownFocus ? _lastGameActive == true : gameActive));
    }

    public void Toggle(bool currentlyVisible)
    {
        _manuallyHidden = currentlyVisible;
        _manualShow = !currentlyVisible;
    }

    public void Hide()
    {
        _manuallyHidden = true;
        _manualShow = false;
    }

    public void Show()
    {
        _manuallyHidden = false;
        _manualShow = true;
    }
}
