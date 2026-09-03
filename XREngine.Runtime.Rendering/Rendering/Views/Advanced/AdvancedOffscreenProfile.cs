namespace XREngine.Rendering;

/// <summary>
/// Capability-based configuration for secondary, capture, and offscreen views.
/// Omits unrequested main-view post-processing, temporal accumulation, and expensive stages.
/// </summary>
public sealed record AdvancedOffscreenProfile
{
    public EAdvancedOffscreenViewKind ViewKind { get; init; }
    public bool EnablePostProcessing { get; init; }
    public bool EnableTemporalHistory { get; init; }
    public bool EnableBloomAndDoF { get; init; }
    public bool EnableLateTransparency { get; init; }

    public AdvancedOffscreenProfile(
        EAdvancedOffscreenViewKind viewKind,
        bool enablePostProcessing = false,
        bool enableTemporalHistory = false,
        bool enableBloomAndDoF = false,
        bool enableLateTransparency = true)
    {
        ViewKind = viewKind;
        EnablePostProcessing = enablePostProcessing;
        EnableTemporalHistory = enableTemporalHistory;
        EnableBloomAndDoF = enableBloomAndDoF;
        EnableLateTransparency = enableLateTransparency;
    }

    public static AdvancedOffscreenProfile ForThumbnail()
        => new(EAdvancedOffscreenViewKind.Thumbnail, enablePostProcessing: false, enableTemporalHistory: false, enableBloomAndDoF: false, enableLateTransparency: false);

    public static AdvancedOffscreenProfile ForReflectionProbe()
        => new(EAdvancedOffscreenViewKind.ReflectionProbe, enablePostProcessing: false, enableTemporalHistory: false, enableBloomAndDoF: false, enableLateTransparency: true);

    public static AdvancedOffscreenProfile ForMirror()
        => new(EAdvancedOffscreenViewKind.Mirror, enablePostProcessing: true, enableTemporalHistory: false, enableBloomAndDoF: false, enableLateTransparency: true);

    public static AdvancedOffscreenProfile ForDepthOnly()
        => new(EAdvancedOffscreenViewKind.DepthOnly, enablePostProcessing: false, enableTemporalHistory: false, enableBloomAndDoF: false, enableLateTransparency: false);

    public static AdvancedOffscreenProfile ForVisibilityOnly()
        => new(EAdvancedOffscreenViewKind.VisibilityOnly, enablePostProcessing: false, enableTemporalHistory: false, enableBloomAndDoF: false, enableLateTransparency: false);
}
