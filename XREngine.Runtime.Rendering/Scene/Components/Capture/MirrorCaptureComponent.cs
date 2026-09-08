using XREngine.Extensions;
using System.Collections.Concurrent;
using System.Numerics;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;
using XREngine.Rendering.Models.Materials;
using XREngine.Scene.Transforms;
using YamlDotNet.Serialization;

namespace XREngine.Components.Lights
{
    /// <summary>
    /// Traditional mirror that renders the scene to a texture and displays it on a quad.
    /// </summary>
    public partial class MirrorCaptureComponent : XRComponent, IRenderable
    {
        public static bool DisallowMirrors { get; private set; } = false;
        
        public MirrorCaptureComponent()
        {
            _material = new XRMaterial(ShaderHelper.LoadEngineShader(Path.Combine("Common", "Mirror.fs")));
            _material.RenderOptions.CullMode = ECullMode.Back;
            _material.RenderOptions.RequiredEngineUniforms = EUniformRequirements.ViewportDimensions;
            _renderFBO = new XRFrameBuffer();

            XRMesh mesh = XRMesh.Create(VertexQuad.PosZ(1, false, 0, false));
            XRMeshRenderer meshRenderer = new(mesh, _material);

            _displayQuadRC = new RenderCommandMesh3D((int)EDefaultRenderPass.OpaqueForward, meshRenderer, Matrix4x4.Identity);
            RenderCommandMethod3D preRenderRC = new((int)EDefaultRenderPass.PreRender, PreRender);
            RenderCommandMethod3D postRenderRC = new((int)EDefaultRenderPass.PostRender, RecordMirrorConsumerCompletion);

            _renderInfo = RenderInfo3D.New(this, _displayQuadRC, preRenderRC, postRenderRC);
            _renderInfo.LocalCullingVolume = AABB.FromCenterSize(Vector3.Zero, new Vector3(1.0f, 1.0f, 0.001f));
            _renderInfo.CullingOffsetMatrix = Matrix4x4.Identity;
            _renderInfo.PreCollectCommandsCallback += RenderCommand_OnPreAddRenderCommands;
            RenderedObjects = [_renderInfo];
        }

        private void PreRender()
        {
            ++_mirrorPreRenderCount;
            if (_mirrorRetirementRequested)
                return;
            SettleMirrorWriter();
            XRCamera? camera = RuntimeEngine.Rendering.State.RenderingCamera;
            UpdateRenderTransform(Transform);
            if (camera is not null && ShouldUpdateCamera(camera))
                UpdateMirrorCamera(camera, true);
            if (!_mirrorResourcesQuarantined && SettleMirrorConsumerFences())
                RenderToFBO();
        }

        private bool ShouldUpdateCamera(XRCamera camera) => 
            _renderingCameras.TryRemove(camera) ||
            camera == RuntimeEngine.VRState.ViewInformation.RightEyeCamera; //Band-aid fix for two-pass VR

        /// <summary>
        /// All cameras that have captured this mirror, and will need a mirrored camera matrix.
        /// </summary>
        private ConcurrentHashSet<XRCamera> _collectedCameras = [];
        private ConcurrentHashSet<XRCamera> _renderingCameras = [];
        private bool RenderCommand_OnPreAddRenderCommands(RenderInfo info, RenderCommandCollection passes, IRuntimeRenderCamera? camera)
        {
            if (camera is not XRCamera renderCamera || ShouldNotRenderThisMirror(renderCamera))
                return false;

            _collectedCameras.Add(renderCamera);
            //UpdateMirrorCamera(camera, true);
            CollectVisible();
            return true;
        }

        private bool ShouldNotRenderThisMirror(XRCamera camera) =>
            DisallowMirrors || //Are mirrors disabled?
            _mirrorRetirementRequested || _mirrorResourcesQuarantined ||
            camera == _mirrorCamera || //Is this camera the mirror camera itself?
            _collectedCameras.Contains(camera); //Has this camera already captured this mirror?

        //private void RenderCommand_OnSwapBuffers(RenderCommand command)
        //    => SwapBuffers();

        public RenderInfo[] RenderedObjects { get; }

        //private readonly RenderCommandMethod3D _renderSceneRC;
        private readonly RenderCommandMesh3D _displayQuadRC;
        private readonly RenderInfo3D _renderInfo;

        private bool _captureDepthCubeMap = false;
        public bool CaptureDepthCubeMap
        {
            get => _captureDepthCubeMap;
            set
            {
                if (SetField(ref _captureDepthCubeMap, value))
                    _captureResourcesDirty = true;
            }
        }

        private bool _useAdvancedCapturePipeline;
        /// <summary>
        /// Selects the explicit Advanced mirror profile. The legacy pipeline remains the
        /// default so existing mirrors do not acquire Advanced resources implicitly.
        /// </summary>
        public bool UseAdvancedCapturePipeline
        {
            get => _useAdvancedCapturePipeline;
            set
            {
                if (!SetField(ref _useAdvancedCapturePipeline, value))
                    return;
                _captureResourcesDirty = true;
            }
        }

        [YamlIgnore]
        public XRViewport? Viewport { get; private set; }

        protected XRRenderBuffer? _tempDepth;

        protected XRTexture2D? _environmentTexture;
        public XRTexture2D? EnvironmentTexture
        {
            get => _environmentTexture;
            set => SetField(ref _environmentTexture, value);
        }

        protected XRTexture2D? _environmentDepthTexture;
        public XRTexture2D? EnvironmentDepthTexture => _environmentDepthTexture;

        private XRFrameBuffer _renderFBO;
        protected XRFrameBuffer RenderFBO => _renderFBO;

        protected override void OnComponentActivated()
        {
            base.OnComponentActivated();
            lock (_mirrorLifetimeSync)
                _mirrorRetirementRequested = false;
            InitializeForCapture();
        }
        protected override void OnComponentDeactivated()
        {
            base.OnComponentDeactivated();
            QueueCaptureResourceRelease();
        }

        protected override void OnDestroying()
        {
            QueueCaptureResourceRelease();
            base.OnDestroying();
        }

        private uint? _textureWidthOverride = 2560u;
        public uint? TextureWidthOverride
        {
            get => _textureWidthOverride;
            set => SetField(ref _textureWidthOverride, value);
        }
        private uint? _textureHeightOverride = 1440u;
        public uint? TextureHeightOverride
        {
            get => _textureHeightOverride;
            set => SetField(ref _textureHeightOverride, value);
        }
        private XRMaterial _material;
        public XRMaterial Material
        {
            get => _material;
            set => SetField(ref _material, value);
        }

        private readonly DrivenWorldTransform _mirrorTransform = new();
        private XRCamera? _mirrorCamera;

        private bool ResolutionChanged()
        {
            var scale = Transform.LocalMatrix.ExtractScale();
            uint width = TextureWidthOverride ?? (uint)scale.X;
            uint height = TextureHeightOverride ?? (uint)scale.Y;
            if (width < 1)
                width = 1;
            if (height < 1)
                height = 1;
            return _environmentTexture?.Width != width || _environmentTexture?.Height != height;
        }

        //protected override void OnTransformRenderWorldMatrixChanged(TransformBase transform)
        //{
        //    base.OnTransformRenderWorldMatrixChanged(transform);
        //    UpdateRenderTransform(transform);
        //}

        private void UpdateRenderTransform(TransformBase transform)
        {
            _displayQuadRC.WorldMatrix = transform.RenderMatrix;
            _renderInfo.CullingOffsetMatrix = transform.RenderMatrix;
            _mirrorCamera?.SetObliqueClippingPlane(transform.RenderTranslation, -transform.RenderForward);
        }

        protected virtual void InitializeForCapture()
        {
            if (!RuntimeEngine.IsRenderThread)
            {
                RuntimeEngine.EnqueueMainThreadTask(InitializeForCapture,
                    "MirrorCapture.Initialize", RenderThreadJobKind.RenderPipelineResource);
                return;
            }
            if (_mirrorRetirementRequested || _mirrorResourcesQuarantined || World is null)
                return;
            SettleMirrorWriter();
            if (HasPendingMirrorWriter || !SettleMirrorConsumerFences())
            {
                _captureResourcesDirty = true;
                return;
            }
            var scale = Transform.LocalMatrix.ExtractScale();
            uint width = TextureWidthOverride ?? (uint)scale.X;
            uint height = TextureHeightOverride ?? (uint)scale.Y;
            if (width < 1)
                width = 1;
            if (height < 1)
                height = 1;

            ReleaseMirrorResources();
            _renderFBO = new XRFrameBuffer();
            bool advanced = UseAdvancedCapturePipeline;
            EnvironmentTexture = new XRTexture2D(width, height,
                advanced ? EPixelInternalFormat.Rgba16f : EPixelInternalFormat.Rgba8,
                EPixelFormat.Rgba, advanced ? EPixelType.HalfFloat : EPixelType.UnsignedByte, false)
            {
                MinFilter = ETexMinFilter.Linear,
                MagFilter = ETexMagFilter.Nearest,
                UWrap = ETexWrapMode.ClampToEdge,
                VWrap = ETexWrapMode.ClampToEdge,
                Resizable = false,
                SizedInternalFormat = advanced ? ESizedInternalFormat.Rgba16f : ESizedInternalFormat.Rgba8,
                Name = "SceneCaptureEnvColor",
                AutoGenerateMipmaps = false,
                //FrameBufferAttachment = EFrameBufferAttachment.ColorAttachment0,
            };
            _captureResourcesDirty = false;
            //_envTex.Generate();
            _material.Textures = [_environmentTexture];

            if (CaptureDepthCubeMap)
            {
                _environmentDepthTexture?.Destroy();
                _environmentDepthTexture = new XRTexture2D(width, height, EPixelInternalFormat.Depth24Stencil8, EPixelFormat.DepthStencil, EPixelType.UnsignedInt248, false)
                {
                    MinFilter = ETexMinFilter.Nearest,
                    MagFilter = ETexMagFilter.Nearest,
                    UWrap = ETexWrapMode.ClampToEdge,
                    VWrap = ETexWrapMode.ClampToEdge,
                    Resizable = false,
                    SizedInternalFormat = ESizedInternalFormat.Depth24Stencil8,
                    Name = "SceneCaptureEnvDepth",
                    AutoGenerateMipmaps = false,
                    //FrameBufferAttachment = EFrameBufferAttachment.DepthAttachment,
                };
                //_envDepthTex.Generate();
            }
            else
            {
                _tempDepth = new XRRenderBuffer(width, height, ERenderBufferStorage.Depth24Stencil8);
                //_tempDepth.Generate();
                //_tempDepth.Allocate();
            }

            _mirrorCamera = new(_mirrorTransform);
            RenderPipelineRequest pipelineRequest = CreatePipelineRequest();
            Viewport = new XRViewport(null, width, height)
            {
                SetRenderPipelineFromCamera = false,
                PipelineRequest = pipelineRequest,
                WorldInstanceOverride = World.GetRenderWorld(),
                Camera = _mirrorCamera,
                RenderPipeline = RuntimeEngine.Rendering.NewRenderPipeline(pipelineRequest),
                AutomaticallyCollectVisible = false,
                AutomaticallySwapBuffers = false,
                AllowUIRender = false,
                CullWithFrustum = false,
            };
        }

        //private (Vector3 mirrorPoint, Vector3 mirrorNormal, Matrix4x4 camMirrorWorld) GetMirrorInfo(XRCamera camera)
        //{
        //    GetMirrorPlane(out Vector3 mirrorPoint, out Vector3 mirrorNormal);
        //    Matrix4x4 camMirrorWorld = CalculateMirrorCameraView(camera, mirrorPoint, mirrorNormal);
        //    return (mirrorPoint, mirrorNormal, camMirrorWorld);
        //}

        private void UpdateMirrorCamera(XRCamera camera, bool render)
        {
            Matrix4x4 camMirrorWorld = CalculateMirrorCameraView(camera, Transform.RenderTranslation, Transform.RenderForward, render);

            if (_mirrorCamera is not null)
            {
                _mirrorCamera.Parameters = camera.Parameters;
                _mirrorCamera.PostProcessStates = camera.PostProcessStates;
            }

            if (render)
                _mirrorTransform.SetRenderMatrix(camMirrorWorld);
            else
                _mirrorTransform.SetWorldMatrix(camMirrorWorld);
        }

        private static Matrix4x4 CalculateMirrorCameraView(XRCamera camera, Vector3 mirrorPoint, Vector3 mirrorNormal, bool render)
        {
            if (mirrorNormal.LengthSquared() < 0.0001f)
                return Matrix4x4.Identity;

            var tfm = camera.Transform;
            Vector3 pos, up, fwd;
            if (render)
            {
                pos = tfm.RenderTranslation;
                up = tfm.RenderUp;
                fwd = tfm.RenderForward;
            }
            else
            {
                pos = tfm.WorldTranslation;
                up = tfm.WorldUp;
                fwd = tfm.WorldForward;
            }

            Vector3 planePerpPoint = XRMath.ProjectPointToPlane(pos, mirrorPoint, mirrorNormal);
            float distance = Vector3.Distance(pos, planePerpPoint);
            Vector3 perpNormal = (pos - planePerpPoint).Normalized();
            if (perpNormal.LengthSquared() < 0.0001f)
                return Matrix4x4.Identity;

            Vector3 camPosMirror = planePerpPoint - perpNormal * distance;
            Vector3 camUpDirMirror = Vector3.Reflect(up, mirrorNormal);
            Vector3 camFwdDirMirror = Vector3.Reflect(fwd, mirrorNormal);

            if (camUpDirMirror.LengthSquared() < 0.0001f)
                camUpDirMirror = Globals.Up;
            if (camFwdDirMirror.LengthSquared() < 0.0001f)
                camFwdDirMirror = Globals.Backward;

            return Matrix4x4.CreateScale(new Vector3(-1.0f, 1.0f, 1.0f)) * Matrix4x4.CreateWorld(camPosMirror, camFwdDirMirror, camUpDirMirror);
        }

        private void CollectVisible()
        {
            //DisallowMirrors = true;
            Viewport?.CollectVisible();
            //DisallowMirrors = false;
        }

        public void SwapBuffers()
        {
            using var sample = RuntimeEngine.Profiler.Start("MirrorCaptureComponent.SwapBuffers");
            AdvanceMirrorCameraFrame();
            Viewport?.SwapBuffers();
        }

        private void AdvanceMirrorCameraFrame()
        {
            (_collectedCameras, _renderingCameras) = (_renderingCameras, _collectedCameras);
            _collectedCameras.Clear();
        }

        private void RenderToFBO()
        {
            if (World is null || RenderFBO is null)
                return;

            if (HasPendingMirrorWriter)
                return;
            if (_captureResourcesDirty || ResolutionChanged())
            {
                InitializeForCapture();
                if (HasPendingMirrorWriter || Viewport is null)
                    return;
                if (RuntimeEngine.Rendering.State.RenderingCamera is XRCamera sourceCamera)
                {
                    UpdateMirrorCamera(sourceCamera, true);
                    UpdateRenderTransform(Transform);
                }
            }

            RenderFBO!.SetRenderTargets(
                (_environmentTexture!, EFrameBufferAttachment.ColorAttachment0, 0, -1),
                (GetDepthAttachment(), EFrameBufferAttachment.DepthStencilAttachment, 0, -1));

            RenderOutputRequest output = CreateMirrorOutputRequest();
            FrameOutputPacingDecision pacing = FrameOutputPacingDecision.Due(
                output.ViewKind, output.OutputKind, output.FrameId) with { Request = output };
            Viewport!.CollectVisible(collectMirrors: false, frameOutputPacing: pacing);
            Viewport.SwapBuffers(allowScreenSpaceUISwap: false);
            RuntimeEngine.Rendering.State.PushMirrorPass();
            try
            {
                ++_mirrorAuthoringAttemptCount;
                _mirrorWriterAuthored = Viewport.TryRenderWithCompletion(
                    RenderFBO,
                    in output,
                    out _mirrorWriterFence,
                    out ERenderOutputCompletionAuthoringDisposition disposition);
                _lastMirrorAuthoringDisposition = disposition;
                _mirrorWriterRenderer = AbstractRenderer.Current;
                _mirrorWriterPipeline = Viewport.RenderPipelineInstance;
                _mirrorWriterCommands = _mirrorWriterPipeline.ActiveMeshRenderCommands;
                _mirrorWriterPackageGeneration = _mirrorWriterCommands.RenderingBackendReadyPackage.PackageGeneration;
                if (_mirrorWriterFence is null && disposition == ERenderOutputCompletionAuthoringDisposition.UnfencedAfterAuthoring)
                    QuarantineMirrorResources(_mirrorWriterRenderer);
                else if (_mirrorWriterFence is null)
                    ReleaseMirrorWriterPackage();
            }
            finally
            {
                RuntimeEngine.Rendering.State.PopMirrorPass();
            }
            AdvanceMirrorCameraFrame();
        }

        private IFrameBufferAttachement GetDepthAttachment()
            => CaptureDepthCubeMap ? _environmentDepthTexture! : _tempDepth!;
    }
}
