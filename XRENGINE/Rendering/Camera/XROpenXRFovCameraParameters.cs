using System;
using System.Numerics;
using XREngine.Data.Geometry;

namespace XREngine.Rendering
{
    /// <summary>
    /// OpenXR-style asymmetric FOV camera parameters.
    /// Angles are in radians and match XrFovf: left/right/up/down.
    /// Useful for VR rendering with asymmetric projections.
    /// </summary>
    [CameraParameterEditor("OpenXR FOV", SortOrder = 3, Category = "VR", Description = "Asymmetric FOV camera for OpenXR/VR rendering.")]
    public class XROpenXRFovCameraParameters(float nearPlane, float farPlane) : XRCameraParameters(nearPlane, farPlane)
    {
        private float _angleLeft;
        private float _angleRight;
        private float _angleUp;
        private float _angleDown;

        public float AngleLeft
        {
            get => _angleLeft;
            set => SetField(ref _angleLeft, value);
        }

        public float AngleRight
        {
            get => _angleRight;
            set => SetField(ref _angleRight, value);
        }

        public float AngleUp
        {
            get => _angleUp;
            set => SetField(ref _angleUp, value);
        }

        public float AngleDown
        {
            get => _angleDown;
            set => SetField(ref _angleDown, value);
        }

        public void SetAngles(float left, float right, float up, float down)
        {
            bool changed =
                _angleLeft != left ||
                _angleRight != right ||
                _angleUp != up ||
                _angleDown != down;

            if (!changed)
                return;
            
            AngleLeft = left;
            AngleRight = right;
            AngleUp = up;
            AngleDown = down;
        }

        protected override Matrix4x4 CalculateProjectionMatrix()
        {
            float n = NearZ;
            if (n <= float.Epsilon)
                n = 0.001f;

            float l = n * MathF.Tan(_angleLeft);
            float r = n * MathF.Tan(_angleRight);
            float b = n * MathF.Tan(_angleDown);
            float t = n * MathF.Tan(_angleUp);

            return Matrix4x4.CreatePerspectiveOffCenter(l, r, b, t, n, FarZ);
        }

        protected override Frustum CalculateUntransformedFrustum()
        {
            Matrix4x4 proj = _projectionMatrix ?? CalculateProjectionMatrix();
            if (!Matrix4x4.Invert(proj, out Matrix4x4 invProj))
                invProj = Matrix4x4.Identity;
            return new Frustum(invProj);
        }

        public override Vector2 GetFrustumSizeAtDistance(float distance)
        {
            float w = distance * (MathF.Tan(_angleRight) - MathF.Tan(_angleLeft));
            float h = distance * (MathF.Tan(_angleUp) - MathF.Tan(_angleDown));
            return new Vector2(w, h);
        }

        public override void SetUniforms(XRRenderProgram program)
        {
            base.SetUniforms(program);

            float hFov = float.RadiansToDegrees(_angleRight - _angleLeft);
            float vFov = float.RadiansToDegrees(_angleUp - _angleDown);
            float aspect = hFov <= float.Epsilon ? 1.0f : hFov / MathF.Max(float.Epsilon, vFov);

            program.Uniform(EEngineUniform.CameraFovY.ToString(), vFov);
            program.Uniform(EEngineUniform.CameraFovX.ToString(), hFov);
            program.Uniform(EEngineUniform.CameraAspect.ToString(), aspect);
        }

        public override string ToString()
            => $"NearZ: {NearZ}, FarZ: {FarZ}, Angles(rad): L={_angleLeft}, R={_angleRight}, U={_angleUp}, D={_angleDown}";

        public override float GetApproximateVerticalFov()
            => float.RadiansToDegrees(_angleUp - _angleDown);

        public override float GetApproximateAspectRatio()
        {
            float hFov = _angleRight - _angleLeft;
            float vFov = _angleUp - _angleDown;
            return vFov <= float.Epsilon ? 1.0f : hFov / vFov;
        }

        /// <summary>
        /// Creates a new OpenXR FOV camera from previous parameters.
        /// Converts symmetric FOV to asymmetric angles when needed.
        /// </summary>
        public override XRCameraParameters CreateFromPrevious(XRCameraParameters? previous)
        {
            var result = new XROpenXRFovCameraParameters(previous?.NearZ ?? 0.1f, previous?.FarZ ?? 10000f);

            if (previous is XROpenXRFovCameraParameters fov)
            {
                result.SetAngles(fov.AngleLeft, fov.AngleRight, fov.AngleUp, fov.AngleDown);
            }
            else if (previous is not null)
            {
                // Convert symmetric FOV to angle parameters
                float vFov = previous.GetApproximateVerticalFov();
                float aspect = previous.GetApproximateAspectRatio();
                float halfVFovRad = (vFov * MathF.PI / 180.0f) / 2.0f;
                float halfHFovRad = halfVFovRad * aspect;
                result.SetAngles(-halfHFovRad, halfHFovRad, halfVFovRad, -halfVFovRad);
            }
            else
            {
                // Default 90-degree symmetric FOV
                float halfFov = MathF.PI / 4.0f; // 45 degrees
                result.SetAngles(-halfFov, halfFov, halfFov, -halfFov);
            }

            return result;
        }

        protected override XRCameraParameters CreateDefaultInstance()
            => new XROpenXRFovCameraParameters(0.1f, 10000f);
    }
}
