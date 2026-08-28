using System.Numerics;
using XREngine.Data;
using XREngine.Components.Physics;
using XREngine.Scene.Transforms;

namespace XREngine.Components.Movement
{
    /// <summary>
    /// Base component for all components that move a player scene node.
    /// </summary>
    public abstract class PlayerMovementComponentBase : DynamicRigidBodyComponent
    {
        private Vector3 _frameInputDirection = Vector3.Zero;
        private Vector3 _currentFrameInputDirection = Vector3.Zero;
        private Vector3 _constantInputDirection = Vector3.Zero;
        private Vector3 _literalInputDirection = Vector3.Zero;
        private Vector3 _continuousInputDirection = Vector3.Zero;
        private float? _inputLerpSpeed = null;
        private readonly object _inputSync = new();

        public Vector3 CurrentFrameInputDirection
        {
            get => _currentFrameInputDirection;
            private set => SetField(ref _currentFrameInputDirection, value);
        }
        public Vector3 TargetFrameInputDirection
        {
            get => _frameInputDirection;
            private set => SetField(ref _frameInputDirection, value);
        }
        public Vector3 ConstantInputDirection
        {
            get => _constantInputDirection;
            set => SetField(ref _constantInputDirection, value);
        }
        public float? InputLerpSpeed
        {
            get => _inputLerpSpeed;
            set => SetField(ref _inputLerpSpeed, value);
        }

        public Vector3 LiteralInputDirection
        {
            get => _literalInputDirection;
            set => SetField(ref _literalInputDirection, value);
        }

        public void AddMovementInput(Vector3 offset)
        {
            lock (_inputSync)
                TargetFrameInputDirection += offset;
        }
        public void AddMovementInput(float x, float y, float z)
            => AddMovementInput(new Vector3(x, y, z));

        /// <summary>
        /// Publishes the latest continuous movement-input snapshot. The value
        /// remains active until replaced, so fixed-thread consumption is not
        /// coupled to the number of Update ticks that occurred.
        /// </summary>
        public void SetMovementInput(Vector3 direction)
        {
            lock (_inputSync)
                SetField(ref _continuousInputDirection, direction);
        }

        /// <summary>
        /// Manually add input directly to the player movement component.
        /// No movement processing designed for controllers and keyboards will be applied to this input.
        /// </summary>
        /// <param name="offset"></param>
        public void AddLiteralInputDelta(Vector3 offset)
        {
            lock (_inputSync)
                LiteralInputDirection += offset;
        }

        /// <summary>
        /// Delta time to apply when consuming input.
        /// Defaults to frame delta, but components that tick input with physics should override.
        /// </summary>
        protected virtual float InputDeltaTime => RuntimeTransformServices.Current?.DilatedUpdateDeltaSeconds ?? 0.0f;

        protected virtual Vector3 ConsumeLiteralInput()
        {
            lock (_inputSync)
            {
                var dir = _literalInputDirection;
                SetField(ref _literalInputDirection, Vector3.Zero, nameof(LiteralInputDirection));
                return dir;
            }
        }

        protected virtual Vector3 ConsumeInput()
        {
            Vector3 target;
            lock (_inputSync)
            {
                target = _continuousInputDirection + TargetFrameInputDirection;
                TargetFrameInputDirection = Vector3.Zero;
            }

            if (InputLerpSpeed is not null)
            {
                float speed = Math.Clamp(InputLerpSpeed.Value, 0.0f, 1.0f);
                float blend = TimeScaledBlend(speed, InputDeltaTime);
                CurrentFrameInputDirection = Interp.Lerp(CurrentFrameInputDirection, target, blend);
            }
            else
            {
                CurrentFrameInputDirection = target;
            }

            return (ConstantInputDirection + CurrentFrameInputDirection) * InputDeltaTime;
        }

        private static float TimeScaledBlend(float referenceStepBlend, float deltaTime)
        {
            if (referenceStepBlend <= 0.0f || deltaTime <= 0.0f)
                return 0.0f;
            if (referenceStepBlend >= 1.0f)
                return 1.0f;
            return 1.0f - MathF.Pow(1.0f - referenceStepBlend, deltaTime * 60.0f);
        }
    }
}
