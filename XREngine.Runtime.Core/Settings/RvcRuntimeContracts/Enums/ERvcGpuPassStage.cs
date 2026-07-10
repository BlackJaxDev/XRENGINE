namespace XREngine;

/// <summary>
/// GPU pass stages used by the engine.
/// </summary>
[Flags]
public enum ERvcGpuPassStage
{
    /// <summary>
    /// No GPU pass stage.
    /// </summary>
    None = 0,
    /// <summary>
    /// Visibility targets stage.
    /// </summary>
    VisibilityTargets = 1 << 0,
    /// <summary>
    /// OpenXR visibility mask stencil stage.
    /// </summary>
    OpenXrVisibilityMaskStencil = 1 << 1,
    /// <summary>
    /// Attribute reconstruction stage.
    /// </summary>
    AttributeReconstruction = 1 << 2,
    /// <summary>
    /// HZB rejection stage.
    /// </summary>
    HzbRejection = 1 << 3,
    /// <summary>
    /// Pixel to shadelet map stage.
    /// </summary>
    PixelToShadeletMap = 1 << 4,
    /// <summary>
    /// Material shadelet shading stage.
    /// </summary>
    MaterialShadeletShading = 1 << 5,
    /// <summary>
    /// Foveated shading rate stage.
    /// </summary>
    FoveatedShadingRate = 1 << 6,
    /// <summary>
    /// Head space light clusters stage.
    /// </summary>
    HeadSpaceLightClusters = 1 << 7,
    /// <summary>
    /// Shared lighting stage.
    /// </summary>
    SharedLighting = 1 << 8,
    /// <summary>
    /// Reuse validation stage.
    /// </summary>
    ReuseValidation = 1 << 9,
    /// <summary>
    /// Temporal cache stage.
    /// </summary>
    TemporalCache = 1 << 10,
    /// <summary>
    /// Foveated resolve stage.
    /// </summary>
    FoveatedResolve = 1 << 11,
    /// <summary>
    /// Transparency forward plus stage.
    /// </summary>
    TransparencyForwardPlus = 1 << 12,
    /// <summary>
    /// Diagnostic overlay stage.
    /// </summary>
    DiagnosticOverlay = 1 << 13,
}
