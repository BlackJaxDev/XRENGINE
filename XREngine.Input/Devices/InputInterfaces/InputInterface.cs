using OpenVR.NET.Input;
using System.Numerics;
using XREngine.Data.Core;
using XREngine.Input.Devices.Types.OpenVR;

namespace XREngine.Input.Devices
{
    public delegate void DelWantsInputsRegistered(InputInterface input);
    /// <summary>
    /// Handles input from keyboards, mice, gamepads, etc.
    /// </summary>
    [Serializable]
    public abstract class InputInterface(int serverIndex) : XRBase
    {
        public event DelWantsInputsRegistered? InputRegistration;
        protected void OnInputRegistration()
            => InputRegistration?.Invoke(this);

        public int ServerIndex { get; } = serverIndex;

        /// <summary>
        /// Unregister is false when the controller has gained focus and is currently adding inputs to handle.
        /// Unregister is true when the controller has lost focus and inputs are being removed.
        /// </summary>
        public bool Unregister { get; set; } = false;
        public abstract bool HideCursor { get; set; }

        public abstract void TryRegisterInput();
        public abstract void TryUnregisterInput();

        public abstract void RegisterAxisButtonPressedAction(string actionName, DelButtonState func);
        public abstract void RegisterButtonPressedAction(string actionName, DelButtonState func);
        public abstract void RegisterAxisButtonEventAction(string actionName, System.Action func);
        public abstract void RegisterButtonEventAction(string actionName, System.Action func);
        public abstract void RegisterAxisUpdateAction(string actionName, DelAxisValue func, bool continuousUpdate);

        /// <summary>
        /// The function provided will be called every frame with the current state of the mouse button.
        /// </summary>
        /// <param name="button"></param>
        /// <param name="func"></param>
        public abstract void RegisterMouseButtonContinuousState(EMouseButton button, DelButtonState func);
        /// <summary>
        /// The function provided will be called when the mouse button is pressed, released, held, or double pressed.
        /// </summary>
        /// <param name="button"></param>
        /// <param name="type"></param>
        /// <param name="func"></param>
        public abstract void RegisterMouseButtonEvent(EMouseButton button, EButtonInputType type, System.Action func);
        public abstract void RegisterMouseScroll(DelMouseScroll func);
        public abstract void RegisterMouseMove(DelCursorUpdate func, EMouseMoveType type);

        /// <summary>
        /// The function provided will be called if the key's pressed state changes.
        /// </summary>
        /// <param name="button"></param>
        /// <param name="func"></param>
        public abstract void RegisterKeyStateChange(EKey button, DelButtonState func);
        public abstract void RegisterKeyEvent(EKey button, EButtonInputType type, System.Action func);
        public abstract void RegisterKeystroke(BaseKeyboard.DelKeystroke func);
        public abstract void RegisterKeyCharacter(Action<char> func);

        public abstract void RegisterAxisButtonPressed(EGamePadAxis axis, DelButtonState func);
        public abstract void RegisterButtonPressed(EGamePadButton button, DelButtonState func);
        public abstract void RegisterButtonEvent(EGamePadButton button, EButtonInputType type, System.Action func);
        public abstract void RegisterAxisButtonEvent(EGamePadAxis button, EButtonInputType type, System.Action func);
        public abstract void RegisterAxisUpdate(EGamePadAxis axis, DelAxisValue func, bool continuousUpdate);

        public delegate void DelVRSkeletonSummary(float ThumbCurl, float IndexCurl, float MiddleCurl, float RingCurl, float PinkyCurl, float ThumbIndexSplay, float IndexMiddleSplay, float MiddleRingSplay, float RingPinkySplay, EVRSkeletalTrackingLevel level);

        public static string MakeVRActionPath(string category, string name, bool vibration)
            => $"/actions/{category}/{(vibration ? "out" : "in")}/{name}";
        
        public abstract void RegisterVRBoolAction<TCategory, TName>(
            TCategory category,
            TName name,
            Action<bool> func)
            where TCategory : struct, Enum 
            where TName : struct, Enum;

        public abstract void RegisterVRFloatAction<TCategory, TName>(
            TCategory category,
            TName name,
            ScalarAction.ValueChangedHandler func)
            where TCategory : struct, Enum
            where TName : struct, Enum;

        public abstract void RegisterVRVector2Action<TCategory, TName>(
            TCategory category,
            TName name,
            Vector2Action.ValueChangedHandler func)
            where TCategory : struct, Enum
            where TName : struct, Enum;

        public abstract void RegisterVRVector3Action<TCategory, TName>(
            TCategory category,
            TName name,
            Vector3Action.ValueChangedHandler func)
            where TCategory : struct, Enum
            where TName : struct, Enum;

        /// <summary>
        /// Sends a vibration signal to the action specified by the category and name.
        /// </summary>
        /// <typeparam name="TCategory"></typeparam>
        /// <typeparam name="TName"></typeparam>
        /// <param name="category"></param>
        /// <param name="name"></param>
        /// <param name="duration"></param>
        /// <param name="frequency"></param>
        /// <param name="amplitude"></param>
        /// <param name="delay"></param>
        /// <returns></returns>
        public abstract bool VibrateVRAction<TCategory, TName>(
            TCategory category,
            TName name,
            double duration,
            double frequency = 40,
            double amplitude = 1,
            double delay = 0)
            where TCategory : struct, Enum
            where TName : struct, Enum;

        public abstract void RegisterVRPose<TCategory, TName>(IVRActionPoseTransform<TCategory, TName> poseTransform)
            where TCategory : struct, Enum
            where TName : struct, Enum;

        public enum EVRSkeletalTransformSpace
        {
            Model = 0,
            Parent = 1,
        }
        public enum EVRSkeletalReferencePose
        {
            BindPose = 0,
            OpenHand = 1,
            Fist = 2,
            GripLimit = 3,
        }
        public enum EVRSkeletalMotionRange
        {
            WithController = 0,
            WithoutController = 1,
        }
        public enum EVRSummaryType
        {
            FromAnimation = 0,
            FromDevice = 1,
        }
        public enum EVRSkeletalTrackingLevel
        {
            VRSkeletalTracking_Estimated = 0,
            VRSkeletalTracking_Partial = 1,
            VRSkeletalTracking_Full = 2,
        }
        public enum EVRHandSkeletonBone
        {
            Root = 0,
            Wrist,
            Thumb0,
            Thumb1,
            Thumb2,
            Thumb3,
            IndexFinger0,
            IndexFinger1,
            IndexFinger2,
            IndexFinger3,
            IndexFinger4,
            MiddleFinger0,
            MiddleFinger1,
            MiddleFinger2,
            MiddleFinger3,
            MiddleFinger4,
            RingFinger0,
            RingFinger1,
            RingFinger2,
            RingFinger3,
            RingFinger4,
            PinkyFinger0,
            PinkyFinger1,
            PinkyFinger2,
            PinkyFinger3,
            PinkyFinger4,
            Aux_Thumb,
            Aux_IndexFinger,
            Aux_MiddleFinger,
            Aux_RingFinger,
            Aux_PinkyFinger,
        };

        /// <summary>
        /// Registers a query for the position and rotation of all bones in the hand.
        /// Enables transforms of type 'VRHandSkeletonBoneTransform' to be updated with the current state of the hand if its category, name, hand, transform space, motion range, and override pose match.
        /// </summary>
        /// <typeparam name="TCategory"></typeparam>
        /// <typeparam name="TName"></typeparam>
        /// <param name="category"></param>
        /// <param name="name"></param>
        /// <param name="left"></param>
        /// <param name="transformSpace"></param>
        /// <param name="motionRange"></param>
        /// <param name="overridePose"></param>
        public abstract void RegisterVRHandSkeletonQuery<TCategory, TName>(
            TCategory category,
            TName name,
            bool left,
            EVRSkeletalTransformSpace transformSpace = EVRSkeletalTransformSpace.Model,
            EVRSkeletalMotionRange motionRange = EVRSkeletalMotionRange.WithController,
            EVRSkeletalReferencePose? overridePose = null);

        public abstract void RegisterVRHandSkeletonSummaryAction<TCategory, TName>(
            TCategory category,
            TName name,
            bool left,
            DelVRSkeletonSummary func,
            EVRSummaryType type);

        /// <summary>
        /// Retrieves the state of the requested mouse button: 
        /// pressed, released, held, or double pressed.
        /// </summary>
        /// <param name="button">The button to read the state of.</param>
        /// <param name="type">The type of state to observe.</param>
        /// <returns>True if the state is current.</returns>
        public abstract bool GetMouseButtonState(EMouseButton button, EButtonInputType type);
        /// <summary>
        /// Retrieves the state of the requested keyboard key: 
        /// pressed, released, held, or double pressed.
        /// </summary>
        /// <param name="key">The button to read the state of.</param>
        /// <param name="type">The type of state to observe.</param>
        /// <returns></returns>
        public abstract bool GetKeyState(EKey key, EButtonInputType type);
        /// <summary>
        /// Retrieves the state of the requested gamepad button: 
        /// pressed, released, held, or double pressed.
        /// </summary>
        /// <param name="button">The button to read the state of.</param>
        /// <param name="type">The type of state to observe.</param>
        /// <returns></returns>
        public abstract bool GetButtonState(EGamePadButton button, EButtonInputType type);
        /// <summary>
        /// Retrieves the state of the requested axis button: 
        /// pressed, released, held, or double pressed.
        /// </summary>
        /// <param name="axis">The axis button to read the state of.</param>
        /// <param name="type">The type of state to observe.</param>
        /// <returns></returns>
        public abstract bool GetAxisState(EGamePadAxis axis, EButtonInputType type);
        /// <summary>
        /// Retrieves the value of the requested axis in the range 0.0f to 1.0f 
        /// or -1.0f to 1.0f for control sticks.
        /// </summary>
        /// <param name="axis">The axis to read the value of.</param>
        /// <returns>The magnitude of the given axis.</returns>
        public abstract float GetAxisValue(EGamePadAxis axis);
    }
}
