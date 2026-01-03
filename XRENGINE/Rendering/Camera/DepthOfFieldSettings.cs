
using System;
using System.Numerics;

namespace XREngine.Rendering
{
    /// <summary>
    /// Configures a camera depth-of-field post effect driven by circle of confusion.
    /// </summary>
    public class DepthOfFieldSettings : PostProcessSettings
    {
        public enum DepthOfFieldControlMode
        {
            Artist = 0,
            Physical = 1,
        }

        private bool _enabled;
        private DepthOfFieldControlMode _mode = DepthOfFieldControlMode.Artist;
        private float _focusDistance = 5.0f;
        private float _focusRange = 1.5f;
        private float _aperture = 2.8f;
        private float _maxCoCRadius = 6.0f;
        private float _bokehRadius = 1.25f;
        private bool _nearBlur = true;
        private float _physicalCircleOfConfusionMm = 0.03f;

        public bool Enabled
        {
            get => _enabled;
            set => SetField(ref _enabled, value);
        }

        public DepthOfFieldControlMode Mode
        {
            get => _mode;
            set => SetField(ref _mode, value);
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

        /// <summary>
        /// Circle of confusion diameter on sensor (mm) used for physical DOF.
        /// Typical full-frame value is ~0.03mm.
        /// </summary>
        public float PhysicalCircleOfConfusionMm
        {
            get => _physicalCircleOfConfusionMm;
            set => SetField(ref _physicalCircleOfConfusionMm, MathF.Max(0.0001f, value));
        }

        public override void SetUniforms(XRRenderProgram program)
            => SetUniforms(program, Vector2.Zero);

        public void SetUniforms(XRRenderProgram program, Vector2 texelSize)
        {
            var camera = Engine.Rendering.State.RenderingPipelineState?.SceneCamera;

            bool usePhysical = _mode == DepthOfFieldControlMode.Physical
                && camera?.Parameters is XRPhysicalCameraParameters;

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
            program.Uniform("MaxCoC", MathF.Max(0.0f, _maxCoCRadius));
            program.Uniform("BokehRadius", MathF.Max(0.0f, _bokehRadius));
            program.Uniform("NearBlur", _nearBlur);

            if (!usePhysical)
            {
                program.Uniform("DoFMode", (int)DepthOfFieldControlMode.Artist);
                program.Uniform("Aperture", MathF.Max(0.0f, _aperture));
                return;
            }

            // Physical mode: derive focus band and CoC sizing from the physical camera.
            var physical = (XRPhysicalCameraParameters)camera!.Parameters;

            float focusDistM = focusDist;
            float focusDistMm = focusDistM * 1000.0f;
            float fMm = MathF.Max(0.001f, physical.FocalLengthMm);
            float cocRefMm = MathF.Max(0.0001f, _physicalCircleOfConfusionMm);
            float renderHeightPx = MathF.Max(1.0f, Engine.Rendering.State.RenderArea.Height);
            float sensorHeightMm = MathF.Max(0.001f, physical.SensorHeightMm);
            float pixelsPerMm = renderHeightPx / sensorHeightMm;

            program.Uniform("DoFMode", (int)DepthOfFieldControlMode.Physical);
            program.Uniform("Aperture", MathF.Max(0.1f, _aperture));

            // Physical DOF uniforms
            program.Uniform("CameraNearZ", camera.NearZ);
            program.Uniform("CameraFarZ", camera.FarZ);
            program.Uniform("DoFPhysicalFocalLengthMm", fMm);
            program.Uniform("DoFPhysicalFocusDistanceMm", focusDistMm);
            program.Uniform("DoFPhysicalCoCRefMm", cocRefMm);
            program.Uniform("DoFPhysicalPixelsPerMm", pixelsPerMm);
        }
    }
}