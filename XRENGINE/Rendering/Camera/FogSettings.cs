

using XREngine.Data.Colors;

namespace XREngine.Rendering
{
    public class FogSettings : PostProcessSettings
    {
        public const string StructUniformName = "DepthFog";

        private float _depthFogIntensity = 0.0f;
        private float _depthFogStartDistance = 100.0f;
        private float _depthFogEndDistance = 10000.0f;
        private ColorF3 _depthFogColor = new(0.5f, 0.5f, 0.5f);

        public float DepthFogIntensity
        {
            get => _depthFogIntensity;
            set => SetField(ref _depthFogIntensity, value);
        }
        public float DepthFogStartDistance
        {
            get => _depthFogStartDistance;
            set => SetField(ref _depthFogStartDistance, value);
        }
        public float DepthFogEndDistance
        {
            get => _depthFogEndDistance;
            set => SetField(ref _depthFogEndDistance, value);
        }
        public ColorF3 DepthFogColor
        {
            get => _depthFogColor;
            set => SetField(ref _depthFogColor, value);
        }

        public override void SetUniforms(XRRenderProgram program)
        {
            XRCamera? camera = Engine.Rendering.State.RenderingPipelineState?.SceneCamera;
            if (camera is null)
                return;

            program.Uniform($"{StructUniformName}.Intensity", DepthFogIntensity);
            if (DepthFogIntensity > 0.0f)
            {
                //TODO: we can cache these values in a camera-float dictionary
                float startDepth = camera.DistanceToDepth(DepthFogStartDistance);
                float endDepth = camera.DistanceToDepth(DepthFogEndDistance);

                program.Uniform($"{StructUniformName}.Start", startDepth);
                program.Uniform($"{StructUniformName}.End", endDepth);
                program.Uniform($"{StructUniformName}.Color", DepthFogColor);
            }
        }
    }
}