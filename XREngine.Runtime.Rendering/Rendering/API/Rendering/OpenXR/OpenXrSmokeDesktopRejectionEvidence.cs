namespace XREngine.Rendering.API.Rendering.OpenXR;

/// <summary>
/// Machine-readable evidence from the controlled desktop-frame rejection used by
/// the Vulkan 5.2.4b validator. The rejected frame is outside the retained SPS
/// cohort; the values are sampled from the owning desktop pipeline on the render
/// thread immediately before the rejection policy is exercised.
/// </summary>
public sealed class OpenXrSmokeDesktopRejectionEvidence
{
    public bool Injected { get; set; }
    public bool Observed { get; set; }
    public string Policy { get; set; } = string.Empty;
    public bool SkippedPresent { get; set; }
    public bool PresentedLastCompletedImage { get; set; }
    public bool PresentAccepted { get; set; }
    public bool ClearedTargetPublished { get; set; }
    public string PipelineName { get; set; } = string.Empty;
    public int PipelineInstanceId { get; set; }
    public ulong OutputId { get; set; }
    public ulong RenderFrameId { get; set; }
    public ulong ManifestFrameId { get; set; }
    public double Exposure { get; set; }
    public double ExposureHistory { get; set; }
    public bool ExposureFinite { get; set; }
    public bool ExposureHistoryFinite { get; set; }
    public bool ExposureNonZeroRequired { get; set; }
    public bool ExposureHistoryNonZeroRequired { get; set; }
    public bool ExposureOwnerMatchesDesktop { get; set; }
    public string Diagnostic { get; set; } = string.Empty;
}
