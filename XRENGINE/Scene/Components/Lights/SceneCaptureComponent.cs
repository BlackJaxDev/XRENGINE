using Extensions;
using System.Numerics;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;
using XREngine.Scene.Transforms;

namespace XREngine.Components.Lights
{
    /// <summary>
    /// Traditional mirror that renders the scene to a texture and displays it on a quad.
    /// </summary>
    public class MirrorCaptureComponent : XRComponent, IRenderable
    {
        public MirrorCaptureComponent()
        {
            _material = XRMaterial.CreateUnlitColorMaterialForward();
            _renderFBO = new XRQuadFrameBuffer(_material);

            _renderSceneRC = new RenderCommandMethod3D((int)EDefaultRenderPass.PreRender, Render);
            _renderSceneRC.OnSwapBuffers += RenderCommand_OnSwapBuffers;
            _displayQuadRC = new RenderCommandMesh3D((int)EDefaultRenderPass.OpaqueForward, _renderFBO.FullScreenMesh, Matrix4x4.Identity);

            _renderInfo = RenderInfo3D.New(this, _displayQuadRC);
            _renderInfo.PreCollectCommandsCallback += RenderCommand_OnPreAddRenderCommands;
            RenderedObjects = [_renderInfo];
        }

        private bool RenderCommand_OnPreAddRenderCommands(RenderInfo info, RenderCommandCollection passes, XRCamera? camera)
        {
            if (camera is null || _collectingVisible)
                return false;

            Matrix4x4 cameraMirror = Matrix4x4.CreateReflection(Transform.WorldForwardPlane) * camera.Transform.WorldMatrix;

            _collectVisibleCamera.Parameters = camera.Parameters;
            _drivenTransformCollectVisible.SetWorldMatrix(cameraMirror);
            CollectVisible();
            return true;
        }

        private void RenderCommand_OnSwapBuffers(RenderCommand command)
            => SwapBuffers();

        public RenderInfo[] RenderedObjects { get; }

        private readonly RenderCommandMethod3D _renderSceneRC;
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

        private readonly XRQuadFrameBuffer _renderFBO;
        protected XRQuadFrameBuffer RenderFBO => _renderFBO;

        protected internal override void OnComponentActivated()
        {
            base.OnComponentActivated();
            InitializeForCapture();
        }

        private uint? _width = 1;
        public uint? Width
        {
            get => _width;
            set => SetField(ref _width, value);
        }
        private uint? _height = 1;
        public uint? Height
        {
            get => _height;
            set => SetField(ref _height, value);
        }
        private XRMaterial? _material;
        public XRMaterial? Material
        {
            get => _material;
            set => SetField(ref _material, value);
        }

        private DrivenWorldTransform _drivenTransformCollectVisible = new();
        private DrivenWorldTransform _drivenTransformRender = new();

        private XRCamera _collectVisibleCamera = new()
        {
            Parameters = new XRPerspectiveCameraParameters(90.0f, 1.0f, 0.1f, 10000.0f),
            PostProcessing = new PostProcessingSettings()
            {
                ColorGrading = new ColorGradingSettings()
                {
                    AutoExposure = false,
                    Exposure = 1.0f
                }
            }
        };
        private XRCamera _renderCamera = new()
        {
            Parameters = new XRPerspectiveCameraParameters(90.0f, 1.0f, 0.1f, 10000.0f),
            PostProcessing = new PostProcessingSettings()
            {
                ColorGrading = new ColorGradingSettings()
                {
                    AutoExposure = false,
                    Exposure = 1.0f
                }
            }
        };

        private bool ResolutionChanged()
        {
            var scale = Transform.LocalMatrix.ExtractScale();
            uint width = Width ?? (uint)scale.X;
            uint height = Height ?? (uint)scale.Y;
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
            uint width = Width ?? (uint)scale.X;
            uint height = Height ?? (uint)scale.Y;
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

            _collectVisibleCamera.Transform = _drivenTransformCollectVisible;
            _renderCamera.Transform = _drivenTransformRender;

            Viewport = new XRViewport(null, width, height)
            {
                WorldInstanceOverride = World,
                Camera = null,
                RenderPipeline = new DefaultRenderPipeline(),
                SetRenderPipelineFromCamera = false,
                AutomaticallyCollectVisible = false,
                AutomaticallySwapBuffers = false,
                AllowUIRender = false,
            };
        }

        private bool _collectingVisible = false;

        public void CollectVisible()
        {
            if (_collectingVisible)
                return;
            _collectingVisible = true;
            Viewport?.CollectVisible(null, _collectVisibleCamera);
            _collectingVisible = false;
        }

        public void SwapBuffers()
        {
            (_drivenTransformCollectVisible, _drivenTransformRender) = (_drivenTransformRender, _drivenTransformCollectVisible);
            (_collectVisibleCamera, _renderCamera) = (_renderCamera, _collectVisibleCamera);
            Viewport?.SwapBuffers();
        }

        /// <summary>
        /// Renders the scene to the ResultTexture cubemap.
        /// </summary>
        public void Render()
        {
            if (World is null || RenderFBO is null)
                return;

            if (ResolutionChanged())
                InitializeForCapture();

            Engine.Rendering.State.IsSceneCapturePass = true;

            RenderFBO!.SetRenderTargets(
                (_environmentTexture!, EFrameBufferAttachment.ColorAttachment0, 0, -1),
                (GetDepthAttachment(), EFrameBufferAttachment.DepthStencilAttachment, 0, -1));

            Viewport!.Render(RenderFBO, null, _renderCamera, false, null);

            if (_environmentTexture is not null)
            {
                _environmentTexture.Bind();
                _environmentTexture.GenerateMipmapsGPU();
            }

            Engine.Rendering.State.IsSceneCapturePass = false;
        }

        private IFrameBufferAttachement GetDepthAttachment()
            => CaptureDepthCubeMap ? _environmentDepthTexture! : _tempDepth!;
    }
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

        public XRViewport?[] Viewports { get; } = new XRViewport?[6];

        protected XRTextureCube? _environmentTextureCubemap;
        protected XRTextureCube? _environmentDepthTextureCubemap;
        protected XRRenderBuffer? _tempDepth;
        private XRCubeFrameBuffer? _renderFBO;

        public XRTextureCube? EnvironmentTextureCubemap
        {
            get => _environmentTextureCubemap;
            set => SetField(ref _environmentTextureCubemap, value);
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
                };
                cam.PostProcessing = new PostProcessingSettings();
                cam.PostProcessing.ColorGrading.AutoExposure = false;
                cam.PostProcessing.ColorGrading.Exposure = 1.0f;
            }
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
            => Viewports[i]?.CollectVisible(null, null);

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

            GetDepthParams(out IFrameBufferAttachement depthAttachment, out int[] depthLayers);

            if (_progressiveRenderEnabled)
            {
                RenderFace(depthAttachment, depthLayers, _currentFace);
                _currentFace = (_currentFace + 1) % 6;
            }
            else
                for (int i = 0; i < 6; ++i)
                    RenderFace(depthAttachment, depthLayers, i);
            
            if (_environmentTextureCubemap is not null)
            {
                _environmentTextureCubemap.Bind();
                _environmentTextureCubemap.GenerateMipmapsGPU();
            }

            Engine.Rendering.State.IsSceneCapturePass = false;
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
