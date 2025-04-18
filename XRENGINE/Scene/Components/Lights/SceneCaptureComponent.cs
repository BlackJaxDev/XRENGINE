using XREngine.Data.Rendering;
using XREngine.Rendering;

namespace XREngine.Components.Lights
{
    public class SceneCaptureComponent : XRComponent
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

        public virtual void CollectVisible()
        {
            if (_progressiveRenderEnabled)
                CollectVisibleFace(_currentFace);
            else
                for (int i = 0; i < 6; ++i)
                    CollectVisibleFace(i);
        }

        private void CollectVisibleFace(int i)
            => Viewports[i]?.CollectVisible(null, null);

        public virtual void SwapBuffers()
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
        public virtual void Render()
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

        /// <summary>
        /// Queues the light probe for capture.
        /// </summary>
        public void QueueCapture()
            => World?.Lights?.QueueForCapture(this);
    }
}
