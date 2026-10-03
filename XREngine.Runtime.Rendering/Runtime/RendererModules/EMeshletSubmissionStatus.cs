namespace XREngine.Rendering;

/// <summary>Outcome of a renderer-owned compute meshlet request; none permits indexed fallback.</summary>
public enum EMeshletSubmissionStatus
{
    Ready,
    Pending,
    Rejected,
}
