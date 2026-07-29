using XREngine.Data.Rendering;

namespace XREngine.Rendering;

/// <summary>
/// Applies disabled, opportunistic, required, and diagnostic selection policy to a
/// capability snapshot without creating a pipeline.
/// </summary>
public static class AdvancedRenderPipelineSelectionResolver
{
    public static AdvancedRenderPipelineSelectionResult Resolve(
        EAdvancedRenderPipelineMode mode,
        in AdvancedRenderPipelineCapabilities capabilities,
        bool stereo)
    {
        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown advanced pipeline selection mode.");

        if (mode == EAdvancedRenderPipelineMode.Disabled)
        {
            return new(
                mode,
                ERenderPipelineKind.LegacyDefault,
                CapabilityEvaluated: false,
                default);
        }

        AdvancedRenderPipelineCapabilityResult capabilityResult =
            AdvancedRenderPipelineCapabilityResolver.Resolve(capabilities, stereo);

        ERenderPipelineKind effectiveKind = mode switch
        {
            EAdvancedRenderPipelineMode.Diagnostic => ERenderPipelineKind.LegacyDefault,
            EAdvancedRenderPipelineMode.Available when !capabilityResult.IsSupported =>
                ERenderPipelineKind.LegacyDefault,
            EAdvancedRenderPipelineMode.Required when !capabilityResult.IsSupported =>
                ERenderPipelineKind.None,
            _ => ERenderPipelineKind.Advanced,
        };

        return new(
            mode,
            effectiveKind,
            CapabilityEvaluated: true,
            capabilityResult);
    }
}
