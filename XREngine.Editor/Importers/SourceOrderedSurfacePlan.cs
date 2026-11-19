using XREngine.Rendering;

namespace XREngine.Scene.Importers;

/// <summary>Records the verified late-pass contract of the original source material.</summary>
internal readonly record struct SourceOrderedSurfacePlan(
    EAdvancedSourceSurfaceContract Contract,
    string? UnsupportedReason)
{
    public bool HasSceneColor => Contract == EAdvancedSourceSurfaceContract.SceneColorToonV1;
}
