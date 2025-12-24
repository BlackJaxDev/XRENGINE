
using System;
using System.Numerics;

namespace XREngine.Rendering
{
    /// <summary>
    /// Configures a camera depth-of-field post effect driven by circle of confusion.
    /// </summary>
    public class DepthOfFieldSettings : PostProcessSettings
    {
        private bool _enabled;
        private float _focusDistance = 5.0f;
        private float _focusRange = 1.5f;
        private float _aperture = 2.8f;
        private float _maxCoCRadius = 6.0f;
        private float _bokehRadius = 1.25f;
        private bool _nearBlur = true;

        public bool Enabled
        {
            get => _enabled;
            set => SetField(ref _enabled, value);
        }

        public float FocusDistance
        {
            get => _focusDistance;
            set => SetField(ref _focusDistance, MathF.Max(0.01f, value));
        }

        public float FocusRange
        {
            get => _focusRange;
            set => SetField(ref _focusRange, MathF.Max(0.01f, value));
        }

        public float Aperture
        {
            get => _aperture;
            set => SetField(ref _aperture, MathF.Max(0.0f, value));
        }

        public float MaxCoCRadius
        {
            get => _maxCoCRadius;
            set => SetField(ref _maxCoCRadius, MathF.Max(0.0f, value));
        }

        public float BokehRadius
        {
            get => _bokehRadius;
            set => SetField(ref _bokehRadius, MathF.Max(0.0f, value));
        }

        public bool NearBlur
        {
            get => _nearBlur;
            set => SetField(ref _nearBlur, value);
        }

        public override void SetUniforms(XRRenderProgram program)
            => SetUniforms(program, Vector2.Zero);

        public void SetUniforms(XRRenderProgram program, Vector2 texelSize)
        {
            var camera = Engine.Rendering.State.RenderingPipelineState?.SceneCamera;

            float focusDist = MathF.Max(0.01f, _focusDistance);
            float focusRange = MathF.Max(0.01f, _focusRange);

            // Convert world-space distances to the camera's depth buffer space so DOF matches the actual depth encoding.
            float focusDepth = camera?.DistanceToDepth(focusDist) ?? 1.0f;
            float nearDepth = camera?.DistanceToDepth(MathF.Max(0.01f, focusDist - focusRange * 0.5f)) ?? focusDepth;
            float farDepth = camera?.DistanceToDepth(focusDist + focusRange * 0.5f) ?? focusDepth;
            float focusRangeDepth = MathF.Max(1e-4f, MathF.Abs(farDepth - nearDepth));

            program.Uniform("TexelSize", texelSize);
            program.Uniform("FocusDepth", focusDepth);
            program.Uniform("FocusRangeDepth", focusRangeDepth);
            program.Uniform("Aperture", MathF.Max(0.0f, _aperture));
            program.Uniform("MaxCoC", MathF.Max(0.0f, _maxCoCRadius));
            program.Uniform("BokehRadius", MathF.Max(0.0f, _bokehRadius));
            program.Uniform("NearBlur", _nearBlur);
        }
    }
}