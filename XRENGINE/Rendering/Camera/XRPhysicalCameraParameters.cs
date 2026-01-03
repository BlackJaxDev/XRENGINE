using System;
using System.Numerics;
using XREngine.Data.Geometry;

namespace XREngine.Rendering
{
    /// <summary>
    /// Perspective camera parameters derived from physical (real-world) pinhole camera intrinsics.
    ///
    /// This maps common physical inputs (sensor/filmback size and focal length) into an off-axis
    /// perspective projection using <see cref="Matrix4x4.CreatePerspectiveOffCenter"/>.
    ///
    /// Notes:
    /// - The projection matrix models an ideal pinhole camera. Real lens distortion should be applied
    ///   separately (e.g. post-process), which XRCamera already supports.
    /// - Principal point is expressed in pixels using the common computer-vision convention:
    ///   origin at the top-left of the image, X right, Y down.
    /// </summary>
    public class XRPhysicalCameraParameters : XRCameraParameters
    {
        private float _sensorWidthMm;
        private float _sensorHeightMm;
        private float _focalLengthMm;

        private int _resolutionWidthPx;
        private int _resolutionHeightPx;
        private bool _inheritResolution;

        private Vector2 _principalPointPx;
        private bool _inheritPrincipalPoint;

        public XRPhysicalCameraParameters(
            float sensorWidthMm,
            float sensorHeightMm,
            float focalLengthMm,
            int resolutionWidthPx,
            int resolutionHeightPx,
            float nearPlane,
            float farPlane) : base(nearPlane, farPlane)
        {
            _sensorWidthMm = sensorWidthMm;
            _sensorHeightMm = sensorHeightMm;
            _focalLengthMm = focalLengthMm;

            _resolutionWidthPx = resolutionWidthPx;
            _resolutionHeightPx = resolutionHeightPx;

            _inheritResolution = false;
            _inheritPrincipalPoint = true;
            _principalPointPx = default;
        }

        /// <summary>
        /// Default physical camera parameters:
        /// - Full-frame sensor (36x24mm)
        /// - 50mm focal length
        /// - Resolution inherited from current render area
        /// </summary>
        public XRPhysicalCameraParameters() : base(0.1f, 10000.0f)
        {
            _sensorWidthMm = 36.0f;
            _sensorHeightMm = 24.0f;
            _focalLengthMm = 50.0f;

            _resolutionWidthPx = 1920;
            _resolutionHeightPx = 1080;
            _inheritResolution = true;

            _inheritPrincipalPoint = true;
            _principalPointPx = default;
        }

        /// <summary>
        /// Sensor/filmback width in millimeters.
        /// </summary>
        public float SensorWidthMm
        {
            get => _sensorWidthMm;
            set => SetField(ref _sensorWidthMm, value);
        }

        /// <summary>
        /// Sensor/filmback height in millimeters.
        /// </summary>
        public float SensorHeightMm
        {
            get => _sensorHeightMm;
            set => SetField(ref _sensorHeightMm, value);
        }

        /// <summary>
        /// Lens focal length in millimeters.
        /// </summary>
        public float FocalLengthMm
        {
            get => _focalLengthMm;
            set => SetField(ref _focalLengthMm, value);
        }

        /// <summary>
        /// If true, the output resolution used for projection math is taken from the active render area.
        /// If false, <see cref="ResolutionWidthPx"/> and <see cref="ResolutionHeightPx"/> are used.
        /// </summary>
        public bool InheritResolution
        {
            get => _inheritResolution;
            set => SetField(ref _inheritResolution, value);
        }

        /// <summary>
        /// The output resolution width in pixels (only used when <see cref="InheritResolution"/> is false).
        /// </summary>
        public int ResolutionWidthPx
        {
            get => _resolutionWidthPx;
            set => SetField(ref _resolutionWidthPx, value);
        }

        /// <summary>
        /// The output resolution height in pixels (only used when <see cref="InheritResolution"/> is false).
        /// </summary>
        public int ResolutionHeightPx
        {
            get => _resolutionHeightPx;
            set => SetField(ref _resolutionHeightPx, value);
        }

        /// <summary>
        /// If true, the principal point is assumed to be the image center.
        /// If false, <see cref="PrincipalPointPx"/> is used.
        /// </summary>
        public bool InheritPrincipalPoint
        {
            get => _inheritPrincipalPoint;
            set => SetField(ref _inheritPrincipalPoint, value);
        }

        /// <summary>
        /// Principal point (optical center) in pixels.
        ///
        /// Convention: origin at top-left of the image, X right, Y down.
        /// Only used when <see cref="InheritPrincipalPoint"/> is false.
        /// </summary>
        public Vector2 PrincipalPointPx
        {
            get => _principalPointPx;
            set => SetField(ref _principalPointPx, value);
        }

        /// <summary>
        /// Vertical field of view (degrees), derived from sensor height and focal length.
        /// </summary>
        public float VerticalFieldOfViewDegrees
            => 2.0f * float.RadiansToDegrees(MathF.Atan2(SensorHeightMm * 0.5f, FocalLengthMm));

        /// <summary>
        /// Horizontal field of view (degrees), derived from sensor width and focal length.
        /// </summary>
        public float HorizontalFieldOfViewDegrees
            => 2.0f * float.RadiansToDegrees(MathF.Atan2(SensorWidthMm * 0.5f, FocalLengthMm));

        private void ResolveResolution(out float widthPx, out float heightPx)
        {
            if (InheritResolution)
            {
                var area = Engine.Rendering.State.RenderArea;
                widthPx = MathF.Max(1.0f, area.Width);
                heightPx = MathF.Max(1.0f, area.Height);
                return;
            }

            widthPx = MathF.Max(1.0f, _resolutionWidthPx);
            heightPx = MathF.Max(1.0f, _resolutionHeightPx);
        }

        private void ResolvePrincipalPoint(float widthPx, float heightPx, out float cx, out float cy)
        {
            if (InheritPrincipalPoint)
            {
                cx = 0.5f * widthPx;
                cy = 0.5f * heightPx;
                return;
            }

            cx = _principalPointPx.X;
            cy = _principalPointPx.Y;
        }

        protected override Matrix4x4 CalculateProjectionMatrix()
        {
            float n = NearZ;

            // Avoid divisions by zero. (Perspective cameras require a positive near plane.)
            if (n <= float.Epsilon)
                n = 0.001f;

            ResolveResolution(out float widthPx, out float heightPx);
            ResolvePrincipalPoint(widthPx, heightPx, out float cx, out float cy);

            float sensorW = MathF.Max(float.Epsilon, SensorWidthMm);
            float sensorH = MathF.Max(float.Epsilon, SensorHeightMm);
            float focal = MathF.Max(float.Epsilon, FocalLengthMm);

            // Intrinsics in pixel units.
            float fx = focal * widthPx / sensorW;
            float fy = focal * heightPx / sensorH;

            // Off-axis frustum at the near plane.
            // Principal point convention: origin top-left, Y down.
            float xMin = -(cx / fx) * n;
            float xMax = ((widthPx - cx) / fx) * n;

            float yMax = (cy / fy) * n;
            float yMin = -((heightPx - cy) / fy) * n;

            return Matrix4x4.CreatePerspectiveOffCenter(xMin, xMax, yMin, yMax, n, FarZ);
        }

        protected override Frustum CalculateUntransformedFrustum()
        {
            // The engine's frustum helpers support reconstruction from inverse projection.
            // This also correctly handles asymmetric (off-axis) projections.
            Matrix4x4 proj = _projectionMatrix ?? CalculateProjectionMatrix();
            if (!Matrix4x4.Invert(proj, out Matrix4x4 invProj))
                invProj = Matrix4x4.Identity;
            return new Frustum(invProj);
        }

        public override void SetUniforms(XRRenderProgram program)
        {
            base.SetUniforms(program);

            // Provide common perspective uniforms so shaders that expect fov/aspect still work.
            ResolveResolution(out float widthPx, out float heightPx);
            float aspect = widthPx / MathF.Max(1.0f, heightPx);

            program.Uniform(EEngineUniform.CameraFovY.ToString(), VerticalFieldOfViewDegrees);
            program.Uniform(EEngineUniform.CameraFovX.ToString(), HorizontalFieldOfViewDegrees);
            program.Uniform(EEngineUniform.CameraAspect.ToString(), aspect);
        }

        public override Vector2 GetFrustumSizeAtDistance(float distance)
        {
            float n = NearZ;
            if (n <= float.Epsilon)
                n = 0.001f;

            ResolveResolution(out float widthPx, out float heightPx);
            ResolvePrincipalPoint(widthPx, heightPx, out float cx, out float cy);

            float sensorW = MathF.Max(float.Epsilon, SensorWidthMm);
            float sensorH = MathF.Max(float.Epsilon, SensorHeightMm);
            float focal = MathF.Max(float.Epsilon, FocalLengthMm);

            float fx = focal * widthPx / sensorW;
            float fy = focal * heightPx / sensorH;

            float xMinN = -(cx / fx) * n;
            float xMaxN = ((widthPx - cx) / fx) * n;

            float yMaxN = (cy / fy) * n;
            float yMinN = -((heightPx - cy) / fy) * n;

            float scale = distance / n;
            float width = (xMaxN - xMinN) * scale;
            float height = (yMaxN - yMinN) * scale;
            return new Vector2(width, height);
        }

        public override string ToString()
            => $"NearZ: {NearZ}, FarZ: {FarZ}, Sensor: {SensorWidthMm}x{SensorHeightMm}mm, Focal: {FocalLengthMm}mm";
    }
}
