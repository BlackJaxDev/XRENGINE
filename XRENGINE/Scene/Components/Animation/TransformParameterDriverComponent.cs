using System.ComponentModel;
using System.Numerics;
using XREngine.Data.Transforms.Rotations;
using XREngine.Scene.Transforms;

namespace XREngine.Components.Animation
{
    /// <summary>
    /// Defines which transform property to use as the source for driving animation parameters.
    /// </summary>
    public enum ETransformSource
    {
        /// <summary>
        /// Use local translation X component.
        /// </summary>
        LocalTranslationX,
        /// <summary>
        /// Use local translation Y component.
        /// </summary>
        LocalTranslationY,
        /// <summary>
        /// Use local translation Z component.
        /// </summary>
        LocalTranslationZ,
        /// <summary>
        /// Use world translation X component.
        /// </summary>
        WorldTranslationX,
        /// <summary>
        /// Use world translation Y component.
        /// </summary>
        WorldTranslationY,
        /// <summary>
        /// Use world translation Z component.
        /// </summary>
        WorldTranslationZ,
        /// <summary>
        /// Use local rotation pitch (X axis rotation in degrees).
        /// </summary>
        LocalRotationPitch,
        /// <summary>
        /// Use local rotation yaw (Y axis rotation in degrees).
        /// </summary>
        LocalRotationYaw,
        /// <summary>
        /// Use local rotation roll (Z axis rotation in degrees).
        /// </summary>
        LocalRotationRoll,
        /// <summary>
        /// Use world rotation pitch (X axis rotation in degrees).
        /// </summary>
        WorldRotationPitch,
        /// <summary>
        /// Use world rotation yaw (Y axis rotation in degrees).
        /// </summary>
        WorldRotationYaw,
        /// <summary>
        /// Use world rotation roll (Z axis rotation in degrees).
        /// </summary>
        WorldRotationRoll,
        /// <summary>
        /// Use local scale X component.
        /// </summary>
        LocalScaleX,
        /// <summary>
        /// Use local scale Y component.
        /// </summary>
        LocalScaleY,
        /// <summary>
        /// Use local scale Z component.
        /// </summary>
        LocalScaleZ,
        /// <summary>
        /// Use lossy (approximate) world scale X component.
        /// </summary>
        WorldScaleX,
        /// <summary>
        /// Use lossy (approximate) world scale Y component.
        /// </summary>
        WorldScaleY,
        /// <summary>
        /// Use lossy (approximate) world scale Z component.
        /// </summary>
        WorldScaleZ,
        /// <summary>
        /// Use the magnitude of the local translation vector.
        /// </summary>
        LocalTranslationMagnitude,
        /// <summary>
        /// Use the magnitude of the world translation vector.
        /// </summary>
        WorldTranslationMagnitude,
        /// <summary>
        /// Use the uniform local scale (average of X, Y, Z).
        /// </summary>
        LocalScaleUniform,
        /// <summary>
        /// Use the uniform world scale (average of X, Y, Z).
        /// </summary>
        WorldScaleUniform,
    }

    /// <summary>
    /// Defines a single parameter binding that maps a transform property to an animation state machine parameter.
    /// </summary>
    [Serializable]
    public class TransformParameterBinding
    {
        /// <summary>
        /// The name of the float parameter in the animation state machine to drive.
        /// </summary>
        public string ParameterName { get; set; } = string.Empty;

        /// <summary>
        /// The source transform property to read the value from.
        /// </summary>
        public ETransformSource Source { get; set; } = ETransformSource.LocalTranslationX;

        /// <summary>
        /// Multiplier applied to the source value before setting the parameter.
        /// </summary>
        [DefaultValue(1.0f)]
        public float Multiplier { get; set; } = 1.0f;

        /// <summary>
        /// Offset added to the source value after multiplication.
        /// </summary>
        [DefaultValue(0.0f)]
        public float Offset { get; set; } = 0.0f;

        /// <summary>
        /// Optional minimum value to clamp the output to. Null means no minimum clamping.
        /// </summary>
        public float? MinValue { get; set; } = null;

        /// <summary>
        /// Optional maximum value to clamp the output to. Null means no maximum clamping.
        /// </summary>
        public float? MaxValue { get; set; } = null;

        /// <summary>
        /// If true, uses the delta (change) in the transform value instead of the absolute value.
        /// Useful for driving parameters based on movement speed rather than position.
        /// </summary>
        [DefaultValue(false)]
        public bool UseDelta { get; set; } = false;

        /// <summary>
        /// The previous value used for delta calculation.
        /// </summary>
        internal float PreviousValue { get; set; } = 0.0f;

        /// <summary>
        /// Whether the previous value has been initialized.
        /// </summary>
        internal bool HasPreviousValue { get; set; } = false;
    }

    /// <summary>
    /// A component that drives float parameters in an AnimStateMachineComponent based on the transform 
    /// (translation, rotation, scale) of the owning scene node. This is useful for procedural animation 
    /// control, such as driving blend trees based on movement direction or speed.
    /// </summary>
    public class TransformParameterDriverComponent : XRComponent
    {
        private AnimStateMachineComponent? _targetAnimator;
        /// <summary>
        /// The target AnimStateMachineComponent to drive parameters on.
        /// If null, will attempt to find one on the same scene node.
        /// </summary>
        public AnimStateMachineComponent? TargetAnimator
        {
            get => _targetAnimator;
            set => SetField(ref _targetAnimator, value);
        }

        private TransformBase? _sourceTransform;
        /// <summary>
        /// The source transform to read values from.
        /// If null, uses this component's scene node transform.
        /// </summary>
        public TransformBase? SourceTransform
        {
            get => _sourceTransform;
            set => SetField(ref _sourceTransform, value);
        }

        private List<TransformParameterBinding> _bindings = [];
        /// <summary>
        /// The list of parameter bindings that define how transform properties drive animation parameters.
        /// </summary>
        public List<TransformParameterBinding> Bindings
        {
            get => _bindings;
            set => SetField(ref _bindings, value ?? []);
        }

        private bool _updateInFixedTick = false;
        /// <summary>
        /// If true, updates parameters during the physics/fixed tick instead of the normal tick.
        /// Useful when the transform is driven by physics.
        /// </summary>
        [DefaultValue(false)]
        public bool UpdateInFixedTick
        {
            get => _updateInFixedTick;
            set => SetField(ref _updateInFixedTick, value);
        }

        /// <summary>
        /// Gets the effective animator component - either the explicitly set one or a sibling component.
        /// </summary>
        private AnimStateMachineComponent? GetEffectiveAnimator()
            => TargetAnimator ?? GetSiblingComponent<AnimStateMachineComponent>();

        /// <summary>
        /// Gets the effective source transform - either the explicitly set one or this node's transform.
        /// </summary>
        private TransformBase GetEffectiveSourceTransform()
            => SourceTransform ?? Transform;

        protected internal override void OnComponentActivated()
        {
            base.OnComponentActivated();
            
            if (UpdateInFixedTick)
                RegisterTick(ETickGroup.PostPhysics, ETickOrder.Animation, UpdateParameters);
            else
                RegisterTick(ETickGroup.Normal, ETickOrder.Animation, UpdateParameters);
        }

        protected internal override void OnComponentDeactivated()
        {
            base.OnComponentDeactivated();
            
            if (UpdateInFixedTick)
                UnregisterTick(ETickGroup.PostPhysics, ETickOrder.Animation, UpdateParameters);
            else
                UnregisterTick(ETickGroup.Normal, ETickOrder.Animation, UpdateParameters);
        }

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            
            if (propName == nameof(UpdateInFixedTick) && IsActiveInHierarchy)
            {
                // Re-register tick with the correct group
                if (prev is bool prevBool && field is bool newBool && prevBool != newBool)
                {
                    if (prevBool)
                        UnregisterTick(ETickGroup.PostPhysics, ETickOrder.Animation, UpdateParameters);
                    else
                        UnregisterTick(ETickGroup.Normal, ETickOrder.Animation, UpdateParameters);

                    if (newBool)
                        RegisterTick(ETickGroup.PostPhysics, ETickOrder.Animation, UpdateParameters);
                    else
                        RegisterTick(ETickGroup.Normal, ETickOrder.Animation, UpdateParameters);
                }
            }
        }

        /// <summary>
        /// Updates all parameter bindings based on the current transform state.
        /// </summary>
        protected virtual void UpdateParameters()
        {
            var animator = GetEffectiveAnimator();
            if (animator is null)
                return;

            var transform = GetEffectiveSourceTransform();
            
            foreach (var binding in Bindings)
            {
                if (string.IsNullOrEmpty(binding.ParameterName))
                    continue;

                float value = GetSourceValue(transform, binding.Source);
                
                if (binding.UseDelta)
                {
                    if (binding.HasPreviousValue)
                    {
                        float delta = value - binding.PreviousValue;
                        value = delta;
                    }
                    else
                    {
                        value = 0.0f;
                    }
                    binding.PreviousValue = GetSourceValue(transform, binding.Source);
                    binding.HasPreviousValue = true;
                }

                // Apply multiplier and offset
                value = value * binding.Multiplier + binding.Offset;

                // Apply clamping
                if (binding.MinValue.HasValue)
                    value = Math.Max(value, binding.MinValue.Value);
                if (binding.MaxValue.HasValue)
                    value = Math.Min(value, binding.MaxValue.Value);

                animator.SetFloat(binding.ParameterName, value);
            }
        }

        /// <summary>
        /// Gets the value from the transform based on the specified source type.
        /// </summary>
        private static float GetSourceValue(TransformBase transform, ETransformSource source)
        {
            return source switch
            {
                // Local Translation
                ETransformSource.LocalTranslationX => transform.LocalTranslation.X,
                ETransformSource.LocalTranslationY => transform.LocalTranslation.Y,
                ETransformSource.LocalTranslationZ => transform.LocalTranslation.Z,
                ETransformSource.LocalTranslationMagnitude => transform.LocalTranslation.Length(),
                
                // World Translation
                ETransformSource.WorldTranslationX => transform.WorldTranslation.X,
                ETransformSource.WorldTranslationY => transform.WorldTranslation.Y,
                ETransformSource.WorldTranslationZ => transform.WorldTranslation.Z,
                ETransformSource.WorldTranslationMagnitude => transform.WorldTranslation.Length(),
                
                // Local Rotation (as Euler angles in degrees)
                ETransformSource.LocalRotationPitch => GetEulerAngles(transform.LocalRotation).X,
                ETransformSource.LocalRotationYaw => GetEulerAngles(transform.LocalRotation).Y,
                ETransformSource.LocalRotationRoll => GetEulerAngles(transform.LocalRotation).Z,
                
                // World Rotation (as Euler angles in degrees)
                ETransformSource.WorldRotationPitch => GetEulerAngles(transform.WorldRotation).X,
                ETransformSource.WorldRotationYaw => GetEulerAngles(transform.WorldRotation).Y,
                ETransformSource.WorldRotationRoll => GetEulerAngles(transform.WorldRotation).Z,
                
                // Local Scale
                ETransformSource.LocalScaleX => GetLocalScale(transform).X,
                ETransformSource.LocalScaleY => GetLocalScale(transform).Y,
                ETransformSource.LocalScaleZ => GetLocalScale(transform).Z,
                ETransformSource.LocalScaleUniform => GetUniformScale(GetLocalScale(transform)),
                
                // World Scale (lossy)
                ETransformSource.WorldScaleX => transform.LossyWorldScale.X,
                ETransformSource.WorldScaleY => transform.LossyWorldScale.Y,
                ETransformSource.WorldScaleZ => transform.LossyWorldScale.Z,
                ETransformSource.WorldScaleUniform => GetUniformScale(transform.LossyWorldScale),
                
                _ => 0.0f
            };
        }

        /// <summary>
        /// Gets local scale from a transform. If it's a standard Transform, use its Scale property directly.
        /// Otherwise, extract from the local matrix.
        /// </summary>
        private static Vector3 GetLocalScale(TransformBase transform)
        {
            if (transform is Transform standardTransform)
                return standardTransform.Scale;
            
            // Extract scale from local matrix
            Matrix4x4.Decompose(transform.LocalMatrix, out Vector3 scale, out _, out _);
            return scale;
        }

        /// <summary>
        /// Converts a quaternion to Euler angles (pitch, yaw, roll) in degrees.
        /// </summary>
        private static Vector3 GetEulerAngles(Quaternion rotation)
        {
            var rotator = Rotator.FromQuaternion(rotation);
            return new Vector3(rotator.Pitch, rotator.Yaw, rotator.Roll);
        }

        /// <summary>
        /// Calculates uniform scale as the average of X, Y, Z components.
        /// </summary>
        private static float GetUniformScale(Vector3 scale)
            => (scale.X + scale.Y + scale.Z) / 3.0f;

        /// <summary>
        /// Adds a new parameter binding.
        /// </summary>
        /// <param name="parameterName">The name of the animation parameter to drive.</param>
        /// <param name="source">The transform property to read from.</param>
        /// <param name="multiplier">Value multiplier.</param>
        /// <param name="offset">Value offset.</param>
        /// <returns>The created binding for further configuration.</returns>
        public TransformParameterBinding AddBinding(
            string parameterName, 
            ETransformSource source, 
            float multiplier = 1.0f, 
            float offset = 0.0f)
        {
            var binding = new TransformParameterBinding
            {
                ParameterName = parameterName,
                Source = source,
                Multiplier = multiplier,
                Offset = offset
            };
            Bindings.Add(binding);
            return binding;
        }

        /// <summary>
        /// Removes all bindings for the specified parameter name.
        /// </summary>
        /// <param name="parameterName">The parameter name to remove bindings for.</param>
        public void RemoveBindings(string parameterName)
            => Bindings.RemoveAll(b => b.ParameterName == parameterName);

        /// <summary>
        /// Clears all parameter bindings.
        /// </summary>
        public void ClearBindings()
        {
            Bindings.Clear();
        }

        /// <summary>
        /// Resets the delta tracking state for all bindings.
        /// Call this when you want to restart delta calculations from scratch.
        /// </summary>
        public void ResetDeltaTracking()
        {
            foreach (var binding in Bindings)
            {
                binding.HasPreviousValue = false;
                binding.PreviousValue = 0.0f;
            }
        }
    }
}
