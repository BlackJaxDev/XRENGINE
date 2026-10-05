using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Scene.Transforms;

namespace XREngine.Components.Lights;

/// <summary>One completion-gated monoscopic spectator slot using the existing offscreen pipeline.</summary>
public sealed class SpectatorTextureCaptureComponent : AdvancedOffscreenTextureCaptureComponent
{
    // The Default offscreen path supplies the clean HDR target without requiring
    // Advanced bindless scene publication during avatar initialization.
    protected override bool UsesAdvancedPipeline => false;

    protected override RenderPipelineOffscreenIntent OffscreenIntent => new(
        ERenderPipelineOffscreenViewIntent.SceneCapture, ERenderPipelineOffscreenOutput.HdrColor,
        EnableLateTransparency: true, EnablePostProcessing: false, EnableTemporalHistory: false,
        EnableBloomAndDoF: false);
    protected override EFrameOutputKind CaptureOutputKind => EFrameOutputKind.SceneCapture;

    protected override void ConfigureCaptureViewport(XRViewport viewport)
    {
        viewport.ApplyCapturePolicy(RenderCapturePolicy.GenericSceneCapture with { OutputHDR = true });
        // Imported skinned bounds may be established only by their first draw.
        // Re-enable culling once this slot has a completed capture.
        viewport.CullWithFrustum = false;
    }

    /// <summary>Ends the bounds warm-up after the slot has finished rendering.</summary>
    public void CompleteBoundsWarmup()
    {
        if (CaptureViewport is { } viewport)
            viewport.CullWithFrustum = true;
    }

    protected override RenderOutputRequest ConfigureOutputRequest(RenderOutputRequest request) => request with
    {
        OutputClass = ERenderOutputClass.BackgroundCapture,
        WorkClass = ERenderOutputWorkClass.Background,
        ReadinessPolicy = ERenderOutputReadinessPolicy.AllowDeferral,
        FallbackPolicy = ERenderOutputFallbackPolicy.AllowBudgetDeferral | ERenderOutputFallbackPolicy.AllowCadenceReduction,
    };

    protected override void ConfigureCaptureCamera(XRCamera? sourceCamera, XRCamera captureCamera, DrivenWorldTransform captureTransform)
    {
        base.ConfigureCaptureCamera(sourceCamera, captureCamera, captureTransform);
        if (sourceCamera is not null)
            captureCamera.CullingMask = sourceCamera.CullingMask;
    }

    public void InvalidateViewHistory() => CaptureViewport?.ActiveCamera?.InvalidateTemporalHistory();
}
