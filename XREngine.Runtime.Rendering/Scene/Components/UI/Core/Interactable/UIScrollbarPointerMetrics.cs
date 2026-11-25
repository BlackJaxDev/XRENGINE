using XREngine.Rendering.UI.State;

namespace XREngine.Rendering.UI;

/// <summary>Scroll thumb geometry frozen with the presented pointer hit.</summary>
public readonly record struct UIScrollbarPointerMetrics(
    UIElementIdentity TargetIdentity,
    long ConfigurationRevision,
    bool Horizontal,
    float TrackLength,
    float ThumbStart,
    float ThumbLength,
    float MaximumOffset);
