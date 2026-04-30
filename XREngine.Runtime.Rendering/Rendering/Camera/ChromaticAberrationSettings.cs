

namespace XREngine.Rendering
{
    public class ChromaticAberrationSettings : PostProcessSettings
    {
        private bool _enabled;
        public bool Enabled
        {
            get => _enabled;
            set => SetField(ref _enabled, value);
        }

        private float _intensity = 0.0f;
        public float Intensity
        {
            get => _intensity;
            set => SetField(ref _intensity, value);
        }

        public override void SetUniforms(XRRenderProgram program)
        {
            program.Uniform("ChromaticAberrationIntensity", _enabled ? _intensity : 0.0f);
        }
    }
}