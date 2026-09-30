namespace XREngine.Rendering;

/// <summary>Identifies why a desktop window exists without exposing its native handle.</summary>
public enum RuntimeWindowPurpose
{
    Presentation,
    EditorViewport,
    SecondaryGpuContext,
}
