using XREngine.Rendering;

namespace XREngine.Scene.Importers;

/// <summary>
/// Records the late-pass contract that the importer selected for the original source material.
/// <see cref="DegradedFeatures"/> lists each source option that the contract cannot reproduce.
/// The importer drops those options and reports each one as a conversion warning.
/// </summary>
internal readonly record struct SourceOrderedSurfacePlan(
    EAdvancedSourceSurfaceContract Contract,
    IReadOnlyList<string>? DegradedFeatures)
{
    public bool HasSceneColor => Contract == EAdvancedSourceSurfaceContract.SceneColorToonV1;
}
