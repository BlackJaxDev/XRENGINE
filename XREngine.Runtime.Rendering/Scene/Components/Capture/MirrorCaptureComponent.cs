using System.Numerics;
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

/// <summary>Planar reflection with two immutable capture slots per admitted source camera.</summary>
public partial class MirrorCaptureComponent : XRComponent, IRenderable
{
    public const int CameraCapacity = AdvancedProjectiveMirrorMaterial.ViewCapacity;
    public const int CaptureSlotsPerCamera = 2;
    public static bool DisallowMirrors { get; private set; }
    private readonly object _mirrorLifetimeSync = new();
    private readonly MirrorCameraBank?[] _views = new MirrorCameraBank?[CameraCapacity];
    private readonly XRCamera?[] _requestedCameras = new XRCamera?[CameraCapacity];
    private readonly AdvancedProjectiveMirrorMaterial _nativeMaterial = new();
    private readonly RenderCommandMesh3D _nativeDisplay;
    private readonly XRMesh _displayMesh;
    private readonly RenderInfo3D _renderInfo;
    private XRWindow? _captureWindow;
    private bool _retirementRequested = true;
    private bool _retirementQueued;
    private bool _configurationDirty;
    private bool _capacityWarning;
    private string? _lastCaptureFailure;
    private long _completedCaptures;

    public MirrorCaptureComponent()
    {
        _displayMesh = XRMesh.Create(VertexQuad.PosZ(1, false, 0, false));
        _nativeMaterial.Name = "Mirror.NativeProjective";
        _nativeMaterial.RenderPass = (int)EDefaultRenderPass.OpaqueDeferred;
        _nativeMaterial.RenderOptions.CullMode = ECullMode.Back;
        _nativeDisplay = new RenderCommandMesh3D((int)EDefaultRenderPass.OpaqueDeferred,
            new XRMeshRenderer(_displayMesh, _nativeMaterial), Matrix4x4.Identity)
        { Enabled = false, GpuProfilingLabel = "Mirror.NativeProjective" };
        _renderInfo = RenderInfo3D.New(this, _nativeDisplay);
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
        _renderInfo.CullingOffsetMatrix = renderMatrix;
    }

    private bool CollectMirrorDisplay(RenderInfo info, RenderCommandCollection passes, IRuntimeRenderCamera? camera)
    {
        if (camera is not XRCamera source || DisallowMirrors)
            return false;
        lock (_mirrorLifetimeSync)
        {
            if (_retirementRequested)
                return false;
            int index = FindOrRequestCamera(source);
            if (index < 0 || _configurationDirty)
                return false;
            if (UseAdvancedCapturePipeline)
            {
                XRCamera? stereoRightEyeCamera =
                    RuntimeEngine.Rendering.State.RenderingStereoRightEyeCamera;
                if (stereoRightEyeCamera is not null &&
                    !ReferenceEquals(stereoRightEyeCamera, source))
                {
                    // Single-pass stereo collects the display surface once with the
                    // left-eye camera. Admit the paired right eye from the scoped
                    // collection state without adding a second display command.
                    FindOrRequestCamera(stereoRightEyeCamera);
                }
                return _nativeDisplay.Enabled;
            }
            // AddCPU retains this exact immutable slot even if the collection
            // never executes. A separate GPU retain starts when its draw records.
            if (_views[index]?.Published is { } slot)
            {
                slot.Display.WorldMatrix = Transform.RenderMatrix;
                slot.Display.CollectedForRender(camera);
                passes.AddCPU(slot.Display, camera);
            }
            return false;
        }
    }
    private int FindOrRequestCamera(XRCamera source)
    {
        for (int i = 0; i < CameraCapacity; ++i)
            if (ReferenceEquals(_requestedCameras[i], source))
                return i;
        for (int i = 0; i < CameraCapacity; ++i)
        {
            if (_requestedCameras[i] is not null)
                continue;
            _requestedCameras[i] = source;
            return i;
        }
        if (!_capacityWarning)
        {
            _capacityWarning = true;
            Debug.RenderingWarning("Mirror camera capacity (three cameras, two slots each) reached; additional cameras are not admitted.");
        }
        return -1;
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
                window.RenderViewportsCallback += AdvanceMirrorCaptures;
                return true;
            }
            return false;
        }
    }
    private void AdvanceMirrorCaptures()
    {
        lock (_mirrorLifetimeSync)
        {
            if (_retirementRequested || DisallowMirrors || World is null)
                return;
            if (_configurationDirty)
            {
                WithdrawAllViews();
                if (!RetireViews())
                    return;
                _configurationDirty = false;
            }
            uint width = Math.Max(1u, TextureWidthOverride ?? 512u);
            uint height = Math.Max(1u, TextureHeightOverride ?? 512u);
            for (int i = 0; i < CameraCapacity; ++i)
            {
                XRCamera? camera = _requestedCameras[i];
                if (camera is null)
                    continue;
                try
                {
                    MirrorCameraBank view = _views[i] ??= CreateView(camera, width, height);
                    AdvanceView(i, view, width, height);
                }
                catch (Exception exception)
                {
                    if (_lastCaptureFailure != exception.Message)
                        Debug.RenderingWarning($"Mirror capture deferred: {exception.Message}");
                    _lastCaptureFailure = exception.Message;
                }
            }
            _nativeDisplay.Enabled = UseAdvancedCapturePipeline && HasPublishedView();
        }
    }
    private MirrorCameraBank CreateView(XRCamera camera, uint width, uint height)
    {
        MirrorCameraBank view = new(camera) { Width = width, Height = height };
        SceneNode? creatingNode = null;
        try
        {
            for (int i = 0; i < CaptureSlotsPerCamera; ++i)
            {
                creatingNode = new(World, $"MirrorCapture.{ID}.{camera.RenderIdentity}.{i}");
                // A single factory closure per persistent slot, never per refresh.
                MirrorTextureCaptureComponent capture = creatingNode.AddComponent(() => new MirrorTextureCaptureComponent
                    { UseAdvancedPipeline = UseAdvancedCapturePipeline, SourceCamera = camera, Width = width, Height = height })!;
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
    private void AdvanceView(int viewIndex, MirrorCameraBank view, uint width, uint height)
    {
        if (view.Width != width || view.Height != height)
        {
            _nativeMaterial.WithdrawView(viewIndex);
            view.Published = null;
            view.Width = width;
            view.Height = height;
            for (int i = 0; i < CaptureSlotsPerCamera; ++i)
            {
                view.Slots[i].Capture.WithdrawPublishedOutput();
                view.Slots[i].Capture.Width = width;
                view.Slots[i].Capture.Height = height;
            }
        }
        for (int i = 0; i < CaptureSlotsPerCamera; ++i)
        {
            MirrorCaptureSlot slot = view.Slots[i];
            slot.SettleReaders();
            bool completed = slot.Capture.TryCompleteCapture();
            if (!slot.AwaitingCapture || slot.Capture.HasPendingCapture)
                continue;
            if (!completed || !slot.Capture.TryPublishCompletedOutput(out XRTexture2D? texture))
            {
                slot.AwaitingCapture = false;
                continue;
            }
            if (!slot.Publish(texture, slot.Capture.ReflectedViewProjection, slot.Capture.FramebufferYDown))
            {
                slot.Capture.WithdrawPublishedOutput();
                slot.AwaitingCapture = false;
                continue;
            }
            MirrorCaptureSlot? old = view.Published;
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
            view.Published = slot;
            if (old is not null && !ReferenceEquals(old, slot))
                old.Capture.WithdrawPublishedOutput();
            ++_completedCaptures;
            _lastCaptureFailure = null;
        }
        for (int i = 0; i < CaptureSlotsPerCamera; ++i)
        {
            MirrorCaptureSlot slot = view.Slots[i];
            if (ReferenceEquals(slot, view.Published) || slot.AwaitingCapture || slot.Unavailable)
                continue;
            MirrorCaptureView request = CaptureReflectedView(view.Camera);
            if (slot.Capture.TryCapture(in request) || slot.Capture.HasPendingCapture)
            {
                slot.AwaitingCapture = true;
                break;
            }
        }
    }
    private MirrorCaptureView CaptureReflectedView(XRCamera source)
    {
        Vector3 normal = -Transform.RenderForward;
        float scale = MathF.Max(MathF.Abs(normal.X), MathF.Max(MathF.Abs(normal.Y), MathF.Abs(normal.Z)));
        if (!float.IsFinite(scale) || scale <= 0)
            throw new InvalidOperationException("The mirror has a non-finite or degenerate plane.");
        normal = Vector3.Normalize(normal / scale);
        Vector3 point = Transform.RenderTranslation;
        Plane plane = new(normal, -Vector3.Dot(normal, point));
        Matrix4x4 world = source.Transform.RenderMatrix * Matrix4x4.CreateReflection(plane);
        bool yDown = RenderClipSpacePolicy.FramebufferTextureYDirection(
            RuntimeRenderingHostServices.FrameTiming.CurrentRenderBackend) == ERenderClipSpaceYDirection.YDown;
        return new(source, source.RenderIdentity, world, point, normal, yDown);
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
