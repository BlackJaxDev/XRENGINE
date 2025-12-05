using Extensions;
using System.ComponentModel;
using System.Runtime.Intrinsics.X86;
using XREngine.Data;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Data.Geometry;

namespace XREngine.Rendering
{
    public class ColorGradingSettings : XRBase
    {
        public const string ColorGradeUniformName = "ColorGrade";

        public ColorGradingSettings()
        {
            Contrast = 1.0f;
        }

        private float _contrast = 0.0f;
        private float _contrastUniformValue;
        private float _exposureTransitionSpeed = 0.01f;
        private ColorF3 _tint = new(1.0f, 1.0f, 1.0f);
        private bool _autoExposure = true;
        private float _autoExposureBias = 0.0f;
        private float _autoExposureScale = 0.5f;
        private float _exposureDividend = 0.1f;
        private float _minExposure = 0.0001f;
        private float _maxExposure = 500.0f;
        private float _exposure = 1.0f;
        private float _gamma = 2.2f;
        private float _hue = 1.0f;
        private float _saturation = 1.0f;
        private float _brightness = 1.0f;

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

        internal void SetUniforms(XRRenderProgram program)
        {
            program.Uniform($"{ColorGradeUniformName}.{nameof(Tint)}", Tint);
            program.Uniform($"{ColorGradeUniformName}.{nameof(Exposure)}", Exposure);
            program.Uniform($"{ColorGradeUniformName}.{nameof(Contrast)}", _contrastUniformValue);
            program.Uniform($"{ColorGradeUniformName}.{nameof(Gamma)}", Gamma);
            program.Uniform($"{ColorGradeUniformName}.{nameof(Hue)}", Hue);
            program.Uniform($"{ColorGradeUniformName}.{nameof(Saturation)}", Saturation);
            program.Uniform($"{ColorGradeUniformName}.{nameof(Brightness)}", Brightness);
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
        public bool RequiresAutoExposure => AutoExposure || Exposure < MinExposure || Exposure > MaxExposure;

        private float _secBetweenExposureUpdates = 1.0f;
        public float SecondsBetweenExposureUpdates
        {
            get => _secBetweenExposureUpdates;
            set => SetField(ref _secBetweenExposureUpdates, value);
        }

        private float _lastUpdateTime = 0.0f;
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

            Engine.Rendering.State.CalculateFrontBufferDotLuminanceAsync(rect, false, OnResult);
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
                    Engine.Rendering.State.CalculateDotLuminanceAsync(t2d, generateMipmapsNow, OnResult);
                    break;
                case XRTexture2DArray t2da:
                    Engine.Rendering.State.CalculateDotLuminanceAsync(t2da, generateMipmapsNow, OnResult);
                    break;
            }
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
            exposure = exposure.Clamp(MinExposure, MaxExposure);

            //If the current exposure is an invalid value, that means we want the exposure to be set immediately.
            if (Exposure < MinExposure || Exposure > MaxExposure)
                Exposure = exposure;
            else
                Exposure = Interp.Lerp(Exposure, exposure, ExposureTransitionSpeed);
        }

        public void Lerp(ColorGradingSettings source, ColorGradingSettings dest, float time)
        {
            Saturation = Interp.Lerp(source.Saturation, dest.Saturation, time);
            Hue = Interp.Lerp(source.Hue, dest.Hue, time);
            Gamma = Interp.Lerp(source.Gamma, dest.Gamma, time);
            Brightness = Interp.Lerp(source.Brightness, dest.Brightness, time);
            Contrast = Interp.Lerp(source.Contrast, dest.Contrast, time);
            Tint = Interp.Lerp(source.Tint, dest.Tint, time);
            MinExposure = Interp.Lerp(source.MinExposure, dest.MinExposure, time);
            MaxExposure = Interp.Lerp(source.MaxExposure, dest.MaxExposure, time);
            ExposureDividend = Interp.Lerp(source.ExposureDividend, dest.ExposureDividend, time);
            AutoExposureBias = Interp.Lerp(source.AutoExposureBias, dest.AutoExposureBias, time);
            AutoExposureScale = Interp.Lerp(source.AutoExposureScale, dest.AutoExposureScale, time);
            ExposureTransitionSpeed = Interp.Lerp(source.ExposureTransitionSpeed, dest.ExposureTransitionSpeed, time);
        }
    }
}