namespace XREngine.Rendering;

/// <summary>
/// Stable shared and diagnostic shader asset identities for document 05.
/// </summary>
public static class AdvancedReconstructionShaderLibrary
{
    public const string Root = "Advanced/Reconstruction";
    public const string SurfaceInclude = Root + "/AdvancedSurface.glslinc";
    public const string InterfaceInclude =
        Root + "/ReconstructionInterface.glslinc";
    public const string ReconstructionInclude =
        Root + "/ReconstructSurface.glslinc";
    public const string ReferenceCompute =
        Root + "/ReconstructionReference.comp";
    public const string DebugCompute =
        Root + "/ReconstructionDebug.comp";
    public const string ValidateCompute =
        Root + "/ValidateReconstruction.comp";
    public const string ResetCountersCompute =
        Root + "/ResetReconstructionCounters.comp";
}
