using MIConvexHull;
using System.Numerics;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Data.Vectors;
using XREngine.Rendering;
using XREngine.Rendering.Info;
using XREngine.Rendering.Models.Materials;
using XREngine.Scene.Transforms;
using XREngine.Timers;
using YamlDotNet.Serialization;

namespace XREngine.Components.Lights
{
    public class LightProbeComponent : SceneCaptureComponent, IRenderable, IVertex
    {
        /// <summary>
        /// Position for delaunay triangulation
        /// </summary>
        double[] IVertex.Position => [Transform.WorldTranslation.X, Transform.WorldTranslation.Y, Transform.WorldTranslation.Z];

        public LightProbeComponent() : base()
        {
            _realtimeCaptureTimer = new GameTimer(this);
            RenderedObjects = 
            [
                VisualRenderInfo = RenderInfo3D.New(this, _visualRC = new RenderCommandMesh3D((int)EDefaultRenderPass.OpaqueForward)),
            ];
        }

        private readonly RenderCommandMesh3D _visualRC;

        public bool PreviewEnabled 
        {
            get => VisualRenderInfo.IsVisible;
            set => VisualRenderInfo.IsVisible = value;
        }

        public RenderInfo3D VisualRenderInfo { get; }
        public RenderInfo[] RenderedObjects { get; }

        private readonly GameTimer _realtimeCaptureTimer;

        public TimeSpan? StopRealtimeCaptureAfter
        {
            get => _realtimeCaptureTimer.StopMultiFireAfter;
            set => _realtimeCaptureTimer.StopMultiFireAfter = value;
        }

        private bool _realtime = false;
        /// <summary>
        /// If true, the light probe will update in real time.
        /// </summary>
        public bool RealtimeCapture
        {
            get => _realtime;
            set => SetField(ref _realtime, value);
        }

        private TimeSpan? _realTimeUpdateInterval = TimeSpan.FromMilliseconds(100.0f);
        public TimeSpan? RealTimeCaptureUpdateInterval 
        {
            get => _realTimeUpdateInterval;
            set => SetField(ref _realTimeUpdateInterval, value);
        }

        private XRCubeFrameBuffer? _irradianceFBO;
        private XRCubeFrameBuffer? _prefilterFBO;
        private XRTextureCube? _irradianceTexture;
        private XRTextureCube? _prefilterTexture;
        private XRMeshRenderer? _previewSphere;

        public XRTextureCube? IrradianceTexture
        {
            get => _irradianceTexture;
            private set => SetField(ref _irradianceTexture, value);
        }
        public XRTextureCube? PrefilterTexture
        {
            get => _prefilterTexture;
            private set => SetField(ref _prefilterTexture, value);
        }
        [YamlIgnore]
        public XRMeshRenderer? PreviewSphere
        {
            get => _previewSphere;
            private set => SetField(ref _previewSphere, value);
        }

        public enum ERenderPreview
        {
            Environment,
            Irradiance,
            Prefilter,
        }

        private ERenderPreview _previewDisplay = ERenderPreview.Environment;
        public ERenderPreview PreviewDisplay
        {
            get => _previewDisplay;
            set => SetField(ref _previewDisplay, value);
        }

        public XRTexture? GetPreviewTexture()
            => PreviewDisplay switch
            {
                ERenderPreview.Irradiance => IrradianceTexture,
                ERenderPreview.Prefilter => PrefilterTexture,
                _ => _environmentTextureCubemap as XRTexture ?? _environmentTextureEquirect,
            };

        public string GetPreviewShaderPath()
            => PreviewDisplay switch
            {
                ERenderPreview.Irradiance or ERenderPreview.Prefilter => "Scene3D\\Cubemap.fs",
                _ => _environmentTextureCubemap is not null ? "Scene3D\\Cubemap.fs" : "Scene3D\\Equirect.fs",
            };

        protected override void OnTransformRenderWorldMatrixChanged(TransformBase transform)
        {
            if (_visualRC != null)
                _visualRC.WorldMatrix = Transform.RenderMatrix;
            
            base.OnTransformRenderWorldMatrixChanged(transform);
        }

        private uint _irradianceResolution = 128;
        public uint IrradianceResolution
        {
            get => _irradianceResolution;
            set => SetField(ref _irradianceResolution, value);
        }

        protected override void InitializeForCapture()
        {
            base.InitializeForCapture();

            //Irradiance texture doesn't need to be very high quality, 
            //linear filtering on low resolution will do fine
            IrradianceTexture = new XRTextureCube(IrradianceResolution, EPixelInternalFormat.Rgb8, EPixelFormat.Rgb, EPixelType.UnsignedByte, false)
            {
                MinFilter = ETexMinFilter.Linear,
                MagFilter = ETexMagFilter.Linear,
                UWrap = ETexWrapMode.ClampToEdge,
                VWrap = ETexWrapMode.ClampToEdge,
                WWrap = ETexWrapMode.ClampToEdge,
                Resizable = false,
                SizedInternalFormat = ESizedInternalFormat.Rgb8,
                AutoGenerateMipmaps = false,
            };

            PrefilterTexture = new XRTextureCube(Resolution, EPixelInternalFormat.Rgb16f, EPixelFormat.Rgb, EPixelType.HalfFloat, false)
            {
                MinFilter = ETexMinFilter.LinearMipmapLinear,
                MagFilter = ETexMagFilter.Linear,
                UWrap = ETexWrapMode.ClampToEdge,
                VWrap = ETexWrapMode.ClampToEdge,
                WWrap = ETexWrapMode.ClampToEdge,
                Resizable = false,
                SizedInternalFormat = ESizedInternalFormat.Rgb16f,
                AutoGenerateMipmaps = false,
            };

            ShaderVar[] prefilterVars =
            [
                new ShaderFloat(0.0f, "Roughness"),
                new ShaderInt((int)Resolution, "CubemapDim"),
            ];

            XRShader irrShader = ShaderHelper.LoadEngineShader("Scene3D\\IrradianceConvolution.fs", EShaderType.Fragment);
            XRShader prefShader = ShaderHelper.LoadEngineShader("Scene3D\\Prefilter.fs", EShaderType.Fragment);

            RenderingParameters r = new();
            r.DepthTest.Enabled = ERenderParamUsage.Disabled;
            XRTexture[] texArray = [_environmentTextureCubemap!];
            XRMaterial irrMat = new([], texArray, irrShader) { RenderOptions = r };
            XRMaterial prefMat = new(prefilterVars, texArray, prefShader) { RenderOptions = r };

            _irradianceFBO = new XRCubeFrameBuffer(irrMat);
            _prefilterFBO = new XRCubeFrameBuffer(prefMat);

            CachePreviewSphere();
        }

        public void InitializeStatic()
        {
            //Irradiance texture doesn't need to be very high quality, 
            //linear filtering on low resolution will do fine
            IrradianceTexture = new XRTextureCube(IrradianceResolution, EPixelInternalFormat.Rgb8, EPixelFormat.Rgb, EPixelType.UnsignedByte, false)
            {
                MinFilter = ETexMinFilter.Linear,
                MagFilter = ETexMagFilter.Linear,
                UWrap = ETexWrapMode.ClampToEdge,
                VWrap = ETexWrapMode.ClampToEdge,
                WWrap = ETexWrapMode.ClampToEdge,
                Resizable = false,
                SizedInternalFormat = ESizedInternalFormat.Rgb8,
                AutoGenerateMipmaps = false,
            };

            PrefilterTexture = new XRTextureCube(Resolution, EPixelInternalFormat.Rgb16f, EPixelFormat.Rgb, EPixelType.HalfFloat, false)
            {
                MinFilter = ETexMinFilter.LinearMipmapLinear,
                MagFilter = ETexMagFilter.Linear,
                UWrap = ETexWrapMode.ClampToEdge,
                VWrap = ETexWrapMode.ClampToEdge,
                WWrap = ETexWrapMode.ClampToEdge,
                Resizable = false,
                SizedInternalFormat = ESizedInternalFormat.Rgb16f,
                AutoGenerateMipmaps = false,
            };

            ShaderVar[] prefilterVars =
            [
                new ShaderFloat(0.0f, "Roughness"),
                new ShaderInt((int)Resolution, "CubemapDim"),
            ];

            XRShader irrShader = ShaderHelper.LoadEngineShader("Scene3D\\IrradianceConvolutionEquirect.fs");
            XRShader prefShader = ShaderHelper.LoadEngineShader("Scene3D\\PrefilterEquirect.fs");

            RenderingParameters r = new();
            r.DepthTest.Enabled = ERenderParamUsage.Disabled;
            XRTexture[] texArray = [_environmentTextureEquirect!];
            _irradianceFBO = new XRCubeFrameBuffer(new([], texArray, irrShader) { RenderOptions = r });
            _prefilterFBO = new XRCubeFrameBuffer(new(prefilterVars, texArray, prefShader) { RenderOptions = r });

            CachePreviewSphere();
        }

        private XRTexture2D? _environmentTextureEquirect;
        public XRTexture2D? EnvironmentTextureEquirect
        {
            get => _environmentTextureEquirect;
            set => SetField(ref _environmentTextureEquirect, value);
        }

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(EnvironmentTextureEquirect):
                    if (EnvironmentTextureEquirect is not null)
                        InitializeStatic();
                    break;
                case nameof(PreviewDisplay):
                    CachePreviewSphere();
                    break;
                case nameof(RealtimeCapture):
                    if (RealtimeCapture)
                        _realtimeCaptureTimer.StartMultiFire(QueueCapture, RealTimeCaptureUpdateInterval ?? TimeSpan.Zero);
                    else
                        _realtimeCaptureTimer.Cancel();
                    break;
                case nameof(RealTimeCaptureUpdateInterval):
                    _realtimeCaptureTimer.TimeBetweenFires = RealTimeCaptureUpdateInterval ?? TimeSpan.Zero;
                    break;
            }
        }

        private void GenerateIrradianceInternal()
        {
            if (IrradianceTexture is null)
                return;

            uint res = IrradianceTexture.Extent;
            AbstractRenderer.Current?.SetRenderArea(new BoundingRectangle(IVector2.Zero, new IVector2((int)res, (int)res)));
            for (int i = 0; i < 6; ++i)
            {
                _irradianceFBO!.SetRenderTargets((IrradianceTexture, EFrameBufferAttachment.ColorAttachment0, 0, i));
                using (_irradianceFBO!.BindForWritingState())
                {
                    Engine.Rendering.State.ClearByBoundFBO();
                    _irradianceFBO.RenderFullscreen(ECubemapFace.PosX + i);
                }
            }

            //Generate mipmaps from the first mip level we just generated
            IrradianceTexture.GenerateMipmapsGPU();
        }

        private void GeneratePrefilterInternal()
        {
            if (PrefilterTexture is null)
                return;

            int maxMipLevels = PrefilterTexture.SmallestMipmapLevel;
            int res = _prefilterFBO!.Material!.Parameter<ShaderInt>(1)!.Value;
            for (int mip = 0; mip < maxMipLevels; ++mip)
            {
                int mipWidth = (int)Math.Ceiling(res * Math.Pow(0.5, mip));
                int mipHeight = (int)Math.Ceiling(res * Math.Pow(0.5, mip));
                float roughness = (float)mip / (maxMipLevels - 1);

                _prefilterFBO.Material.SetFloat(0, roughness);
                _prefilterFBO.Material.SetInt(1, (int)Resolution);

                AbstractRenderer.Current?.SetRenderArea(new BoundingRectangle(IVector2.Zero, new IVector2(mipWidth, mipHeight)));
                for (int i = 0; i < 6; ++i)
                {
                    _prefilterFBO.SetRenderTargets((PrefilterTexture, EFrameBufferAttachment.ColorAttachment0, mip, i));
                    using (_prefilterFBO.BindForWritingState())
                    {
                        Engine.Rendering.State.ClearByBoundFBO();
                        _prefilterFBO.RenderFullscreen(ECubemapFace.PosX + i);
                    }
                }
            }
        }

        private void CachePreviewSphere()
        {
            PreviewSphere?.Destroy();

            int pass = (int)EDefaultRenderPass.OpaqueForward;
            var mesh = XRMesh.Shapes.SolidSphere(Vector3.Zero, 0.5f, 20u);
            var mat = new XRMaterial([GetPreviewTexture()], XRShader.EngineShader(GetPreviewShaderPath(), EShaderType.Fragment)) { RenderPass = pass };
            PreviewSphere = new XRMeshRenderer(mesh, mat);

            _visualRC.Mesh = PreviewSphere;
            _visualRC.WorldMatrix = Transform.RenderMatrix;
            _visualRC.RenderPass = pass;

            VisualRenderInfo.LocalCullingVolume = PreviewSphere?.Mesh?.Bounds;
            VisualRenderInfo.CullingOffsetMatrix = Transform.RenderMatrix;
        }

        protected internal override void OnComponentActivated()
        {
            base.OnComponentActivated();
            World?.Lights.LightProbes.Add(this);
            if (!RealtimeCapture)
            {
                ProgressiveRenderEnabled = false;
                FullCapture(128, false);
            }
        }

        protected internal override void OnComponentDeactivated()
        {
            base.OnComponentDeactivated();
            World?.Lights.LightProbes.Remove(this);
        }

        //private bool _capturing = false;
        //public override void CollectVisible()
        //{
        //    if (RealTimeCapture && RealTimeCaptureUpdateInterval is not null && (DateTime.Now - _lastUpdateTime < RealTimeCaptureUpdateInterval))
        //        return;
            
        //    _lastUpdateTime = DateTime.Now;
        //    _capturing = true;
        //    base.CollectVisible();
        //}
        //public override void SwapBuffers()
        //{
        //    if (_capturing)
        //        base.SwapBuffers();
        //}
        public override void Render()
        {
            //if (!_capturing)
            //    return;
            //_capturing = false;

            base.Render();
            GenerateIrradianceInternal();
            GeneratePrefilterInternal();
        }
    }
}
