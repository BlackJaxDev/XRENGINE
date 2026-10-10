namespace XREngine.Rendering;

/// <summary>Outcome of a renderer-owned authored indexed request; none permits another submission path.</summary>
public enum EAuthoredIndexedSubmissionStatus
{
    Ready,
    Pending,
    Rejected,
}
