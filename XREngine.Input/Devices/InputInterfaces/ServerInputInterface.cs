using OpenVR.NET.Input;
using XREngine.Input.Devices.Types.OpenVR;

namespace XREngine.Input.Devices
{
    public class ServerInputInterface(int serverPlayerIndex) : InputInterface(serverPlayerIndex)
    {
        private bool _hideCursor;

        public override bool HideCursor
        {
            get => _hideCursor;
            set => _hideCursor = value;
        }

        public override void RegisterKeyCharacter(Action<char> func) { }
        public override void RegisterKeystroke(BaseKeyboard.DelKeystroke func) { }
        public override bool GetAxisState(EGamePadAxis axis, EButtonInputType type) => false;
        public override float GetAxisValue(EGamePadAxis axis) => 0.0f;
        public override bool GetButtonState(EGamePadButton button, EButtonInputType type) => false;
        public override bool GetKeyState(EKey key, EButtonInputType type) => false;
        public override bool GetMouseButtonState(EMouseButton button, EButtonInputType type) => false;

        public override void RegisterAxisButtonEvent(EGamePadAxis button, EButtonInputType type, System.Action func) { }
        public override void RegisterAxisButtonEventAction(string actionName, System.Action func) { }
        public override void RegisterAxisButtonPressed(EGamePadAxis axis, DelButtonState func) { }
        public override void RegisterAxisButtonPressedAction(string actionName, DelButtonState func) { }
        public override void RegisterAxisUpdate(EGamePadAxis axis, DelAxisValue func, bool continuousUpdate) { }
        public override void RegisterAxisUpdateAction(string actionName, DelAxisValue func, bool continuousUpdate) { }
        public override void RegisterMouseButtonEvent(EMouseButton button, EButtonInputType type, System.Action func) { }
        public override void RegisterButtonEvent(EGamePadButton button, EButtonInputType type, System.Action func) { }
        public override void RegisterButtonEventAction(string actionName, System.Action func) { }
        public override void RegisterMouseButtonContinuousState(EMouseButton button, DelButtonState func) { }
        public override void RegisterButtonPressed(EGamePadButton button, DelButtonState func) { }
        public override void RegisterButtonPressedAction(string actionName, DelButtonState func) { }
        public override void RegisterKeyEvent(EKey button, EButtonInputType type, System.Action func) { }
        public override void RegisterKeyStateChange(EKey button, DelButtonState func) { }
        public override void RegisterMouseMove(DelCursorUpdate func, EMouseMoveType type) { }
        public override void RegisterMouseScroll(DelMouseScroll func) { }

        public override void TryRegisterInput()
        {
            Unregister = false;
            OnInputRegistration();
        }

        public override void TryUnregisterInput()
        {
            Unregister = true;
            OnInputRegistration();
            Unregister = false;
        }

        public override void RegisterVRBoolAction<TCategory, TName>(TCategory category, TName name, Action<bool> func) { }
        public override void RegisterVRFloatAction<TCategory, TName>(TCategory category, TName name, ScalarAction.ValueChangedHandler func) { }
        public override void RegisterVRVector2Action<TCategory, TName>(TCategory category, TName name, Vector2Action.ValueChangedHandler func) { }
        public override void RegisterVRVector3Action<TCategory, TName>(TCategory category, TName name, Vector3Action.ValueChangedHandler func) { }
        public override bool VibrateVRAction<TCategory, TName>(TCategory category, TName name, double duration, double frequency = 40, double amplitude = 1, double delay = 0) => false;
        public override void RegisterVRHandSkeletonQuery<TCategory, TName>(TCategory category, TName name, bool left, EVRSkeletalTransformSpace transformSpace = EVRSkeletalTransformSpace.Model, EVRSkeletalMotionRange motionRange = EVRSkeletalMotionRange.WithController, EVRSkeletalReferencePose? overridePose = null) { }
        public override void RegisterVRHandSkeletonSummaryAction<TCategory, TName>(TCategory category, TName name, bool left, DelVRSkeletonSummary func, EVRSummaryType type) { }
        public override void RegisterVRPose<TCategory, TName>(IVRActionPoseTransform<TCategory, TName> poseTransform) { }
    }
}
