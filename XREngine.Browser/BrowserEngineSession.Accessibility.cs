namespace XREngine.Browser;

internal sealed partial class BrowserEngineSession
{
    private readonly BrowserAccessibleControlBridge _accessibleControl = new();

    public int RefreshAccessibleControls()
        => _accessibleControl.Refresh(_runtimeWorld, _renderViewport, _localPlayer);
    public int AccessibleControlCount => _accessibleControl.Count;
    public int AccessibleControlGeneration(int index) => _accessibleControl.Generation(index);
    public int AccessibleControlVersion(int index) => _accessibleControl.Version(index);
    public int AccessibleControlRole(int index) => _accessibleControl.Role(index);
    public string AccessibleControlName(int index) => _accessibleControl.Name(index);
    public bool AccessibleControlReadOnly(int index) => _accessibleControl.ReadOnly(index);
    public bool AccessibleControlMultiline(int index) => _accessibleControl.Multiline(index);
    public int AccessibleControlChecked(int index) => _accessibleControl.Checked(index);
    public bool AccessibleControlFocused(int index) => _accessibleControl.Focused(index);
    public float AccessibleControlX(int index) => _accessibleControl.X(index);
    public float AccessibleControlY(int index) => _accessibleControl.Y(index);
    public float AccessibleControlWidth(int index) => _accessibleControl.Width(index);
    public float AccessibleControlHeight(int index) => _accessibleControl.Height(index);
    public bool FocusAccessibleControl(int generation) => _accessibleControl.Focus(generation);
    public bool ActivateAccessibleControl(int generation) => _accessibleControl.Activate(generation);
}
