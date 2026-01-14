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
        /// Creates a new perspective camera from previous parameters.
        /// Intelligently converts FOV from physical cameras and other types.
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

            // Use approximate FOV from other types
            float fov = previous.GetApproximateVerticalFov();
            return new XRPerspectiveCameraParameters(fov, null, previous.NearZ, previous.FarZ)
            {
                InheritAspectRatio = true
            };
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

        public Matrix4x4 GetNormalizedProjectionSlice(float nearDepth, float farDepth)
        {
            float nearZ = XRMath.DepthToDistance(nearDepth, NearZ, FarZ);
            float farZ = XRMath.DepthToDistance(farDepth, NearZ, FarZ);
            return GetProjectionSlice(nearZ, farZ);
        }

        override public string ToString()
            => $"NearZ: {NearZ}, FarZ: {FarZ}, Vertical FOV: {VerticalFieldOfView}, Aspect Ratio: {AspectRatio}";
    }
}
