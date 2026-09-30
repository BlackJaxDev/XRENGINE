using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Scene.Transforms;

namespace XREngine.Components.Lights;

/// <summary>One completion-gated monoscopic spectator slot using the existing offscreen pipeline.</summary>
public sealed class SpectatorTextureCaptureComponent : AdvancedOffscreenTextureCaptureComponent
{
    protected override RenderPipelineOffscreenIntent OffscreenIntent => new(
        ERenderPipelineOffscreenViewIntent.SceneCapture, ERenderPipelineOffscreenOutput.HdrColor,
        EnableLateTransparency: true, EnablePostProcessing: true, EnableTemporalHistory: true,
        EnableBloomAndDoF: true);
    protected override EFrameOutputKind CaptureOutputKind => EFrameOutputKind.SceneCapture;

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
