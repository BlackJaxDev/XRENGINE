#if !XRENGINE_HAS_RIVESHARP
using XREngine.Core.Attributes;
using XREngine.Data.Core;
using XREngine.Rendering.UI;

namespace XREngine.Rendering.UI;

[XRTypeRedirect("XREngine.Scene.Components.UI.RiveUIComponent")]
[RequireComponents(typeof(UIMaterialComponent))]
public class RiveUIComponent : UIInteractableComponent
{
    private static bool _reportedUnavailable;
    private string? _artboardName;
    private string? _animationName;
    private string? _stateMachineName;

    public EventList<StateMachineInput> Inputs { get; } = [];

    public string? ArtboardName
    {
        get => _artboardName;
        set => SetField(ref _artboardName, value);
    }

    public string? AnimationName
    {
        get => _animationName;
        set => SetField(ref _animationName, value);
    }

    public string? StateMachineName
    {
        get => _stateMachineName;
        set => SetField(ref _stateMachineName, value);
    }

    public RiveUIComponent()
        => ReportUnavailable();

    public void SetSource(string newSourceName)
        => ReportUnavailable();

    public void SetBool(string name, bool value)
        => ReportUnavailable();

    public void SetNumber(string name, float value)
        => ReportUnavailable();

    public void FireTrigger(string name)
        => ReportUnavailable();

    private static void ReportUnavailable()
    {
        if (_reportedUnavailable)
            return;

        _reportedUnavailable = true;
        Debug.UIWarning("Rive UI is unavailable because RiveSharp.dll was not built. Run Tools\\Build-Submodules.bat after installing the Visual Studio C++ build tools to enable it.");
    }
}
#endif
