namespace XREngine.Rendering;

/// <summary>
/// Selects boolean or exact occlusion-query semantics.
/// </summary>
public enum EOcclusionResultMode
{
    AnySamplesPassed,
    AnySamplesPassedConservative,
    ExactSamplesPassed,
}
