using XREngine.Extensions;
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

            float exposure = ClampExposureToResolvedBounds(Exposure);
            bool useGpuExposure = UseGpuAutoExposureThisFrame;
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
                    exposure = _lastUpdateTime == float.MinValue ? physicalBase : ClampExposureToResolvedBounds(Exposure);
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

        private float _lastUpdateTime = float.MinValue;
        private float _lastLumDot = 0.0f;
        private bool _gpuAutoExposureReadyThisFrame;
        private long _autoExposureResetRequests;
        private long _autoExposureResetsApplied;
        private long _autoExposureResetMinimumEpoch;
        private long _cpuSnapRequestsInFlight;
        private bool _snapThisDispatch;
        internal const float MaxGpuAutoExposureDeltaSeconds = 1.0f / 15.0f;

        /// <summary>
        /// Makes the next automatic exposure update go to the metered exposure in one step, for any frame.
        /// See <see cref="ResetAutoExposureHistory(ulong)"/>.
        /// </summary>
        public void ResetAutoExposureHistory()
            => ResetAutoExposureHistory(0UL);

        /// <summary>
        /// Makes the next automatic exposure update of a frame captured at or after <paramref name="minimumFrameCameraEpoch"/>
        /// go to the metered exposure in one step, with no adaptation from the earlier exposure. Use it after a camera cut or a
        /// jump in time, where the old exposure belongs to another view. Render work is pipelined, so a frame captured before
        /// the jump can still render after this call. That frame has an older camera history epoch, so it does not use the
        /// reset. Each reset is used once: the first successful update of a frame with a new enough epoch clears it.
        /// The CPU path also ignores its update interval once. Any thread can call it. It does not allocate.
        /// </summary>
        /// <param name="minimumFrameCameraEpoch">
        /// The <see cref="XRCamera.TemporalHistoryEpoch"/> after the discontinuity. Zero lets any frame use the reset.
        /// </param>
        public void ResetAutoExposureHistory(ulong minimumFrameCameraEpoch)
        {
            // The minimum is written before the request count, so an update that reads the count also sees the minimum.
            long minimum = unchecked((long)minimumFrameCameraEpoch);
            long current = Volatile.Read(ref _autoExposureResetMinimumEpoch);
            while ((ulong)minimum > (ulong)current)
            {
                long previous = Interlocked.CompareExchange(ref _autoExposureResetMinimumEpoch, minimum, current);
                if (previous == current)
                    break;
                current = previous;
            }
            Interlocked.Increment(ref _autoExposureResetRequests);
        }

        /// <summary>True while a reset from <see cref="ResetAutoExposureHistory(ulong)"/> waits for an exposure update.</summary>
        [Browsable(false)]
        public bool IsAutoExposureResetPending
            => Volatile.Read(ref _autoExposureResetRequests) != Volatile.Read(ref _autoExposureResetsApplied);

        /// <summary>
        /// Checks if a pending reset applies to a frame. A frame with no captured epoch counts as a current frame.
        /// </summary>
        /// <param name="requests">The request count that the caller read first.</param>
        /// <param name="frameCameraEpoch">The camera history epoch that the capture of the frame recorded, or null.</param>
        private bool IsAutoExposureResetDue(long requests, ulong? frameCameraEpoch)
        {
            if (requests == Volatile.Read(ref _autoExposureResetsApplied))
                return false;
            return frameCameraEpoch is not ulong epoch
                || epoch >= unchecked((ulong)Volatile.Read(ref _autoExposureResetMinimumEpoch));
        }

        /// <summary>
        /// Starts one GPU exposure dispatch for a frame. While a reset is due for the frame,
        /// <see cref="ComputeGpuAutoExposureBlend"/> gives one until <see cref="EndGpuAutoExposureDispatch"/>.
        /// </summary>
        /// <param name="frameCameraEpoch">The camera history epoch that the capture of the frame recorded, or null.</param>
        /// <param name="capturedRequests">The request count to pass to <see cref="EndGpuAutoExposureDispatch"/>.</param>
        /// <returns>True if the dispatch uses the reset.</returns>
        internal bool BeginGpuAutoExposureDispatch(ulong? frameCameraEpoch, out long capturedRequests)
        {
            capturedRequests = Volatile.Read(ref _autoExposureResetRequests);
            _snapThisDispatch = IsAutoExposureResetDue(capturedRequests, frameCameraEpoch);
            return _snapThisDispatch;
        }

        /// <summary>Ends a GPU exposure dispatch. A successful dispatch that used the reset clears it.</summary>
        /// <param name="success">True if the backend wrote the exposure.</param>
        /// <param name="capturedRequests">The value from <see cref="BeginGpuAutoExposureDispatch"/>.</param>
        internal void EndGpuAutoExposureDispatch(bool success, long capturedRequests)
        {
            if (success && _snapThisDispatch)
                MarkAutoExposureResetApplied(capturedRequests);
            _snapThisDispatch = false;
        }

        /// <summary>
        /// The blend factor of a GPU exposure update from the current exposure toward the metered exposure.
        /// It is one inside a dispatch that uses a reset, so the update replaces the old exposure.
        /// </summary>
        /// <param name="deltaTime">The render delta in seconds.</param>
        internal float ComputeGpuAutoExposureBlend(float deltaTime)
            => _snapThisDispatch
                ? 1.0f
                : 1.0f - MathF.Exp(-ExposureTransitionSpeed * SanitizeGpuAutoExposureDeltaSeconds(deltaTime));

        // A request that arrives after the capture stays pending for the next update.
        private void MarkAutoExposureResetApplied(long capturedRequests)
        {
            long applied = Volatile.Read(ref _autoExposureResetsApplied);
            while (capturedRequests > applied)
            {
                long previous = Interlocked.CompareExchange(ref _autoExposureResetsApplied, capturedRequests, applied);
                if (previous == applied)
                    return;
                applied = previous;
            }
        }

        // The CPU path reads the luminance later, so one snap request at a time is in flight. Later frames use the interval.
        private bool BeginCpuAutoExposureSnap(ulong? frameCameraEpoch, out long requests)
        {
            requests = Volatile.Read(ref _autoExposureResetRequests);
            if (requests == Volatile.Read(ref _cpuSnapRequestsInFlight) || !IsAutoExposureResetDue(requests, frameCameraEpoch))
                return false;
            Volatile.Write(ref _cpuSnapRequestsInFlight, requests);
            return true;
        }

        private void EndCpuAutoExposureSnap(bool applied, long requests)
        {
            if (applied)
                MarkAutoExposureResetApplied(requests);
            else
                Interlocked.CompareExchange(ref _cpuSnapRequestsInFlight, 0L, requests);
        }

        [Browsable(false)]
        internal bool UseGpuAutoExposureThisFrame => AutoExposure && _gpuAutoExposureReadyThisFrame;

        internal void MarkGpuAutoExposureReady(bool ready)
            => _gpuAutoExposureReadyThisFrame = AutoExposure && ready;

        internal static float SanitizeGpuAutoExposureDeltaSeconds(double deltaSeconds)
        {
            if (!double.IsFinite(deltaSeconds) || deltaSeconds <= 0.0)
                return 0.0f;

            return (float)Math.Min(deltaSeconds, MaxGpuAutoExposureDeltaSeconds);
        }

        public void UpdateExposure(BoundingRectangle rect)
        {
            _gpuAutoExposureReadyThisFrame = false;

            if (!RequiresAutoExposure || RuntimeEngine.Rendering.State.IsLightProbePass || RuntimeEngine.Rendering.State.IsShadowPass || RuntimeEngine.Rendering.State.IsSceneCapturePass)
                return;

            bool snap = BeginCpuAutoExposureSnap(null, out long resetRequests);
            float time = RuntimeEngine.ElapsedTime;
            if (!snap && time - _lastUpdateTime < _secBetweenExposureUpdates)
                return;

            _lastUpdateTime = time;

            void OnResult(bool success, float dot)
            {
                _lastLumDot = dot;
                bool applied = LerpExposure(success, dot, snap);
                if (snap)
                    EndCpuAutoExposureSnap(applied, resetRequests);
            }

            RuntimeEngine.Rendering.State.CalculateFrontBufferDotLuminanceAsync(rect, false, AutoExposureLuminanceWeights, OnResult);
        }

        /// <summary>Updates the exposure on the CPU from a luminance read of a texture.</summary>
        /// <param name="tex">The HDR scene texture.</param>
        /// <param name="generateMipmapsNow">True to generate the mipmaps before the read.</param>
        /// <param name="frameCameraEpoch">
        /// The camera history epoch that the capture of the rendered frame recorded, or null if the frame has none.
        /// It decides if a reset from <see cref="ResetAutoExposureHistory(ulong)"/> applies to this frame.
        /// </param>
        public void UpdateExposure(XRTexture tex, bool generateMipmapsNow, ulong? frameCameraEpoch = null)
        {
            _gpuAutoExposureReadyThisFrame = false;

            if (!RequiresAutoExposure || RuntimeEngine.Rendering.State.IsLightProbePass || RuntimeEngine.Rendering.State.IsShadowPass || RuntimeEngine.Rendering.State.IsSceneCapturePass)
                return;

            bool snap = BeginCpuAutoExposureSnap(frameCameraEpoch, out long resetRequests);
            float time = RuntimeEngine.ElapsedTime;
            if (!snap && time - _lastUpdateTime < _secBetweenExposureUpdates)
                return;

            _lastUpdateTime = time;

            void OnResult(bool success, float dot)
            {
                _lastLumDot = dot;
                bool applied = LerpExposure(success, dot, snap);
                if (snap)
                    EndCpuAutoExposureSnap(applied, resetRequests);
            }

            switch (tex)
            {
                case XRTexture2D t2d:
                    RuntimeEngine.Rendering.State.CalculateDotLuminanceAsync(t2d, generateMipmapsNow, AutoExposureLuminanceWeights, OnResult);
                    break;
                case XRTexture2DArray t2da:
                    RuntimeEngine.Rendering.State.CalculateDotLuminanceAsync(t2da, generateMipmapsNow, AutoExposureLuminanceWeights, OnResult);
                    break;
                default:
                    if (snap)
                        EndCpuAutoExposureSnap(false, resetRequests);
                    break;
            }
        }

        public void UpdateExposureGpu(XRTexture sourceTex, XRTexture2D exposureTex, bool generateMipmapsNow)
            => UpdateExposureGpu(sourceTex, exposureTex, generateMipmapsNow, null);

        /// <summary>Updates exposure using an explicitly selected cooked program on backends that require one.</summary>
        /// <param name="sourceTex">The HDR scene texture.</param>
        /// <param name="exposureTex">The one-texel exposure texture that keeps the adapted exposure.</param>
        /// <param name="generateMipmapsNow">True to generate the mipmaps before the read.</param>
        /// <param name="gpuProgramBinding">The cooked program for backends that require one, or null.</param>
        /// <param name="frameCameraEpoch">
        /// The camera history epoch that the capture of the rendered frame recorded, or null if the frame has none.
        /// It decides if a reset from <see cref="ResetAutoExposureHistory(ulong)"/> applies to this frame.
        /// </param>
        public void UpdateExposureGpu(XRTexture sourceTex, XRTexture2D exposureTex, bool generateMipmapsNow, string? gpuProgramBinding, ulong? frameCameraEpoch = null)
        {
            _gpuAutoExposureReadyThisFrame = false;

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
            float deltaTime = _lastUpdateTime == float.MinValue ? 0.0f : SanitizeGpuAutoExposureDeltaSeconds(rawRenderDeltaSeconds);

            // The backend reads ComputeGpuAutoExposureBlend, so a reset that is due for this frame gives a blend of one.
            BeginGpuAutoExposureDispatch(frameCameraEpoch, out long resetRequests);
            bool success = false;
            try
            {
                success = renderer.UpdateAutoExposureGpu(sourceTex, exposureTex, this, deltaTime, generateMipmapsNow, gpuProgramBinding);
            }
            finally
            {
                EndGpuAutoExposureDispatch(success, resetRequests);
            }
            _gpuAutoExposureReadyThisFrame = success;

            // Only consume the timestamp budget when the GPU dispatch actually succeeded.
            // If GPU fails, leaving _lastUpdateTime untouched lets the CPU fallback path
            // pass its own throttle check and produce a valid exposure value this frame.
            if (success)
                _lastUpdateTime = RuntimeEngine.ElapsedTime;
        }

        /// <returns>True if the exposure was set from a metered value.</returns>
        private bool LerpExposure(bool success, float lumDot, bool snap)
        {
            if (!success)
                return false;

            GetResolvedExposureBounds(out float minExposure, out float maxExposure);

            //If the dot factor is zero, this means the screen is perfectly black.
            //Usually that means nothing is being rendered, so don't update the exposure now.
            //If we were to update the exposure now, the scene would look very bright once it finally starts rendering.
            if (!float.IsFinite(lumDot) || lumDot <= 0.0f)
            {
                if (Exposure < minExposure)
                    Exposure = minExposure;
                if (Exposure > maxExposure)
                    Exposure = maxExposure;
                return false;
            }

            float exposure = ExposureDividend / lumDot;
            exposure = AutoExposureBias + AutoExposureScale * exposure;
            if (ExposureMode == ExposureControlMode.Physical)
                exposure *= ComputePhysicalExposureMultiplier();
            exposure = ClampExposureToResolvedBounds(exposure);

            //If the current exposure is an invalid value, that means we want the exposure to be set immediately.
            //A reset of the exposure history also sets it immediately.
            float currentExposure = Exposure;
            if (snap || !float.IsFinite(currentExposure) || currentExposure < minExposure || currentExposure > maxExposure)
                Exposure = exposure;
            else
                Exposure = Interp.Lerp(currentExposure, exposure, ExposureTransitionSpeed);
            return true;
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
                return NormalizeLuminanceWeights(RuntimeEngine.Rendering.Settings.DefaultLuminance);
            return w / sum;
        }
    }
}
