namespace XREngine.Rendering.GI.Contracts;

/// <summary>
/// Stable host phase at which a GI module may contribute work.
/// </summary>
public enum EGlobalIlluminationExecutionAnchor
{
    ScenePreparation,
    FieldUpdate,
    SurfaceResolve,
    MaterialSampling,
    DebugPresentation,
}
