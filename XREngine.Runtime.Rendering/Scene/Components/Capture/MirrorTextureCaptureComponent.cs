using System.Numerics;
using XREngine.Components.Scene.Transforms;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Scene;
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
    private LayerMask _captureCullingMask = DefaultLayers.EverythingExceptGizmos;
    private uint _msaaSampleCount = 1u;
    private Matrix4x4 _reflectedViewProjection = Matrix4x4.Identity;
    private Matrix4x4 _capturedMirrorWorldMatrix = Matrix4x4.Identity;
    private bool _framebufferYDown;
    private XROpenXRFovCameraParameters? _captureFovParameters;

    internal Matrix4x4 ReflectedViewProjection => _reflectedViewProjection;
    internal Matrix4x4 CapturedMirrorWorldMatrix => _capturedMirrorWorldMatrix;
    internal bool FramebufferYDown => _framebufferYDown;

    /// <summary>Must be selected before activation; changing it requires owner retirement/rebuild.</summary>
    public bool UseAdvancedPipeline
    {
        get => _useAdvancedPipeline;
        set => SetField(ref _useAdvancedPipeline, value);
    }

    /// <summary>
    /// Layer mask applied to the reflected capture camera. Defaults to
    /// <see cref="DefaultLayers.EverythingExceptGizmos"/> so world mirrors render the full scene
    /// and local VR avatar while excluding gizmos and head-chopped first-person proxies.
    /// </summary>
    public LayerMask CaptureCullingMask
    {
        get => _captureCullingMask;
        set => SetField(ref _captureCullingMask, value);
    }

    /// <summary>
    /// Hardware MSAA sample count for Advanced offscreen mirror captures. Values above 1 enable
    /// multisample visibility and per-sample shading resolved into the single-sample HDR target.
    /// </summary>
    public uint MsaaSampleCount
    {
        get => _msaaSampleCount;
        set => SetField(ref _msaaSampleCount, Math.Max(1u, value));
    }

    protected override bool UsesAdvancedPipeline => _useAdvancedPipeline;

    protected override RenderPipelineOffscreenIntent OffscreenIntent
        => new(ERenderPipelineOffscreenViewIntent.Mirror, ERenderPipelineOffscreenOutput.HdrColor,
            EnableLateTransparency: true);

    protected override EFrameOutputKind CaptureOutputKind => EFrameOutputKind.InWorldMirror;

    /// <summary>Authorizes this slot to render the supplied exact source-camera view.</summary>
    public bool TryCapture(in MirrorCaptureView view)
        => TryCapture(in view, Matrix4x4.Identity);

    /// <summary>
    /// Authorizes this slot to render the supplied source-camera view and records the mirror quad's
    /// world transform at capture time so moving mirrors keep exact tight-frustum alignment.
    /// </summary>
    internal bool TryCapture(in MirrorCaptureView view, in Matrix4x4 mirrorWorldMatrix)
    {
        SetField(ref _view, view, publishNotifications: false);
        SetField(ref _hasView, true, publishNotifications: false);
        _capturedMirrorWorldMatrix = mirrorWorldMatrix;
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
        if (view.HasTightFrustum)
        {
            // Fit the off-axis projection to the four mirror-quad corners from the reflected eye
            // position so 100% of the capture texture covers the visible mirror rectangle.
            _captureFovParameters ??= new XROpenXRFovCameraParameters(view.NearZ, view.FarZ);
            _captureFovParameters.NearZ = view.NearZ;
            _captureFovParameters.FarZ = view.FarZ;
            _captureFovParameters.SetAngles(
                view.AngleLeft,
                view.AngleRight,
                view.AngleUp,
                view.AngleDown);
            captureCamera.Parameters = _captureFovParameters;
        }
        else if (sourceCamera.Parameters is XROpenXRFovCameraParameters sourceFov)
        {
            // Copy asymmetric OpenXR FOV angles into a capture-owned instance so the mirror
            // viewport never aliases or mutates the live XR eye camera's parameters.
            _captureFovParameters ??= new XROpenXRFovCameraParameters(sourceFov.NearZ, sourceFov.FarZ);
            _captureFovParameters.NearZ = sourceFov.NearZ;
            _captureFovParameters.FarZ = sourceFov.FarZ;
            _captureFovParameters.SetAngles(
                sourceFov.AngleLeft,
                sourceFov.AngleRight,
                sourceFov.AngleUp,
                sourceFov.AngleDown);
            captureCamera.Parameters = _captureFovParameters;
        }
        else
            captureCamera.Parameters = sourceCamera.Parameters;

        captureCamera.CullingMask = _captureCullingMask;
        captureCamera.DepthMode = sourceCamera.DepthMode;
        if (UsesAdvancedPipeline)
        {
            if (_msaaSampleCount > 1u)
            {
                captureCamera.AntiAliasingModeOverride = EAntiAliasingMode.Msaa;
                captureCamera.MsaaSampleCountOverride = _msaaSampleCount;
            }
            else
            {
                captureCamera.AntiAliasingModeOverride = EAntiAliasingMode.None;
                captureCamera.MsaaSampleCountOverride = 1u;
            }
        }
        captureTransform.SetWorldMatrix(view.ReflectedWorld, setRenderMatrixNow: true,
            childRecalcType: ELoopType.Sequential);
        // When the capture camera aligns with the mirror plane, NearZ already equals the mirror
        // plane distance and needs no oblique projection matrix modification.
        if (!view.HasTightFrustum)
            captureCamera.SetObliqueClippingPlane(view.PlanePoint, view.PlaneNormal);
        SetField(ref _reflectedViewProjection,
            captureTransform.InverseRenderMatrix * captureCamera.ProjectionMatrixUnjittered,
            publishNotifications: false);
        SetField(ref _framebufferYDown,
            RenderClipSpacePolicy.FramebufferTextureYDirection(RuntimeRenderingHostServices.FrameTiming.CurrentRenderBackend) ==
            ERenderClipSpaceYDirection.YDown, publishNotifications: false);
    }
}
