using Assimp;
using Extensions;
using System.Collections.Concurrent;
using System.Numerics;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;
using XREngine.Rendering.Models.Materials;
using XREngine.Scene.Transforms;

namespace XREngine.Components.Lights
{
    /// <summary>
    /// Traditional mirror that renders the scene to a texture and displays it on a quad.
    /// </summary>
    public class MirrorCaptureComponent : XRComponent, IRenderable
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

            _renderInfo = RenderInfo3D.New(this, _displayQuadRC, preRenderRC);
            _renderInfo.PreCollectCommandsCallback += RenderCommand_OnPreAddRenderCommands;
            RenderedObjects = [_renderInfo];
        }

        private void PreRender()
        {
            XRCamera? camera = Engine.Rendering.State.RenderingCamera;
            if (camera is null || !_renderingCameras.TryGetValue(camera, out (Vector3 mirrorPoint, Vector3 mirrorNormal, Matrix4x4 camMirrorWorld) info))
                return;

            UpdateMirrorCamera(camera, info.mirrorPoint, info.mirrorNormal, info.camMirrorWorld);
            Render();
        }

        /// <summary>
        /// All cameras that have captured this mirror, and will need a mirrored camera matrix.
        /// </summary>
        private ConcurrentDictionary<XRCamera, (Vector3 mirrorPoint, Vector3 mirrorNormal, Matrix4x4 camMirrorWorld)> _collectedCameras = [];
        private ConcurrentDictionary<XRCamera, (Vector3 mirrorPoint, Vector3 mirrorNormal, Matrix4x4 camMirrorWorld)> _renderingCameras = [];
        private bool RenderCommand_OnPreAddRenderCommands(RenderInfo info, RenderCommandCollection passes, XRCamera? camera)
        {
            if (camera is null || ShouldNotRenderThisMirror(camera))
                return false;

            _collectedCameras.TryAdd(camera, GetMirrorInfo(camera));
            CollectVisible();
            return true;
        }

        private bool ShouldNotRenderThisMirror(XRCamera camera) =>
            DisallowMirrors || //Are mirrors disabled?
            //(World?.Lights?.CollectingVisibleShadowMaps ?? false) || //Are we collecting shadow maps?
            _collectedCameras.ContainsKey(camera) || //Has this camera already captured this mirror?
            camera == _mirrorCamera; //Is this camera the mirror camera itself?

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
            set => SetField(ref _captureDepthCubeMap, value);
        }

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

        private readonly XRFrameBuffer _renderFBO;
        protected XRFrameBuffer RenderFBO => _renderFBO;

        protected internal override void OnComponentActivated()
        {
            base.OnComponentActivated();
            InitializeForCapture();
            Engine.Time.Timer.SwapBuffers += SwapBuffers;
        }
        protected internal override void OnComponentDeactivated()
        {
            base.OnComponentDeactivated();
            Engine.Time.Timer.SwapBuffers -= SwapBuffers;
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

        protected override void OnTransformRenderWorldMatrixChanged(TransformBase transform)
        {
            base.OnTransformRenderWorldMatrixChanged(transform);
            _displayQuadRC.WorldMatrix = transform.WorldMatrix;
        }

        protected virtual void InitializeForCapture()
        {
            var scale = Transform.LocalMatrix.ExtractScale();
            uint width = TextureWidthOverride ?? (uint)scale.X;
            uint height = TextureHeightOverride ?? (uint)scale.Y;
            if (width < 1)
                width = 1;
            if (height < 1)
                height = 1;

            _environmentTexture?.Destroy();
            _environmentTexture = new XRTexture2D(width, height, EPixelInternalFormat.Rgba8, EPixelFormat.Rgba, EPixelType.UnsignedByte, false)
            {
                MinFilter = ETexMinFilter.NearestMipmapLinear,
                MagFilter = ETexMagFilter.Nearest,
                UWrap = ETexWrapMode.ClampToEdge,
                VWrap = ETexWrapMode.ClampToEdge,
                Resizable = false,
                SizedInternalFormat = ESizedInternalFormat.Rgba8,
                Name = "SceneCaptureEnvColor",
                AutoGenerateMipmaps = false,
                //FrameBufferAttachment = EFrameBufferAttachment.ColorAttachment0,
            };
            //_envTex.Generate();
            _material.Textures = [_environmentTexture];

            if (CaptureDepthCubeMap)
            {
                _environmentDepthTexture?.Destroy();
                _environmentDepthTexture = new XRTexture2D(width, height, EPixelInternalFormat.DepthComponent24, EPixelFormat.DepthStencil, EPixelType.UnsignedInt248, false)
                {
                    MinFilter = ETexMinFilter.NearestMipmapLinear,
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
            Viewport = new XRViewport(null, width, height)
            {
                WorldInstanceOverride = World,
                Camera = _mirrorCamera,
                RenderPipeline = new DefaultRenderPipeline(),
                SetRenderPipelineFromCamera = false,
                AutomaticallyCollectVisible = false,
                AutomaticallySwapBuffers = false,
                AllowUIRender = false,
            };
        }

        private (Vector3 mirrorPoint, Vector3 mirrorNormal, Matrix4x4 camMirrorWorld) GetMirrorInfo(XRCamera camera)
        {
            GetMirrorPlane(out Vector3 mirrorPoint, out Vector3 mirrorNormal);
            Matrix4x4 camMirrorWorld = CalculateMirrorCameraView(camera, mirrorPoint, mirrorNormal);
            return (mirrorPoint, mirrorNormal, camMirrorWorld);
        }

        private void UpdateMirrorCamera(XRCamera camera, Vector3 mirrorPoint, Vector3 mirrorNormal, Matrix4x4 camMirrorWorld)
        {
            if (_mirrorCamera is not null)
            {
                _mirrorCamera.Parameters = camera.Parameters;
                _mirrorCamera.PostProcessing = camera.PostProcessing;
            }

            _mirrorTransform.SetWorldMatrix(camMirrorWorld);

            //mirror transform is technically not in a world, recalc render matrix here
            _mirrorTransform.RecalculateMatrixHeirarchy(true, true, false);

            _mirrorCamera?.SetObliqueClippingPlane(mirrorPoint, -mirrorNormal);
        }

        private static Matrix4x4 CalculateMirrorCameraView(XRCamera camera, Vector3 mirrorPoint, Vector3 mirrorNormal)
        {
            if (mirrorNormal.LengthSquared() < 0.0001f)
                return Matrix4x4.Identity;

            //Mirror to camera line
            Vector3 camWorld = camera.Transform.RenderTranslation;
            Vector3 planePerpPoint = XRMath.ProjectPointToPlane(camWorld, mirrorPoint, mirrorNormal);
            float distance = Vector3.Distance(camWorld, planePerpPoint);
            Vector3 perpNormal = (camWorld - planePerpPoint).Normalized();
            if (perpNormal.LengthSquared() < 0.0001f)
                return Matrix4x4.Identity;

            Vector3 camPosMirror = planePerpPoint - perpNormal * distance;
            Vector3 camUpDirMirror = Vector3.Reflect(camera.Transform.RenderUp, mirrorNormal);
            Vector3 camFwdDirMirror = Vector3.Reflect(camera.Transform.RenderForward, mirrorNormal);

            if (camUpDirMirror.LengthSquared() < 0.0001f)
                camUpDirMirror = Globals.Up;
            if (camFwdDirMirror.LengthSquared() < 0.0001f)
                camFwdDirMirror = Globals.Backward;

            return Matrix4x4.CreateScale(new Vector3(-1.0f, 1.0f, 1.0f)) * Matrix4x4.CreateWorld(camPosMirror, camFwdDirMirror, camUpDirMirror);
        }

        private void GetMirrorPlane(out Vector3 mirrorPoint, out Vector3 mirrorNormal)
        {
            mirrorPoint = Transform.WorldTranslation;
            mirrorNormal = Vector3.Normalize(Transform.WorldForward);
        }

        private void CollectVisible()
        {
            //DisallowMirrors = true;
            Viewport?.CollectVisible(null, null);
            //DisallowMirrors = false;
        }

        public void SwapBuffers()
        {
            (_collectedCameras, _renderingCameras) = (_renderingCameras, _collectedCameras);
            _collectedCameras.Clear();
            Viewport?.SwapBuffers();
        }

        private void Render()
        {
            if (World is null || RenderFBO is null)
                return;

            if (ResolutionChanged())
                InitializeForCapture();

            Engine.Rendering.State.IsSceneCapturePass = true;
            Engine.Rendering.State.ReverseCulling = true;

            RenderFBO!.SetRenderTargets(
                (_environmentTexture!, EFrameBufferAttachment.ColorAttachment0, 0, -1),
                (GetDepthAttachment(), EFrameBufferAttachment.DepthStencilAttachment, 0, -1));

            Viewport!.Render(RenderFBO, null, null, false, null);

            Engine.Rendering.State.ReverseCulling = false;
            Engine.Rendering.State.IsSceneCapturePass = false;
        }

        private IFrameBufferAttachement GetDepthAttachment()
            => CaptureDepthCubeMap ? _environmentDepthTexture! : _tempDepth!;
    }
}
