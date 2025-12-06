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
        private readonly GameTimer _realtimeCaptureTimer;

        private bool _autoShowPreviewOnSelect = true;
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

        #endregion

        #region Constructor

        public LightProbeComponent() : base()
        {
            _realtimeCaptureTimer = new GameTimer(this);
            _debugAxesCommand = new RenderCommandMethod3D(EDefaultRenderPass.OnTopForward, RenderCameraOrientationDebug)
            {
                Enabled = false,
            };
            RenderedObjects =
            [
                VisualRenderInfo = RenderInfo3D.New(this, _visualRC = new RenderCommandMesh3D((int)EDefaultRenderPass.OpaqueForward)),
            ];
            VisualRenderInfo.RenderCommands.Add(_debugAxesCommand);
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

        #region Component Lifecycle

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
            if (AutoShowPreviewOnSelect)
                PreviewEnabled = IsSceneNodeSelected();
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
