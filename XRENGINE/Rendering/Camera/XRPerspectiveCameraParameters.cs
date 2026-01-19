using System;
using System.Numerics;
using XREngine.Data.Core;
using XREngine.Data.Geometry;

namespace XREngine.Rendering
{
    /// <summary>
    /// Standard perspective camera parameters using vertical FOV and aspect ratio.
    /// This is the most common camera type for 3D rendering.
    /// </summary>
    [CameraParameterEditor("Perspective", SortOrder = 0, Description = "Standard perspective projection with FOV and aspect ratio.")]
    public class XRPerspectiveCameraParameters : XRCameraParameters
    {
        private float _aspectRatio;
        private bool _inheritAspectRatio;
        private float _verticalFieldOfView;

        public XRPerspectiveCameraParameters(float verticalFieldOfView, float? aspectRatio, float nearPlane, float farPlane) : base(nearPlane, farPlane)
        {
            _verticalFieldOfView = verticalFieldOfView;
            _aspectRatio = aspectRatio ?? 1.0f;
            _inheritAspectRatio = aspectRatio is null;
        }

        public XRPerspectiveCameraParameters(float nearPlane, float farPlane) : base(nearPlane, farPlane)
        {
            _verticalFieldOfView = 60.0f;
            _aspectRatio = 1.0f;
            _inheritAspectRatio = true;
        }

        public XRPerspectiveCameraParameters() : this(0.1f, 10000.0f) { }

        /// <summary>
        /// Field of view on the Y axis in degrees.
        /// </summary>
        public float VerticalFieldOfView
        {
            get => _verticalFieldOfView;
            set => SetField(ref _verticalFieldOfView, value);
        }

        /// <summary>
        /// Field of view on the X axis in degrees.
        /// </summary>
        public float HorizontalFieldOfView
        {
            get => VerticalFieldOfView * AspectRatio;
            set => VerticalFieldOfView = value / AspectRatio;
        }

        /// <summary>
        /// The aspect ratio of the camera, calculated as width / height.
        /// </summary>
        public float AspectRatio
        {
            get => _aspectRatio;
            set => SetField(ref _aspectRatio, value);
        }

        /// <summary>
        /// If true, the aspect ratio will be inherited from the aspect ratio of the viewport.
        /// </summary>
        public bool InheritAspectRatio
        {
            get => _inheritAspectRatio;
            set => SetField(ref _inheritAspectRatio, value);
        }

        /// <summary>
        /// Easy way to set the aspect ratio by providing the width and height of the camera.
        /// </summary>
        /// <param name="width"></param>
        /// <param name="height"></param>
        public void SetAspectRatio(float width, float height)
            => AspectRatio = width / height;

        protected override Matrix4x4 CalculateProjectionMatrix()
        {
            float fovY = XRMath.DegToRad(VerticalFieldOfView);
            float yMax = NearZ * (float)MathF.Tan(0.5f * fovY);
            float yMin = -yMax;
            float xMin = yMin * AspectRatio;
            float xMax = yMax * AspectRatio;
            return Matrix4x4.CreatePerspectiveOffCenter(xMin, xMax, yMin, yMax, NearZ, FarZ);
        }

        protected override Frustum CalculateUntransformedFrustum()
            => new(VerticalFieldOfView, AspectRatio, NearZ, FarZ, Globals.Forward, Globals.Up, Vector3.Zero);

        public override void SetUniforms(XRRenderProgram program)
        {
            base.SetUniforms(program);
            program.Uniform(EEngineUniform.CameraFovY.ToString(), VerticalFieldOfView);
            program.Uniform(EEngineUniform.CameraFovX.ToString(), HorizontalFieldOfView);
            program.Uniform(EEngineUniform.CameraAspect.ToString(), AspectRatio);
        }

        public override Vector2 GetFrustumSizeAtDistance(float distance)
        {
            float height = 2.0f * distance * MathF.Tan(float.DegreesToRadians(VerticalFieldOfView) / 2.0f);
            float width = height * AspectRatio;
            return new Vector2(width, height);
        }

        public override float GetApproximateVerticalFov() => VerticalFieldOfView;
        public override float GetApproximateAspectRatio() => AspectRatio;

        /// <summary>
        /// Calculates the camera distance needed to achieve a specific frustum height at that distance.
        /// </summary>
        /// <param name="frustumHeight">The desired frustum height at the calculated distance.</param>
        /// <returns>The distance from camera where the frustum will have the specified height.</returns>
        public float GetDistanceForFrustumHeight(float frustumHeight)
        {
            float fovRad = float.DegreesToRadians(VerticalFieldOfView);
            return frustumHeight / (2f * MathF.Tan(fovRad / 2f));
        }

        /// <summary>
        /// Calculates the best FOV and distance to match an orthographic view.
        /// </summary>
        /// <param name="orthoHeight">The orthographic view height.</param>
        /// <param name="targetFov">The desired target FOV in degrees. If null, uses 60 degrees.</param>
        /// <returns>A tuple of (fov, distance) where distance is how far back the camera should be positioned.</returns>
        public static (float fov, float distance) CalculateForOrthoMatch(float orthoHeight, float? targetFov = null)
        {
            float fov = targetFov ?? 60f;
            float fovRad = fov * MathF.PI / 180f;
            float distance = orthoHeight / (2f * MathF.Tan(fovRad / 2f));
            return (fov, distance);
        }

        /// <summary>
        /// Creates a new perspective camera from previous parameters.
        /// Intelligently converts FOV from physical cameras and other types.
        /// For orthographic cameras, calculates appropriate FOV based on view size.
        /// </summary>
        public override XRCameraParameters CreateFromPrevious(XRCameraParameters? previous)
        {
            if (previous is null)
                return new XRPerspectiveCameraParameters();

            if (previous is XRPerspectiveCameraParameters persp)
            {
                return new XRPerspectiveCameraParameters(
                    persp.VerticalFieldOfView,
                    persp.InheritAspectRatio ? null : persp.AspectRatio,
                    persp.NearZ,
                    persp.FarZ)
                {
                    InheritAspectRatio = persp.InheritAspectRatio
                };
            }

            if (previous is XROrthographicCameraParameters ortho)
            {
                // For orthographic, calculate a reasonable FOV based on the ortho dimensions
                // We'll use a default FOV and store the aspect ratio
                float fov = 60f;
                float aspect = ortho.Width / ortho.Height;
                return new XRPerspectiveCameraParameters(fov, aspect, ortho.NearZ, ortho.FarZ)
                {
                    InheritAspectRatio = false // Preserve the ortho aspect ratio initially
                };
            }

            // Use approximate FOV from other types
            float approxFov = previous.GetApproximateVerticalFov();
            return new XRPerspectiveCameraParameters(approxFov, null, previous.NearZ, previous.FarZ)
            {
                InheritAspectRatio = true
            };
        }

        /// <summary>
        /// Creates a perspective camera configured to match an orthographic view at the given focus distance.
        /// The caller should position the camera at the returned distance from the focus point.
        /// </summary>
        /// <param name="ortho">The orthographic camera to match.</param>
        /// <param name="targetFov">The desired FOV in degrees.</param>
        /// <returns>A tuple of (parameters, distance) where distance is how far to position the camera.</returns>
        public static (XRPerspectiveCameraParameters parameters, float distance) CreateFromOrthographic(
            XROrthographicCameraParameters ortho,
            float targetFov = 60f)
        {
            float aspect = ortho.Width / ortho.Height;
            float fovRad = targetFov * MathF.PI / 180f;
            float distance = ortho.Height / (2f * MathF.Tan(fovRad / 2f));
            
            var persp = new XRPerspectiveCameraParameters(targetFov, aspect, ortho.NearZ, ortho.FarZ)
            {
                InheritAspectRatio = false
            };
            
            return (persp, distance);
        }

        protected override XRCameraParameters CreateDefaultInstance()
            => new XRPerspectiveCameraParameters();

        public Frustum GetUntransformedFrustumSlice(float nearZ, float farZ)
            => new(VerticalFieldOfView, AspectRatio, nearZ, farZ, Globals.Forward, Globals.Up, Vector3.Zero);

        public Matrix4x4 GetProjectionSlice(float nearZ, float farZ)
        {
            float fovY = XRMath.DegToRad(VerticalFieldOfView);
            float yMax = nearZ * (float)MathF.Tan(0.5f * fovY);
            float yMin = -yMax;
            float xMin = yMin * AspectRatio;
            float xMax = yMax * AspectRatio;
            return Matrix4x4.CreatePerspectiveOffCenter(xMin, xMax, yMin, yMax, nearZ, farZ);
        }

        public Matrix4x4 GetNormalizedProjectionSlice(float nearDepth, float farDepth, bool reversedZ = false)
        {
            float nearZ = XRMath.DepthToDistance(nearDepth, NearZ, FarZ, reversedZ);
            float farZ = XRMath.DepthToDistance(farDepth, NearZ, FarZ, reversedZ);
            return GetProjectionSlice(nearZ, farZ);
        }

        override public string ToString()
            => $"NearZ: {NearZ}, FarZ: {FarZ}, Vertical FOV: {VerticalFieldOfView}, Aspect Ratio: {AspectRatio}";
    }
}
