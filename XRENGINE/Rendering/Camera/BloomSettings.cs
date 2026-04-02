using System;

namespace XREngine.Rendering
{
    public class BloomSettings : PostProcessSettings
    {
        private float _intensity = 1.0f;
        private float _threshold = 0.8f;
        private float _softKnee = 0.5f;
        private float _radius = 1.0f;
        private float _strength = 0.5f;
        private int _startMip = 1;
        private int _endMip = 1;
        private float _lod0Weight = 0.0f;
        private float _lod1Weight = 1.0f;
        private float _lod2Weight = 0.0f;
        private float _lod3Weight = 0.0f;
        private float _lod4Weight = 0.0f;

        public BloomSettings()
        {

        }

        public float Intensity
        {
            get => _intensity;
            set => SetField(ref _intensity, value);
        }
        public float Threshold
        {
            get => _threshold;
            set => SetField(ref _threshold, value);
        }
        public float SoftKnee
        {
            get => _softKnee;
            set => SetField(ref _softKnee, value);
        }
        public float Radius
        {
            get => _radius;
            set => SetField(ref _radius, value);
        }

        /// <summary>
        /// Overall bloom strength applied when compositing bloom into the scene.
        /// Controls how much of the accumulated bloom contribution is added.
        /// Typical range: 0.05–0.50.
        /// </summary>
        public float Strength
        {
            get => _strength;
            set => SetField(ref _strength, MathF.Max(0.0f, value));
        }

        /// <summary>
        /// Smallest bloom mip to contribute (0 = full res). Raising this lowers quality/cost.
        /// </summary>
        public int StartMip
        {
            get => _startMip;
            set => SetField(ref _startMip, Math.Clamp(value, 0, 4));
        }

        /// <summary>
        /// Largest bloom mip to contribute (4 = 1/16 res). Lowering this reduces blur footprint.
        /// </summary>
        public int EndMip
        {
            get => _endMip;
            set => SetField(ref _endMip, Math.Clamp(value, 0, 4));
        }

        public float Lod0Weight
        {
            get => _lod0Weight;
            set => SetField(ref _lod0Weight, MathF.Max(0.0f, value));
        }

        public float Lod1Weight
        {
            get => _lod1Weight;
            set => SetField(ref _lod1Weight, MathF.Max(0.0f, value));
        }

        public float Lod2Weight
        {
            get => _lod2Weight;
            set => SetField(ref _lod2Weight, MathF.Max(0.0f, value));
        }

        public float Lod3Weight
        {
            get => _lod3Weight;
            set => SetField(ref _lod3Weight, MathF.Max(0.0f, value));
        }

        public float Lod4Weight
        {
            get => _lod4Weight;
            set => SetField(ref _lod4Weight, MathF.Max(0.0f, value));
        }

        public override void SetUniforms(XRRenderProgram program)
            => SetBrightPassUniforms(program);

        public void SetBrightPassUniforms(XRRenderProgram program)
        {
            float threshold = MathF.Max(0.0f, Threshold);
            float softKnee = MathF.Min(1.0f, MathF.Max(0.0f, SoftKnee));

            program.Uniform("BloomIntensity", MathF.Max(0.0f, Intensity));
            program.Uniform("BloomThreshold", threshold);
            program.Uniform("SoftKnee", softKnee);
            program.Uniform("Luminance", Engine.Rendering.Settings.DefaultLuminance);
        }

        public void SetDownsampleUniforms(XRRenderProgram program, int sourceLod, bool firstLevel)
        {
            float threshold = MathF.Max(0.0f, Threshold);
            float softKnee = MathF.Min(1.0f, MathF.Max(0.0f, SoftKnee));

            program.Uniform("SourceLOD", sourceLod);
            // BrightPass.fs already applies the soft-knee threshold before writing to mip 0,
            // so the downsample chain must NOT re-threshold (that would kill the signal).
            program.Uniform("UseThreshold", false);
            program.Uniform("BloomThreshold", threshold);
            program.Uniform("BloomSoftKnee", softKnee);
            program.Uniform("BloomIntensity", MathF.Max(0.0f, Intensity));
            program.Uniform("Luminance", Engine.Rendering.Settings.DefaultLuminance);
            program.Uniform("UseKarisAverage", firstLevel);
        }

        public void SetUpsampleUniforms(XRRenderProgram program, int sourceLod)
        {
            program.Uniform("SourceLOD", sourceLod);
            program.Uniform("Radius", MathF.Max(0.1f, Radius));
        }

        public void SetCombineUniforms(XRRenderProgram program)
        {
            // Clamp ordering to avoid invalid ranges.
            int startMip = Math.Clamp(StartMip, 0, 4);
            int endMip = Math.Clamp(EndMip, startMip, 4);

            // Provide per-lod weights; unused mips get zero weight.
            Span<float> weights =
            [
                _lod0Weight,
                _lod1Weight,
                _lod2Weight,
                _lod3Weight,
                _lod4Weight
            ];

            program.Uniform("BloomStrength", MathF.Max(0.0f, Strength));
            program.Uniform("BloomStartMip", startMip);
            program.Uniform("BloomEndMip", endMip);
            program.Uniform("BloomLodWeights", weights);
        }
    }
}