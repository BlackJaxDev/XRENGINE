using XREngine.Extensions;
using System.Numerics;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.X86;
using XREngine.Data;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;

namespace XREngine.Rendering
{
    public class ColorGradingSettings : PostProcessSettings
    {
        public const string ColorGradeUniformName = "ColorGrade";

        public enum ExposureControlMode
        {
            Artist = 0,
            Physical = 1,
        }

        public enum AutoExposureMeteringMode
        {
            /// <summary>
            /// Current behavior: sample the 1x1 (smallest) mip.
            /// Fast but can be dominated by bright sky/highlights.
            /// </summary>
            Average = 0,

            /// <summary>
            /// Samples a small mip and computes geometric-mean luminance.
            /// More stable across bright highlights.
            /// </summary>
            LogAverage = 1,

            /// <summary>
            /// Samples a small mip and weights toward the screen center.
            /// Useful when skybox dominates the edges.
            /// </summary>
            CenterWeighted = 2,

            /// <summary>
            /// Samples a small mip and ignores the brightest X% of samples.
            /// Helps avoid a small bright region forcing the whole scene dark.
            /// </summary>
            IgnoreTopPercent = 3,
        }

        public ColorGradingSettings()
        {
            Contrast = 1.0f;
        }

        private ExposureControlMode _exposureMode = ExposureControlMode.Artist;
        private float _physicalApertureFNumber = 2.8f;
        private float _physicalShutterSpeedSeconds = 1.0f / 60.0f;
        private float _physicalIso = 100.0f;
        private float _physicalExposureCompensationEV = 0.0f;
        private float _physicalExposureScale = 1.0f;

        private float _contrast = 0.0f;
        private float _contrastUniformValue;
        private float _exposureTransitionSpeed = 0.5f;
        private ColorF3 _tint = new(1.0f, 1.0f, 1.0f);
        private bool _autoExposure = true;
        private bool _independentEyeExposure;
        private float _autoExposureBias = 0.0f;
        private float _autoExposureScale = 1.0f;
        private float _exposureDividend = 0.1f;
        private float _minExposure = 0.0001f;
        private float _maxExposure = 100.0f;

        private AutoExposureMeteringMode _autoExposureMetering = AutoExposureMeteringMode.LogAverage;
        private int _autoExposureMeteringTargetSize = 16;
        private float _autoExposureIgnoreTopPercent = 0.02f;
        private float _autoExposureCenterWeightStrength = 1.0f;
        private float _autoExposureCenterWeightPower = 2.0f;

        private Vector3 _autoExposureLuminanceWeights = NormalizeLuminanceWeights(RuntimeEngine.Rendering.Settings.DefaultLuminance);

        private float _exposure = 1.0f;
        private float _gamma = 2.2f;
        private float _hue = 1.0f;
        private float _saturation = 1.0f;
        private float _brightness = 1.0f;

        private const float DefaultExposure = 1.0f;
        private const float DefaultMinExposure = 0.0001f;
        private const float DefaultMaxExposure = 100.0f;

        public ExposureControlMode ExposureMode
        {
            get => _exposureMode;
            set => SetField(ref _exposureMode, value);
        }

        /// <summary>
        /// Physical aperture in f-stops (e.g. 1.4, 2.8, 5.6).
        /// Used when <see cref="ExposureMode"/> is <see cref="ExposureControlMode.Physical"/>.
        /// </summary>
        public float PhysicalApertureFNumber
        {
            get => _physicalApertureFNumber;
            set => SetField(ref _physicalApertureFNumber, MathF.Max(0.1f, value));
        }

        /// <summary>
        /// Physical shutter speed in seconds (e.g. 0.0167 for 1/60s).
        /// Used when <see cref="ExposureMode"/> is <see cref="ExposureControlMode.Physical"/>.
        /// </summary>
        public float PhysicalShutterSpeedSeconds
        {
            get => _physicalShutterSpeedSeconds;
            set => SetField(ref _physicalShutterSpeedSeconds, MathF.Max(0.000001f, value));
        }

        /// <summary>
        /// Physical ISO sensitivity (e.g. 100, 400, 1600).
        /// Used when <see cref="ExposureMode"/> is <see cref="ExposureControlMode.Physical"/>.
        /// </summary>
        public float PhysicalISO
        {
            get => _physicalIso;
            set => SetField(ref _physicalIso, MathF.Max(1.0f, value));
        }

        /// <summary>
        /// Exposure compensation in EV (positive = brighter).
        /// Used when <see cref="ExposureMode"/> is <see cref="ExposureControlMode.Physical"/>.
        /// </summary>
        public float PhysicalExposureCompensationEV
        {
            get => _physicalExposureCompensationEV;
            set => SetField(ref _physicalExposureCompensationEV, value);
        }

        /// <summary>
        /// Scale factor to map the engine's lighting units into a photographic exposure scale.
        /// This is intentionally left as a tunable parameter.
        /// Used when <see cref="ExposureMode"/> is <see cref="ExposureControlMode.Physical"/>.
        /// </summary>
        public float PhysicalExposureScale
        {
            get => _physicalExposureScale;
            set => SetField(ref _physicalExposureScale, MathF.Max(0.0f, value));
        }

        public ColorF3 Tint
        {
            get => _tint;
            set => SetField(ref _tint, value);
        }
        public bool AutoExposure
        {
            get => _autoExposure;
            set => SetField(ref _autoExposure, value);
        }

        /// <summary>
        /// Selects how auto exposure adapts on a pair of two-pass VR eyes (OpenXR or OpenVR, one
        /// pipeline per eye). False (default): one headset exposure. The left eye meters its own
        /// image, and the right eye uses the left eye's adapted value. True: each eye meters its own
        /// image and adapts on its own, as real eyes do, so the eyes can differ in brightness.
        /// The two eyes always share all other settings, and each eye keeps its own adaptation
        /// state in both modes. Desktop views, mirrors and single-pass stereo ignore this setting.
        /// </summary>
        public bool IndependentEyeExposure
        {
            get => _independentEyeExposure;
            set => SetField(ref _independentEyeExposure, value);
        }
        public float AutoExposureBias
        {
            get => _autoExposureBias;
            set => SetField(ref _autoExposureBias, value);
        }
        public float AutoExposureScale
        {
            get => _autoExposureScale;
            set => SetField(ref _autoExposureScale, value);
        }
        public float ExposureDividend
        {
            get => _exposureDividend;
            set => SetField(ref _exposureDividend, value);
        }
        public float MinExposure
        {
            get => _minExposure;
            set => SetField(ref _minExposure, float.IsFinite(value) ? value : DefaultMinExposure);
        }
        public float MaxExposure
        {
            get => _maxExposure;
            set => SetField(ref _maxExposure, float.IsFinite(value) ? value : DefaultMaxExposure);
        }

        internal void GetResolvedExposureBounds(out float minExposure, out float maxExposure)
        {
            float min = float.IsFinite(MinExposure) ? MinExposure : DefaultMinExposure;
            float max = float.IsFinite(MaxExposure) ? MaxExposure : DefaultMaxExposure;
            min = MathF.Max(min, 0.0f);
            max = MathF.Max(max, 0.0f);

            minExposure = MathF.Min(min, max);
            maxExposure = MathF.Max(min, max);
        }

        internal float ClampExposureToResolvedBounds(float exposure)
        {
            GetResolvedExposureBounds(out float minExposure, out float maxExposure);
            if (!float.IsFinite(exposure))
                exposure = DefaultExposure;
            return Math.Clamp(exposure, minExposure, maxExposure);
        }

        public AutoExposureMeteringMode AutoExposureMetering
        {
            get => _autoExposureMetering;
            set => SetField(ref _autoExposureMetering, value);
        }

        /// <summary>
        /// Target maximum dimension (in texels) for the mip used by advanced metering modes.
        /// Clamped to [1, 64].
        /// </summary>
        public int AutoExposureMeteringTargetSize
        {
            get => _autoExposureMeteringTargetSize;
            set => SetField(ref _autoExposureMeteringTargetSize, Math.Clamp(value, 1, 64));
        }

        /// <summary>
        /// For IgnoreTopPercent metering: fraction of brightest samples to drop.
        /// Clamped to [0, 0.5].
        /// </summary>
        public float AutoExposureIgnoreTopPercent
        {
            get => _autoExposureIgnoreTopPercent;
            set => SetField(ref _autoExposureIgnoreTopPercent, value.Clamp(0.0f, 0.5f));
        }

        /// <summary>
        /// For CenterWeighted metering: 0 = uniform, 1 = fully center-weighted.
        /// </summary>
        public float AutoExposureCenterWeightStrength
        {
            get => _autoExposureCenterWeightStrength;
            set => SetField(ref _autoExposureCenterWeightStrength, value.Clamp(0.0f, 1.0f));
        }

        /// <summary>
        /// For CenterWeighted metering: power of the radial falloff curve.
        /// </summary>
        public float AutoExposureCenterWeightPower
        {
            get => _autoExposureCenterWeightPower;
            set => SetField(ref _autoExposureCenterWeightPower, MathF.Max(0.1f, value));
        }

        /// <summary>
        /// Luminance weights used by auto exposure (dot(rgb, weights)).
        /// Values are sanitized to be finite, non-negative, and normalized to sum to 1.
        /// </summary>
        public Vector3 AutoExposureLuminanceWeights
        {
            get => _autoExposureLuminanceWeights;
            set => SetField(ref _autoExposureLuminanceWeights, NormalizeLuminanceWeights(value));
        }
        public float ExposureTransitionSpeed
        {
            get => _exposureTransitionSpeed;
            set => SetField(ref _exposureTransitionSpeed, value.Clamp(0.0f, 1.0f));
        }
        public float Exposure
        {
            get => _exposure;
            set => SetField(ref _exposure, ClampExposureToResolvedBounds(value));
        }
        public float Contrast
        {
            get => _contrast;
            set => SetField(ref _contrast, value);
        }
        public float Gamma
        {
            get => _gamma;
            set => SetField(ref _gamma, MathF.Max(0.1f, value));
        }
        public float Hue
        {
            get => _hue;
            set => SetField(ref _hue, value);
        }
        public float Saturation
        {
            get => _saturation;
            set => SetField(ref _saturation, value);
        }
        public float Brightness
        {
            get => _brightness;
            set => SetField(ref _brightness, value);
        }

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(Contrast):
                    _contrastUniformValue = (100.0f + _contrast) / 100.0f;
                    _contrastUniformValue *= _contrastUniformValue;
                    break;
            }
        }

        public override void SetUniforms(XRRenderProgram program)
        {
            program.Uniform($"{ColorGradeUniformName}.{nameof(Tint)}", Tint);

            AutoExposureRuntimeState runtime = ResolveAutoExposureState(null);
            // A paired VR eye keeps its CPU-adapted exposure in its own runtime state.
            float exposure = ClampExposureToResolvedBounds(
                AutoExposure && runtime.HasAdaptedExposure ? runtime.AdaptedExposure : Exposure);
            bool useGpuExposure = AutoExposure && runtime.GpuReadyThisFrame;
            if (ExposureMode == ExposureControlMode.Physical)
            {
                float physicalBase = ClampExposureToResolvedBounds(ComputePhysicalExposureMultiplier());
                if (!AutoExposure)
                {
                    exposure = physicalBase;
                    useGpuExposure = false;
                }
                else if (useGpuExposure)
                {
                    // GPU path computes the full absolute exposure; this is a fallback until the texture is ready.
                    exposure = physicalBase;
                }
                else
                {
                    // CPU path stores the current absolute exposure in Exposure.
                    exposure = runtime.LastUpdateTime == float.MinValue
                        ? physicalBase
                        : ClampExposureToResolvedBounds(runtime.HasAdaptedExposure ? runtime.AdaptedExposure : Exposure);
                }
            }

            exposure = ClampExposureToResolvedBounds(exposure);

            program.Uniform($"{ColorGradeUniformName}.{nameof(Exposure)}", exposure);
            program.Uniform($"{ColorGradeUniformName}.{nameof(Contrast)}", _contrastUniformValue);
            program.Uniform($"{ColorGradeUniformName}.{nameof(Gamma)}", Gamma);
            program.Uniform($"{ColorGradeUniformName}.{nameof(Hue)}", Hue);
            program.Uniform($"{ColorGradeUniformName}.{nameof(Saturation)}", Saturation);
            program.Uniform($"{ColorGradeUniformName}.{nameof(Brightness)}", Brightness);

            // Only tell the shader to sample the 1x1 GPU exposure texture when it was
            // successfully written this frame.  Otherwise the texture may contain stale or
            // uninitialised data (common after resize / FBO recreation).
            program.Uniform("UseGpuAutoExposure", useGpuExposure);
        }

        internal static string WriteShaderSetup()
        {
            return @"
struct ColorGradeStruct
{
    vec3 Tint;

    float Exposure;
    float Contrast;
    float Gamma;

    float Hue;
    float Saturation;
    float Brightness;
};
uniform ColorGradeStruct ColorGrade;";
        }

        [Browsable(false)]
        public bool RequiresAutoExposure => AutoExposure;

        internal float ComputePhysicalExposureMultiplier()
        {
            float n = MathF.Max(0.1f, PhysicalApertureFNumber);
            float t = MathF.Max(0.000001f, PhysicalShutterSpeedSeconds);
            float iso = MathF.Max(1.0f, PhysicalISO);

            // EV100 = log2(N^2 / t * 100/ISO)
            float ev100 = MathF.Log2((n * n / t) * (100.0f / iso));
            float ev = ev100 + PhysicalExposureCompensationEV;

            // Convert EV to a linear exposure multiplier used by the tonemapper.
            // Higher EV => darker image.
            float multiplier = MathF.Pow(2.0f, -ev);
            float exposure = PhysicalExposureScale * multiplier;
            return float.IsFinite(exposure) && exposure > 0.0f ? exposure : DefaultExposure;
        }

        private float _secBetweenExposureUpdates = 1.0f;
        public float SecondsBetweenExposureUpdates
        {
            get => _secBetweenExposureUpdates;
            set => SetField(ref _secBetweenExposureUpdates, value);
        }

        internal const float MaxGpuAutoExposureDeltaSeconds = 1.0f / 15.0f;

        // Adaptation state per render pipeline instance. The two VR eyes share this settings
        // object but render with two pipeline instances, so each eye gets its own entry. The table
        // holds the instances weakly; an entry is created once per instance.
        private readonly ConditionalWeakTable<XRRenderPipelineInstance, AutoExposureRuntimeState> _autoExposureStates = new();
        // State for calls made outside any pipeline instance.
        private readonly AutoExposureRuntimeState _detachedAutoExposureState = new();

        /// <summary>
        /// Returns the adaptation state of this settings object for one pipeline instance. Null
        /// selects the instance that renders on this thread (<c>RuntimeEngine.Rendering.State
        /// .CurrentRenderingPipeline</c>), which is the pipeline that called the exposure update or
        /// sets the tonemap uniforms.
        /// </summary>
        internal AutoExposureRuntimeState ResolveAutoExposureState(XRRenderPipelineInstance? instance)
        {
            instance ??= RuntimeEngine.Rendering.State.CurrentRenderingPipeline;
            return instance is null
                ? _detachedAutoExposureState
                : _autoExposureStates.GetValue(instance, static _ => new AutoExposureRuntimeState());
        }

        /// <summary>
        /// True when the GPU exposure texture of the rendering pipeline instance was written this
        /// frame, so the tonemap may sample it.
        /// </summary>
        [Browsable(false)]
        internal bool UseGpuAutoExposureThisFrame => AutoExposure && ResolveAutoExposureState(null).GpuReadyThisFrame;

        internal void MarkGpuAutoExposureReady(bool ready)
            => ResolveAutoExposureState(null).GpuReadyThisFrame = AutoExposure && ready;

        /// <summary>
        /// True when the rendering pipeline instance is one eye of a two-pass VR eye pair
        /// (<see cref="RenderPipeline.IsVrEyePostProcessPair"/>). Such an eye keeps its CPU-adapted
        /// exposure in its runtime state instead of the shared <see cref="Exposure"/> setting.
        /// </summary>
        private static bool IsRenderingPairedVrEye()
            => RuntimeEngine.Rendering.State.CurrentRenderingPipeline?.Pipeline?.IsVrEyePostProcessPair == true;

        internal static float SanitizeGpuAutoExposureDeltaSeconds(double deltaSeconds)
        {
            if (!double.IsFinite(deltaSeconds) || deltaSeconds <= 0.0)
                return 0.0f;

            return (float)Math.Min(deltaSeconds, MaxGpuAutoExposureDeltaSeconds);
        }

        public void UpdateExposure(BoundingRectangle rect)
        {
            AutoExposureRuntimeState runtime = ResolveAutoExposureState(null);
            runtime.GpuReadyThisFrame = false;

            if (!RequiresAutoExposure || RuntimeEngine.Rendering.State.IsLightProbePass || RuntimeEngine.Rendering.State.IsShadowPass || RuntimeEngine.Rendering.State.IsSceneCapturePass)
                return;

            float time = RuntimeEngine.ElapsedTime;
            if (time - runtime.LastUpdateTime < _secBetweenExposureUpdates)
                return;

            runtime.LastUpdateTime = time;
            bool pairedEye = IsRenderingPairedVrEye();

            void OnResult(bool success, float dot)
            {
                runtime.LastLumDot = dot;
                LerpExposure(runtime, pairedEye, success, dot);
            }

            RuntimeEngine.Rendering.State.CalculateFrontBufferDotLuminanceAsync(rect, false, AutoExposureLuminanceWeights, OnResult);
        }

        public void UpdateExposure(XRTexture tex, bool generateMipmapsNow)
        {
            AutoExposureRuntimeState runtime = ResolveAutoExposureState(null);
            runtime.GpuReadyThisFrame = false;

            if (!RequiresAutoExposure || RuntimeEngine.Rendering.State.IsLightProbePass || RuntimeEngine.Rendering.State.IsShadowPass || RuntimeEngine.Rendering.State.IsSceneCapturePass)
                return;

            float time = RuntimeEngine.ElapsedTime;
            if (time - runtime.LastUpdateTime < _secBetweenExposureUpdates)
                return;

            runtime.LastUpdateTime = time;
            bool pairedEye = IsRenderingPairedVrEye();

            void OnResult(bool success, float dot)
            {
                runtime.LastLumDot = dot;
                LerpExposure(runtime, pairedEye, success, dot);
            }

            switch (tex)
            {
                case XRTexture2D t2d:
                    RuntimeEngine.Rendering.State.CalculateDotLuminanceAsync(t2d, generateMipmapsNow, AutoExposureLuminanceWeights, OnResult);
                    break;
                case XRTexture2DArray t2da:
                    RuntimeEngine.Rendering.State.CalculateDotLuminanceAsync(t2da, generateMipmapsNow, AutoExposureLuminanceWeights, OnResult);
                    break;
            }
        }

        public void UpdateExposureGpu(XRTexture sourceTex, XRTexture2D exposureTex, bool generateMipmapsNow)
            => UpdateExposureGpu(sourceTex, exposureTex, generateMipmapsNow, null);

        /// <summary>Updates exposure using an explicitly selected cooked program on backends that require one.</summary>
        public void UpdateExposureGpu(XRTexture sourceTex, XRTexture2D exposureTex, bool generateMipmapsNow, string? gpuProgramBinding)
        {
            AutoExposureRuntimeState runtime = ResolveAutoExposureState(null);
            runtime.GpuReadyThisFrame = false;

            if (!RequiresAutoExposure || RuntimeEngine.Rendering.State.IsLightProbePass || RuntimeEngine.Rendering.State.IsShadowPass || RuntimeEngine.Rendering.State.IsSceneCapturePass)
                return;

            if (!AutoExposure)
                return;

            var renderer = AbstractRenderer.Current;
            if (renderer?.SupportsGpuAutoExposure != true)
                return;

            // For GPU auto-exposure, we update every frame to ensure smooth transitions.
            // Use the current render tick, not wall-clock elapsed time between successful
            // exposure writes. Render-on-demand can skip 3D scene frames for seconds; the
            // next real scene render should advance by the current render tick instead of
            // catching up in one large adaptation step.
            double rawRenderDeltaSeconds = RuntimeRenderingHostServices.FrameTiming.RenderDeltaSeconds;
            float deltaTime = runtime.LastUpdateTime == float.MinValue ? 0.0f : SanitizeGpuAutoExposureDeltaSeconds(rawRenderDeltaSeconds);

            bool success = renderer.UpdateAutoExposureGpu(sourceTex, exposureTex, this, deltaTime, generateMipmapsNow, gpuProgramBinding);
            runtime.GpuReadyThisFrame = success;

            // Only consume the timestamp budget when the GPU dispatch actually succeeded.
            // If GPU fails, leaving LastUpdateTime untouched lets the CPU fallback path
            // pass its own throttle check and produce a valid exposure value this frame.
            if (success)
                runtime.LastUpdateTime = RuntimeEngine.ElapsedTime;
        }

        /// <summary>
        /// Writes the headset exposure of a two-pass VR eye pair into the exposure texture of the
        /// rendering (right) eye: the renderer copies the value that the left eye published with
        /// <see cref="AbstractRenderer.PublishHeadsetAutoExposureGpu"/>. The right eye does not
        /// meter its own image in this mode. On success the tonemap of the right eye samples the
        /// texture like a normal GPU exposure.
        /// </summary>
        internal void FollowHeadsetExposureGpu(XRTexture2D exposureTex)
        {
            AutoExposureRuntimeState runtime = ResolveAutoExposureState(null);
            runtime.GpuReadyThisFrame = false;

            if (!RequiresAutoExposure || RuntimeEngine.Rendering.State.IsLightProbePass || RuntimeEngine.Rendering.State.IsShadowPass || RuntimeEngine.Rendering.State.IsSceneCapturePass)
                return;

            AbstractRenderer? renderer = AbstractRenderer.Current;
            if (renderer?.SupportsHeadsetSharedAutoExposure != true)
                return;

            bool success = renderer.FollowHeadsetAutoExposureGpu(exposureTex, this);
            runtime.GpuReadyThisFrame = success;
            if (success)
                runtime.LastUpdateTime = RuntimeEngine.ElapsedTime;
        }

        private void LerpExposure(AutoExposureRuntimeState runtime, bool pairedEye, bool success, float lumDot)
        {
            if (!success)
                return;

            GetResolvedExposureBounds(out float minExposure, out float maxExposure);

            //If the dot factor is zero, this means the screen is perfectly black.
            //Usually that means nothing is being rendered, so don't update the exposure now.
            //If we were to update the exposure now, the scene would look very bright once it finally starts rendering.
            if (!float.IsFinite(lumDot) || lumDot <= 0.0f)
            {
                if (pairedEye)
                {
                    if (runtime.HasAdaptedExposure)
                        runtime.AdaptedExposure = Math.Clamp(runtime.AdaptedExposure, minExposure, maxExposure);
                    return;
                }
                if (Exposure < minExposure)
                    Exposure = minExposure;
                if (Exposure > maxExposure)
                    Exposure = maxExposure;
                return;
            }

            float exposure = ExposureDividend / lumDot;
            exposure = AutoExposureBias + AutoExposureScale * exposure;
            if (ExposureMode == ExposureControlMode.Physical)
                exposure *= ComputePhysicalExposureMultiplier();
            exposure = ClampExposureToResolvedBounds(exposure);

            //If the current exposure is an invalid value, that means we want the exposure to be set immediately.
            // A paired VR eye adapts its own runtime value, so it never writes the setting that
            // both eyes share.
            float currentExposure = pairedEye ? runtime.AdaptedExposure : Exposure;
            float nextExposure = !float.IsFinite(currentExposure) || currentExposure < minExposure || currentExposure > maxExposure
                ? exposure
                : Interp.Lerp(currentExposure, exposure, ExposureTransitionSpeed);
            if (pairedEye)
                runtime.AdaptedExposure = nextExposure;
            else
                Exposure = nextExposure;
        }

        public void Lerp(ColorGradingSettings source, ColorGradingSettings dest, float time)
        {
            ExposureMode = time < 0.5f ? source.ExposureMode : dest.ExposureMode;
            IndependentEyeExposure = time < 0.5f ? source.IndependentEyeExposure : dest.IndependentEyeExposure;

            Saturation = Interp.Lerp(source.Saturation, dest.Saturation, time);
            Hue = Interp.Lerp(source.Hue, dest.Hue, time);
            Gamma = Interp.Lerp(source.Gamma, dest.Gamma, time);
            Brightness = Interp.Lerp(source.Brightness, dest.Brightness, time);
            Contrast = Interp.Lerp(source.Contrast, dest.Contrast, time);
            Tint = Interp.Lerp(source.Tint, dest.Tint, time);

            PhysicalApertureFNumber = Interp.Lerp(source.PhysicalApertureFNumber, dest.PhysicalApertureFNumber, time);
            PhysicalShutterSpeedSeconds = Interp.Lerp(source.PhysicalShutterSpeedSeconds, dest.PhysicalShutterSpeedSeconds, time);
            PhysicalISO = Interp.Lerp(source.PhysicalISO, dest.PhysicalISO, time);
            PhysicalExposureCompensationEV = Interp.Lerp(source.PhysicalExposureCompensationEV, dest.PhysicalExposureCompensationEV, time);
            PhysicalExposureScale = Interp.Lerp(source.PhysicalExposureScale, dest.PhysicalExposureScale, time);

            MinExposure = Interp.Lerp(source.MinExposure, dest.MinExposure, time);
            MaxExposure = Interp.Lerp(source.MaxExposure, dest.MaxExposure, time);
            ExposureDividend = Interp.Lerp(source.ExposureDividend, dest.ExposureDividend, time);
            AutoExposureBias = Interp.Lerp(source.AutoExposureBias, dest.AutoExposureBias, time);
            AutoExposureScale = Interp.Lerp(source.AutoExposureScale, dest.AutoExposureScale, time);
            ExposureTransitionSpeed = Interp.Lerp(source.ExposureTransitionSpeed, dest.ExposureTransitionSpeed, time);

            AutoExposureMetering = time < 0.5f ? source.AutoExposureMetering : dest.AutoExposureMetering;
            AutoExposureMeteringTargetSize = time < 0.5f ? source.AutoExposureMeteringTargetSize : dest.AutoExposureMeteringTargetSize;
            AutoExposureIgnoreTopPercent = Interp.Lerp(source.AutoExposureIgnoreTopPercent, dest.AutoExposureIgnoreTopPercent, time);
            AutoExposureCenterWeightStrength = Interp.Lerp(source.AutoExposureCenterWeightStrength, dest.AutoExposureCenterWeightStrength, time);
            AutoExposureCenterWeightPower = Interp.Lerp(source.AutoExposureCenterWeightPower, dest.AutoExposureCenterWeightPower, time);

            AutoExposureLuminanceWeights = Vector3.Lerp(source.AutoExposureLuminanceWeights, dest.AutoExposureLuminanceWeights, time);
        }

        private static Vector3 NormalizeLuminanceWeights(Vector3 w)
        {
            static float Sanitize(float v) => float.IsFinite(v) ? MathF.Max(0.0f, v) : 0.0f;

            w = new Vector3(Sanitize(w.X), Sanitize(w.Y), Sanitize(w.Z));
            float sum = w.X + w.Y + w.Z;
            if (!(sum > 0.0f) || float.IsNaN(sum) || float.IsInfinity(sum))
                return NormalizeLuminanceWeights(RuntimeEngine.Rendering.Settings.DefaultLuminance);
            return w / sum;
        }
    }
}
