namespace XREngine.Rendering;

/// <summary>
/// Stable logical stages in the advanced desktop frame contract.
/// </summary>
public enum EAdvancedRenderStage
{
    FrameBegin = 0,
    Deformation,
    VisibilityPreparation,
    VisibilityRaster,
    DepthPyramidAndLateVisibility,
    WorkClassification,
    AttributeReconstruction,
    NativeOpaqueShading,
    LatePasses,
    TemporalAndPostProcessing,
    Output,
    UserInterface,
}
