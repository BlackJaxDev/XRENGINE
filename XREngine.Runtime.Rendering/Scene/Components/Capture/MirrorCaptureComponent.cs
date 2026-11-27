using System.Numerics;
using XREngine.Components.Scene.Transforms;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;
using XREngine.Rendering.Models.Materials;
using XREngine.Scene;
using XREngine.Scene.Transforms;
using YamlDotNet.Serialization;

namespace XREngine.Components.Lights;

/// <summary>Planar reflection with triple-buffered immutable capture slots per admitted source camera.</summary>
public partial class MirrorCaptureComponent : XRComponent, IRenderable
{
    public const int CameraCapacity = AdvancedProjectiveMirrorMaterial.ViewCapacity;
    public const int CaptureSlotsPerCamera = 4;
    private const int StereoTransparentDisplayCapacity = CaptureSlotsPerCamera * CaptureSlotsPerCamera + CaptureSlotsPerCamera * 2;
    private const ulong StaleCameraEvictionFrames = 8ul;
    private const ulong DesktopDemandFreshnessFrames = 2ul;
    public static bool DisallowMirrors { get; private set; }
    private readonly object _mirrorLifetimeSync = new();
    private readonly MirrorCameraBank?[] _views = new MirrorCameraBank?[CameraCapacity];
    private SharedOffscreenCapturePipeline? _sharedAdvancedPipeline;
    private SharedOffscreenCapturePipeline? _sharedDefaultPipeline;
    private readonly XRCamera?[] _requestedCameras = new XRCamera?[CameraCapacity];
    private readonly ulong[] _requestedCameraLastSeenFrame = new ulong[CameraCapacity];
    private readonly bool[] _requestedCameraIsStereoEye = new bool[CameraCapacity];
    private readonly bool[] _slotRebindRequested = new bool[CameraCapacity];
    private readonly MirrorStereoTransparentDisplayCommand?[] _stereoTransparentDisplays =
        new MirrorStereoTransparentDisplayCommand?[StereoTransparentDisplayCapacity];
    private readonly RenderCommandMesh3D.SharedModelHistory _sharedDisplayModelHistory = new();
    private int _stereoTransparentDisplayCount;
    private readonly AdvancedProjectiveMirrorMaterial _nativeMaterial = new();
    private readonly XRMaterial _portalMaterial;
    private readonly RenderCommandMesh3D _nativeDisplay;
    private readonly RenderCommandMesh3D _portalDisplay;
    private readonly XRMesh _displayMesh;
    private readonly RenderInfo3D _renderInfo;
    private XRWindow? _captureWindow;
    private bool _beforeXrEyeRenderSubscribed;
    private ulong _lastMirrorCaptureFrameId = ulong.MaxValue;
    private bool _retirementRequested = true;
    private bool _retirementQueued;
    private bool _configurationDirty;
    private bool _capacityWarning;
    private bool _traditionalDisplayInAdvancedViewWarning;
    private string? _lastCaptureFailure;
    private long _completedCaptures;

    public MirrorCaptureComponent()
    {
        // Canonical Advanced GPU scene admission accepts triangle meshes only; triangulate the display quad.
        _displayMesh = XRMesh.Create(VertexQuad.PosZ(1, false, 0, false).ToTriangles());
        _nativeMaterial.Name = "Mirror.NativeProjective";
        _nativeMaterial.RenderPass = (int)EDefaultRenderPass.OpaqueDeferred;
        _nativeMaterial.RenderOptions.CullMode = ECullMode.Back;
        _nativeDisplay = new RenderCommandMesh3D((int)EDefaultRenderPass.OpaqueDeferred,
            new XRMeshRenderer(_displayMesh, _nativeMaterial), Matrix4x4.Identity)
        { Enabled = false, GpuProfilingLabel = "Mirror.NativeProjective" };
        _portalMaterial = CreatePortalSurfaceMaterial();
        _portalDisplay = new RenderCommandMesh3D((int)EDefaultRenderPass.OpaqueDeferred,
            new XRMeshRenderer(_displayMesh, _portalMaterial), Matrix4x4.Identity)
        { Enabled = false, GpuProfilingLabel = "Mirror.InPlacePortalSurface" };
        _renderInfo = RenderInfo3D.New(this, _nativeDisplay, _portalDisplay);
        _renderInfo.LocalCullingVolume = AABB.FromCenterSize(Vector3.Zero, new Vector3(1, 1, 0.001f));
        _renderInfo.CullingOffsetMatrix = Matrix4x4.Identity;
        _renderInfo.PreCollectCommandsCallback += CollectMirrorDisplay;
        RenderedObjects = [_renderInfo];
    }

    public RenderInfo[] RenderedObjects { get; }
    private bool _useAdvancedCapturePipeline;
    /// <summary>Selects native Advanced capture and projective shading for this mirror.</summary>
    public bool UseAdvancedCapturePipeline
    {
        get => _useAdvancedCapturePipeline;
        set
        {
            lock (_mirrorLifetimeSync)
                if (SetField(ref _useAdvancedCapturePipeline, value))
                    _configurationDirty = true;
        }
    }
    private bool _transparentBackground;
    /// <summary>
    /// Renders the mirror in the late transparent pass so uncovered capture pixels (alpha 0)
    /// reveal the live scene behind the mirror instead of an opaque black rectangle.
    /// </summary>
    public bool TransparentBackground
    {
        get => _transparentBackground;
        set
        {
            lock (_mirrorLifetimeSync)
                if (SetField(ref _transparentBackground, value) && value)
                    _nativeDisplay.Enabled = false;
        }
    }
    private uint? _textureWidthOverride = 512u;
    public uint? TextureWidthOverride
    {
        get => _textureWidthOverride;
        set => SetField(ref _textureWidthOverride, value);
    }
    private uint? _textureHeightOverride = 512u;
    public uint? TextureHeightOverride
    {
        get => _textureHeightOverride;
        set => SetField(ref _textureHeightOverride, value);
    }
    private uint _msaaSampleCount = 1u;
    /// <summary>
    /// Hardware MSAA sample count for Advanced mirror captures. Values above 1 resolve
    /// multisample visibility and coverage alpha into the single-sample mirror texture.
    /// </summary>
    public uint MsaaSampleCount
    {
        get => _msaaSampleCount;
        set => SetField(ref _msaaSampleCount, Math.Max(1u, value));
    }
    private LayerMask _captureCullingMask = DefaultLayers.EverythingExceptGizmos;
    /// <summary>
    /// Layer mask rendered by this mirror's reflected capture cameras.
    /// Defaults to <see cref="DefaultLayers.EverythingExceptGizmos"/>.
    /// Set <see cref="DefaultLayers.CharactersMask"/> to reflect avatars and characters only.
    /// </summary>
    public LayerMask CaptureCullingMask
    {
        get => _captureCullingMask;
        set => SetField(ref _captureCullingMask, value);
    }
    [YamlIgnore]
    public XRMaterial Material => _nativeMaterial;
    /// <summary>Diagnostic first-camera output. GPU readers require an explicit retained generation.</summary>
    [YamlIgnore]
    public XRTexture2D? EnvironmentTexture => _views[0]?.Published?.Texture;
    [YamlIgnore]
    public XRViewport? Viewport => _views[0]?.Published?.Capture.CaptureViewport ??
        _views[0]?.Slots[0]?.Capture.CaptureViewport;

    protected override void OnComponentActivated()
    {
        base.OnComponentActivated();
        lock (_mirrorLifetimeSync)
            _retirementRequested = false;
        RegisterTransparentPortalSurfaceIfNeeded();
        RuntimeEngine.AddRenderThreadCoroutine(AttachCaptureWindow,
            "MirrorCapture.Attach", RenderThreadJobKind.RenderPipelineResource);
    }
    protected override void OnComponentDeactivated()
    {
        RequestMirrorRetirement();
        base.OnComponentDeactivated();
    }
    protected override void OnDestroying()
    {
        RequestMirrorRetirement();
        base.OnDestroying();
    }
    protected override void OnTransformRenderWorldMatrixChanged(TransformBase transform, Matrix4x4 renderMatrix)
    {
        base.OnTransformRenderWorldMatrixChanged(transform, renderMatrix);
        _nativeDisplay.WorldMatrix = renderMatrix;
        _portalDisplay.WorldMatrix = renderMatrix;
        _renderInfo.CullingOffsetMatrix = renderMatrix;
        RecordPortalWorldTransform(in renderMatrix, RuntimeEngine.Rendering.State.RenderFrameId);
        if (RuntimeEngine.IsRenderThread)
        {
            for (int i = 0; i < _stereoTransparentDisplayCount; ++i)
                _stereoTransparentDisplays[i]?.UpdateLateRenderWorldMatrix(in renderMatrix);
        }
    }

    private bool CollectMirrorDisplay(RenderInfo info, RenderCommandCollection passes, IRuntimeRenderCamera? camera)
    {
        if (camera is not XRCamera source || DisallowMirrors)
            return false;

        bool registerTransparentPortal = false;
        bool unregisterPortal = false;
        bool result;
        lock (_mirrorLifetimeSync)
        {
            if (_retirementRequested)
                return false;
            // A shadow pass draws depth from a light camera. That camera does not
            // look at the mirror, so it gets no capture bank. Without this check
            // the directional light's shadow camera takes a bank, and its capture
            // work runs in every desktop frame. The native display stays a shadow
            // caster while it is shown, as in a normal view.
            if (passes.IsShadowPass)
                return !TransparentBackground && UseAdvancedCapturePipeline && _nativeDisplay.Enabled;
            // The traditional display is a CPU-only draw in the opaque forward
            // pass. An Advanced view submits only canonical scene draws in its
            // opaque passes, and rejects its whole frame package when one draw has
            // no canonical submission. The desktop then never presents. Do not
            // add the traditional display to an Advanced view, and do not start
            // captures for it. Set UseAdvancedCapturePipeline for such views.
            bool viewerUsesAdvanced = CameraUsesAdvancedPipeline(source);
            if (viewerUsesAdvanced && UsesInPlaceStencilOnAdvanced)
            {
                // In-place stencil mode renders directly inside the main AdvancedRenderPipeline
                // buffers and never creates private offscreen capture camera banks.
                _nativeDisplay.Enabled = false;
                if (_transparentBackground)
                {
                    // Transparent-background in-place mirrors keep the live room visible behind
                    // uncovered mirror pixels, so skip the opaque primary blocker quad and register
                    // directly with the portal surface publisher outside _mirrorLifetimeSync.
                    _portalDisplay.Enabled = false;
                    registerTransparentPortal = true;
                    result = false;
                }
                else
                {
                    _portalDisplay.Enabled = true;
                    result = true;
                }
                goto FinishPortalRegistry;
            }
            _portalDisplay.Enabled = false;
            unregisterPortal = true;
            if (!UseAdvancedCapturePipeline && viewerUsesAdvanced)
            {
                WarnTraditionalDisplayInAdvancedView();
                result = false;
                goto FinishPortalRegistry;
            }
            ulong frameId = RuntimeEngine.Rendering.State.RenderFrameId;
            // CollectVisibleStereo runs on the update thread before RenderingStereoRightEyeCamera
            // is bound on the render thread. Fall back to the active VR right-eye viewport camera
            // when collecting for the left eye so both XR eyes receive mirror captures.
            XRCamera? stereoRightEyeCamera =
                RuntimeEngine.Rendering.State.RenderingStereoRightEyeCamera
                ?? (source.StereoEyeLeft == true
                    ? RuntimeEngine.VRState.ViewInformation.RightEyeCamera ?? RuntimeEngine.VRState.RightEyeViewport?.Camera
                    : null);
            bool isStereoPair = stereoRightEyeCamera is not null &&
                !ReferenceEquals(stereoRightEyeCamera, source);
            int index = FindOrRequestCamera(source, isStereoPair, frameId);
            if (index < 0 || _configurationDirty)
            {
                result = false;
                goto FinishPortalRegistry;
            }
            int rightIndex = -1;
            if (isStereoPair)
            {
                // Single-pass stereo collects the display surface once with the
                // left-eye camera. Admit the paired right eye from the scoped
                // collection state without adding a second display command.
                rightIndex = FindOrRequestCamera(stereoRightEyeCamera!, isStereoEye: true, frameId);
            }
            if (TransparentBackground)
            {
                CollectTransparentMirrorDisplay(passes, camera, index, isStereoPair, rightIndex);
                result = false;
                goto FinishPortalRegistry;
            }
            if (UseAdvancedCapturePipeline && viewerUsesAdvanced)
            {
                result = _nativeDisplay.Enabled;
                goto FinishPortalRegistry;
            }
            // DefaultRenderPipeline does not shade AdvancedProjectiveMirrorMaterial; render
            // the published capture with the projective display shader instead of solid gray.
            // AddCPU retains this exact immutable slot even if the collection
            // never executes. A separate GPU retain starts when its draw records.
            if (_views[index]?.Published is { } slot)
            {
                slot.Display.WorldMatrix = Transform.RenderMatrix;
                slot.Display.CollectedForRender(camera);
                passes.AddCPU(slot.Display, camera);
            }
            result = false;
        }

    FinishPortalRegistry:
        if (registerTransparentPortal)
            RegisterTransparentPortalSurfaceIfNeeded();
        else if (unregisterPortal)
            UnregisterPortalSurfaceFromWorld();
        return result;
    }

    private void CollectTransparentMirrorDisplay(
        RenderCommandCollection passes,
        IRuntimeRenderCamera camera,
        int leftIndex,
        bool isStereoPair,
        int rightIndex)
    {
        if (isStereoPair)
        {
            MirrorCameraBank? leftBank = _views[leftIndex];
            MirrorCameraBank? rightBank = rightIndex >= 0 ? _views[rightIndex] : null;
            MirrorCaptureSlot? leftSlot = leftBank?.Published;
            MirrorCaptureSlot? rightSlot = rightBank?.Published;
            if (leftSlot is null && rightSlot is null)
                return;

            MirrorCaptureSlot resolvedLeft = leftSlot ?? rightSlot!;
            MirrorCaptureSlot resolvedRight = rightSlot ?? leftSlot!;
            MirrorStereoTransparentDisplayCommand display = GetOrCreateStereoTransparentDisplay(
                resolvedLeft, resolvedRight, leftBank, rightBank);
            display.WorldMatrix = Transform.RenderMatrix;
            display.CollectedForRender(camera);
            passes.AddCPU(display, camera);
            return;
        }

        if (_views[leftIndex]?.Published is { } monoSlot)
        {
            MirrorDisplayCommand display = monoSlot.GetOrCreateTransparentDisplay();
            display.AttachSharedModelHistory(_sharedDisplayModelHistory);
            display.WorldMatrix = Transform.RenderMatrix;
            display.CollectedForRender(camera);
            passes.AddCPU(display, camera);
        }
    }

    private MirrorStereoTransparentDisplayCommand GetOrCreateStereoTransparentDisplay(
        MirrorCaptureSlot leftSlot,
        MirrorCaptureSlot rightSlot,
        MirrorCameraBank? leftBank,
        MirrorCameraBank? rightBank)
    {
        for (int i = 0; i < _stereoTransparentDisplayCount; ++i)
        {
            MirrorStereoTransparentDisplayCommand? existing = _stereoTransparentDisplays[i];
            if (existing is not null && existing.MatchesSlots(leftSlot, rightSlot))
                return existing;
        }

        MirrorStereoTransparentDisplayCommand created =
            MirrorStereoTransparentDisplayCommand.Create(
                _displayMesh,
                leftSlot,
                rightSlot,
                leftBank,
                rightBank,
                Transform,
                _sharedDisplayModelHistory);
        if (_stereoTransparentDisplayCount < _stereoTransparentDisplays.Length)
            _stereoTransparentDisplays[_stereoTransparentDisplayCount++] = created;
        return created;
    }

    private void ClearStereoTransparentDisplays()
    {
        if (_stereoTransparentDisplayCount == 0)
            return;
        Array.Clear(_stereoTransparentDisplays, 0, _stereoTransparentDisplayCount);
        _stereoTransparentDisplayCount = 0;
    }

    private static bool CameraUsesAdvancedPipeline(XRCamera camera)
        => camera.GetConfiguredRenderPipeline() is IAdvancedRenderStageFamilyHost { UsesAdvancedStageFamily: true };
    private void WarnTraditionalDisplayInAdvancedView()
    {
        if (_traditionalDisplayInAdvancedViewWarning)
            return;
        _traditionalDisplayInAdvancedViewWarning = true;
        Debug.RenderingWarning(
            $"Mirror '{SceneNode.Name}' uses the traditional display, which an AdvancedRenderPipeline view cannot submit. " +
            $"The mirror is not shown in that view. Set {nameof(UseAdvancedCapturePipeline)} to true.");
    }
    private int FindOrRequestCamera(XRCamera source, bool isStereoEye, ulong frameId)
    {
        for (int i = 0; i < CameraCapacity; ++i)
        {
            if (!ReferenceEquals(_requestedCameras[i], source))
                continue;
            _requestedCameraLastSeenFrame[i] = frameId;
            _requestedCameraIsStereoEye[i] |= isStereoEye;
            return i;
        }
        for (int i = 0; i < CameraCapacity; ++i)
        {
            if (_requestedCameras[i] is not null)
                continue;
            _requestedCameras[i] = source;
            _requestedCameraLastSeenFrame[i] = frameId;
            _requestedCameraIsStereoEye[i] = isStereoEye;
            _slotRebindRequested[i] = false;
            return i;
        }
        int victim = FindEvictableCameraSlot(isStereoEye, frameId);
        if (victim >= 0)
        {
            _requestedCameras[victim] = source;
            _requestedCameraLastSeenFrame[victim] = frameId;
            _requestedCameraIsStereoEye[victim] = isStereoEye;
            _slotRebindRequested[victim] = true;
            return victim;
        }
        if (!_capacityWarning)
        {
            _capacityWarning = true;
            Debug.RenderingWarning("Mirror camera capacity (three cameras, two slots each) reached; additional cameras are not admitted.");
        }
        return -1;
    }

    private int FindEvictableCameraSlot(bool incomingIsStereoEye, ulong frameId)
    {
        int staleVictim = -1;
        ulong oldestStaleFrame = ulong.MaxValue;
        int nonStereoVictim = -1;
        ulong oldestNonStereoFrame = ulong.MaxValue;

        for (int i = 0; i < CameraCapacity; ++i)
        {
            XRCamera? existing = _requestedCameras[i];
            if (existing is null || existing.Transform is null)
                return i;

            ulong lastSeen = _requestedCameraLastSeenFrame[i];
            if (frameId >= lastSeen && frameId - lastSeen >= StaleCameraEvictionFrames && lastSeen < oldestStaleFrame)
            {
                oldestStaleFrame = lastSeen;
                staleVictim = i;
            }

            if (incomingIsStereoEye && !_requestedCameraIsStereoEye[i] && lastSeen < oldestNonStereoFrame)
            {
                oldestNonStereoFrame = lastSeen;
                nonStereoVictim = i;
            }
        }

        return staleVictim >= 0 ? staleVictim : nonStereoVictim;
    }
    private bool AttachCaptureWindow()
    {
        lock (_mirrorLifetimeSync)
        {
            if (_retirementRequested || IsDestroyed || World is null || _captureWindow is not null)
                return true;
            foreach (XRWindow window in RuntimeEngine.Windows)
            {
                if (!ReferenceEquals(window.TargetWorldInstance, World.GetRenderWorld()))
                    continue;
                _captureWindow = window;
                window.PreRenderViewportsCallback += AdvanceMirrorCapturesFromPreRender;
                if (!_beforeXrEyeRenderSubscribed)
                {
                    RuntimeEngine.VRState.BeforeXrEyeRender += AdvanceMirrorCapturesForXr;
                    _beforeXrEyeRenderSubscribed = true;
                }
                AdmitActiveVrEyeCameras(RuntimeEngine.Rendering.State.RenderFrameId);
                return true;
            }
            return false;
        }
    }
    private void AdvanceMirrorCapturesFromPreRender()
    {
        // When OpenXR is active, mirror captures run only in BeforeXrEyeRender after
        // OpenXR refreshes late eye, tracker, avatar, and mirror poses. Running here while
        // HasPreparedEyeFrame is false would consume RenderFrameId and submit stale captures.
        if (RuntimeEngine.VRState.IsOpenXRActive)
            return;
        AdvanceMirrorCaptures();
    }
    private void AdvanceMirrorCapturesForXr()
        => AdvanceMirrorCaptures();
    private void AdvanceMirrorCaptures()
    {
        bool registerTransparentPortal = false;
        bool unregisterPortal = false;
        lock (_mirrorLifetimeSync)
        {
            if (_retirementRequested || DisallowMirrors || World is null)
                return;
            ulong frameId = RuntimeEngine.Rendering.State.RenderFrameId;
            if (_lastMirrorCaptureFrameId == frameId)
                return;
            _lastMirrorCaptureFrameId = frameId;
            if (_configurationDirty)
            {
                WithdrawAllViews();
                if (!RetireViews())
                    return;
                _configurationDirty = false;
            }
            if (UsesInPlaceStencilOnAdvanced)
            {
                _nativeDisplay.Enabled = false;
                if (_transparentBackground)
                {
                    _portalDisplay.Enabled = false;
                    registerTransparentPortal = true;
                }
                else
                    _portalDisplay.Enabled = true;
                goto FinishPortalRegistry;
            }
            _portalDisplay.Enabled = false;
            unregisterPortal = true;
            AdmitActiveVrEyeCameras(frameId);
            bool skipDesktopCameraInVr = ShouldSkipDesktopCameraInVr();
            uint width = Math.Max(1u, TextureWidthOverride ?? 512u);
            uint height = Math.Max(1u, TextureHeightOverride ?? 512u);
            LayerMask cullingMask = _captureCullingMask;
            if (HasAnyViewExtentMismatch(width, height))
            {
                // Drain all slots and the shared capture pipeline cleanly before rebuilding at
                // the new resolution so no in-flight capture observes a resized pipeline.
                WithdrawAllViews();
                if (!RetireViews())
                    return;
            }
            for (int i = 0; i < CameraCapacity; ++i)
            {
                XRCamera? camera = _requestedCameras[i];
                if (camera is null)
                    continue;
                bool isStereoEye = _requestedCameraIsStereoEye[i];
                bool desktopDemandSkipped = !isStereoEye &&
                    (skipDesktopCameraInVr || !IsDesktopCameraRecentlyCollected(i, frameId));
                if (desktopDemandSkipped)
                {
                    if (_views[i] is { } idleView)
                        SettleViewSlots(i, idleView, cullingMask);
                    continue;
                }
                if (_slotRebindRequested[i])
                {
                    WithdrawSingleView(i);
                    if (!RetireSingleView(i))
                        continue;
                    _slotRebindRequested[i] = false;
                }
                if (camera.Transform is null)
                    continue;
                try
                {
                    MirrorCameraBank view = _views[i] ??= CreateView(camera, width, height, cullingMask);
                    AdvanceView(i, view, width, height, cullingMask);
                }
                catch (Exception exception)
                {
                    if (_lastCaptureFailure != exception.Message)
                        Debug.RenderingWarning($"Mirror capture deferred: {exception.Message}");
                    _lastCaptureFailure = exception.Message;
                }
            }
            _nativeDisplay.Enabled = !TransparentBackground && UseAdvancedCapturePipeline && HasPublishedView();
        }

    FinishPortalRegistry:
        if (registerTransparentPortal)
            RegisterTransparentPortalSurfaceIfNeeded();
        else if (unregisterPortal)
            UnregisterPortalSurfaceFromWorld();
    }
    private bool HasAnyViewExtentMismatch(uint width, uint height)
    {
        for (int i = 0; i < CameraCapacity; ++i)
            if (_views[i] is { } view && (view.Width != width || view.Height != height))
                return true;
        return false;
    }
    private bool IsDesktopCameraRecentlyCollected(int index, ulong frameId)
    {
        // Keep capturing until the desktop camera gets its first published frame, then capture
        // only while desktop visibility collection actively sees the mirror.
        if (_views[index]?.Published is null)
            return true;
        ulong lastSeen = _requestedCameraLastSeenFrame[index];
        return frameId >= lastSeen && frameId - lastSeen <= DesktopDemandFreshnessFrames;
    }
    private void SettleViewSlots(int viewIndex, MirrorCameraBank view, LayerMask cullingMask)
    {
        for (int i = 0; i < CaptureSlotsPerCamera; ++i)
        {
            MirrorCaptureSlot slot = view.Slots[i];
            if (slot.Capture.CaptureCullingMask.Value != cullingMask.Value)
                slot.Capture.CaptureCullingMask = cullingMask;
            if (slot.Capture.MsaaSampleCount != _msaaSampleCount)
                slot.Capture.MsaaSampleCount = _msaaSampleCount;
            slot.SettleReaders();
            bool completed = slot.Capture.TryCompleteCapture();
            if (!slot.AwaitingCapture || slot.Capture.HasPendingCapture)
                continue;
            if (slot.CaptureVersion == slot.Capture.CaptureVersion && slot.CaptureVersion > 0)
            {
                slot.AwaitingCapture = false;
                continue;
            }
            if (!completed || !slot.Capture.TryPublishCompletedOutput(out XRTexture2D? texture))
            {
                slot.AwaitingCapture = false;
                continue;
            }
            PublishCompletedSlotToView(viewIndex, view, slot, texture, writerPending: false);
        }
    }
    private void AdmitActiveVrEyeCameras(ulong frameId)
    {
        if (!RuntimeRenderingHostServices.Presentation.IsInVR && !RuntimeEngine.VRState.EmulatedRenderActive)
            return;
        XRCamera? leftEye = RuntimeEngine.VRState.ViewInformation.LeftEyeCamera ??
            RuntimeEngine.VRState.LeftEyeViewport?.Camera;
        if (leftEye is not null &&
            (!UseAdvancedCapturePipeline || CameraUsesAdvancedPipeline(leftEye)))
            FindOrRequestCamera(leftEye, isStereoEye: true, frameId);
        XRCamera? rightEye = RuntimeEngine.VRState.ViewInformation.RightEyeCamera ??
            RuntimeEngine.VRState.RightEyeViewport?.Camera;
        if (rightEye is not null &&
            (!UseAdvancedCapturePipeline || CameraUsesAdvancedPipeline(rightEye)))
            FindOrRequestCamera(rightEye, isStereoEye: true, frameId);
    }
    private static bool ShouldSkipDesktopCameraInVr()
    {
        IRuntimeRenderPresentationServices presentation = RuntimeRenderingHostServices.Presentation;
        if (!presentation.IsInVR)
            return false;
        return !presentation.RenderWindowsWhileInVR ||
            presentation.VrMirrorMode != EVrMirrorMode.FullIndependentRender;
    }
    private MirrorCameraBank CreateView(XRCamera camera, uint width, uint height, LayerMask cullingMask)
    {
        MirrorCameraBank view = new(camera) { Width = width, Height = height };
        SceneNode? creatingNode = null;
        try
        {
            bool useAdvancedCapture = UseAdvancedCapturePipeline && CameraUsesAdvancedPipeline(camera);
            SharedOffscreenCapturePipeline sharedPipeline = useAdvancedCapture
                ? (_sharedAdvancedPipeline ??= new SharedOffscreenCapturePipeline())
                : (_sharedDefaultPipeline ??= new SharedOffscreenCapturePipeline());
            for (int i = 0; i < CaptureSlotsPerCamera; ++i)
            {
                creatingNode = new(World, $"MirrorCapture.{ID}.{camera.RenderIdentity}.{i}");
                // A single factory closure per persistent slot, never per refresh.
                MirrorTextureCaptureComponent capture = creatingNode.AddComponent(() => new MirrorTextureCaptureComponent
                    { SharedPipeline = sharedPipeline, UseAdvancedPipeline = useAdvancedCapture, CaptureCullingMask = cullingMask, MsaaSampleCount = _msaaSampleCount, SourceCamera = camera, Width = width, Height = height })!;
                view.Slots[i] = new MirrorCaptureSlot(creatingNode, capture, _displayMesh);
                creatingNode = null;
            }
            return view;
        }
        catch
        {
            // No slot can have a writer or reader before the complete bank is admitted.
            creatingNode?.Destroy();
            for (int i = 0; i < CaptureSlotsPerCamera; ++i)
                view.Slots[i]?.Node.Destroy();
            throw;
        }
    }
    private void AdvanceView(int viewIndex, MirrorCameraBank view, uint width, uint height, LayerMask cullingMask)
    {
        SettleViewSlots(viewIndex, view, cullingMask);
        bool sameFramePublish = _captureMode == EMirrorCaptureMode.SameFrameOffscreen;
        for (int i = 0; i < CaptureSlotsPerCamera; ++i)
        {
            MirrorCaptureSlot slot = view.Slots[i];
            if (ReferenceEquals(slot, view.Published) || slot.AwaitingCapture || slot.Unavailable ||
                !slot.ReadersRetired || slot.Capture.HasPendingCapture)
                continue;
            if (!TryCaptureReflectedView(view.Camera, out MirrorCaptureView request, out Matrix4x4 mirrorWorld))
                break;
            if (slot.Capture.TryCapture(in request, in mirrorWorld) || slot.Capture.HasPendingCapture)
            {
                if (sameFramePublish &&
                    slot.Capture.TryPublishSubmittedSameQueueOutput(out XRTexture2D? sameFrameTexture))
                {
                    PublishCompletedSlotToView(
                        viewIndex,
                        view,
                        slot,
                        sameFrameTexture,
                        writerPending: slot.Capture.HasPendingCapture);
                }
                else
                    slot.AwaitingCapture = true;
                break;
            }
        }
    }
    private void PublishCompletedSlotToView(
        int viewIndex,
        MirrorCameraBank view,
        MirrorCaptureSlot slot,
        XRTexture2D texture,
        bool writerPending)
    {
        if (!slot.Publish(
            texture,
            slot.Capture.ReflectedViewProjection,
            slot.Capture.CapturedMirrorWorldMatrix,
            slot.Capture.FramebufferYDown,
            writerPending))
        {
            slot.Capture.WithdrawPublishedOutput();
            slot.AwaitingCapture = writerPending;
            return;
        }
        MirrorCaptureSlot? old = view.Published;
        if (!_transparentBackground)
        {
            try
            {
                _nativeMaterial.PublishView(viewIndex, view.Camera.RenderIdentity, texture,
                    slot.Capture.ReflectedViewProjection, slot.Capture.FramebufferYDown);
            }
            catch
            {
                // This slot has not been exposed to collection. Keep the previous
                // published view intact and make the rejected slot writable again.
                slot.Capture.WithdrawPublishedOutput();
                throw;
            }
        }
        view.Published = slot;
        if (old is not null && !ReferenceEquals(old, slot))
            old.Capture.WithdrawPublishedOutput();
        ++_completedCaptures;
        _lastCaptureFailure = null;
    }
    private bool TryCaptureReflectedView(XRCamera source, out MirrorCaptureView view, out Matrix4x4 mirrorWorld)
    {
        view = default;
        mirrorWorld = Transform.RenderMatrix;
        Vector3 normal = -Transform.RenderForward;
        float scale = MathF.Max(MathF.Abs(normal.X), MathF.Max(MathF.Abs(normal.Y), MathF.Abs(normal.Z)));
        if (!float.IsFinite(scale) || scale <= 0.0f)
            throw new InvalidOperationException("The mirror has a non-finite or degenerate plane.");
        normal = Vector3.Normalize(normal / scale);

        Vector3 point = mirrorWorld.Translation;
        Vector3 eyePos = ResolveSourceEyeWorldPosition(source);
        float eyeDistance = Vector3.Dot(eyePos - point, normal);
        if (!float.IsFinite(eyeDistance) || eyeDistance <= 0.01f)
            return false;

        bool yDown = RenderClipSpacePolicy.FramebufferTextureYDirection(
            RuntimeRenderingHostServices.FrameTiming.CurrentRenderBackend) == ERenderClipSpaceYDirection.YDown;

        // Place a proper right-handed camera at the reflected eye position, looking along the
        // mirror normal through the four world-space quad corners. Fitting an off-axis frustum
        // to those corners uses the full capture texture for the mirror surface and keeps
        // standard triangle winding without an improper determinant -1 reflection matrix.
        Vector3 reflectedEye = eyePos - 2.0f * eyeDistance * normal;
        Vector3 c0 = Vector3.Transform(new Vector3(-0.5f, -0.5f, 0.0f), mirrorWorld);
        Vector3 c1 = Vector3.Transform(new Vector3(0.5f, -0.5f, 0.0f), mirrorWorld);
        Vector3 c2 = Vector3.Transform(new Vector3(0.5f, 0.5f, 0.0f), mirrorWorld);
        Vector3 c3 = Vector3.Transform(new Vector3(-0.5f, 0.5f, 0.0f), mirrorWorld);

        Vector3 rawUp = c3 - c0;
        Vector3 orthoUp = rawUp - normal * Vector3.Dot(rawUp, normal);
        if (orthoUp.LengthSquared() < 1.0e-8f)
        {
            Vector3 fallbackUp = MathF.Abs(normal.Y) < 0.99f ? Vector3.UnitY : Vector3.UnitX;
            orthoUp = fallbackUp - normal * Vector3.Dot(fallbackUp, normal);
        }
        Vector3 mirrorUp = Vector3.Normalize(orthoUp);
        Matrix4x4 reflectedWorld = Matrix4x4.CreateWorld(reflectedEye, normal, mirrorUp);
        if (!Matrix4x4.Invert(reflectedWorld, out Matrix4x4 reflectedView))
            return false;

        Vector3 v0 = Vector3.Transform(c0, reflectedView);
        Vector3 v1 = Vector3.Transform(c1, reflectedView);
        Vector3 v2 = Vector3.Transform(c2, reflectedView);
        Vector3 v3 = Vector3.Transform(c3, reflectedView);

        float minX = MathF.Min(MathF.Min(v0.X, v1.X), MathF.Min(v2.X, v3.X));
        float maxX = MathF.Max(MathF.Max(v0.X, v1.X), MathF.Max(v2.X, v3.X));
        float minY = MathF.Min(MathF.Min(v0.Y, v1.Y), MathF.Min(v2.Y, v3.Y));
        float maxY = MathF.Max(MathF.Max(v0.Y, v1.Y), MathF.Max(v2.Y, v3.Y));
        float spanX = maxX - minX;
        float spanY = maxY - minY;
        if (!float.IsFinite(spanX) || !float.IsFinite(spanY) || spanX <= 1.0e-4f || spanY <= 1.0e-4f)
            return false;

        // Add a small border guard so bilinear taps at the mirror quad edge stay inside [0, 1].
        float padX = spanX * 0.01f;
        float padY = spanY * 0.01f;
        const float MaxHalfAngleRadians = 1.52f;
        float angleLeft = Math.Clamp(MathF.Atan((minX - padX) / eyeDistance), -MaxHalfAngleRadians, MaxHalfAngleRadians);
        float angleRight = Math.Clamp(MathF.Atan((maxX + padX) / eyeDistance), -MaxHalfAngleRadians, MaxHalfAngleRadians);
        float angleDown = Math.Clamp(MathF.Atan((minY - padY) / eyeDistance), -MaxHalfAngleRadians, MaxHalfAngleRadians);
        float angleUp = Math.Clamp(MathF.Atan((maxY + padY) / eyeDistance), -MaxHalfAngleRadians, MaxHalfAngleRadians);
        if (angleRight - angleLeft <= 1.0e-4f || angleUp - angleDown <= 1.0e-4f)
            return false;

        float nearZ = MathF.Max(0.01f, eyeDistance * 0.995f);
        float farZ = MathF.Max(source.Parameters.FarZ, nearZ + 100.0f);
        view = new MirrorCaptureView(
            source,
            source.RenderIdentity,
            reflectedWorld,
            point,
            normal,
            yDown,
            hasTightFrustum: true,
            angleLeft,
            angleRight,
            angleUp,
            angleDown,
            nearZ,
            farZ);
        return true;
    }
    private static Vector3 ResolveSourceEyeWorldPosition(XRCamera source)
    {
        if (source.StereoEyeLeft is bool leftEye &&
            RuntimeEngine.VRState.IsOpenXRActive &&
            RuntimeEngine.VRState.OpenXRApi is { } openXr &&
            openXr.TryGetEyeLocalPose(
                XREngine.Rendering.API.Rendering.OpenXR.RuntimeOpenXrPoseTiming.Late,
                leftEye,
                out Matrix4x4 eyeLocal))
        {
            Matrix4x4 rootRender = source.Transform.Parent?.ParentRenderMatrix ?? Matrix4x4.Identity;
            return (eyeLocal * rootRender).Translation;
        }

        return source.Transform.RenderTranslation;
    }

    private bool HasPublishedView()
    {
        for (int i = 0; i < CameraCapacity; ++i)
            if (_views[i]?.Published is not null)
                return true;
        return false;
    }
    /// <summary>Capture slots own their collection/swap cadence.</summary>
    public void SwapBuffers() { }
}
