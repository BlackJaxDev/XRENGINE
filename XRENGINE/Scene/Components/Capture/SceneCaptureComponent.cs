using System;
using System.Numerics;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Data.Vectors;
using XREngine.Data.Transforms.Rotations;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Scene.Transforms;
using YamlDotNet.Serialization;

namespace XREngine.Components.Lights
{
    public class SceneCaptureComponent : SceneCaptureComponentBase
    {
        private uint _colorResolution = Engine.Rendering.Settings.LightProbeResolution;
        public uint Resolution
        {
            get => _colorResolution;
            set => SetField(ref _colorResolution, value);
        }

        private bool _captureDepthCubeMap = Engine.Rendering.Settings.LightProbesCaptureDepth;
        public bool CaptureDepthCubeMap
        {
            get => _captureDepthCubeMap;
            set => SetField(ref _captureDepthCubeMap, value);
        }

        protected XRViewport? XPosVP => Viewports[0];
        protected XRViewport? XNegVP => Viewports[1];
        protected XRViewport? YPosVP => Viewports[2];
        protected XRViewport? YNegVP => Viewports[3];
        protected XRViewport? ZPosVP => Viewports[4];
        protected XRViewport? ZNegVP => Viewports[5];

        [YamlIgnore]
        public XRViewport?[] Viewports { get; } = new XRViewport?[6];

        private static readonly Quaternion[] FaceRotationOffsets =
        [
            new Rotator(0.0f, -90.0f, 180.0f).ToQuaternion(), // +X
            new Rotator(0.0f, 90.0f, 180.0f).ToQuaternion(),  // -X
            new Rotator(90.0f, 0.0f, 0.0f).ToQuaternion(),    // +Y
            new Rotator(-90.0f, 0.0f, 0.0f).ToQuaternion(),   // -Y
            new Rotator(0.0f, 180.0f, 180.0f).ToQuaternion(), // +Z
            new Rotator(0.0f, 0.0f, 180.0f).ToQuaternion(),   // -Z
        ];

        protected XRTextureCube? _environmentTextureCubemap;
        protected XRTexture2D? _environmentTextureOctahedral;
        protected XRTextureCube? _environmentDepthTextureCubemap;
        protected XRRenderBuffer? _tempDepth;
        private XRCubeFrameBuffer? _renderFBO;
        private XRQuadFrameBuffer? _octahedralFBO;
        private XRMaterial? _octahedralMaterial;

        private const uint OctahedralResolutionMultiplier = 2u;
        private static XRShader? s_cubemapToOctaShader;
        private static XRShader? s_fullscreenTriVertexShader;

        public XRTextureCube? EnvironmentTextureCubemap
        {
            get => _environmentTextureCubemap;
            set => SetField(ref _environmentTextureCubemap, value);
        }

        public XRTexture2D? EnvironmentTextureOctahedral
        {
            get => _environmentTextureOctahedral;
            private set => SetField(ref _environmentTextureOctahedral, value);
        }
        public XRTextureCube? EnvironmentDepthTextureCubemap => _environmentDepthTextureCubemap;
        protected XRCubeFrameBuffer? RenderFBO => _renderFBO;

        public void SetCaptureResolution(uint colorResolution, bool captureDepth = false)
        {
            Resolution = colorResolution;
            CaptureDepthCubeMap = captureDepth;
            InitializeForCapture();
        }

        protected internal override void OnComponentActivated()
        {
            base.OnComponentActivated();
            InitializeForCapture();
        }

        protected virtual void InitializeForCapture()
        {
            _environmentTextureCubemap?.Destroy();
            _environmentTextureCubemap = new XRTextureCube(Resolution, EPixelInternalFormat.Rgba8, EPixelFormat.Rgba, EPixelType.UnsignedByte, false)
            {
                MinFilter = ETexMinFilter.NearestMipmapLinear,
                MagFilter = ETexMagFilter.Nearest,
                UWrap = ETexWrapMode.ClampToEdge,
                VWrap = ETexWrapMode.ClampToEdge,
                WWrap = ETexWrapMode.ClampToEdge,
                Resizable = false,
                SizedInternalFormat = ESizedInternalFormat.Rgba8,
                Name = "SceneCaptureEnvColor",
                AutoGenerateMipmaps = false,
                //FrameBufferAttachment = EFrameBufferAttachment.ColorAttachment0,
            };
            //_envTex.Generate();

            if (CaptureDepthCubeMap)
            {
                _environmentDepthTextureCubemap?.Destroy();
                _environmentDepthTextureCubemap = new XRTextureCube(Resolution, EPixelInternalFormat.DepthComponent24, EPixelFormat.DepthStencil, EPixelType.UnsignedInt248, false)
                {
                    MinFilter = ETexMinFilter.NearestMipmapLinear,
                    MagFilter = ETexMagFilter.Nearest,
                    UWrap = ETexWrapMode.ClampToEdge,
                    VWrap = ETexWrapMode.ClampToEdge,
                    WWrap = ETexWrapMode.ClampToEdge,
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
                _tempDepth = new XRRenderBuffer(Resolution, Resolution, ERenderBufferStorage.Depth24Stencil8);
                //_tempDepth.Generate();
                //_tempDepth.Allocate();
            }

            _renderFBO = new XRCubeFrameBuffer(null);
            //_renderFBO.Generate();

            InitializeOctahedralEncodingResources();

            var cameras = XRCubeFrameBuffer.GetCamerasPerFace(0.1f, 10000.0f, true, Transform);
            for (int i = 0; i < cameras.Length; i++)
            {
                XRCamera cam = cameras[i];
                Viewports[i] = new XRViewport(null, Resolution, Resolution)
                {
                    WorldInstanceOverride = World,
                    Camera = cam,
                    RenderPipeline = new DefaultRenderPipeline(),
                    SetRenderPipelineFromCamera = false,
                    AutomaticallyCollectVisible = false,
                    AutomaticallySwapBuffers = false,
                    AllowUIRender = false,
                    CullWithFrustum = true,
                };
                var colorStage = cam.GetPostProcessStageState<ColorGradingSettings>();
                if (colorStage?.TryGetBacking(out ColorGradingSettings? grading) == true)
                {
                    grading.AutoExposure = false;
                    grading.Exposure = 1.0f;
                }
                else
                {
                    colorStage?.SetValue(nameof(ColorGradingSettings.AutoExposure), false);
                    colorStage?.SetValue(nameof(ColorGradingSettings.Exposure), 1.0f);
                }
            }

            SyncCaptureCameraTransforms();
        }

        private bool _progressiveRenderEnabled = true;
        /// <summary>
        /// If true, the SceneCaptureComponent will render one face of the cubemap each time a Render call is made.
        /// </summary>
        public bool ProgressiveRenderEnabled
        {
            get => _progressiveRenderEnabled;
            set => SetField(ref _progressiveRenderEnabled, value);
        }

        private int _currentFace = 0;

        public override void CollectVisible()
        {
            if (_progressiveRenderEnabled)
                CollectVisibleFace(_currentFace);
            else
                for (int i = 0; i < 6; ++i)
                    CollectVisibleFace(i);
        }

        private void CollectVisibleFace(int i)
            => Viewports[i]?.CollectVisible(false);

        public override void SwapBuffers()
        {
            if (_progressiveRenderEnabled)
                SwapBuffersFace(_currentFace);
            else
                for (int i = 0; i < 6; ++i)
                    SwapBuffersFace(i);
        }

        private void SwapBuffersFace(int i)
            => Viewports[i]?.SwapBuffers();

        /// <summary>
        /// Renders the scene to the ResultTexture cubemap.
        /// </summary>
        public override void Render()
        {
            if (World is null || RenderFBO is null)
                return;

            Engine.Rendering.State.IsSceneCapturePass = true;

            SyncCaptureCameraTransforms();

            GetDepthParams(out IFrameBufferAttachement depthAttachment, out int[] depthLayers);

            if (_progressiveRenderEnabled)
            {
                RenderFace(depthAttachment, depthLayers, _currentFace);
                _currentFace = (_currentFace + 1) % 6;
            }
            else
            {
                for (int i = 0; i < 6; ++i)
                    RenderFace(depthAttachment, depthLayers, i);
            }

            bool completedCycle = !_progressiveRenderEnabled || _currentFace == 0;

            if (!completedCycle)
                World?.Lights?.QueueForCapture(this);

            if (completedCycle && _environmentTextureCubemap is not null)
            {
                _environmentTextureCubemap.Bind();
                _environmentTextureCubemap.GenerateMipmapsGPU();
            }

            if (completedCycle)
                EncodeEnvironmentToOctahedralMap();

            Engine.Rendering.State.IsSceneCapturePass = false;
        }

        protected override void OnTransformRenderWorldMatrixChanged(TransformBase transform, Matrix4x4 renderMatrix)
        {
            base.OnTransformRenderWorldMatrixChanged(transform, renderMatrix);
            SyncCaptureCameraTransforms();
        }

        private void SyncCaptureCameraTransforms()
        {
            TransformBase? probeTransform = Transform;
            if (probeTransform is null)
                return;

            if (Viewports is null || Viewports.Length == 0)
                return;

            for (int i = 0; i < Viewports.Length; ++i)
            {
                var viewport = Viewports[i];
                var camera = viewport?.Camera;
                if (camera?.Transform is not Transform faceTransform)
                    continue;

                if (!ReferenceEquals(faceTransform.Parent, probeTransform))
                    faceTransform.SetParent(probeTransform, false, true);

                faceTransform.Translation = Vector3.Zero;
                faceTransform.Rotation = FaceRotationOffsets[i];
                faceTransform.Scale = Vector3.One;
                faceTransform.RecalculateMatrices(true, true);
            }
        }

        private void InitializeOctahedralEncodingResources()
        {
            if (_environmentTextureCubemap is null)
                return;

            uint extent = GetOctahedralExtent();
            if (extent == 0u)
                return;

            _environmentTextureOctahedral?.Destroy();
            EnvironmentTextureOctahedral = CreateOctahedralTexture(extent);

            if (_octahedralMaterial is null)
            {
                RenderingParameters renderParams = new();
                renderParams.DepthTest.Enabled = ERenderParamUsage.Disabled;

                _octahedralMaterial = new XRMaterial([_environmentTextureCubemap], GetFullscreenTriVertexShader(), GetCubemapToOctaShader())
                {
                    RenderOptions = renderParams,
                };
                _octahedralFBO = new XRQuadFrameBuffer(_octahedralMaterial);
            }
            else
            {
                _octahedralMaterial.Textures[0] = _environmentTextureCubemap;
            }

            _octahedralFBO!.SetRenderTargets((_environmentTextureOctahedral!, EFrameBufferAttachment.ColorAttachment0, 0, -1));
        }

        private uint GetOctahedralExtent()
            => Math.Max(1u, Resolution * OctahedralResolutionMultiplier);

        private static XRTexture2D CreateOctahedralTexture(uint extent)
            => new(extent, extent, EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat, false)
            {
                MinFilter = ETexMinFilter.Linear,
                MagFilter = ETexMagFilter.Linear,
                UWrap = ETexWrapMode.ClampToEdge,
                VWrap = ETexWrapMode.ClampToEdge,
                Resizable = false,
                SizedInternalFormat = ESizedInternalFormat.Rgba16f,
                Name = "SceneCaptureEnvOcta",
                AutoGenerateMipmaps = false,
            };

        private static XRShader GetCubemapToOctaShader()
            => s_cubemapToOctaShader ??= ShaderHelper.LoadEngineShader("Scene3D\\CubemapToOctahedron.fs", EShaderType.Fragment);

        private static XRShader GetFullscreenTriVertexShader()
            => s_fullscreenTriVertexShader ??= ShaderHelper.LoadEngineShader("Scene3D\\FullscreenTri.vs", EShaderType.Vertex);

        private void EncodeEnvironmentToOctahedralMap()
        {
            if (_octahedralFBO is null || _environmentTextureOctahedral is null)
                return;

            int width = (int)Math.Max(1u, _environmentTextureOctahedral.Width);
            int height = (int)Math.Max(1u, _environmentTextureOctahedral.Height);
            AbstractRenderer.Current?.SetRenderArea(new BoundingRectangle(IVector2.Zero, new IVector2(width, height)));

            using (_octahedralFBO.BindForWritingState())
            {
                Engine.Rendering.State.ClearByBoundFBO();
                _octahedralFBO.Render(null, true);
            }

            _environmentTextureOctahedral.GenerateMipmapsGPU();
        }

        private void GetDepthParams(out IFrameBufferAttachement depthAttachment, out int[] depthLayers)
        {
            if (CaptureDepthCubeMap)
            {
                depthAttachment = _environmentDepthTextureCubemap!;
                depthLayers = [0, 1, 2, 3, 4, 5];
            }
            else
            {
                depthAttachment = _tempDepth!;
                depthLayers = [0, 0, 0, 0, 0, 0];
            }
        }

        private void RenderFace(IFrameBufferAttachement depthAttachment, int[] depthLayers, int i)
        {
            RenderFBO!.SetRenderTargets(
                (_environmentTextureCubemap!, EFrameBufferAttachment.ColorAttachment0, 0, i),
                (depthAttachment, EFrameBufferAttachment.DepthStencilAttachment, 0, depthLayers[i]));

            Viewports[i]!.Render(RenderFBO, null, null, false, null);
        }

        public void FullCapture(uint colorResolution, bool captureDepth)
        {
            SetCaptureResolution(colorResolution, captureDepth);
            QueueCapture();
        }

        public void QueueCapture()
            => World?.Lights?.QueueForCapture(this);
    }
}
