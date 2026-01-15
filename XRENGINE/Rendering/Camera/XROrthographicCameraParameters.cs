using System;
using System.Numerics;
using XREngine.Data.Geometry;

namespace XREngine.Rendering
{
    /// <summary>
    /// Orthographic camera parameters for 2D or isometric rendering.
    /// Objects appear the same size regardless of distance.
    /// </summary>
    [CameraParameterEditor("Orthographic", SortOrder = 1, Description = "Orthographic projection for 2D/UI or isometric rendering.")]
    public class XROrthographicCameraParameters : XRCameraParameters
    {
        private Vector2 _originPercentages = Vector2.Zero;
        private Vector2 _origin;

        public Vector2 Origin => _origin;

        private float _orthoLeft = 0.0f;
        private float _orthoRight = 1.0f;
        private float _orthoBottom = 0.0f;
        private float _orthoTop = 1.0f;

        private float _orthoLeftPercentage = 0.0f;
        private float _orthoRightPercentage = 1.0f;
        private float _orthoBottomPercentage = 0.0f;
        private float _orthoTopPercentage = 1.0f;
        private float _width;
        private float _height;
        private bool _inheritAspectRatio = true;

        public XROrthographicCameraParameters(float width, float height, float nearPlane, float farPlane) : base(nearPlane, farPlane)
        {
            _width = width;
            _height = height;
            Resized();
        }

        public XROrthographicCameraParameters() : this(1.0f, 1.0f, 0.1f, 10000.0f)
        {
        }

        /// <summary>
        /// Width of the orthographic view in world units.
        /// When <see cref="InheritAspectRatio"/> is true, this is automatically calculated from Height and the viewport's aspect ratio.
        /// </summary>
        public float Width
        {
            get => _width;
            set
            {
                // Capture aspect ratio before changing width
                float currentAspect = AspectRatio;
                if (SetField(ref _width, value) && _inheritAspectRatio && currentAspect > 0)
                {
                    // Maintain the aspect ratio by updating height
                    _height = value / currentAspect;
                    OnPropertyChanged(nameof(Height), _height * currentAspect, _height);
                }
            }
        }

        /// <summary>
        /// Height of the orthographic view in world units.
        /// This is the primary size control - Width is derived from this when InheritAspectRatio is enabled.
        /// </summary>
        public float Height 
        {
            get => _height;
            set
            {
                // Capture aspect ratio before changing height
                float currentAspect = AspectRatio;
                if (SetField(ref _height, value) && _inheritAspectRatio && currentAspect > 0)
                {
                    // Maintain the aspect ratio by updating width
                    _width = value * currentAspect;
                    OnPropertyChanged(nameof(Width), _width / currentAspect, _width);
                }
            }
        }

        /// <summary>
        /// If true, the aspect ratio will be inherited from the viewport, and Width will be automatically
        /// calculated as Height * viewportAspectRatio. Similar to perspective camera's InheritAspectRatio.
        /// </summary>
        public bool InheritAspectRatio
        {
            get => _inheritAspectRatio;
            set => SetField(ref _inheritAspectRatio, value);
        }

        /// <summary>
        /// The current aspect ratio (Width / Height).
        /// </summary>
        public float AspectRatio => _height > 0 ? _width / _height : 1f;

        /// <summary>
        /// Sets the aspect ratio by providing viewport dimensions.
        /// When InheritAspectRatio is true, this updates Width to maintain the aspect ratio based on current Height.
        /// Called by the viewport when it resizes.
        /// </summary>
        public void SetAspectRatio(float viewportWidth, float viewportHeight)
        {
            if (!_inheritAspectRatio || viewportHeight <= 0)
                return;

            float aspectRatio = viewportWidth / viewportHeight;
            _width = _height * aspectRatio;
            Resized();
        }

        public void Resize(float width, float height)
        {
            _width = width;
            _height = height;
            Resized();
        }

        /// <summary>
        /// Scales the orthographic view uniformly by the given factor, maintaining aspect ratio.
        /// Values > 1 zoom out (show more), values &lt; 1 zoom in (show less).
        /// </summary>
        public void Scale(float factor)
        {
            if (factor <= 0)
                return;
            _width *= factor;
            _height *= factor;
            Resized();
        }

        public void SetOriginCentered()
            => SetOriginPercentages(0.5f, 0.5f);
        public void SetOriginBottomLeft()
            => SetOriginPercentages(0.0f, 0.0f);
        public void SetOriginTopLeft()
            => SetOriginPercentages(0.0f, 1.0f);
        public void SetOriginBottomRight()
            => SetOriginPercentages(1.0f, 0.0f);
        public void SetOriginTopRight()
            => SetOriginPercentages(1.0f, 1.0f);

        public void SetOriginPercentages(Vector2 percentages)
            => SetOriginPercentages(percentages.X, percentages.Y);
        public void SetOriginPercentages(float xPercentage, float yPercentage)
        {
            _originPercentages.X = xPercentage;
            _originPercentages.Y = yPercentage;
            _orthoLeftPercentage = 0.0f - xPercentage;
            _orthoRightPercentage = 1.0f - xPercentage;
            _orthoBottomPercentage = 0.0f - yPercentage;
            _orthoTopPercentage = 1.0f - yPercentage;
            Resized();
        }
        private void Resized()
        {
            _orthoLeft = _orthoLeftPercentage * Width;
            _orthoRight = _orthoRightPercentage * Width;
            _orthoBottom = _orthoBottomPercentage * Height;
            _orthoTop = _orthoTopPercentage * Height;
            _origin = new Vector2(_orthoLeft, _orthoBottom) + _originPercentages * new Vector2(Width, Height);
            ForceInvalidateProjection();
        }

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            switch (propName)
            {
                case nameof(Width):
                case nameof(Height):
                    Resized();
                    break;
            }
        }

        public Vector3 AlignScreenPoint(Vector3 screenPoint)
            => new(screenPoint.X + _orthoLeft, screenPoint.Y + _orthoBottom, screenPoint.Z);
        public Vector3 UnAlignScreenPoint(Vector3 screenPoint)
            => new(screenPoint.X - _orthoLeft, screenPoint.Y - _orthoBottom, screenPoint.Z);

        protected override Matrix4x4 CalculateProjectionMatrix()
            => Matrix4x4.CreateOrthographicOffCenter(_orthoLeft, _orthoRight, _orthoBottom, _orthoTop, NearZ, FarZ);

        protected override Frustum CalculateUntransformedFrustum()
            => new(Width, Height, NearZ, FarZ);

        public override void SetUniforms(XRRenderProgram program)
        {
            //base.SetUniforms(program);
            program.Uniform(EEngineUniform.CameraNearZ.ToString(), NearZ);
            program.Uniform(EEngineUniform.CameraFarZ.ToString(), FarZ);

            program.Uniform(EEngineUniform.ScreenWidth.ToString(), Width);
            program.Uniform(EEngineUniform.ScreenHeight.ToString(), Height);
            program.Uniform(EEngineUniform.ScreenOrigin.ToString(), Origin);
        }

        public BoundingRectangleF GetBounds()
            => new(_orthoLeft, _orthoRight, _orthoBottom, _orthoTop);

        public override Vector2 GetFrustumSizeAtDistance(float drawDistance)
            => new(Width, Height);

        public override float GetApproximateVerticalFov()
        {
            // Orthographic has no real FOV, but we can approximate one based on the view size
            // Use a reference distance of 10 units to compute an equivalent FOV
            const float referenceDistance = 10f;
            return 2f * MathF.Atan2(Height / 2f, referenceDistance) * (180f / MathF.PI);
        }

        public override float GetApproximateAspectRatio()
            => Height > 0 ? Width / Height : 1f;

        /// <summary>
        /// Calculates the best perspective FOV and camera distance to match this orthographic view.
        /// The returned FOV and distance will produce a perspective frustum that matches
        /// the orthographic view size at the specified focus distance.
        /// </summary>
        /// <param name="targetFov">Desired target FOV in degrees. If null, uses 60 degrees.</param>
        /// <returns>A tuple of (fov, distance) where distance is how far back the camera should be.</returns>
        public (float fov, float distance) CalculatePerspectiveEquivalent(float? targetFov = null)
        {
            float fov = targetFov ?? 60f;
            float fovRad = fov * MathF.PI / 180f;
            // distance = frustumHeight / (2 * tan(fov/2))
            float distance = Height / (2f * MathF.Tan(fovRad / 2f));
            return (fov, distance);
        }

        /// <summary>
        /// Creates a new orthographic camera from previous parameters.
        /// For perspective cameras, uses the frustum size at a reasonable focus distance.
        /// </summary>
        public override XRCameraParameters CreateFromPrevious(XRCameraParameters? previous)
        {
            if (previous is null)
                return new XROrthographicCameraParameters();

            if (previous is XROrthographicCameraParameters ortho)
            {
                var result = new XROrthographicCameraParameters(ortho.Width, ortho.Height, ortho.NearZ, ortho.FarZ)
                {
                    InheritAspectRatio = ortho.InheritAspectRatio
                };
                result.SetOriginPercentages(ortho._originPercentages);
                return result;
            }

            // For perspective cameras, calculate a reasonable focus distance based on near/far range
            // Use a distance that's about 10 units or near + 1, whichever is larger
            float focusDistance = MathF.Max(previous.NearZ + 1.0f, 10.0f);
            Vector2 frustum = previous.GetFrustumSizeAtDistance(focusDistance);
            
            var newOrtho = new XROrthographicCameraParameters(
                MathF.Max(0.1f, frustum.X),
                MathF.Max(0.1f, frustum.Y),
                previous.NearZ,
                previous.FarZ);
            
            // Center the view and inherit aspect ratio from viewport
            newOrtho.SetOriginCentered();
            newOrtho.InheritAspectRatio = true;
            
            return newOrtho;
        }

        /// <summary>
        /// Creates an orthographic camera that matches the view of a perspective camera at the given focus distance.
        /// </summary>
        public static XROrthographicCameraParameters CreateFromPerspective(
            XRPerspectiveCameraParameters perspective, 
            float focusDistance)
        {
            Vector2 frustum = perspective.GetFrustumSizeAtDistance(focusDistance);
            var ortho = new XROrthographicCameraParameters(
                frustum.X,
                frustum.Y,
                perspective.NearZ,
                perspective.FarZ);
            ortho.SetOriginCentered();
            ortho.InheritAspectRatio = perspective.InheritAspectRatio;
            return ortho;
        }

        protected override XRCameraParameters CreateDefaultInstance()
            => new XROrthographicCameraParameters();

        public override string ToString()
            => $"NearZ: {NearZ}, FarZ: {FarZ}, Width: {Width}, Height: {Height}, Origin: {Origin}";
    }
}
