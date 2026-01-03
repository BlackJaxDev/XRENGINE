using Extensions;
using System.Numerics;
using System.ComponentModel;
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
        private float _autoExposureBias = 0.0f;
        private float _autoExposureScale = 1.0f;
        private float _exposureDividend = 0.18f;
        private float _minExposure = 0.01f;
        private float _maxExposure = 500.0f;

        private AutoExposureMeteringMode _autoExposureMetering = AutoExposureMeteringMode.Average;
        private int _autoExposureMeteringTargetSize = 16;
        private float _autoExposureIgnoreTopPercent = 0.02f;
        private float _autoExposureCenterWeightStrength = 1.0f;
        private float _autoExposureCenterWeightPower = 2.0f;

        private Vector3 _autoExposureLuminanceWeights = NormalizeLuminanceWeights(Engine.Rendering.Settings.DefaultLuminance);

        private float _exposure = 1.0f;
        private float _gamma = 2.2f;
        private float _hue = 1.0f;
        private float _saturation = 1.0f;
        private float _brightness = 1.0f;

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
            set => SetField(ref _minExposure, value);
        }
        public float MaxExposure
        {
            get => _maxExposure;
            set => SetField(ref _maxExposure, value);
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
            set => SetField(ref _exposure, value);
        }
        public float Contrast
        {
            get => _contrast;
            set => SetField(ref _contrast, value);
        }
        public float Gamma
        {
            get => _gamma;
            set => SetField(ref _gamma, value);
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

            float exposure = Exposure;
            bool useGpuExposure = AutoExposure && AbstractRenderer.Current?.SupportsGpuAutoExposure == true;
            if (ExposureMode == ExposureControlMode.Physical)
            {
                float physicalBase = ComputePhysicalExposureMultiplier();
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
                    exposure = _lastUpdateTime == float.MinValue ? physicalBase : Exposure;
                }
            }

            program.Uniform($"{ColorGradeUniformName}.{nameof(Exposure)}", exposure);
            program.Uniform($"{ColorGradeUniformName}.{nameof(Contrast)}", _contrastUniformValue);
            program.Uniform($"{ColorGradeUniformName}.{nameof(Gamma)}", Gamma);
            program.Uniform($"{ColorGradeUniformName}.{nameof(Hue)}", Hue);
            program.Uniform($"{ColorGradeUniformName}.{nameof(Saturation)}", Saturation);
            program.Uniform($"{ColorGradeUniformName}.{nameof(Brightness)}", Brightness);

            // Optional GPU-driven auto exposure path (PostProcess.fs / PostProcessStereo.fs)
            // Shader-side logic falls back to uniform exposure if the GPU texture isn't valid yet.
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
            return PhysicalExposureScale * multiplier;
        }

        private float _secBetweenExposureUpdates = 1.0f;
        public float SecondsBetweenExposureUpdates
        {
            get => _secBetweenExposureUpdates;
            set => SetField(ref _secBetweenExposureUpdates, value);
        }

        private float _lastUpdateTime = float.MinValue;
        private float _lastLumDot = 0.0f;

        public void UpdateExposure(BoundingRectangle rect)
        {
            if (!RequiresAutoExposure || Engine.Rendering.State.IsLightProbePass || Engine.Rendering.State.IsShadowPass || Engine.Rendering.State.IsSceneCapturePass)
                return;

            float time = Engine.ElapsedTime;
            if (time - _lastUpdateTime < _secBetweenExposureUpdates)
                return;

            _lastUpdateTime = time;

            void OnResult(bool success, float dot)
            {
                _lastLumDot = dot;
                LerpExposure(success, dot);
            }

            Engine.Rendering.State.CalculateFrontBufferDotLuminanceAsync(rect, false, AutoExposureLuminanceWeights, OnResult);
        }

        public void UpdateExposure(XRTexture tex, bool generateMipmapsNow)
        {
            if (!RequiresAutoExposure || Engine.Rendering.State.IsLightProbePass || Engine.Rendering.State.IsShadowPass || Engine.Rendering.State.IsSceneCapturePass)
                return;

            float time = Engine.ElapsedTime;
            if (time - _lastUpdateTime < _secBetweenExposureUpdates)
                return;

            _lastUpdateTime = time;

            void OnResult(bool success, float dot)
            {
                _lastLumDot = dot;
                LerpExposure(success, dot);
            }

            switch (tex)
            {
                case XRTexture2D t2d:
                    Engine.Rendering.State.CalculateDotLuminanceAsync(t2d, generateMipmapsNow, AutoExposureLuminanceWeights, OnResult);
                    break;
                case XRTexture2DArray t2da:
                    Engine.Rendering.State.CalculateDotLuminanceAsync(t2da, generateMipmapsNow, AutoExposureLuminanceWeights, OnResult);
                    break;
            }
        }

        public void UpdateExposureGpu(XRTexture sourceTex, XRTexture2D exposureTex, bool generateMipmapsNow)
        {
            if (!RequiresAutoExposure || Engine.Rendering.State.IsLightProbePass || Engine.Rendering.State.IsShadowPass || Engine.Rendering.State.IsSceneCapturePass)
                return;

            if (!AutoExposure)
                return;

            var renderer = AbstractRenderer.Current;
            if (renderer?.SupportsGpuAutoExposure != true)
                return;

            // For GPU auto-exposure, we update every frame to ensure smooth transitions.
            // The compute shader or the renderer will handle the time-based lerp using deltaTime.
            float time = Engine.ElapsedTime;
            float deltaTime = _lastUpdateTime == float.MinValue ? 0.0f : time - _lastUpdateTime;
            _lastUpdateTime = time;

            renderer.UpdateAutoExposureGpu(sourceTex, exposureTex, this, deltaTime, generateMipmapsNow);
        }

        private void LerpExposure(bool success, float lumDot)
        {
            if (!success)
                return;

            //If the dot factor is zero, this means the screen is perfectly black.
            //Usually that means nothing is being rendered, so don't update the exposure now.
            //If we were to update the exposure now, the scene would look very bright once it finally starts rendering.
            if (lumDot <= 0.0f)
            {
                if (Exposure < MinExposure)
                    Exposure = MinExposure;
                if (Exposure > MaxExposure)
                    Exposure = MaxExposure;
                return;
            }

            float exposure = ExposureDividend / lumDot;
            exposure = AutoExposureBias + AutoExposureScale * exposure;
            if (ExposureMode == ExposureControlMode.Physical)
                exposure *= ComputePhysicalExposureMultiplier();
            exposure = exposure.Clamp(MinExposure, MaxExposure);

            //If the current exposure is an invalid value, that means we want the exposure to be set immediately.
            if (Exposure < MinExposure || Exposure > MaxExposure)
                Exposure = exposure;
            else
                Exposure = Interp.Lerp(Exposure, exposure, ExposureTransitionSpeed);
        }

        public void Lerp(ColorGradingSettings source, ColorGradingSettings dest, float time)
        {
            ExposureMode = time < 0.5f ? source.ExposureMode : dest.ExposureMode;

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
                return NormalizeLuminanceWeights(Engine.Rendering.Settings.DefaultLuminance);
            return w / sum;
        }
    }
}