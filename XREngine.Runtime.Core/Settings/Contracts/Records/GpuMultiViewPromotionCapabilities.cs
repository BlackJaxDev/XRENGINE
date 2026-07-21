namespace XREngine;

/// <summary>Runtime capabilities required by optional stereo/quad GPU promotion lanes.</summary>
public readonly record struct GpuMultiViewPromotionCapabilities(
    bool ExactViewMasks,
    bool StableLogicalViewMapping,
    bool PerFamilyCullingOwner,
    bool LayeredHiZ,
    bool SupportsStereo,
    bool SupportsQuad);
