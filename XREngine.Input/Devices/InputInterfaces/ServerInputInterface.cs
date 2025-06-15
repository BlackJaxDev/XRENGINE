using OpenVR.NET.Input;
using XREngine.Input.Devices.Types.OpenVR;

namespace XREngine.Input.Devices
{
    public class ServerInputInterface(int serverPlayerIndex) : InputInterface(serverPlayerIndex)
    {
        public override bool HideCursor { get; set; }

        public override void RegisterKeyCharacter(Action<char> func)
        {
            throw new NotImplementedException();
        }
        public override void RegisterKeystroke(BaseKeyboard.DelKeystroke func)
        {
            throw new NotImplementedException();
        }
        public override bool GetAxisState(EGamePadAxis axis, EButtonInputType type)
        {
            throw new NotImplementedException();
        }

        public override float GetAxisValue(EGamePadAxis axis)
        {
            throw new NotImplementedException();
        }

        public override bool GetButtonState(EGamePadButton button, EButtonInputType type)
        {
            throw new NotImplementedException();
        }

        public override bool GetKeyState(EKey key, EButtonInputType type)
        {
            throw new NotImplementedException();
        }

        public override bool GetMouseButtonState(EMouseButton button, EButtonInputType type)
        {
            throw new NotImplementedException();
        }

        public override void RegisterAxisButtonEvent(EGamePadAxis button, EButtonInputType type, System.Action func)
        {
            throw new NotImplementedException();
        }

        public override void RegisterAxisButtonEventAction(string actionName, System.Action func)
        {
            throw new NotImplementedException();
        }

        public override void RegisterAxisButtonPressed(EGamePadAxis axis, DelButtonState func)
        {
            throw new NotImplementedException();
        }

        public override void RegisterAxisButtonPressedAction(string actionName, DelButtonState func)
        {
            throw new NotImplementedException();
        }

        public override void RegisterAxisUpdate(EGamePadAxis axis, DelAxisValue func, bool continuousUpdate)
        {
            throw new NotImplementedException();
        }

        public override void RegisterAxisUpdateAction(string actionName, DelAxisValue func, bool continuousUpdate)
        {
            throw new NotImplementedException();
        }

        public override void RegisterMouseButtonEvent(EMouseButton button, EButtonInputType type, System.Action func)
        {
            throw new NotImplementedException();
        }

        public override void RegisterButtonEvent(EGamePadButton button, EButtonInputType type, System.Action func)
        {
            throw new NotImplementedException();
        }

        public override void RegisterButtonEventAction(string actionName, System.Action func)
        {
            throw new NotImplementedException();
        }

        public override void RegisterMouseButtonContinuousState(EMouseButton button, DelButtonState func)
        {
            throw new NotImplementedException();
        }

        public override void RegisterButtonPressed(EGamePadButton button, DelButtonState func)
        {
            throw new NotImplementedException();
        }

        public override void RegisterButtonPressedAction(string actionName, DelButtonState func)
        {
            throw new NotImplementedException();
        }

        public override void RegisterKeyEvent(EKey button, EButtonInputType type, System.Action func)
        {
            throw new NotImplementedException();
        }

        public override void RegisterKeyStateChange(EKey button, DelButtonState func)
        {
            throw new NotImplementedException();
        }

        public override void RegisterMouseMove(DelCursorUpdate func, EMouseMoveType type)
        {
            throw new NotImplementedException();
        }

        public override void RegisterMouseScroll(DelMouseScroll func)
        {
            throw new NotImplementedException();
        }

        public override void TryRegisterInput()
        {
            throw new NotImplementedException();
        }

        public override void TryUnregisterInput()
        {
            throw new NotImplementedException();
        }

        public override void RegisterVRBoolAction<TCategory, TName>(TCategory category, TName name, Action<bool> func)
        {
            throw new NotImplementedException();
        }

        public override void RegisterVRFloatAction<TCategory, TName>(TCategory category, TName name, ScalarAction.ValueChangedHandler func)
        {
            throw new NotImplementedException();
        }

        public override void RegisterVRVector2Action<TCategory, TName>(TCategory category, TName name, Vector2Action.ValueChangedHandler func)
        {
            throw new NotImplementedException();
        }

        public override void RegisterVRVector3Action<TCategory, TName>(TCategory category, TName name, Vector3Action.ValueChangedHandler func)
        {
            throw new NotImplementedException();
        }

        public override bool VibrateVRAction<TCategory, TName>(TCategory category, TName name, double duration, double frequency = 40, double amplitude = 1, double delay = 0)
        {
            throw new NotImplementedException();
        }

        public override void RegisterVRHandSkeletonQuery<TCategory, TName>(TCategory category, TName name, bool left, EVRSkeletalTransformSpace transformSpace = EVRSkeletalTransformSpace.Model, EVRSkeletalMotionRange motionRange = EVRSkeletalMotionRange.WithController, EVRSkeletalReferencePose? overridePose = null)
        {
            throw new NotImplementedException();
        }

        public override void RegisterVRHandSkeletonSummaryAction<TCategory, TName>(TCategory category, TName name, bool left, DelVRSkeletonSummary func, EVRSummaryType type)
        {
            throw new NotImplementedException();
        }

        public override void RegisterVRPose<TCategory, TName>(IVRActionPoseTransform<TCategory, TName> poseTransform)
        {
            throw new NotImplementedException();
        }
    }
}
