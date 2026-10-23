namespace XREngine.Rendering;

public sealed partial class XRWindow
{
    /// <summary>Describes native accessibility support for this window.</summary>
    public string AccessibilitySupportStatus =>
        "Retained UI semantics are available to the engine. This window has no operating-system accessibility bridge.";
}
