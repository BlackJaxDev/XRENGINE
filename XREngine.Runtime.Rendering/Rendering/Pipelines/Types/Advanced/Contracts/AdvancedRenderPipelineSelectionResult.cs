using XREngine.Data.Rendering;

namespace XREngine.Rendering;

/// <summary>
/// Resolves a requested selection policy into an effective standard renderer architecture.
/// </summary>
public readonly record struct AdvancedRenderPipelineSelectionResult(
    EAdvancedRenderPipelineMode RequestedMode,
    ERenderPipelineKind EffectiveKind,
    bool CapabilityEvaluated,
    AdvancedRenderPipelineCapabilityResult CapabilityResult)
{
    public bool SelectsAdvanced
        => EffectiveKind == ERenderPipelineKind.Advanced;

    public bool RequiresFailure
        => RequestedMode == EAdvancedRenderPipelineMode.Required &&
           EffectiveKind == ERenderPipelineKind.None;

    public string Diagnostic
        => RequestedMode switch
        {
            EAdvancedRenderPipelineMode.Disabled =>
                "Advanced rendering is disabled; the legacy default pipeline remains active.",
            EAdvancedRenderPipelineMode.Diagnostic =>
                $"Advanced rendering is diagnostic-only; the legacy default pipeline remains active. {CapabilityResult.Diagnostic}",
            _ when SelectsAdvanced =>
                $"Advanced rendering is selected. {CapabilityResult.Diagnostic}",
            _ when RequiresFailure =>
                $"Advanced rendering is required but unavailable. {CapabilityResult.Diagnostic}",
            _ =>
                $"Advanced rendering is unavailable; the legacy default pipeline remains active. {CapabilityResult.Diagnostic}",
        };
}
