namespace XREngine.Rendering;

/// <summary>
/// Status and readiness criteria for the Advanced Render Pipeline production cutover.
/// Certifies that ARP 01 through ARP 09 are complete and classic G-Buffer stages are eliminated.
/// </summary>
public static class AdvancedProductionCutoverContract
{
    public const string ProductionPipelineName = "AdvancedRenderPipeline";
    public const string ProductionOpenXrPipelineName = "RvcRenderPipeline";

    /// <summary>
    /// Evaluates whether all required architectural milestones have passed for full cutover.
    /// </summary>
    public static bool EvaluateCutoverReadiness(
        bool hasClassification,
        bool hasNativeShading,
        bool hasTransparency,
        bool hasStereoMultiview,
        bool isClassicGBufferEliminated,
        bool isOpenXrEyeOwnershipPreserved,
        out string? blockerReason)
    {
        if (!hasClassification)
        {
            blockerReason = "ARP 06 GPU Material Classification is not active or verified.";
            return false;
        }

        if (!hasNativeShading)
        {
            blockerReason = "ARP 07 Native Opaque Shading & Clustered Lighting is not active or verified.";
            return false;
        }

        if (!hasTransparency)
        {
            blockerReason = "ARP 08 Transparency, Special Passes, & Post Chain is not active or verified.";
            return false;
        }

        if (!hasStereoMultiview)
        {
            blockerReason = "ARP 09 Stereo, Multiview, & Editor View Integration is not active or verified.";
            return false;
        }

        if (!isClassicGBufferEliminated)
        {
            blockerReason = "Classic multi-channel G-Buffer passes must be completely eliminated from the production path.";
            return false;
        }

        if (!isOpenXrEyeOwnershipPreserved)
        {
            blockerReason = "Production OpenXR eye ownership must remain strictly preserved in RvcRenderPipeline.";
            return false;
        }

        blockerReason = null;
        return true;
    }
}
