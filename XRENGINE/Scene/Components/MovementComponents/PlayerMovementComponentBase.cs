using System.Numerics;
using XREngine.Data;
using XREngine.Scene.Components.Physics;

namespace XREngine.Components
{
    /// <summary>
    /// Base component for all components that move a player scene node.
    /// </summary>
    public abstract class PlayerMovementComponentBase : DynamicRigidBodyComponent
    {
        private Vector3 _frameInputDirection = Vector3.Zero;
        private Vector3 _currentFrameInputDirection = Vector3.Zero;
        private Vector3 _constantInputDirection = Vector3.Zero;
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

        public void AddMovementInput(Vector3 offset)
            => TargetFrameInputDirection += offset;
        public void AddMovementInput(float x, float y, float z)
            => AddMovementInput(new Vector3(x, y, z));

        public virtual Vector3 ConsumeInput()
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
            return (ConstantInputDirection + CurrentFrameInputDirection) * Engine.Delta;
        }
    }
}
