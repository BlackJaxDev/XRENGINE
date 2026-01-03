

using System;
using System.Numerics;

namespace XREngine.Rendering
{
    public class LensDistortionSettings : PostProcessSettings
    {
        public enum LensDistortionControlMode
        {
            Artist = 0,
            Physical = 1,
        }

        public static readonly Vector2 DefaultDistortionCenterUv = new(0.5f, 0.5f);

        private LensDistortionControlMode _controlMode = LensDistortionControlMode.Artist;
        public LensDistortionControlMode ControlMode
        {
            get => _controlMode;
            set => SetField(ref _controlMode, value);
        }

        private ELensDistortionMode _mode = ELensDistortionMode.None;
        /// <summary>
        /// The lens distortion projection mode.
        /// </summary>
        public ELensDistortionMode Mode
        {
            get => _mode;
            set => SetField(ref _mode, value);
        }

        private float _intensity = 0.0f;
        /// <summary>
        /// Radial distortion intensity. Negative = barrel, Positive = pincushion.
        /// Only used when Mode is Radial.
        /// </summary>
        public float Intensity
        {
            get => _intensity;
            set => SetField(ref _intensity, value);
        }

        private float _paniniDistance = 1.0f;
        /// <summary>
        /// Panini projection distance parameter (0 to 1).
        /// 0 = rectilinear (no effect), 1 = full cylindrical Panini.
        /// Only used when Mode is Panini.
        /// </summary>
        public float PaniniDistance
        {
            get => _paniniDistance;
            set => SetField(ref _paniniDistance, Math.Clamp(value, 0.0f, 1.0f));
        }

        private float _paniniCropToFit = 1.0f;
        /// <summary>
        /// Panini crop-to-fit parameter (0 to 1).
        /// 0 = no cropping (may show black edges), 1 = crop to fill screen.
        /// Only used when Mode is Panini.
        /// </summary>
        public float PaniniCropToFit
        {
            get => _paniniCropToFit;
            set => SetField(ref _paniniCropToFit, Math.Clamp(value, 0.0f, 1.0f));
        }

        private float _brownConradyK1;
        private float _brownConradyK2;
        private float _brownConradyK3;
        private float _brownConradyP1;
        private float _brownConradyP2;

        public float BrownConradyK1
        {
            get => _brownConradyK1;
            set => SetField(ref _brownConradyK1, value);
        }
        public float BrownConradyK2
        {
            get => _brownConradyK2;
            set => SetField(ref _brownConradyK2, value);
        }
        public float BrownConradyK3
        {
            get => _brownConradyK3;
            set => SetField(ref _brownConradyK3, value);
        }
        public float BrownConradyP1
        {
            get => _brownConradyP1;
            set => SetField(ref _brownConradyP1, value);
        }
        public float BrownConradyP2
        {
            get => _brownConradyP2;
            set => SetField(ref _brownConradyP2, value);
        }

        public override void SetUniforms(XRRenderProgram program)
            => SetUniforms(program, null, 1.0f, null);

        public void SetUniforms(XRRenderProgram program, float? cameraVerticalFovDegrees)
            => SetUniforms(program, cameraVerticalFovDegrees, 1.0f, null);

        public void SetUniforms(XRRenderProgram program, float? cameraVerticalFovDegrees, float aspectRatio)
            => SetUniforms(program, cameraVerticalFovDegrees, aspectRatio, null);

        public void SetUniforms(XRRenderProgram program, float? cameraVerticalFovDegrees, float aspectRatio, Vector2? distortionCenterUv)
        {
            int mode = (int)_mode;
            if (_controlMode == LensDistortionControlMode.Physical)
                mode = (int)ELensDistortionMode.BrownConrady;
            else if (_mode == ELensDistortionMode.BrownConrady)
                mode = (int)ELensDistortionMode.None;
            program.Uniform("LensDistortionMode", mode);

            Vector2 center = distortionCenterUv ?? DefaultDistortionCenterUv;
            program.Uniform("LensDistortionCenter", center);

            // Radial distortion intensity
            float radialIntensity = 0.0f;
            if (_controlMode == LensDistortionControlMode.Artist && _mode == ELensDistortionMode.Radial)
                radialIntensity = _intensity;
            else if (_controlMode == LensDistortionControlMode.Artist && _mode == ELensDistortionMode.RadialAutoFromFOV && cameraVerticalFovDegrees.HasValue)
                radialIntensity = ComputeCorrectionFromFovDegrees(cameraVerticalFovDegrees.Value);

            program.Uniform("LensDistortionIntensity", radialIntensity);

            // Panini parameters
            if (_controlMode == LensDistortionControlMode.Artist && _mode == ELensDistortionMode.Panini && cameraVerticalFovDegrees.HasValue)
            {
                float fovRad = cameraVerticalFovDegrees.Value * MathF.PI / 180.0f;
                var viewExtents = CalcViewExtents(fovRad, aspectRatio);
                var cropExtents = CalcCropExtents(fovRad, _paniniDistance, aspectRatio);

                float scaleX = cropExtents.X / viewExtents.X;
                float scaleY = cropExtents.Y / viewExtents.Y;
                float scaleF = MathF.Min(scaleX, scaleY);

                float paniniS = float.Lerp(1.0f, Math.Clamp(scaleF, 0.0f, 1.0f), _paniniCropToFit);

                program.Uniform("PaniniDistance", _paniniDistance);
                program.Uniform("PaniniCrop", paniniS);
                program.Uniform("PaniniViewExtents", viewExtents);
            }
            else
            {
                program.Uniform("PaniniDistance", 0.0f);
                program.Uniform("PaniniCrop", 1.0f);
                program.Uniform("PaniniViewExtents", new Vector2(1.0f, 1.0f));
            }

            // Brown-Conrady coefficients (used when LensDistortionMode == BrownConrady)
            if (_controlMode == LensDistortionControlMode.Physical)
            {
                program.Uniform("BrownConradyRadial", new Vector3(_brownConradyK1, _brownConradyK2, _brownConradyK3));
                program.Uniform("BrownConradyTangential", new Vector2(_brownConradyP1, _brownConradyP2));
            }
            else
            {
                program.Uniform("BrownConradyRadial", Vector3.Zero);
                program.Uniform("BrownConradyTangential", Vector2.Zero);
            }
        }

        /// <summary>
        /// Calculates view extents (tan(fov/2) scaled by aspect).
        /// </summary>
        public static Vector2 CalcViewExtents(float verticalFovRadians, float aspectRatio)
        {
            float viewExtY = MathF.Tan(0.5f * verticalFovRadians);
            float viewExtX = aspectRatio * viewExtY;
            return new Vector2(viewExtX, viewExtY);
        }

        /// <summary>
        /// Calculates crop extents for Panini projection (how much the image needs to scale to fit).
        /// </summary>
        public static Vector2 CalcCropExtents(float verticalFovRadians, float d, float aspectRatio)
        {
            // Based on Unity's implementation
            float viewDist = 1f + d;
            var projPos = CalcViewExtents(verticalFovRadians, aspectRatio);
            float projHyp = MathF.Sqrt(projPos.X * projPos.X + 1f);

            float cylDistMinusD = 1f / projHyp;
            float cylDist = cylDistMinusD + d;
            var cylPos = projPos * cylDistMinusD;

            return cylPos * (viewDist / cylDist);
        }

        /// <summary>
        /// Computes a lens distortion correction factor for a given vertical field of view.
        /// Returns a negative value (barrel correction) that counteracts perspective stretching.
        /// </summary>
        /// <param name="verticalFovRadians">Vertical FOV in radians.</param>
        /// <returns>Distortion intensity suitable for the shader's radial model.</returns>
        public static float ComputeCorrectionFromFov(float verticalFovRadians)
        {
            float halfFov = verticalFovRadians * 0.5f;
            float quarterFov = verticalFovRadians * 0.25f;
            float tanHalf = MathF.Tan(halfFov);
            float tanQuarter = MathF.Tan(quarterFov);

            if (MathF.Abs(tanHalf) < 1e-6f)
                return 0.0f;

            // Negative value produces barrel correction (pulls edges inward)
            return -tanQuarter / tanHalf;
        }

        /// <summary>
        /// Computes a lens distortion correction factor for a given vertical field of view in degrees.
        /// </summary>
        /// <param name="verticalFovDegrees">Vertical FOV in degrees.</param>
        /// <returns>Distortion intensity suitable for the shader's radial model.</returns>
        public static float ComputeCorrectionFromFovDegrees(float verticalFovDegrees)
            => ComputeCorrectionFromFov(verticalFovDegrees * MathF.PI / 180.0f);
    }
}