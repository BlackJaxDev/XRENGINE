using XREngine.Rendering.UI.State;

namespace XREngine.Rendering.UI;

/// <summary>Split geometry from the layout result that produced a pointer hit.</summary>
public readonly record struct UISplitterPointerMetrics(
    UIElementIdentity TargetIdentity,
    long ConfigurationRevision,
    bool Vertical,
    int FixedSide,
    float Extent,
    float InitialSize);
