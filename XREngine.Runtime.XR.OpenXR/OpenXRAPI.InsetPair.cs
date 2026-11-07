using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;

namespace XREngine.Rendering.API.Rendering.OpenXR;

/// <summary>
/// Inset view pair for the OpenXR quad-view configuration. Views 0 and 1 are the
/// wide pair and use the rig eye cameras; views 2 and 3 are the inset pair. Each
/// inset camera shares its eye's transform (same pose) and owns its own field of
/// view. The inset pair renders through its own layered stereo viewport and RVC
/// pipeline instance at the inset extent.
/// </summary>
public unsafe partial class OpenXRAPI
{
    private XRCamera? _openXrLeftInsetEyeCamera;
    private XRCamera? _openXrRightInsetEyeCamera;
    private XRViewport? _openXrInsetStereoViewport;
    private RenderPipeline? _openXrInsetStereoRenderPipeline;

    /// <summary>True when the quad-view configuration is active with four views.</summary>
    private bool IsOpenXrInsetPairActive
        => IsOpenXrQuadViewConfigurationActive && _viewCount == 4u;

    /// <summary>
    /// Creates or refreshes the inset cameras from the rig eye cameras. The inset
    /// camera follows its eye's transform, so it needs no rig node of its own.
    /// </summary>
    private bool EnsureOpenXrInsetEyeCameras(XRCamera baseCamera)
    {
        if (_openXrLeftEyeCamera is null || _openXrRightEyeCamera is null)
            return false;

        _openXrLeftInsetEyeCamera = EnsureOpenXrInsetEyeCamera(_openXrLeftInsetEyeCamera, _openXrLeftEyeCamera);
        _openXrRightInsetEyeCamera = EnsureOpenXrInsetEyeCamera(_openXrRightInsetEyeCamera, _openXrRightEyeCamera);
        CopyCameraCommon(baseCamera, _openXrLeftInsetEyeCamera);
        CopyCameraCommon(baseCamera, _openXrRightInsetEyeCamera);
        return true;
    }

    private static XRCamera EnsureOpenXrInsetEyeCamera(XRCamera? insetCamera, XRCamera eyeCamera)
    {
        if (insetCamera is not null && ReferenceEquals(insetCamera.Transform, eyeCamera.Transform))
            return insetCamera;

        return new XRCamera(
            eyeCamera.Transform,
            new XROpenXRFovCameraParameters(eyeCamera.NearZ, eyeCamera.FarZ));
    }

    private void EnsureOpenXrInsetStereoViewport(uint width, uint height)
    {
        _openXrInsetStereoViewport ??= new XRViewport(null)
        {
            PipelineRequest = RenderPipelineRequest.OpenXrEye(stereo: true),
            AutomaticallyCollectVisible = false,
            AutomaticallySwapBuffers = false,
            AllowUIRender = false,
            SetRenderPipelineFromCamera = false,
            AllowAutomaticInternalResolution = true,
            RendersToExternalSwapchainTarget = true,
            OpenXrViewPair = 1,
        };

        _openXrInsetStereoViewport.CullWithFrustum = RuntimeEngine.Rendering.Settings.OpenXrCullWithFrustum;
        _openXrInsetStereoViewport.SetFullScreen();
        if (_openXrInsetStereoViewport.Width == (int)width &&
            _openXrInsetStereoViewport.Height == (int)height)
        {
            return;
        }

        _openXrInsetStereoViewport.Resize(width, height, setInternalResolution: false);
        if (_openXrInsetStereoViewport.InternalWidth <= 0 || _openXrInsetStereoViewport.InternalHeight <= 0)
            _openXrInsetStereoViewport.SetInternalResolution((int)width, (int)height, correctAspect: false);
    }

    private RenderPipeline GetOrCreateOpenXrInsetStereoPipeline(RenderPipeline? sourcePipeline)
    {
        sourcePipeline ??= RuntimeEngine.Rendering.NewRenderPipeline(stereo: false);
        return GetOrCreateOpenXrPipelineInSlot(sourcePipeline, stereo: true, ref _openXrInsetStereoRenderPipeline);
    }

    /// <summary>
    /// Prepares and collects the inset pair for one frame: cameras, viewport, pipeline,
    /// per-view field of view, post-process state copied from the wide pair, and the
    /// combined stereo visibility of views 2 and 3.
    /// </summary>
    private bool TryCollectOpenXrInsetPair(
        IRuntimeRenderWorld world,
        XRCamera baseCamera,
        RenderPipeline sourcePipeline,
        RenderPipeline widePipeline,
        out int commandsAdded)
    {
        commandsAdded = 0;
        if (!EnsureOpenXrInsetEyeCameras(baseCamera))
            return false;

        EnsureOpenXrInsetStereoViewport(GetOpenXrSwapchainWidth(2), GetOpenXrSwapchainHeight(2));
        XRViewport viewport = _openXrInsetStereoViewport!;
        XRCamera left = _openXrLeftInsetEyeCamera!;
        XRCamera right = _openXrRightInsetEyeCamera!;
        RenderPipeline insetPipeline = GetOrCreateOpenXrInsetStereoPipeline(sourcePipeline);
        viewport.WorldInstanceOverride = world;
        viewport.Camera = left;
        viewport.MeshRenderCommandsOverride = null;
        if (!ReferenceEquals(viewport.RenderPipeline, insetPipeline))
            viewport.RenderPipeline = insetPipeline;

        // The inset shows the same scene as the wide pair, so it takes the wide
        // eye cameras' post-process state.
        CopyPostProcessState(widePipeline, insetPipeline, _openXrLeftEyeCamera!, left);
        CopyPostProcessState(widePipeline, insetPipeline, _openXrRightEyeCamera!, right);
        left.RenderPipeline = insetPipeline;
        right.RenderPipeline = insetPipeline;
        UpdateOpenXrEyeCameraFromView(left, 2u);
        UpdateOpenXrEyeCameraFromView(right, 3u);

        RenderCommandCollection meshCommands = viewport.RenderPipelineInstance.MeshRenderCommands;
        meshCommands.SetOwnerPipeline(viewport.RenderPipelineInstance);
        return CollectOpenXrStereoVisible(
            world,
            left,
            right,
            viewport,
            meshCommands,
            cullProjectionScale: 1.0f,
            out commandsAdded,
            out _);
    }

    /// <summary>
    /// Publishes the inset pair's collected commands with the wide pair's swap. An empty
    /// collect keeps the previous rendering commands, like the wide pair.
    /// </summary>
    private void SwapOpenXrInsetPairCommands()
    {
        if (!IsOpenXrInsetPairActive || _openXrInsetStereoViewport is not { } viewport)
            return;

        RenderCommandCollection commands = viewport.RenderPipelineInstance.MeshRenderCommands;
        if (commands.GetUpdatingCommandCount() == 0 && commands.GetRenderingCommandCount() > 0)
            return;

        viewport.SwapBuffers(commands, allowScreenSpaceUISwap: false);
    }

    /// <summary>Releases the inset pair's frame packages with the other eye packages.</summary>
    private void CancelOpenXrInsetFramePackages()
        => _openXrInsetStereoViewport?.RenderPipelineInstance.MeshRenderCommands.CancelBackendReadyFramePackages();
}
