using System.ComponentModel;
using System.Numerics;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using YamlDotNet.Serialization;

namespace XREngine.Components.Capture.Lights
{
    public partial class LightProbeComponent
    {
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

        public bool AutoCaptureOnActivate
        {
            get => _autoCaptureOnActivate;
            set => SetField(ref _autoCaptureOnActivate, value);
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

        /// <summary>
        /// When true, dynamic captures skip the environment octahedral intermediate and
        /// generate the final irradiance/prefilter octa textures directly from the captured cubemap.
        /// </summary>
        public bool UseDirectCubemapIblGeneration
        {
            get => _useDirectCubemapIblGeneration;
            set => SetField(ref _useDirectCubemapIblGeneration, value);
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
    }
}
