using MIConvexHull;
using System.ComponentModel;
using System.Numerics;
using XREngine.Components;
using XREngine.Components.Lights;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Data.Vectors;
using XREngine.Rendering;
using XREngine.Components.Scene.Transforms;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;
using XREngine.Rendering.Models.Materials;
using XREngine.Scene.Transforms;
using XREngine.Timers;
using YamlDotNet.Serialization;

namespace XREngine.Components.Capture.Lights
{
    [XRComponentEditor("XREngine.Editor.ComponentEditors.LightProbeComponentEditor")]
    public class LightProbeComponent : SceneCaptureComponent, IRenderable, IVertex
    {
        #region Nested Types

        public enum ERenderPreview
        {
            Environment,
            Irradiance,
            Prefilter,
        }

        public enum EInfluenceShape
        {
            Sphere,
            Box,
        }

        /// <summary>
        /// HDR encoding format for baked/static probes. Dynamic captures use Rgb16f.
        /// </summary>
        public enum EHdrEncoding
        {
            /// <summary>Default full-precision half-float RGB.</summary>
            Rgb16f,
            /// <summary>RGBM encoding (RGB * M in alpha, 8-bit per channel).</summary>
            RGBM,
            /// <summary>RGBE shared-exponent encoding.</summary>
            RGBE,
            /// <summary>YCoCg color space encoding.</summary>
            YCoCg,
        }

        private readonly record struct FaceDebugInfo(string Name, ColorF4 Color);

        #endregion

        #region Constants

        private const uint OctahedralResolutionMultiplier = 2u;

        #endregion

        #region Static Fields

        private static XRShader? s_fullscreenTriVertexShader;

        private static readonly FaceDebugInfo[] s_faceDebugInfos =
        [
            new("+X", ColorF4.LightRed),
            new("-X", ColorF4.DarkRed),
            new("+Y", ColorF4.LightGreen),
            new("-Y", ColorF4.DarkGreen),
            new("+Z", ColorF4.LightBlue),
            new("-Z", ColorF4.DarkBlue),
        ];

        #endregion

        #region Instance Fields

        private readonly RenderCommandMesh3D _visualRC;
        private readonly RenderCommandMethod3D _debugAxesCommand;
        private readonly RenderCommandMethod3D _debugInfluenceCommand;
        private readonly GameTimer _realtimeCaptureTimer;

        private bool _parallaxCorrectionEnabled = false;
        private Vector3 _proxyBoxCenterOffset = Vector3.Zero;
        private Vector3 _proxyBoxHalfExtents = Vector3.One;
        private Quaternion _proxyBoxRotation = Quaternion.Identity;

        private EInfluenceShape _influenceShape = EInfluenceShape.Sphere;
        private Vector3 _influenceOffset = Vector3.Zero;
        private float _influenceSphereInnerRadius = 0.0f;
        private float _influenceSphereOuterRadius = 5.0f;
        private Vector3 _influenceBoxInnerExtents = Vector3.Zero;
        private Vector3 _influenceBoxOuterExtents = new(5.0f, 5.0f, 5.0f);

        // HDR encoding & normalization
        private EHdrEncoding _hdrEncoding = EHdrEncoding.Rgb16f;
        private bool _normalizedCubemap = false;
        private float _normalizationScale = 1.0f;

        // Mip streaming state
        private int _streamedMipLevel = 0;
        private int _targetMipLevel = 0;
        private bool _streamHighMipsOnDemand = false;

        private bool _autoShowPreviewOnSelect = true;
        private bool _renderInfluenceOnSelection = true;
        private bool _realtime = false;
        private TimeSpan? _realTimeUpdateInterval = TimeSpan.FromMilliseconds(100.0f);
        private uint _irradianceResolution = 32;
        private ERenderPreview _previewDisplay = ERenderPreview.Environment;

        private XRQuadFrameBuffer? _irradianceFBO;
        private XRQuadFrameBuffer? _prefilterFBO;
        private int _prefilterSourceDimension = 1;
        private XRTexture2D? _irradianceTexture;
        private XRTexture2D? _prefilterTexture;
        private XRMeshRenderer? _previewSphere;
        private XRTexture2D? _environmentTextureEquirect;

        private XRWorldInstance? _registeredWorld;

        #endregion

        #region Constructor

        public LightProbeComponent() : base()
        {
            _realtimeCaptureTimer = new GameTimer(this);
            _debugAxesCommand = new RenderCommandMethod3D(EDefaultRenderPass.OnTopForward, RenderCameraOrientationDebug)
            {
                Enabled = false,
            };
            _debugInfluenceCommand = new RenderCommandMethod3D(EDefaultRenderPass.OnTopForward, RenderVolumesDebug)
            {
                Enabled = false,
            };
            RenderedObjects =
            [
                VisualRenderInfo = RenderInfo3D.New(this, _visualRC = new RenderCommandMesh3D((int)EDefaultRenderPass.OpaqueForward)),
            ];
            VisualRenderInfo.Layer = DefaultLayers.GizmosIndex;
            VisualRenderInfo.RenderCommands.Add(_debugAxesCommand);
            VisualRenderInfo.RenderCommands.Add(_debugInfluenceCommand);
            VisualRenderInfo.PreCollectCommandsCallback += OnPreCollectRenderInfo;
        }

        #endregion

        #region IVertex Implementation

        double[] IVertex.Position => [Transform.WorldTranslation.X, Transform.WorldTranslation.Y, Transform.WorldTranslation.Z];

        #endregion

        #region IRenderable Properties

        public RenderInfo3D VisualRenderInfo { get; }
        public RenderInfo[] RenderedObjects { get; }

        #endregion

        #region Preview Properties

        public bool PreviewEnabled
        {
            get => VisualRenderInfo.IsVisible;
            set => VisualRenderInfo.IsVisible = value;
        }

        public bool AutoShowPreviewOnSelect
        {
            get => _autoShowPreviewOnSelect;
            set => SetField(ref _autoShowPreviewOnSelect, value);
        }

        public bool RenderInfluenceOnSelection
        {
            get => _renderInfluenceOnSelection;
            set => SetField(ref _renderInfluenceOnSelection, value);
        }

        public ERenderPreview PreviewDisplay
        {
            get => _previewDisplay;
            set => SetField(ref _previewDisplay, value);
        }

        [YamlIgnore]
        public XRMeshRenderer? PreviewSphere
        {
            get => _previewSphere;
            private set => SetField(ref _previewSphere, value);
        }

        #endregion

        #region Debug Properties

        [Category("Debug")]
        public bool RenderDebugAxesOnSelection
        {
            get => _debugAxesCommand.Enabled;
            set => _debugAxesCommand.Enabled = value;
        }

        #endregion

        #region Realtime Capture Properties

        public TimeSpan? StopRealtimeCaptureAfter
        {
            get => _realtimeCaptureTimer.StopMultiFireAfter;
            set => _realtimeCaptureTimer.StopMultiFireAfter = value;
        }

        /// <summary>
        /// If true, the light probe will update in real time.
        /// </summary>
        public bool RealtimeCapture
        {
            get => _realtime;
            set => SetField(ref _realtime, value);
        }

        public TimeSpan? RealTimeCaptureUpdateInterval
        {
            get => _realTimeUpdateInterval;
            set => SetField(ref _realTimeUpdateInterval, value);
        }

        #endregion

        #region IBL Resource Properties

        public uint IrradianceResolution
        {
            get => _irradianceResolution;
            set => SetField(ref _irradianceResolution, value);
        }

        public XRTexture2D? IrradianceTexture
        {
            get => _irradianceTexture;
            private set => SetField(ref _irradianceTexture, value);
        }

        public XRTexture2D? PrefilterTexture
        {
            get => _prefilterTexture;
            private set => SetField(ref _prefilterTexture, value);
        }

        public XRTexture2D? EnvironmentTextureEquirect
        {
            get => _environmentTextureEquirect;
            set => SetField(ref _environmentTextureEquirect, value);
        }

        [Browsable(false)]
        public uint CaptureVersion { get; private set; }

        #endregion

        #region Parallax Proxy Properties

        /// <summary>
        /// Enable parallax correction using the proxy box settings below.
        /// </summary>
        public bool ParallaxCorrectionEnabled
        {
            get => _parallaxCorrectionEnabled;
            set => SetField(ref _parallaxCorrectionEnabled, value);
        }

        /// <summary>
        /// Center of the proxy box relative to the probe origin.
        /// </summary>
        public Vector3 ProxyBoxCenterOffset
        {
            get => _proxyBoxCenterOffset;
            set => SetField(ref _proxyBoxCenterOffset, value);
        }

        /// <summary>
        /// Half extents of the proxy box. Must be positive.
        /// </summary>
        public Vector3 ProxyBoxHalfExtents
        {
            get => _proxyBoxHalfExtents;
            set => SetField(ref _proxyBoxHalfExtents, ClampHalfExtents(value));
        }

        /// <summary>
        /// Orientation of the proxy box in local space.
        /// </summary>
        public Quaternion ProxyBoxRotation
        {
            get => _proxyBoxRotation;
            set => SetField(ref _proxyBoxRotation, value);
        }

        #endregion

        #region Influence Volume Properties

        /// <summary>
        /// Shape used to weight contribution for blending.
        /// </summary>
        public EInfluenceShape InfluenceShape
        {
            get => _influenceShape;
            set => SetField(ref _influenceShape, value);
        }

        /// <summary>
        /// Optional offset for the influence volume relative to the probe origin.
        /// </summary>
        public Vector3 InfluenceOffset
        {
            get => _influenceOffset;
            set => SetField(ref _influenceOffset, value);
        }

        public float InfluenceSphereInnerRadius
        {
            get => _influenceSphereInnerRadius;
            set => SetField(ref _influenceSphereInnerRadius, ClampNonNegative(value, _influenceSphereOuterRadius));
        }

        public float InfluenceSphereOuterRadius
        {
            get => _influenceSphereOuterRadius;
            set
            {
                float clampedOuter = MathF.Max(value, _influenceSphereInnerRadius + float.Epsilon);
                if (SetField(ref _influenceSphereOuterRadius, clampedOuter))
                    _influenceSphereInnerRadius = ClampNonNegative(_influenceSphereInnerRadius, clampedOuter);
            }
        }

        public Vector3 InfluenceBoxInnerExtents
        {
            get => _influenceBoxInnerExtents;
            set => SetField(ref _influenceBoxInnerExtents, ClampBoxInnerExtents(value, _influenceBoxOuterExtents));
        }

        public Vector3 InfluenceBoxOuterExtents
        {
            get => _influenceBoxOuterExtents;
            set
            {
                Vector3 clampedOuter = ClampHalfExtents(value);
                if (SetField(ref _influenceBoxOuterExtents, clampedOuter))
                    _influenceBoxInnerExtents = ClampBoxInnerExtents(_influenceBoxInnerExtents, clampedOuter);
            }
        }

        #endregion

        #region HDR Encoding & Normalization Properties

        /// <summary>
        /// HDR encoding format for baked/static probes. Dynamic captures always use Rgb16f.
        /// </summary>
        public EHdrEncoding HdrEncoding
        {
            get => _hdrEncoding;
            set => SetField(ref _hdrEncoding, value);
        }

        /// <summary>
        /// When true, the cubemap is normalized (divided by average luminance) and
        /// NormalizationScale stores the original intensity. At runtime, multiply
        /// by diffuse ambient/SH to reuse the same probe in multiple locations.
        /// </summary>
        public bool NormalizedCubemap
        {
            get => _normalizedCubemap;
            set => SetField(ref _normalizedCubemap, value);
        }

        /// <summary>
        /// Scale factor to recover original intensity from a normalized cubemap.
        /// Set automatically during bake if NormalizedCubemap is enabled.
        /// </summary>
        public float NormalizationScale
        {
            get => _normalizationScale;
            set => SetField(ref _normalizationScale, MathF.Max(0.0001f, value));
        }

        #endregion

        #region Mip Streaming Properties

        /// <summary>
        /// When true, only low mips are loaded initially; high mips stream on demand
        /// when the probe enters view or becomes dominant in the blend set.
        /// </summary>
        public bool StreamHighMipsOnDemand
        {
            get => _streamHighMipsOnDemand;
            set => SetField(ref _streamHighMipsOnDemand, value);
        }

        /// <summary>
        /// Current highest-resolution mip level loaded (0 = full res).
        /// </summary>
        [Browsable(false)]
        public int StreamedMipLevel
        {
            get => _streamedMipLevel;
            private set => SetField(ref _streamedMipLevel, value);
        }

        /// <summary>
        /// Target mip level requested by the blending system (0 = full res).
        /// </summary>
        [Browsable(false)]
        public int TargetMipLevel
        {
            get => _targetMipLevel;
            set => SetField(ref _targetMipLevel, Math.Max(0, value));
        }

        /// <summary>
        /// Request streaming to a specific mip level (0 = highest detail).
        /// </summary>
        public void RequestMipLevel(int level)
        {
            TargetMipLevel = level;
            // TODO: Queue async mip upload when streaming is implemented
        }

        #endregion

        #region Component Lifecycle

        protected internal override void OnComponentActivated()
        {
            base.OnComponentActivated();

            var world = World;
            if (world is not null)
            {
                if (_registeredWorld is not null && _registeredWorld != world)
                    _registeredWorld.Lights.RemoveLightProbe(this);

                world.Lights.AddLightProbe(this);
                _registeredWorld = world;
            }
            if (!RealtimeCapture)
            {
                ProgressiveRenderEnabled = false;
                FullCapture(128, false);
            }
        }

        protected internal override void OnComponentDeactivated()
        {
            base.OnComponentDeactivated();

            if (_registeredWorld is not null)
            {
                _registeredWorld.Lights.RemoveLightProbe(this);
                _registeredWorld = null;
            }

            DestroyIblResources();
        }

        // ...existing code...

        #endregion

        #region Property Change Handling

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

        #endregion

        #region Transform Handling

        protected override void OnTransformRenderWorldMatrixChanged(TransformBase transform, Matrix4x4 renderMatrix)
        {
            _visualRC.WorldMatrix = renderMatrix;
            base.OnTransformRenderWorldMatrixChanged(transform, renderMatrix);
        }

        #endregion

        #region Capture and IBL Methods

        protected override void InitializeForCapture()
        {
            base.InitializeForCapture();

            if (EnvironmentTextureOctahedral is null)
                return;

            InitializeIblResources(
                EnvironmentTextureOctahedral,
                "Scene3D\\IrradianceConvolutionOcta.fs",
                "Scene3D\\PrefilterOcta.fs",
                (int)Math.Max(1u, Resolution),
                GetOctaExtent(IrradianceResolution),
                GetOctaExtent(Resolution));
        }

        public void InitializeStatic()
        {
            if (EnvironmentTextureEquirect is null)
                return;

            InitializeIblResources(
                EnvironmentTextureEquirect,
                "Scene3D\\IrradianceConvolutionEquirectOcta.fs",
                "Scene3D\\PrefilterEquirectOcta.fs",
                (int)Math.Max(EnvironmentTextureEquirect.Width, EnvironmentTextureEquirect.Height),
                GetOctaExtent(IrradianceResolution),
                GetOctaExtent(Resolution));
        }

        private void InitializeIblResources(
            XRTexture sourceTexture,
            string irradianceShaderPath,
            string prefilterShaderPath,
            int sourceDimension,
            uint irradianceExtent,
            uint prefilterExtent)
        {
            DestroyIblResources();

            IrradianceTexture = CreateIrradianceTexture(irradianceExtent);
            PrefilterTexture = CreatePrefilterTexture(prefilterExtent);

            ShaderVar[] prefilterVars = CreatePrefilterShaderVars(sourceDimension);
            _prefilterSourceDimension = Math.Max(1, sourceDimension);

            RenderingParameters renderParams = new();
            renderParams.DepthTest.Enabled = ERenderParamUsage.Disabled;

            XRShader fullscreenVertex = GetFullscreenTriVertexShader();
            XRShader irradianceFragment = ShaderHelper.LoadEngineShader(irradianceShaderPath, EShaderType.Fragment);
            XRShader prefilterFragment = ShaderHelper.LoadEngineShader(prefilterShaderPath, EShaderType.Fragment);

            XRMaterial irradianceMaterial = new([], [sourceTexture], fullscreenVertex, irradianceFragment)
            {
                RenderOptions = renderParams,
            };

            XRMaterial prefilterMaterial = new(prefilterVars, [sourceTexture], fullscreenVertex, prefilterFragment)
            {
                RenderOptions = renderParams,
            };

            _irradianceFBO = new XRQuadFrameBuffer(irradianceMaterial);
            _irradianceFBO.SetRenderTargets((IrradianceTexture!, EFrameBufferAttachment.ColorAttachment0, 0, -1));

            _prefilterFBO = new XRQuadFrameBuffer(prefilterMaterial);
            _prefilterFBO.SetRenderTargets((PrefilterTexture!, EFrameBufferAttachment.ColorAttachment0, 0, -1));

            CachePreviewSphere();
        }

        private void DestroyIblResources()
        {
            _irradianceFBO?.Destroy();
            _irradianceFBO = null;
            _prefilterFBO?.Destroy();
            _prefilterFBO = null;

            IrradianceTexture?.Destroy();
            IrradianceTexture = null;
            PrefilterTexture?.Destroy();
            PrefilterTexture = null;
        }

        private void GenerateIrradianceInternal()
        {
            if (_irradianceFBO is null || IrradianceTexture is null)
                return;

            int width = (int)Math.Max(1u, IrradianceTexture.Width);
            int height = (int)Math.Max(1u, IrradianceTexture.Height);
            AbstractRenderer.Current?.SetRenderArea(new BoundingRectangle(IVector2.Zero, new IVector2(width, height)));

            using (_irradianceFBO.BindForWritingState())
            {
                Engine.Rendering.State.ClearByBoundFBO();
                _irradianceFBO.Render(null, true);
            }

            IrradianceTexture.GenerateMipmapsGPU();
        }

        private void GeneratePrefilterInternal()
        {
            if (_prefilterFBO is null || PrefilterTexture is null)
                return;

            int baseExtent = (int)Math.Max(PrefilterTexture.Width, PrefilterTexture.Height);
            int maxMipLevels = PrefilterTexture.SmallestMipmapLevel;
            for (int mip = 0; mip < maxMipLevels; ++mip)
            {
                int mipWidth = Math.Max(1, baseExtent >> mip);
                int mipHeight = Math.Max(1, baseExtent >> mip);
                float roughness = maxMipLevels <= 1 ? 0.0f : (float)mip / (maxMipLevels - 1);

                _prefilterFBO.Material?.SetFloat(0, roughness);
                _prefilterFBO.Material?.SetInt(1, _prefilterSourceDimension);

                _prefilterFBO.SetRenderTargets((PrefilterTexture, EFrameBufferAttachment.ColorAttachment0, mip, -1));
                AbstractRenderer.Current?.SetRenderArea(new BoundingRectangle(IVector2.Zero, new IVector2(mipWidth, mipHeight)));

                using (_prefilterFBO.BindForWritingState())
                {
                    Engine.Rendering.State.ClearByBoundFBO();
                    _prefilterFBO.Render(null, true);
                }
            }
        }

        public override void Render()
        {
            Engine.Rendering.State.IsLightProbePass = true;

            base.Render();
            GenerateIrradianceInternal();
            GeneratePrefilterInternal();
            CaptureVersion++;

            Engine.Rendering.State.IsLightProbePass = false;
        }

        #endregion

        #region Static Helper Methods

        private static ShaderVar[] CreatePrefilterShaderVars(int sourceDimension)
            =>
            [
                new ShaderFloat(0.0f, "Roughness"),
                new ShaderInt(Math.Max(1, sourceDimension), "SourceDim"),
            ];

        private static Vector3 ClampHalfExtents(Vector3 extents)
            => new(
                MathF.Max(0.0001f, MathF.Abs(extents.X)),
                MathF.Max(0.0001f, MathF.Abs(extents.Y)),
                MathF.Max(0.0001f, MathF.Abs(extents.Z)));

        private static Vector3 ClampBoxInnerExtents(Vector3 inner, Vector3 outer)
            => new(
                MathF.Max(0.0f, MathF.Min(MathF.Abs(inner.X), outer.X)),
                MathF.Max(0.0f, MathF.Min(MathF.Abs(inner.Y), outer.Y)),
                MathF.Max(0.0f, MathF.Min(MathF.Abs(inner.Z), outer.Z)));

        private static float ClampNonNegative(float value, float maxInclusive)
            => MathF.Max(0.0f, MathF.Min(value, maxInclusive));

        private static uint GetOctaExtent(uint baseResolution)
            => Math.Max(1u, baseResolution * OctahedralResolutionMultiplier);

        private static XRTexture2D CreateIrradianceTexture(uint extent)
            => new(extent, extent, EPixelInternalFormat.Rgb8, EPixelFormat.Rgb, EPixelType.UnsignedByte, false)
            {
                MinFilter = ETexMinFilter.Linear,
                MagFilter = ETexMagFilter.Linear,
                UWrap = ETexWrapMode.ClampToEdge,
                VWrap = ETexWrapMode.ClampToEdge,
                Resizable = false,
                SizedInternalFormat = ESizedInternalFormat.Rgb8,
                AutoGenerateMipmaps = false,
                Name = "LightProbeIrradianceOcta",
            };

        private static XRTexture2D CreatePrefilterTexture(uint extent)
            => new(extent, extent, EPixelInternalFormat.Rgb16f, EPixelFormat.Rgb, EPixelType.HalfFloat, false)
            {
                MinFilter = ETexMinFilter.LinearMipmapLinear,
                MagFilter = ETexMagFilter.Linear,
                UWrap = ETexWrapMode.ClampToEdge,
                VWrap = ETexWrapMode.ClampToEdge,
                Resizable = false,
                SizedInternalFormat = ESizedInternalFormat.Rgb16f,
                AutoGenerateMipmaps = false,
                Name = "LightProbePrefilterOcta",
            };

        private static XRShader GetFullscreenTriVertexShader()
            => s_fullscreenTriVertexShader ??= ShaderHelper.LoadEngineShader("Scene3D\\FullscreenTri.vs", EShaderType.Vertex);

        #endregion

        #region Preview Methods

        public XRTexture? GetPreviewTexture()
            => PreviewDisplay switch
            {
                ERenderPreview.Irradiance => IrradianceTexture,
                ERenderPreview.Prefilter => PrefilterTexture,
                _ => EnvironmentTextureOctahedral ?? _environmentTextureEquirect,
            };

        public string GetPreviewShaderPath()
            => PreviewDisplay switch
            {
                ERenderPreview.Irradiance or ERenderPreview.Prefilter => "Scene3D\\OctahedralEnv.fs",
                _ => EnvironmentTextureOctahedral is not null
                    ? "Scene3D\\OctahedralEnv.fs"
                    : "Scene3D\\Equirect.fs",
            };

        private bool OnPreCollectRenderInfo(RenderInfo info, RenderCommandCollection passes, XRCamera? camera)
        {
            if (camera != null && !camera.CullingMask.Contains(DefaultLayers.GizmosIndex))
                return false;
            if (AutoShowPreviewOnSelect)
                PreviewEnabled = IsSceneNodeSelected();
            _debugInfluenceCommand.Enabled = RenderInfluenceOnSelection && IsSceneNodeSelected();
            return true;
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

        private bool IsSceneNodeSelected()
            => EditorSelectionAccessor.Instance.Value?.IsNodeSelected(SceneNode) ?? false;

        #endregion

        #region Debug Rendering Methods

        private void RenderCameraOrientationDebug()
        {
            using var prof = Engine.Profiler.Start("LightProbeComponent.RenderCameraOrientationDebug");

            const float forwardOffset = 0.7f;
            const float frameHalfExtent = 0.18f;
            const float frameInset = 0.02f;
            const float labelLift = 0.25f;
            const float axisLength = 0.32f;
            const float axisOffset = 0.08f;
            const float arrowSize = 0.08f;

            for (int i = 0; i < Viewports.Length; ++i)
            {
                var viewport = Viewports[i];
                var transform = viewport?.Camera?.Transform;
                if (transform is null)
                    continue;

                Vector3 cameraOrigin = transform.RenderTranslation + transform.RenderForward * forwardOffset;
                FaceDebugInfo faceInfo = i < s_faceDebugInfos.Length ? s_faceDebugInfos[i] : s_faceDebugInfos[^1];

                RenderFaceFrame(cameraOrigin, transform, frameHalfExtent, frameInset, faceInfo.Color);
                Engine.Rendering.Debug.RenderText(
                    cameraOrigin + transform.RenderUp * labelLift,
                    $"{faceInfo.Name} face",
                    faceInfo.Color);

                RenderAxisPair(cameraOrigin, transform.RenderRight, ColorF4.Red, "+X", "-X", axisLength, axisOffset, arrowSize);
                RenderAxisPair(cameraOrigin, transform.RenderUp, ColorF4.Green, "+Y", "-Y", axisLength, axisOffset, arrowSize);
                RenderAxisPair(cameraOrigin, transform.RenderForward, ColorF4.Blue, "-Z", "+Z", axisLength, axisOffset, arrowSize);
            }
        }

        private void RenderVolumesDebug()
        {
            using var prof = Engine.Profiler.Start("LightProbeComponent.RenderVolumesDebug");

            Vector3 probeOrigin = Transform.RenderTranslation;

            // Influence volume visualization
            Vector3 influenceCenter = probeOrigin + InfluenceOffset;
            const float alpha = 0.35f;
            ColorF4 outerColor = new(0.35f, 0.75f, 1.0f, alpha);
            ColorF4 innerColor = new(0.65f, 1.0f, 0.85f, alpha * 0.9f);

            if (InfluenceShape == EInfluenceShape.Sphere)
            {
                Engine.Rendering.Debug.RenderSphere(influenceCenter, InfluenceSphereOuterRadius, false, outerColor);
                if (InfluenceSphereInnerRadius > 0.0001f)
                    Engine.Rendering.Debug.RenderSphere(influenceCenter, InfluenceSphereInnerRadius, false, innerColor);
            }
            else
            {
                Matrix4x4 influenceTransform = Matrix4x4.Identity;
                Engine.Rendering.Debug.RenderBox(InfluenceBoxOuterExtents, influenceCenter, influenceTransform, false, outerColor);
                Engine.Rendering.Debug.RenderBox(InfluenceBoxInnerExtents, influenceCenter, influenceTransform, false, innerColor);
            }

            // Proxy box visualization (parallax volume)
            ColorF4 proxyColor = new(1.0f, 0.6f, 0.2f, alpha);
            Matrix4x4 proxyRotation = Matrix4x4.CreateFromQuaternion(ProxyBoxRotation);
            Vector3 proxyCenter = probeOrigin + ProxyBoxCenterOffset;
            Engine.Rendering.Debug.RenderBox(ProxyBoxHalfExtents, proxyCenter, proxyRotation, false, proxyColor);
        }

        private static void RenderAxisPair(
            Vector3 origin,
            Vector3 direction,
            ColorF4 color,
            string positiveLabel,
            string negativeLabel,
            float length,
            float offset,
            float arrowSize)
        {
            if (direction.LengthSquared() <= float.Epsilon)
                return;

            Vector3 normalized = Vector3.Normalize(direction);
            Vector3 positiveStart = origin + normalized * offset;
            Vector3 positiveEnd = origin + normalized * (offset + length);
            RenderArrow(positiveStart, positiveEnd, normalized, color, arrowSize);
            Engine.Rendering.Debug.RenderText(positiveEnd + normalized * (offset * 0.5f), positiveLabel, color);

            Vector3 negDir = -normalized;
            Vector3 negativeStart = origin + negDir * offset;
            Vector3 negativeEnd = origin + negDir * (offset + length);
            var negativeColor = color * 0.7f;
            RenderArrow(negativeStart, negativeEnd, negDir, negativeColor, arrowSize * 0.75f);
            Engine.Rendering.Debug.RenderText(negativeEnd + negDir * (offset * 0.5f), negativeLabel, negativeColor);
        }

        private static void RenderArrow(Vector3 start, Vector3 end, Vector3 direction, ColorF4 color, float arrowSize)
        {
            Engine.Rendering.Debug.RenderLine(start, end, color);

            Vector3 dirNorm = Vector3.Normalize(direction);
            Vector3 ortho = Math.Abs(Vector3.Dot(dirNorm, Vector3.UnitY)) > 0.9f
                ? Vector3.Normalize(Vector3.Cross(dirNorm, Vector3.UnitX))
                : Vector3.Normalize(Vector3.Cross(dirNorm, Vector3.UnitY));
            Vector3 ortho2 = Vector3.Normalize(Vector3.Cross(dirNorm, ortho));

            Vector3 headBase = end - dirNorm * arrowSize;
            Vector3 wingA = headBase + ortho * arrowSize * 0.5f;
            Vector3 wingB = headBase - ortho * arrowSize * 0.5f;
            Vector3 wingC = headBase + ortho2 * arrowSize * 0.5f;
            Vector3 wingD = headBase - ortho2 * arrowSize * 0.5f;

            Engine.Rendering.Debug.RenderLine(end, wingA, color);
            Engine.Rendering.Debug.RenderLine(end, wingB, color);
            Engine.Rendering.Debug.RenderLine(end, wingC, color);
            Engine.Rendering.Debug.RenderLine(end, wingD, color);
        }

        private static void RenderFaceFrame(Vector3 origin, TransformBase transform, float halfExtent, float inset, ColorF4 color)
        {
            Vector3 right = Vector3.Normalize(transform.RenderRight);
            Vector3 up = Vector3.Normalize(transform.RenderUp);
            Vector3 forward = Vector3.Normalize(transform.RenderForward);

            Vector3 topLeft = origin + (-right * halfExtent) + (up * halfExtent);
            Vector3 topRight = origin + (right * halfExtent) + (up * halfExtent);
            Vector3 bottomLeft = origin + (-right * halfExtent) - (up * halfExtent);
            Vector3 bottomRight = origin + (right * halfExtent) - (up * halfExtent);

            Engine.Rendering.Debug.RenderLine(topLeft, topRight, color);
            Engine.Rendering.Debug.RenderLine(topRight, bottomRight, color);
            Engine.Rendering.Debug.RenderLine(bottomRight, bottomLeft, color);
            Engine.Rendering.Debug.RenderLine(bottomLeft, topLeft, color);

            Vector3 crossHorizontalStart = origin + (-right * (halfExtent - inset));
            Vector3 crossHorizontalEnd = origin + (right * (halfExtent - inset));
            Vector3 crossVerticalStart = origin + (-up * (halfExtent - inset));
            Vector3 crossVerticalEnd = origin + (up * (halfExtent - inset));

            Engine.Rendering.Debug.RenderLine(crossHorizontalStart, crossHorizontalEnd, color * 0.9f);
            Engine.Rendering.Debug.RenderLine(crossVerticalStart, crossVerticalEnd, color * 0.9f);

            Engine.Rendering.Debug.RenderLine(origin, origin + forward * inset, color);
        }

        #endregion
    }
}
