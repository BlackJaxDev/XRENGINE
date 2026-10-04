

using XREngine.Data.Colors;
using XREngine.Data.Core;

namespace XREngine.Rendering
{
    public class FogSettings : PostProcessSettings
    {
        public const string StructUniformName = "DepthFog";

        [ThreadStatic]
        private static RenderFrameViewSelection? s_frozenUniformView;

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
            RenderFrameViewSelection? frozenView = s_frozenUniformView;
            XRCamera? camera = RuntimeEngine.Rendering.State.RenderingPipelineState?.SceneCamera;
            if (camera is null && !frozenView.HasValue)
                return;

            program.Uniform($"{StructUniformName}.Intensity", DepthFogIntensity);
            if (DepthFogIntensity > 0.0f)
            {
                //TODO: we can cache these values in a camera-float dictionary
                float startDepth = GetDepth(DepthFogStartDistance, camera, frozenView);
                float endDepth = GetDepth(DepthFogEndDistance, camera, frozenView);

                program.Uniform($"{StructUniformName}.Start", startDepth);
                program.Uniform($"{StructUniformName}.End", endDepth);
                program.Uniform($"{StructUniformName}.Color", DepthFogColor);
            }
        }

        /// <summary>Preserves authored virtual overrides while base thresholds use the captured depth producer.</summary>
        public void SetUniforms(XRRenderProgram program, RenderFrameViewSelection? frozenView)
        {
            RenderFrameViewSelection? previous = s_frozenUniformView;
            s_frozenUniformView = frozenView;
            try { SetUniforms(program); }
            finally { s_frozenUniformView = previous; }
        }

        private static float GetDepth(float distance, XRCamera? camera, RenderFrameViewSelection? frozenView)
            => frozenView is { } view
                ? XRMath.DistanceToDepth(distance, view.View.CameraPositionAndNear.W,
                    view.View.CameraForwardAndFar.W, view.View.ReversedDepth)
                : camera!.DistanceToDepth(distance);
    }
}
