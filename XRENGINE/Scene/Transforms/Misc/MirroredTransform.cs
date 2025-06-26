using System.Numerics;
using XREngine.Data.Core;

namespace XREngine.Scene.Transforms
{
    /// <summary>
    /// Transforms the node to a mirrored position and orientation of its parent based on the forward axis of the reflection normal transform.
    /// Useful for orienting a "mirror camera" to render a mirrored view of the scene.
    /// 
    /// This class creates a transformation that reflects objects across a plane defined by the reflection normal transform.
    /// It's particularly useful for:
    /// - Creating mirror effects in rendering
    /// - Setting up virtual cameras for reflective surfaces
    /// - Implementing planar reflections in a scene
    /// </summary>
    public class MirroredTransform : TransformBase
    {
        public MirroredTransform() { }
        public MirroredTransform(TransformBase parent)
            : base(parent) { }

        private TransformBase? _reflectionNormalTransform;
        /// <summary>
        /// The transform to use to retrieve a world forward axis as the reflection normal.
        /// This defines the reflection plane - the normal vector to this plane is the forward direction
        /// of the specified transform, and the plane passes through the transform's position.
        /// </summary>
        public TransformBase? ReflectionNormalTransform
        {
            get => _reflectionNormalTransform;
            set => SetField(ref _reflectionNormalTransform, value);
        }

        /// <summary>
        /// Handles property change notifications before they happen, detaching event listeners if needed.
        /// </summary>
        protected override bool OnPropertyChanging<T>(string? propName, T field, T @new)
        {
            bool change = base.OnPropertyChanging(propName, field, @new);
            if (change)
            {
                switch (propName)
                {
                    case nameof(ReflectionNormalTransform):
                        if (ReflectionNormalTransform is not null)
                            ReflectionNormalTransform.WorldMatrixChanged -= OnSourceMatrixChanged;
                        break;
                }
            }
            return change;
        }

        /// <summary>
        /// Handles property changes after they happen, attaching event listeners if needed.
        /// </summary>
        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(ReflectionNormalTransform):
                    if (ReflectionNormalTransform is not null)
                        ReflectionNormalTransform.WorldMatrixChanged += OnSourceMatrixChanged;
                    break;
            }
        }

        /// <summary>
        /// When the reflection normal transform changes, we need to update our world matrix.
        /// </summary>
        private void OnSourceMatrixChanged(TransformBase @base, Matrix4x4 worldMatrix)
            => MarkWorldModified();

        /// <summary>
        /// Creates the world matrix that represents the mirrored transformation.
        /// 
        /// This method:
        /// 1. Retrieves the mirror plane's normal and position from the reflection normal transform
        /// 2. Gets the parent's camera position and target (position + forward direction)
        /// 3. Reflects both camera position and target across the mirror plane
        /// 4. Creates a look-at matrix from the reflected position to the reflected target
        /// 
        /// The result is a transformation that positions objects as if they were reflected in a mirror.
        /// </summary>
        /// <returns>A matrix representing the mirrored world transformation</returns>
        protected override Matrix4x4 CreateWorldMatrix()
        {
            //Take parent world matrix and mirror it along the forward axis of the mirror transform
            if (ReflectionNormalTransform is null || Parent is null)
                return Parent?.WorldMatrix ?? Matrix4x4.Identity;

            // Get the mirror plane's normal (forward direction of reflection transform)
            Vector3 mirrorNormal = ReflectionNormalTransform.WorldForward;
            // Get the mirror plane's position
            Vector3 mirrorPosition = ReflectionNormalTransform.WorldTranslation;

            // Get the parent's camera position
            Vector3 cameraPos = Parent.WorldTranslation;
            // Get the point the camera is looking at (position + forward direction)
            Vector3 cameraTarget = Parent.WorldTranslation + Parent.WorldForward;

            // Reflect camera position across the mirror plane
            Vector3 reflectedCameraPos = XRMath.Reflect(cameraPos - mirrorPosition, mirrorNormal) + mirrorPosition;
            // Reflect camera target across the mirror plane
            Vector3 reflectedCameraTarget = XRMath.Reflect(cameraTarget - mirrorPosition, mirrorNormal) + mirrorPosition;

            // Create a look-at matrix from the reflected position to the reflected target
            // This properly mirrors both position and orientation
            return Matrix4x4.CreateLookAt(reflectedCameraPos, reflectedCameraTarget, Parent.WorldUp);
        }

        /// <summary>
        /// The local matrix is identity since all the mirroring happens in world space.
        /// </summary>
        /// <returns>Identity matrix</returns>
        protected override Matrix4x4 CreateLocalMatrix()
            => Matrix4x4.Identity;
    }
}