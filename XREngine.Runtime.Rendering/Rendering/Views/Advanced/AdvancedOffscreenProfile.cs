using XREngine.Data.Rendering;

namespace XREngine.Rendering;

/// <summary>
/// Capability-based configuration for secondary, capture, and offscreen views.
/// Omits unrequested main-view post-processing, temporal accumulation, and expensive stages.
/// </summary>
public sealed record AdvancedOffscreenProfile
{
    public EAdvancedOffscreenViewKind ViewKind { get; }
    public ERenderPipelineOffscreenOutput Output { get; }
    public bool EnablePostProcessing { get; }
    public bool EnableTemporalHistory { get; }
    public bool EnableBloomAndDoF { get; }
    public bool EnableLateTransparency { get; }

    /// <summary>
    /// True when this profile exports an authoritative visibility producer
    /// attachment without material reconstruction, classification, or shading.
    /// </summary>
    internal bool IsMinimalVisibilityOutput
        => Output is ERenderPipelineOffscreenOutput.Depth or
            ERenderPipelineOffscreenOutput.Visibility;

    public AdvancedOffscreenProfile(
        EAdvancedOffscreenViewKind viewKind,
        ERenderPipelineOffscreenOutput output = ERenderPipelineOffscreenOutput.HdrColor,
        bool enablePostProcessing = false,
        bool enableTemporalHistory = false,
        bool enableBloomAndDoF = false,
        bool enableLateTransparency = true)
    {
        if (output is ERenderPipelineOffscreenOutput.Depth or
            ERenderPipelineOffscreenOutput.Visibility)
        {
            if (enablePostProcessing || enableTemporalHistory ||
                enableBloomAndDoF || enableLateTransparency)
            {
                throw new ArgumentException(
                    $"Advanced offscreen {output} output supports only the " +
                    "visibility/depth producer family.",
                    nameof(enableLateTransparency));
            }
        }

        ViewKind = viewKind;
        Output = output;
        EnablePostProcessing = enablePostProcessing;
        EnableTemporalHistory = enableTemporalHistory;
        EnableBloomAndDoF = enableBloomAndDoF;
        EnableLateTransparency = enableLateTransparency;
    }

    public static AdvancedOffscreenProfile ForThumbnail()
        => new(EAdvancedOffscreenViewKind.Thumbnail, ERenderPipelineOffscreenOutput.HdrColor, enablePostProcessing: false, enableTemporalHistory: false, enableBloomAndDoF: false, enableLateTransparency: false);

    public static AdvancedOffscreenProfile ForReflectionProbe()
        => new(EAdvancedOffscreenViewKind.ReflectionProbe, ERenderPipelineOffscreenOutput.HdrColor, enablePostProcessing: false, enableTemporalHistory: false, enableBloomAndDoF: false, enableLateTransparency: true);

    public static AdvancedOffscreenProfile ForMirror()
        => new(EAdvancedOffscreenViewKind.Mirror, ERenderPipelineOffscreenOutput.HdrColor, enablePostProcessing: false, enableTemporalHistory: false, enableBloomAndDoF: false, enableLateTransparency: true);

    public static AdvancedOffscreenProfile ForDepthOnly()
        => new(EAdvancedOffscreenViewKind.DepthOnly, ERenderPipelineOffscreenOutput.Depth, enablePostProcessing: false, enableTemporalHistory: false, enableBloomAndDoF: false, enableLateTransparency: false);

    public static AdvancedOffscreenProfile ForVisibilityOnly()
        => new(EAdvancedOffscreenViewKind.VisibilityOnly, ERenderPipelineOffscreenOutput.Visibility, enablePostProcessing: false, enableTemporalHistory: false, enableBloomAndDoF: false, enableLateTransparency: false);

    internal static AdvancedOffscreenProfile FromIntent(in RenderPipelineOffscreenIntent intent)
        => new(
            intent.ViewIntent switch
            {
                ERenderPipelineOffscreenViewIntent.SceneCapture => EAdvancedOffscreenViewKind.SceneCapture,
                ERenderPipelineOffscreenViewIntent.Mirror => EAdvancedOffscreenViewKind.Mirror,
                ERenderPipelineOffscreenViewIntent.Portal => EAdvancedOffscreenViewKind.Portal,
                ERenderPipelineOffscreenViewIntent.ReflectionProbe => EAdvancedOffscreenViewKind.ReflectionProbe,
                ERenderPipelineOffscreenViewIntent.Thumbnail => EAdvancedOffscreenViewKind.Thumbnail,
                ERenderPipelineOffscreenViewIntent.DepthOnly => EAdvancedOffscreenViewKind.DepthOnly,
                ERenderPipelineOffscreenViewIntent.VisibilityOnly => EAdvancedOffscreenViewKind.VisibilityOnly,
                _ => throw new ArgumentOutOfRangeException(nameof(intent), intent.ViewIntent, "Unknown advanced offscreen view intent."),
            },
            intent.Output,
            intent.EnablePostProcessing,
            intent.EnableTemporalHistory,
            intent.EnableBloomAndDoF,
            intent.EnableLateTransparency);
}
