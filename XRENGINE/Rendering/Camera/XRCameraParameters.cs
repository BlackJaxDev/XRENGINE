using System.Numerics;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XRBase = XREngine.Data.Core.XRBase;

namespace XREngine.Rendering
{
    /// <summary>
    /// Abstract base class for camera projection parameters.
    /// Derived classes define specific projection types (perspective, orthographic, physical, etc.).
    /// 
    /// To create a custom camera parameter type:
    /// 1. Derive from this class and implement the abstract methods
    /// 2. Optionally apply <see cref="CameraParameterEditorAttribute"/> for editor customization
    /// 3. Optionally override <see cref="CreateFromPrevious"/> for smart type conversion
    /// </summary>
    public abstract class XRCameraParameters(float nearPlane, float farPlane) : XRBase
    {
        public XREvent<XRCameraParameters>? ProjectionMatrixChanged { get; }

        public void ForceInvalidateProjection()
            => _projectionMatrix = null;

        protected bool ProjectionInvalidated
            => _projectionMatrix is null;

        protected Matrix4x4? _projectionMatrix;
        protected Frustum? _untransformedFrustum;

        /// <summary>
        /// The distance to the near clipping plane (closest to the eye).
        /// This value must be less than the far plane distance but does not need to be positive for orthographic cameras.
        /// </summary>
        public float NearZ
        {
            get => nearPlane;
            set => SetField(ref nearPlane, value);
        }

        /// <summary>
        /// The distance to the far clipping plane (farthest from the eye).
        /// This value must be greater than the near plane distance.
        /// </summary>
        public float FarZ
        {
            get => farPlane;
            set => SetField(ref farPlane, value);
        }

        public override string ToString()
            => $"NearZ: {NearZ}, FarZ: {FarZ}";

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            _projectionMatrix = null;
        }

        private void VerifyProjection()
        {
            bool changed = false;

            if (_projectionMatrix is null)
            {
                _projectionMatrix = CalculateProjectionMatrix();
                changed = true;
            }

            if (_untransformedFrustum is null || changed)
                _untransformedFrustum = CalculateUntransformedFrustum();

            if (changed)
                ProjectionMatrixChanged?.Invoke(this);
        }

        /// <summary>
        /// Returns the projection matrix for the parameters set in this class.
        /// Recalculates the projection matrix if it has been invalidated by any parameter changes.
        /// </summary>
        /// <returns></returns>
        public Matrix4x4 GetProjectionMatrix()
        {
            VerifyProjection();
            var matrix = _projectionMatrix;
            return matrix ?? CalculateProjectionMatrix();
        }

        /// <summary>
        /// Requests the projection matrix to be recalculated by a derived class.
        /// </summary>
        /// <returns></returns>
        protected abstract Matrix4x4 CalculateProjectionMatrix();

        private object _untransformedFrustumLock = new();
        public Frustum GetUntransformedFrustum()
        {
            lock (_untransformedFrustumLock)
            {
                VerifyProjection();
                return _untransformedFrustum!.Value;
            }
        }
        protected abstract Frustum CalculateUntransformedFrustum();

        public virtual void SetUniforms(XRRenderProgram program)
        {
            program.Uniform(EEngineUniform.CameraNearZ.ToString(), NearZ);
            program.Uniform(EEngineUniform.CameraFarZ.ToString(), FarZ);

            var area = Engine.Rendering.State.RenderArea;
            program.Uniform(EEngineUniform.ScreenWidth.ToString(), (float)area.Width);
            program.Uniform(EEngineUniform.ScreenHeight.ToString(), (float)area.Height);
            program.Uniform(EEngineUniform.ScreenOrigin.ToString(), new Vector2(0.0f, 0.0f));
        }

        public abstract Vector2 GetFrustumSizeAtDistance(float drawDistance);

        #region Type Conversion

        /// <summary>
        /// Creates a new instance of this camera parameter type, optionally copying settings from a previous instance.
        /// Override this method in derived classes to provide intelligent conversion between parameter types.
        /// 
        /// The default implementation creates a new instance using <see cref="CreateDefaultInstance"/>
        /// and copies only the near/far plane values.
        /// </summary>
        /// <param name="previous">The previous camera parameters to convert from, or null for default values.</param>
        /// <returns>A new instance of this camera parameter type with appropriate settings.</returns>
        /// <example>
        /// <code>
        /// public override XRCameraParameters CreateFromPrevious(XRCameraParameters? previous)
        /// {
        ///     var instance = new MyCustomCameraParameters(previous?.NearZ ?? 0.1f, previous?.FarZ ?? 10000f);
        ///     
        ///     // Copy relevant settings from known types
        ///     if (previous is XRPerspectiveCameraParameters persp)
        ///         instance.FieldOfView = persp.VerticalFieldOfView;
        ///     else if (previous is MyCustomCameraParameters custom)
        ///         instance.CopyFrom(custom);
        ///     
        ///     return instance;
        /// }
        /// </code>
        /// </example>
        public virtual XRCameraParameters CreateFromPrevious(XRCameraParameters? previous)
        {
            var instance = CreateDefaultInstance();
            if (previous is not null)
            {
                instance.NearZ = previous.NearZ;
                instance.FarZ = previous.FarZ;
            }
            return instance;
        }

        /// <summary>
        /// Creates a new instance of this camera parameter type with default values.
        /// Override this method to provide custom default construction logic.
        /// </summary>
        /// <returns>A new instance with default values.</returns>
        protected virtual XRCameraParameters CreateDefaultInstance()
        {
            // Try to create using parameterless constructor first
            var type = GetType();
            try
            {
                var instance = Activator.CreateInstance(type) as XRCameraParameters;
                if (instance is not null)
                    return instance;
            }
            catch
            {
                // Fall through to constructor with parameters
            }

            // Try constructor with (float, float) signature for near/far
            try
            {
                var ctor = type.GetConstructor([typeof(float), typeof(float)]);
                if (ctor is not null)
                {
                    var instance = ctor.Invoke([0.1f, 10000f]) as XRCameraParameters;
                    if (instance is not null)
                        return instance;
                }
            }
            catch
            {
                // Fall through
            }

            // As a last resort, return a perspective camera
            return new XRPerspectiveCameraParameters(0.1f, 10000f);
        }

        /// <summary>
        /// Gets the approximate vertical field of view in degrees for this camera.
        /// Used for conversion between camera types. Override for accurate values.
        /// </summary>
        /// <returns>Vertical FOV in degrees, or 90 if not applicable.</returns>
        public virtual float GetApproximateVerticalFov()
        {
            // Default to 90 degrees; derived classes should override for accuracy
            return 90.0f;
        }

        /// <summary>
        /// Gets the approximate aspect ratio for this camera.
        /// Used for conversion between camera types. Override for accurate values.
        /// </summary>
        /// <returns>Aspect ratio (width/height), or 16/9 if not applicable.</returns>
        public virtual float GetApproximateAspectRatio()
        {
            // Default to 16:9; derived classes should override for accuracy
            return 16.0f / 9.0f;
        }

        /// <summary>
        /// Creates a new instance of the specified camera parameter type, converting from an existing instance.
        /// This is the primary factory method for creating camera parameters of any type.
        /// </summary>
        /// <param name="targetType">The type of camera parameters to create. Must derive from <see cref="XRCameraParameters"/>.</param>
        /// <param name="previous">The previous parameters to convert from, or null for defaults.</param>
        /// <returns>A new instance of the target type with converted settings.</returns>
        /// <exception cref="ArgumentException">Thrown if <paramref name="targetType"/> is not a valid camera parameter type.</exception>
        public static XRCameraParameters CreateFromType(Type targetType, XRCameraParameters? previous)
        {
            if (targetType is null || !typeof(XRCameraParameters).IsAssignableFrom(targetType))
                throw new ArgumentException($"Type must derive from {nameof(XRCameraParameters)}", nameof(targetType));

            // Create a temporary instance to call CreateFromPrevious on
            XRCameraParameters? template = null;
            float nearZ = previous?.NearZ ?? 0.1f;
            float farZ = previous?.FarZ ?? 10000f;
            
            // Try parameterless constructor first
            try
            {
                template = Activator.CreateInstance(targetType) as XRCameraParameters;
            }
            catch { }

            // Try constructor with (float, float) for near/far
            if (template is null)
            {
                try
                {
                    var ctor = targetType.GetConstructor([typeof(float), typeof(float)]);
                    if (ctor is not null)
                        template = ctor.Invoke([nearZ, farZ]) as XRCameraParameters;
                }
                catch { }
            }

            // Try constructor with (bool, float, float) for eye-specific cameras like XROVRCameraParameters
            if (template is null)
            {
                try
                {
                    var ctor = targetType.GetConstructor([typeof(bool), typeof(float), typeof(float)]);
                    if (ctor is not null)
                        template = ctor.Invoke([true, nearZ, farZ]) as XRCameraParameters;
                }
                catch { }
            }

            if (template is null)
            {
                // If we still can't create the type, fall back to perspective
                return previous is not null 
                    ? new XRPerspectiveCameraParameters(previous.NearZ, previous.FarZ)
                    : new XRPerspectiveCameraParameters(0.1f, 10000f);
            }

            // Use the template's CreateFromPrevious to get proper conversion
            return template.CreateFromPrevious(previous);
        }

        /// <summary>
        /// Creates a new instance of the specified camera parameter type with default values.
        /// </summary>
        /// <typeparam name="T">The type of camera parameters to create.</typeparam>
        /// <returns>A new instance of type <typeparamref name="T"/> with default values.</returns>
        public static T CreateFromType<T>() where T : XRCameraParameters
        {
            return (T)CreateFromType(typeof(T), null);
        }

        /// <summary>
        /// Creates a new instance of the specified camera parameter type, converting from an existing instance.
        /// </summary>
        /// <typeparam name="T">The type of camera parameters to create.</typeparam>
        /// <param name="previous">The previous parameters to convert from.</param>
        /// <returns>A new instance of type <typeparamref name="T"/> with converted settings.</returns>
        public static T CreateFromType<T>(XRCameraParameters previous) where T : XRCameraParameters
        {
            return (T)CreateFromType(typeof(T), previous);
        }

        #endregion
    }
}
