using XREngine.Data.Rendering;
using XREngine.Rendering;

namespace XREngine.Components.Lights;

/// <summary>One GPU completion-gated texture slot in a VR spectator output pair.</summary>
public sealed class VrSpectatorCaptureComponent : AdvancedOffscreenTextureCaptureComponent
{
    private bool _captureCullWithFrustum;

    /// <summary>Controls collection culling for this capture's private viewport.</summary>
    public bool CaptureCullWithFrustum
    {
        get => CaptureViewport?.CullWithFrustum ?? _captureCullWithFrustum;
        set
        {
            SetField(ref _captureCullWithFrustum, value);
            if (CaptureViewport is { } viewport)
                viewport.CullWithFrustum = value;
        }
    }

    // The capture needs one completion-gated HDR target. The Default offscreen path
    // writes that target without depending on Advanced's transient bindless scene publication.
    protected override bool UsesAdvancedPipeline => false;

    protected override void ConfigureCaptureViewport(XRViewport viewport)
    {
        // Render straight into the completion-gated HDR target and omit editor/debug
        // overlay passes from the clean spectator feed.
        viewport.ApplyCapturePolicy(RenderCapturePolicy.GenericSceneCapture with { OutputHDR = true });
        // An imported skinned avatar may not have published its live culling bounds
        // until it is drawn once in this view. Re-arm the warm-up on target rebuild.
        CaptureCullWithFrustum = false;
    }

    protected override RenderPipelineOffscreenIntent OffscreenIntent
        => new(ERenderPipelineOffscreenViewIntent.SceneCapture,
            ERenderPipelineOffscreenOutput.HdrColor,
            EnableLateTransparency: true,
            EnablePostProcessing: false,
            EnableTemporalHistory: false,
            EnableBloomAndDoF: false);

    protected override EFrameOutputKind CaptureOutputKind => EFrameOutputKind.SceneCapture;

}
