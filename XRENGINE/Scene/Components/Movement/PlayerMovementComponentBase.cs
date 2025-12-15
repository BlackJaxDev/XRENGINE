using System.Numerics;
using XREngine.Data;
using XREngine.Components.Physics;

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
        private float? _inputLerpSpeed = null;

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
            => TargetFrameInputDirection += offset;
        public void AddMovementInput(float x, float y, float z)
            => AddMovementInput(new Vector3(x, y, z));

        /// <summary>
        /// Manually add input directly to the player movement component.
        /// No movement processing designed for controllers and keyboards will be applied to this input.
        /// </summary>
        /// <param name="offset"></param>
        public void AddLiteralInputDelta(Vector3 offset)
            => LiteralInputDirection += offset;

        /// <summary>
        /// Delta time to apply when consuming input.
        /// Defaults to frame delta, but components that tick input with physics should override.
        /// </summary>
        protected virtual float InputDeltaTime => Engine.Delta;

        protected virtual Vector3 ConsumeLiteralInput()
        {
            var dir = _literalInputDirection;
            _literalInputDirection = Vector3.Zero;
            return dir;
        }

        protected virtual Vector3 ConsumeInput()
        {
            if (InputLerpSpeed is not null)
            {
                float speed = InputLerpSpeed.Value;
                CurrentFrameInputDirection = Interp.Lerp(CurrentFrameInputDirection, TargetFrameInputDirection, speed);
            }
            else
            {
                CurrentFrameInputDirection = TargetFrameInputDirection;
            }

            TargetFrameInputDirection = Vector3.Zero;
            return (ConstantInputDirection + CurrentFrameInputDirection) * InputDeltaTime;
        }
    }
}
