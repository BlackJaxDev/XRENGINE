namespace XREngine.Rendering;

/// <summary>
/// Typed aggregate of one boolean or exact occlusion query.
/// </summary>
public readonly record struct OcclusionQueryResult(
    bool AnySamplesPassed,
    ulong SamplesPassed,
    uint ViewSlotCount);
