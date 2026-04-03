using System;

namespace XREngine.Rendering
{
    public class BloomSettings : PostProcessSettings
    {
        private float _intensity = 1.0f;
        private float _threshold = 0.8f;
        private float _softKnee = 0.5f;
        private float _radius = 1.0f;
        private float _scatter = 0.75f;
        private float _strength = 0.15f;
        private int _startMip = 2;
        private int _endMip = 4;
        private float _lod0Weight = 0.0f;
        private float _lod1Weight = 0.0f;
        private float _lod2Weight = 0.65f;
        private float _lod3Weight = 0.25f;
        private float _lod4Weight = 0.10f;

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
        /// Controls how much energy each upsample level contributes to the bloom.
        /// Higher values spread bloom wider (more contribution from lower-res mips).
        /// At 0 only the first downsample level contributes; at 1 all levels contribute equally.
        /// Typical range: 0.5–0.9.
        /// </summary>
        public float Scatter
        {
            get => _scatter;
            set => SetField(ref _scatter, MathF.Max(0.0f, MathF.Min(1.0f, value)));
        }

        /// <summary>
        /// Overall bloom strength applied when compositing bloom into the scene.
        /// Controls how much of the accumulated bloom contribution is added.
        /// Typical range: 0.05–0.30.
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

        public void SetDownsampleUniforms(XRRenderProgram program, bool firstLevel)
        {
            float threshold = MathF.Max(0.0f, Threshold);
            float softKnee = MathF.Min(1.0f, MathF.Max(0.0f, SoftKnee));

            // SourceLOD is set dynamically via the material's ShaderInt before each
            // downsample pass. Do NOT set it here — this callback fires after material
            // uniforms and would override the correct per-level value.

            // Apply the bright-pass threshold on the first downsample level to extract
            // only energy above the threshold. Without this, the entire scene ends up in
            // the bloom texture and the result is brightness increase instead of a halo.
            program.Uniform("UseThreshold", firstLevel);
            program.Uniform("BloomThreshold", threshold);
            program.Uniform("BloomSoftKnee", softKnee);
            program.Uniform("BloomIntensity", MathF.Max(0.0f, Intensity));
            program.Uniform("Luminance", Engine.Rendering.Settings.DefaultLuminance);
            program.Uniform("UseKarisAverage", firstLevel);
        }

        public void SetUpsampleUniforms(XRRenderProgram program)
        {
            // SourceLOD is set dynamically via the material's ShaderInt before each
            // upsample pass. Do NOT set it here — this callback fires after material
            // uniforms and would override the correct per-level value.
            program.Uniform("Radius", MathF.Max(0.1f, Radius));
            program.Uniform("Scatter", MathF.Max(0.0f, MathF.Min(1.0f, Scatter)));
        }

        public void SetCombineUniforms(XRRenderProgram program)
        {
            bool usesLegacySingleMipProfile =
                MathF.Abs(_strength - 0.5f) <= 0.0001f &&
                _startMip == 1 &&
                _endMip == 1 &&
                MathF.Abs(_lod0Weight - 0.0f) <= 0.0001f &&
                MathF.Abs(_lod1Weight - 1.0f) <= 0.0001f &&
                MathF.Abs(_lod2Weight - 0.0f) <= 0.0001f &&
                MathF.Abs(_lod3Weight - 0.0f) <= 0.0001f &&
                MathF.Abs(_lod4Weight - 0.0f) <= 0.0001f;

            float bloomStrength = usesLegacySingleMipProfile ? 0.15f : MathF.Max(0.0f, Strength);

            // Clamp ordering to avoid invalid ranges.
            int startMip = Math.Clamp(usesLegacySingleMipProfile ? 2 : StartMip, 0, 4);
            int endMip = Math.Clamp(usesLegacySingleMipProfile ? 4 : EndMip, startMip, 4);

            // Provide per-lod weights; unused mips get zero weight.
            Span<float> weights =
            [
                usesLegacySingleMipProfile ? 0.0f : _lod0Weight,
                usesLegacySingleMipProfile ? 0.0f : _lod1Weight,
                usesLegacySingleMipProfile ? 0.65f : _lod2Weight,
                usesLegacySingleMipProfile ? 0.25f : _lod3Weight,
                usesLegacySingleMipProfile ? 0.10f : _lod4Weight
            ];

            program.Uniform("BloomStrength", bloomStrength);
            program.Uniform("BloomStartMip", startMip);
            program.Uniform("BloomEndMip", endMip);
            program.Uniform("BloomLodWeights", weights);
        }
    }
}