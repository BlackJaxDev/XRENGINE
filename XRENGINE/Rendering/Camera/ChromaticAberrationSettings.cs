
using XREngine.Data.Core;

namespace XREngine.Rendering
{
    public class ChromaticAberrationSettings : XRBase
    {
        public float Intensity { get; set; } = 0.0f;

        public ChromaticAberrationSettings()
        {

        }

        public void SetUniforms(XRRenderProgram program)
        {
            program.Uniform("ChromaticAberrationIntensity", Intensity);
        }
    }
}