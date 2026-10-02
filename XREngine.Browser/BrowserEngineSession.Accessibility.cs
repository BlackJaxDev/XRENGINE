namespace XREngine.Browser;

internal sealed partial class BrowserEngineSession
{
    private readonly BrowserAccessibleControlBridge _accessibleControl = new();

    public int RefreshAccessibleControl()
        => _accessibleControl.Refresh(_localPlayer?.FocusedInteractable, _renderViewport);
    public string AccessibleControlLabel => _accessibleControl.Label;
    public int AccessibleControlLabelVersion => _accessibleControl.LabelVersion;
    public float AccessibleControlX => _accessibleControl.X;
    public float AccessibleControlY => _accessibleControl.Y;
    public float AccessibleControlWidth => _accessibleControl.Width;
    public float AccessibleControlHeight => _accessibleControl.Height;
    public bool ActivateAccessibleControl(int generation)
        => _accessibleControl.Activate(generation, _localPlayer?.FocusedInteractable);
}
