using System.Numerics;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Scene.Transforms;

namespace XREngine.Components.Lights;

/// <summary>
/// One completion-gated mirror output slot. It uses the same ownership protocol for both
/// legacy and Advanced displays; only the selected offscreen pipeline family differs.
/// </summary>
public sealed class MirrorTextureCaptureComponent : AdvancedOffscreenTextureCaptureComponent
{
    private MirrorCaptureView _view;
    private bool _hasView;
    private bool _useAdvancedPipeline = true;
    private Matrix4x4 _reflectedViewProjection = Matrix4x4.Identity;
    private bool _framebufferYDown;

    internal Matrix4x4 ReflectedViewProjection => _reflectedViewProjection;
    internal bool FramebufferYDown => _framebufferYDown;

    /// <summary>Must be selected before activation; changing it requires owner retirement/rebuild.</summary>
    public bool UseAdvancedPipeline
    {
        get => _useAdvancedPipeline;
        set => SetField(ref _useAdvancedPipeline, value);
    }

    protected override bool UsesAdvancedPipeline => _useAdvancedPipeline;

    protected override RenderPipelineOffscreenIntent OffscreenIntent
        => new(ERenderPipelineOffscreenViewIntent.Mirror, ERenderPipelineOffscreenOutput.HdrColor,
            EnableLateTransparency: true);

    protected override EFrameOutputKind CaptureOutputKind => EFrameOutputKind.InWorldMirror;

    /// <summary>Authorizes this slot to render the supplied exact source-camera view.</summary>
    public bool TryCapture(in MirrorCaptureView view)
    {
        SetField(ref _view, view, publishNotifications: false);
        SetField(ref _hasView, true, publishNotifications: false);
        SourceCamera = view.SourceCamera;
        return TryCapture();
    }

    /// <summary>Clears a stale pending pose after a rejected admission or retirement.</summary>
    public void ClearRequestedView()
        => SetField(ref _hasView, false, publishNotifications: false);

    protected override void ConfigureCaptureCamera(
        XRCamera? sourceCamera, XRCamera captureCamera, DrivenWorldTransform captureTransform)
    {
        if (!_hasView || sourceCamera is null)
            throw new InvalidOperationException("A mirror capture requires an admitted source-camera view.");

        MirrorCaptureView view = _view;
        captureCamera.ClearObliqueClippingPlane();
        captureCamera.Parameters = sourceCamera.Parameters;
        captureCamera.DepthMode = sourceCamera.DepthMode;
        captureTransform.SetWorldMatrix(view.ReflectedWorld, setRenderMatrixNow: true,
            childRecalcType: ELoopType.Sequential);
        // The reflected transform and lens must be installed before oblique projection calculation.
        captureCamera.SetObliqueClippingPlane(view.PlanePoint, view.PlaneNormal);
        SetField(ref _reflectedViewProjection,
            captureTransform.InverseRenderMatrix * captureCamera.ProjectionMatrixUnjittered,
            publishNotifications: false);
        SetField(ref _framebufferYDown,
            RenderClipSpacePolicy.FramebufferTextureYDirection(RuntimeRenderingHostServices.FrameTiming.CurrentRenderBackend) ==
            ERenderClipSpaceYDirection.YDown, publishNotifications: false);
    }
}
