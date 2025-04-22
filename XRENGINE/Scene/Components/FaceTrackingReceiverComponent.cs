using OscCore;
using XREngine.Animation;
using XREngine.Scene.Components;
using static XREngine.Animation.AnimTransitionCondition;

namespace XREngine.Data.Components
{
    public class FaceTrackingReceiverComponent : OscReceiverComponent
    {
        public const string Tracking_Eye_CenterPitchYaw = "/tracking/eye/CenterPitchYaw";
        public const string Tracking_Eye_EyesClosedAmount = "/tracking/eye/EyesClosedAmount";

        public const string Avatar_EyeX = "FT/v2/EyeX";
        public const string Avatar_EyeY = "FT/v2/EyeY";
        public const string Avatar_EyeLeftX = "FT/v2/EyeLeftX";
        public const string Avatar_EyeLeftY = "FT/v2/EyeLeftY";
        public const string Avatar_EyeRightX = "FT/v2/EyeRightX";
        public const string Avatar_EyeRightY = "FT/v2/EyeRightY";
        public const string Avatar_EyeOpenLeft = "FT/v2/EyeOpenLeft";
        public const string Avatar_EyeOpenRight = "FT/v2/EyeOpenRight";
        public const string Avatar_EyeOpen = "FT/v2/EyeOpen";
        public const string Avatar_EyeClosedLeft = "FT/v2/EyeClosedLeft";
        public const string Avatar_EyeClosedRight = "FT/v2/EyeClosedRight";
        public const string Avatar_EyeClosed = "FT/v2/EyeClosed";
        public const string Avatar_EyeLidLeft = "FT/v2/EyeLidLeft";
        public const string Avatar_EyeLidRight = "FT/v2/EyeLidRight";
        public const string Avatar_EyeLid = "FT/v2/EyeLid";
        public const string Avatar_EyeSquint = "FT/v2/EyeSquint";
        public const string Avatar_EyesSquint = "FT/v2/EyesSquint";
        public const string Avatar_EyeSquintRight = "FT/v2/EyeSquintRight";
        public const string Avatar_EyeSquintLeft = "FT/v2/EyeSquintLeft";

        public const string Avatar_BrowPinchRight = "FT/v2/BrowPinchRight";
        public const string Avatar_BrowPinchLeft = "FT/v2/BrowPinchLeft";
        public const string Avatar_BrowLowererRight = "FT/v2/BrowLowererRight";
        public const string Avatar_BrowLowererLeft = "FT/v2/BrowLowererLeft";
        public const string Avatar_BrowInnerUpRight = "FT/v2/BrowInnerUpRight";
        public const string Avatar_BrowInnerUpLeft = "FT/v2/BrowInnerUpLeft";
        public const string Avatar_BrowUp = "FT/v2/BrowUp";
        public const string Avatar_BrowDown = "FT/v2/BrowDown";
        public const string Avatar_BrowInnerUp = "FT/v2/BrowInnerUp";
        public const string Avatar_BrowUpRight = "FT/v2/BrowUpRight";
        public const string Avatar_BrowUpLeft = "FT/v2/BrowUpLeft";
        public const string Avatar_BrowDownRight = "FT/v2/BrowDownRight";
        public const string Avatar_BrowDownLeft = "FT/v2/BrowDownLeft";
        public const string Avatar_BrowExpressionRight = "FT/v2/BrowExpressionRight";
        public const string Avatar_BrowExpressionLeft = "FT/v2/BrowExpressionLeft";
        public const string Avatar_BrowExpression = "FT/v2/BrowExpression";

        public const string Avatar_CheekSquintRight = "FT/v2/CheekSquintRight";
        public const string Avatar_CheekSquintLeft = "FT/v2/CheekSquintLeft";
        public const string Avatar_CheekPuffRight = "FT/v2/CheekPuffRight";
        public const string Avatar_CheekPuffLeft = "FT/v2/CheekPuffLeft";

        public const string Avatar_JawOpen = "FT/v2/JawOpen";
        public const string Avatar_JawRight = "FT/v2/JawRight";
        public const string Avatar_JawForward = "FT/v2/JawForward";
        public const string Avatar_JawX = "FT/v2/JawX";
        public const string Avatar_JawZ = "FT/v2/JawZ";

        public const string Avatar_MouthClosed = "FT/v2/MouthClosed";
        public const string Avatar_MouthX = "FT/v2/MouthX";

        public const string Avatar_MouthUpperUp = "FT/v2/MouthUpperUp";
        public const string Avatar_MouthUpperUpRight = "FT/v2/MouthUpperUpRight";
        public const string Avatar_MouthUpperUpLeft = "FT/v2/MouthUpperUpLeft";

        public const string Avatar_MouthLowerDown = "FT/v2/MouthLowerDown";
        public const string Avatar_MouthLowerDownRight = "FT/v2/MouthLowerDownRight";
        public const string Avatar_MouthLowerDownLeft = "FT/v2/MouthLowerDownLeft";

        public const string Avatar_MouthUpperDeepenRight = "FT/v2/MouthUpperDeepenRight";
        public const string Avatar_MouthUpperDeepenLeft = "FT/v2/MouthUpperDeepenLeft";
        public const string Avatar_MouthUpperRight = "FT/v2/MouthUpperRight";
        public const string Avatar_MouthLowerRight = "FT/v2/MouthLowerRight";
        public const string Avatar_MouthStretchRight = "FT/v2/MouthStretchRight";
        public const string Avatar_MouthStretchLeft = "FT/v2/MouthStretchLeft";
        public const string Avatar_MouthDimpleRight = "FT/v2/MouthDimpleRight";
        public const string Avatar_MouthDimpleLeft = "FT/v2/MouthDimpleLeft";
        public const string Avatar_MouthRaiserUpper = "FT/v2/MouthRaiserUpper";
        public const string Avatar_MouthRaiserLower = "FT/v2/MouthRaiserLower";
        public const string Avatar_MouthPress = "FT/v2/MouthPress";
        public const string Avatar_MouthPressRight = "FT/v2/MouthPressRight";
        public const string Avatar_MouthPressLeft = "FT/v2/MouthPressLeft";

        public const string Avatar_MouthSadRight = "FT/v2/MouthSadRight";
        public const string Avatar_MouthSadLeft = "FT/v2/MouthSadLeft";
        public const string Avatar_MouthFrownRight = "FT/v2/MouthFrownRight";
        public const string Avatar_MouthFrownLeft = "FT/v2/MouthFrownLeft";
        public const string Avatar_SmileFrownRight = "FT/v2/SmileFrownRight";
        public const string Avatar_SmileFrownLeft = "FT/v2/SmileFrownLeft";

        public const string Avatar_LipSuckUpper = "FT/v2/LipSuckUpper";
        public const string Avatar_LipSuckUpperRight = "FT/v2/LipSuckUpperRight";
        public const string Avatar_LipSuckUpperLeft = "FT/v2/LipSuckUpperLeft";

        public const string Avatar_LipSuckLower = "FT/v2/LipSuckLower";
        public const string Avatar_LipSuckLowerRight = "FT/v2/LipSuckLowerRight";
        public const string Avatar_LipSuckLowerLeft = "FT/v2/LipSuckLowerLeft";

        public const string Avatar_LipFunnel = "FT/v2/LipFunnel";
        public const string Avatar_LipFunnelUpperRight = "FT/v2/LipFunnelUpperRight";
        public const string Avatar_LipFunnelUpperLeft = "FT/v2/LipFunnelUpperLeft";
        public const string Avatar_LipFunnelLowerRight = "FT/v2/LipFunnelLowerRight";
        public const string Avatar_LipFunnelLowerLeft = "FT/v2/LipFunnelLowerLeft";

        public const string Avatar_LipPucker = "FT/v2/LipPucker";
        public const string Avatar_LipPuckerUpperRight = "FT/v2/LipPuckerUpperRight";
        public const string Avatar_LipPuckerUpperLeft = "FT/v2/LipPuckerUpperLeft";
        public const string Avatar_LipPuckerLowerRight = "FT/v2/LipPuckerLowerRight";
        public const string Avatar_LipPuckerLowerLeft = "FT/v2/LipPuckerLowerLeft";

        public const string Avatar_NoseSneer = "FT/v2/NoseSneer";
        public const string Avatar_NoseSneerRight = "FT/v2/NoseSneerRight";
        public const string Avatar_NoseSneerLeft = "FT/v2/NoseSneerLeft";

        public const string Avatar_TongueOut = "FT/v2/TongueOut";

        public const string Param_EyeTrackingActive = "EyeTrackingActive";
        public const string Param_LipTrackingActive = "LipTrackingActive";
        public const string Param_StateTrackingActive = "State/TrackingActive";
        public const string Param_IsLocal = "IsLocal";
        public const string Param_StateVisemesEnable = "State/VisemesEnable";
        public const string Param_VisemesEnable = "VisemesEnable";
        public const string Param_StateEyeTracking = "State/EyeTracking";
        public const string Param_DirectBlend = "FT/DirectBlend";
        public const string Param_FacialExpressionsDisabled = "FacialExpressionsDisabled";
        public const string Param_RemoteModeActive = "RemoteModeActive";
        public const string Param_EyeDilationEnable = "EyeDilationEnable";
        public const string Param_FaceTrackingEmulation = "FaceTrackingEmulation";
        public const string Param_SmoothingLocal = "Smoothing/Local";
        public const string DefaultFaceTrackedNodeName = "Face";

        public AnimStateMachineComponent? AnimStateMachine { get; set; } = null;
        public AnimStateMachineComponent? GetAnimStateMachine()
            => AnimStateMachine ?? GetSiblingComponent<AnimStateMachineComponent>();

        public FaceTrackingReceiverComponent()
        {
            // Eye tracking
            ReceiverAddresses.Add(Tracking_Eye_CenterPitchYaw, Tracking_Eye_CenterPitchYaw_Method);
            ReceiverAddresses.Add(Tracking_Eye_EyesClosedAmount, Tracking_Eye_EyesClosedAmount_Method);

            // Eye parameters
            ReceiverAddresses.Add(Avatar_EyeX, Avatar_EyeX_Method);
            ReceiverAddresses.Add(Avatar_EyeY, Avatar_EyeY_Method);
            ReceiverAddresses.Add(Avatar_EyeLeftX, Avatar_EyeLeftX_Method);
            ReceiverAddresses.Add(Avatar_EyeLeftY, Avatar_EyeLeftY_Method);
            ReceiverAddresses.Add(Avatar_EyeRightX, Avatar_EyeRightX_Method);
            ReceiverAddresses.Add(Avatar_EyeRightY, Avatar_EyeRightY_Method);
            ReceiverAddresses.Add(Avatar_EyeOpenLeft, Avatar_EyeOpenLeft_Method);
            ReceiverAddresses.Add(Avatar_EyeOpenRight, Avatar_EyeOpenRight_Method);
            ReceiverAddresses.Add(Avatar_EyeOpen, Avatar_EyeOpen_Method);
            ReceiverAddresses.Add(Avatar_EyeClosedLeft, Avatar_EyeClosedLeft_Method);
            ReceiverAddresses.Add(Avatar_EyeClosedRight, Avatar_EyeClosedRight_Method);
            ReceiverAddresses.Add(Avatar_EyeClosed, Avatar_EyeClosed_Method);
            ReceiverAddresses.Add(Avatar_EyeLidLeft, Avatar_EyeLidLeft_Method);
            ReceiverAddresses.Add(Avatar_EyeLidRight, Avatar_EyeLidRight_Method);
            ReceiverAddresses.Add(Avatar_EyeLid, Avatar_EyeLid_Method);
            ReceiverAddresses.Add(Avatar_EyeSquint, Avatar_EyeSquint_Method);
            ReceiverAddresses.Add(Avatar_EyesSquint, Avatar_EyesSquint_Method);
            ReceiverAddresses.Add(Avatar_EyeSquintRight, Avatar_EyeSquintRight_Method);
            ReceiverAddresses.Add(Avatar_EyeSquintLeft, Avatar_EyeSquintLeft_Method);

            // Brow parameters
            ReceiverAddresses.Add(Avatar_BrowPinchRight, Avatar_BrowPinchRight_Method);
            ReceiverAddresses.Add(Avatar_BrowPinchLeft, Avatar_BrowPinchLeft_Method);
            ReceiverAddresses.Add(Avatar_BrowLowererRight, Avatar_BrowLowererRight_Method);
            ReceiverAddresses.Add(Avatar_BrowLowererLeft, Avatar_BrowLowererLeft_Method);
            ReceiverAddresses.Add(Avatar_BrowInnerUpRight, Avatar_BrowInnerUpRight_Method);
            ReceiverAddresses.Add(Avatar_BrowInnerUpLeft, Avatar_BrowInnerUpLeft_Method);
            ReceiverAddresses.Add(Avatar_BrowUp, Avatar_BrowUp_Method);
            ReceiverAddresses.Add(Avatar_BrowDown, Avatar_BrowDown_Method);
            ReceiverAddresses.Add(Avatar_BrowInnerUp, Avatar_BrowInnerUp_Method);
            ReceiverAddresses.Add(Avatar_BrowUpRight, Avatar_BrowUpRight_Method);
            ReceiverAddresses.Add(Avatar_BrowUpLeft, Avatar_BrowUpLeft_Method);
            ReceiverAddresses.Add(Avatar_BrowDownRight, Avatar_BrowDownRight_Method);
            ReceiverAddresses.Add(Avatar_BrowDownLeft, Avatar_BrowDownLeft_Method);
            ReceiverAddresses.Add(Avatar_BrowExpressionRight, Avatar_BrowExpressionRight_Method);
            ReceiverAddresses.Add(Avatar_BrowExpressionLeft, Avatar_BrowExpressionLeft_Method);
            ReceiverAddresses.Add(Avatar_BrowExpression, Avatar_BrowExpression_Method);

            // Cheek parameters
            ReceiverAddresses.Add(Avatar_CheekSquintRight, Avatar_CheekSquintRight_Method);
            ReceiverAddresses.Add(Avatar_CheekSquintLeft, Avatar_CheekSquintLeft_Method);
            ReceiverAddresses.Add(Avatar_CheekPuffRight, Avatar_CheekPuffRight_Method);
            ReceiverAddresses.Add(Avatar_CheekPuffLeft, Avatar_CheekPuffLeft_Method);

            // Jaw parameters
            ReceiverAddresses.Add(Avatar_JawOpen, Avatar_JawOpen_Method);
            ReceiverAddresses.Add(Avatar_JawRight, Avatar_JawRight_Method);
            ReceiverAddresses.Add(Avatar_JawForward, Avatar_JawForward_Method);
            ReceiverAddresses.Add(Avatar_JawX, Avatar_JawX_Method);
            ReceiverAddresses.Add(Avatar_JawZ, Avatar_JawZ_Method);

            // Mouth parameters
            ReceiverAddresses.Add(Avatar_MouthClosed, Avatar_MouthClosed_Method);
            ReceiverAddresses.Add(Avatar_MouthUpperUpRight, Avatar_MouthUpperUpRight_Method);
            ReceiverAddresses.Add(Avatar_MouthUpperUpLeft, Avatar_MouthUpperUpLeft_Method);
            ReceiverAddresses.Add(Avatar_MouthUpperDeepenRight, Avatar_MouthUpperDeepenRight_Method);
            ReceiverAddresses.Add(Avatar_MouthUpperDeepenLeft, Avatar_MouthUpperDeepenLeft_Method);
            ReceiverAddresses.Add(Avatar_MouthLowerDownRight, Avatar_MouthLowerDownRight_Method);
            ReceiverAddresses.Add(Avatar_MouthLowerDownLeft, Avatar_MouthLowerDownLeft_Method);
            ReceiverAddresses.Add(Avatar_MouthUpperRight, Avatar_MouthUpperRight_Method);
            ReceiverAddresses.Add(Avatar_MouthLowerRight, Avatar_MouthLowerRight_Method);
            ReceiverAddresses.Add(Avatar_MouthFrownRight, Avatar_MouthFrownRight_Method);
            ReceiverAddresses.Add(Avatar_MouthFrownLeft, Avatar_MouthFrownLeft_Method);
            ReceiverAddresses.Add(Avatar_MouthStretchRight, Avatar_MouthStretchRight_Method);
            ReceiverAddresses.Add(Avatar_MouthStretchLeft, Avatar_MouthStretchLeft_Method);
            ReceiverAddresses.Add(Avatar_MouthDimpleRight, Avatar_MouthDimpleRight_Method);
            ReceiverAddresses.Add(Avatar_MouthDimpleLeft, Avatar_MouthDimpleLeft_Method);
            ReceiverAddresses.Add(Avatar_MouthRaiserUpper, Avatar_MouthRaiserUpper_Method);
            ReceiverAddresses.Add(Avatar_MouthRaiserLower, Avatar_MouthRaiserLower_Method);
            ReceiverAddresses.Add(Avatar_MouthPressRight, Avatar_MouthPressRight_Method);
            ReceiverAddresses.Add(Avatar_MouthPressLeft, Avatar_MouthPressLeft_Method);
            ReceiverAddresses.Add(Avatar_MouthSadRight, Avatar_MouthSadRight_Method);
            ReceiverAddresses.Add(Avatar_MouthSadLeft, Avatar_MouthSadLeft_Method);

            // Lip parameters
            ReceiverAddresses.Add(Avatar_LipSuckUpperRight, Avatar_LipSuckUpperRight_Method);
            ReceiverAddresses.Add(Avatar_LipSuckUpperLeft, Avatar_LipSuckUpperLeft_Method);
            ReceiverAddresses.Add(Avatar_LipSuckLowerRight, Avatar_LipSuckLowerRight_Method);
            ReceiverAddresses.Add(Avatar_LipSuckLowerLeft, Avatar_LipSuckLowerLeft_Method);
            ReceiverAddresses.Add(Avatar_LipFunnelUpperRight, Avatar_LipFunnelUpperRight_Method);
            ReceiverAddresses.Add(Avatar_LipFunnelUpperLeft, Avatar_LipFunnelUpperLeft_Method);
            ReceiverAddresses.Add(Avatar_LipFunnelLowerRight, Avatar_LipFunnelLowerRight_Method);
            ReceiverAddresses.Add(Avatar_LipFunnelLowerLeft, Avatar_LipFunnelLowerLeft_Method);
            ReceiverAddresses.Add(Avatar_LipPuckerUpperRight, Avatar_LipPuckerUpperRight_Method);
            ReceiverAddresses.Add(Avatar_LipPuckerUpperLeft, Avatar_LipPuckerUpperLeft_Method);
            ReceiverAddresses.Add(Avatar_LipPuckerLowerRight, Avatar_LipPuckerLowerRight_Method);
            ReceiverAddresses.Add(Avatar_LipPuckerLowerLeft, Avatar_LipPuckerLowerLeft_Method);

            // Nose parameters
            ReceiverAddresses.Add(Avatar_NoseSneerRight, Avatar_NoseSneerRight_Method);
            ReceiverAddresses.Add(Avatar_NoseSneerLeft, Avatar_NoseSneerLeft_Method);

            // Tongue parameters
            ReceiverAddresses.Add(Avatar_TongueOut, Avatar_TongueOut_Method);
        }

        #region Eye Methods
        private void Avatar_EyeY_Method(OscMessageValues values)
            => GetAnimStateMachine()?.SetFloat(Avatar_EyeY, values.ReadFloatElement(0));
        private void Avatar_EyeX_Method(OscMessageValues values)
            => GetAnimStateMachine()?.SetFloat(Avatar_EyeX, values.ReadFloatElement(0));
        private void Avatar_EyeLeftX_Method(OscMessageValues values)
            => GetAnimStateMachine()?.SetFloat(Avatar_EyeLeftX, values.ReadFloatElement(0));
        private void Avatar_EyeLeftY_Method(OscMessageValues values)
            => GetAnimStateMachine()?.SetFloat(Avatar_EyeLeftY, values.ReadFloatElement(0));
        private void Avatar_EyeRightX_Method(OscMessageValues values)
            => GetAnimStateMachine()?.SetFloat(Avatar_EyeRightX, values.ReadFloatElement(0));
        private void Avatar_EyeRightY_Method(OscMessageValues values)
            => GetAnimStateMachine()?.SetFloat(Avatar_EyeRightY, values.ReadFloatElement(0));
        private void Avatar_EyeOpenLeft_Method(OscMessageValues values)
            => GetAnimStateMachine()?.SetFloat(Avatar_EyeOpenLeft, values.ReadFloatElement(0));
        private void Avatar_EyeOpenRight_Method(OscMessageValues values)
            => GetAnimStateMachine()?.SetFloat(Avatar_EyeOpenRight, values.ReadFloatElement(0));
        private void Avatar_EyeOpen_Method(OscMessageValues values)
            => GetAnimStateMachine()?.SetFloat(Avatar_EyeOpen, values.ReadFloatElement(0));
        private void Avatar_EyeClosedLeft_Method(OscMessageValues values)
            => GetAnimStateMachine()?.SetFloat(Avatar_EyeClosedLeft, values.ReadFloatElement(0));
        private void Avatar_EyeClosedRight_Method(OscMessageValues values)
            => GetAnimStateMachine()?.SetFloat(Avatar_EyeClosedRight, values.ReadFloatElement(0));
        private void Avatar_EyeClosed_Method(OscMessageValues values)
            => GetAnimStateMachine()?.SetFloat(Avatar_EyeClosed, values.ReadFloatElement(0));
        private void Avatar_EyeLidLeft_Method(OscMessageValues values)
            => GetAnimStateMachine()?.SetFloat(Avatar_EyeLidLeft, values.ReadFloatElement(0));
        private void Avatar_EyeLidRight_Method(OscMessageValues values)
            => GetAnimStateMachine()?.SetFloat(Avatar_EyeLidRight, values.ReadFloatElement(0));
        private void Avatar_EyeLid_Method(OscMessageValues values)
            => GetAnimStateMachine()?.SetFloat(Avatar_EyeLid, values.ReadFloatElement(0));
        private void Avatar_EyeSquint_Method(OscMessageValues values)
            => GetAnimStateMachine()?.SetFloat(Avatar_EyeSquint, values.ReadFloatElement(0));
        private void Avatar_EyesSquint_Method(OscMessageValues values)
            => GetAnimStateMachine()?.SetFloat(Avatar_EyesSquint, values.ReadFloatElement(0));
        private void Avatar_EyeSquintRight_Method(OscMessageValues values)
            => GetAnimStateMachine()?.SetFloat(Avatar_EyeSquintRight, values.ReadFloatElement(0));
        private void Avatar_EyeSquintLeft_Method(OscMessageValues values)
            => GetAnimStateMachine()?.SetFloat(Avatar_EyeSquintLeft, values.ReadFloatElement(0));
        #endregion

        #region Brow Methods
        private void Avatar_BrowPinchRight_Method(OscMessageValues values)
            => GetAnimStateMachine()?.SetFloat(Avatar_BrowPinchRight, values.ReadFloatElement(0));
        private void Avatar_BrowPinchLeft_Method(OscMessageValues values)
            => GetAnimStateMachine()?.SetFloat(Avatar_BrowPinchLeft, values.ReadFloatElement(0));
        private void Avatar_BrowLowererRight_Method(OscMessageValues values)
            => GetAnimStateMachine()?.SetFloat(Avatar_BrowLowererRight, values.ReadFloatElement(0));
        private void Avatar_BrowLowererLeft_Method(OscMessageValues values)
            => GetAnimStateMachine()?.SetFloat(Avatar_BrowLowererLeft, values.ReadFloatElement(0));
        private void Avatar_BrowInnerUpRight_Method(OscMessageValues values)
            => GetAnimStateMachine()?.SetFloat(Avatar_BrowInnerUpRight, values.ReadFloatElement(0));
        private void Avatar_BrowInnerUpLeft_Method(OscMessageValues values)
            => GetAnimStateMachine()?.SetFloat(Avatar_BrowInnerUpLeft, values.ReadFloatElement(0));
        private void Avatar_BrowUp_Method(OscMessageValues values)
            => GetAnimStateMachine()?.SetFloat(Avatar_BrowUp, values.ReadFloatElement(0));
        private void Avatar_BrowDown_Method(OscMessageValues values)
            => GetAnimStateMachine()?.SetFloat(Avatar_BrowDown, values.ReadFloatElement(0));
        private void Avatar_BrowInnerUp_Method(OscMessageValues values)
            => GetAnimStateMachine()?.SetFloat(Avatar_BrowInnerUp, values.ReadFloatElement(0));
        private void Avatar_BrowUpRight_Method(OscMessageValues values)
            => GetAnimStateMachine()?.SetFloat(Avatar_BrowUpRight, values.ReadFloatElement(0));
        private void Avatar_BrowUpLeft_Method(OscMessageValues values)
            => GetAnimStateMachine()?.SetFloat(Avatar_BrowUpLeft, values.ReadFloatElement(0));
        private void Avatar_BrowDownRight_Method(OscMessageValues values)
            => GetAnimStateMachine()?.SetFloat(Avatar_BrowDownRight, values.ReadFloatElement(0));
        private void Avatar_BrowDownLeft_Method(OscMessageValues values)
            => GetAnimStateMachine()?.SetFloat(Avatar_BrowDownLeft, values.ReadFloatElement(0));
        private void Avatar_BrowExpressionRight_Method(OscMessageValues values)
            => GetAnimStateMachine()?.SetFloat(Avatar_BrowExpressionRight, values.ReadFloatElement(0));
        private void Avatar_BrowExpressionLeft_Method(OscMessageValues values)
            => GetAnimStateMachine()?.SetFloat(Avatar_BrowExpressionLeft, values.ReadFloatElement(0));
        private void Avatar_BrowExpression_Method(OscMessageValues values)
            => GetAnimStateMachine()?.SetFloat(Avatar_BrowExpression, values.ReadFloatElement(0));
        #endregion

        #region Cheek Methods
        private void Avatar_CheekSquintRight_Method(OscMessageValues values)
            => GetAnimStateMachine()?.SetFloat(Avatar_CheekSquintRight, values.ReadFloatElement(0));
        private void Avatar_CheekSquintLeft_Method(OscMessageValues values)
            => GetAnimStateMachine()?.SetFloat(Avatar_CheekSquintLeft, values.ReadFloatElement(0));
        private void Avatar_CheekPuffRight_Method(OscMessageValues values)
            => GetAnimStateMachine()?.SetFloat(Avatar_CheekPuffRight, values.ReadFloatElement(0));
        private void Avatar_CheekPuffLeft_Method(OscMessageValues values)
            => GetAnimStateMachine()?.SetFloat(Avatar_CheekPuffLeft, values.ReadFloatElement(0));
        #endregion

        #region Jaw Methods
        private void Avatar_JawOpen_Method(OscMessageValues values)
            => GetAnimStateMachine()?.SetFloat(Avatar_JawOpen, values.ReadFloatElement(0));
        private void Avatar_JawRight_Method(OscMessageValues values)
            => GetAnimStateMachine()?.SetFloat(Avatar_JawRight, values.ReadFloatElement(0));
        private void Avatar_JawForward_Method(OscMessageValues values)
            => GetAnimStateMachine()?.SetFloat(Avatar_JawForward, values.ReadFloatElement(0));
        private void Avatar_JawX_Method(OscMessageValues values)
            => GetAnimStateMachine()?.SetFloat(Avatar_JawX, values.ReadFloatElement(0));
        private void Avatar_JawZ_Method(OscMessageValues values)
            => GetAnimStateMachine()?.SetFloat(Avatar_JawZ, values.ReadFloatElement(0));
        #endregion

        #region Mouth Methods
        private void Avatar_MouthClosed_Method(OscMessageValues values)
            => GetAnimStateMachine()?.SetFloat(Avatar_MouthClosed, values.ReadFloatElement(0));
        private void Avatar_MouthUpperUpRight_Method(OscMessageValues values)
            => GetAnimStateMachine()?.SetFloat(Avatar_MouthUpperUpRight, values.ReadFloatElement(0));
        private void Avatar_MouthUpperUpLeft_Method(OscMessageValues values)
            => GetAnimStateMachine()?.SetFloat(Avatar_MouthUpperUpLeft, values.ReadFloatElement(0));
        private void Avatar_MouthUpperDeepenRight_Method(OscMessageValues values)
            => GetAnimStateMachine()?.SetFloat(Avatar_MouthUpperDeepenRight, values.ReadFloatElement(0));
        private void Avatar_MouthUpperDeepenLeft_Method(OscMessageValues values)
            => GetAnimStateMachine()?.SetFloat(Avatar_MouthUpperDeepenLeft, values.ReadFloatElement(0));
        private void Avatar_MouthLowerDownRight_Method(OscMessageValues values)
            => GetAnimStateMachine()?.SetFloat(Avatar_MouthLowerDownRight, values.ReadFloatElement(0));
        private void Avatar_MouthLowerDownLeft_Method(OscMessageValues values)
            => GetAnimStateMachine()?.SetFloat(Avatar_MouthLowerDownLeft, values.ReadFloatElement(0));
        private void Avatar_MouthUpperRight_Method(OscMessageValues values)
            => GetAnimStateMachine()?.SetFloat(Avatar_MouthUpperRight, values.ReadFloatElement(0));
        private void Avatar_MouthLowerRight_Method(OscMessageValues values)
            => GetAnimStateMachine()?.SetFloat(Avatar_MouthLowerRight, values.ReadFloatElement(0));
        private void Avatar_MouthFrownRight_Method(OscMessageValues values)
            => GetAnimStateMachine()?.SetFloat(Avatar_MouthFrownRight, values.ReadFloatElement(0));
        private void Avatar_MouthFrownLeft_Method(OscMessageValues values)
            => GetAnimStateMachine()?.SetFloat(Avatar_MouthFrownLeft, values.ReadFloatElement(0));
        private void Avatar_MouthStretchRight_Method(OscMessageValues values)
            => GetAnimStateMachine()?.SetFloat(Avatar_MouthStretchRight, values.ReadFloatElement(0));
        private void Avatar_MouthStretchLeft_Method(OscMessageValues values)
            => GetAnimStateMachine()?.SetFloat(Avatar_MouthStretchLeft, values.ReadFloatElement(0));
        private void Avatar_MouthDimpleRight_Method(OscMessageValues values)
            => GetAnimStateMachine()?.SetFloat(Avatar_MouthDimpleRight, values.ReadFloatElement(0));
        private void Avatar_MouthDimpleLeft_Method(OscMessageValues values)
            => GetAnimStateMachine()?.SetFloat(Avatar_MouthDimpleLeft, values.ReadFloatElement(0));
        private void Avatar_MouthRaiserUpper_Method(OscMessageValues values)
            => GetAnimStateMachine()?.SetFloat(Avatar_MouthRaiserUpper, values.ReadFloatElement(0));
        private void Avatar_MouthRaiserLower_Method(OscMessageValues values)
            => GetAnimStateMachine()?.SetFloat(Avatar_MouthRaiserLower, values.ReadFloatElement(0));
        private void Avatar_MouthPressRight_Method(OscMessageValues values)
            => GetAnimStateMachine()?.SetFloat(Avatar_MouthPressRight, values.ReadFloatElement(0));
        private void Avatar_MouthPressLeft_Method(OscMessageValues values)
            => GetAnimStateMachine()?.SetFloat(Avatar_MouthPressLeft, values.ReadFloatElement(0));
        private void Avatar_MouthSadRight_Method(OscMessageValues values)
            => GetAnimStateMachine()?.SetFloat(Avatar_MouthSadRight, values.ReadFloatElement(0));
        private void Avatar_MouthSadLeft_Method(OscMessageValues values)
            => GetAnimStateMachine()?.SetFloat(Avatar_MouthSadLeft, values.ReadFloatElement(0));
        #endregion

        #region Lip Methods
        private void Avatar_LipSuckUpperRight_Method(OscMessageValues values)
            => GetAnimStateMachine()?.SetFloat(Avatar_LipSuckUpperRight, values.ReadFloatElement(0));
        private void Avatar_LipSuckUpperLeft_Method(OscMessageValues values)
            => GetAnimStateMachine()?.SetFloat(Avatar_LipSuckUpperLeft, values.ReadFloatElement(0));
        private void Avatar_LipSuckLowerRight_Method(OscMessageValues values)
            => GetAnimStateMachine()?.SetFloat(Avatar_LipSuckLowerRight, values.ReadFloatElement(0));
        private void Avatar_LipSuckLowerLeft_Method(OscMessageValues values)
            => GetAnimStateMachine()?.SetFloat(Avatar_LipSuckLowerLeft, values.ReadFloatElement(0));
        private void Avatar_LipFunnelUpperRight_Method(OscMessageValues values)
            => GetAnimStateMachine()?.SetFloat(Avatar_LipFunnelUpperRight, values.ReadFloatElement(0));
        private void Avatar_LipFunnelUpperLeft_Method(OscMessageValues values)
            => GetAnimStateMachine()?.SetFloat(Avatar_LipFunnelUpperLeft, values.ReadFloatElement(0));
        private void Avatar_LipFunnelLowerRight_Method(OscMessageValues values)
            => GetAnimStateMachine()?.SetFloat(Avatar_LipFunnelLowerRight, values.ReadFloatElement(0));
        private void Avatar_LipFunnelLowerLeft_Method(OscMessageValues values)
            => GetAnimStateMachine()?.SetFloat(Avatar_LipFunnelLowerLeft, values.ReadFloatElement(0));
        private void Avatar_LipPuckerUpperRight_Method(OscMessageValues values)
            => GetAnimStateMachine()?.SetFloat(Avatar_LipPuckerUpperRight, values.ReadFloatElement(0));
        private void Avatar_LipPuckerUpperLeft_Method(OscMessageValues values)
            => GetAnimStateMachine()?.SetFloat(Avatar_LipPuckerUpperLeft, values.ReadFloatElement(0));
        private void Avatar_LipPuckerLowerRight_Method(OscMessageValues values)
            => GetAnimStateMachine()?.SetFloat(Avatar_LipPuckerLowerRight, values.ReadFloatElement(0));
        private void Avatar_LipPuckerLowerLeft_Method(OscMessageValues values)
            => GetAnimStateMachine()?.SetFloat(Avatar_LipPuckerLowerLeft, values.ReadFloatElement(0));
        #endregion

        #region Nose Methods
        private void Avatar_NoseSneerRight_Method(OscMessageValues values)
            => GetAnimStateMachine()?.SetFloat(Avatar_NoseSneerRight, values.ReadFloatElement(0));
        private void Avatar_NoseSneerLeft_Method(OscMessageValues values)
            => GetAnimStateMachine()?.SetFloat(Avatar_NoseSneerLeft, values.ReadFloatElement(0));
        #endregion

        #region Tongue Methods
        private void Avatar_TongueOut_Method(OscMessageValues values)
            => GetAnimStateMachine()?.SetFloat(Avatar_TongueOut, values.ReadFloatElement(0));
        #endregion

        private void Tracking_Eye_EyesClosedAmount_Method(OscMessageValues values)
            => GetAnimStateMachine()?.SetFloat("EyesClosed", values.ReadFloatElement(0));

        private void Tracking_Eye_CenterPitchYaw_Method(OscMessageValues values)
        {
            var machine = GetAnimStateMachine();
            if (machine is null)
                return;

            machine.SetFloat("EyePitch", values.ReadFloatElement(0));
            machine.SetFloat("EyeYaw", values.ReadFloatElement(1));
        }

        public void GenerateARKit()
        {
            var sm = GetAnimStateMachine()?.StateMachine;
            if (sm is null)
                return;

            sm.DeleteAllVariables();
            sm.NewBool(Param_DirectBlend, true);
            sm.NewBool(Param_IsLocal, false);
            sm.NewBool(Param_EyeTrackingActive, false);
            sm.NewBool(Param_LipTrackingActive, false);
            sm.NewBool(Param_RemoteModeActive, false);
            sm.NewBool(Param_FacialExpressionsDisabled, false);
            sm.NewBool(Param_VisemesEnable, false);
            sm.NewBool(Param_StateTrackingActive, false);
            sm.NewBool(Param_StateVisemesEnable, true);
            sm.NewBool(Param_SmoothingLocal, false);
            sm.NewBool(Param_FaceTrackingEmulation, true);
            sm.NewFloat(Avatar_EyeLeftX, 0.0f);
            sm.NewFloat(Avatar_EyeRightX, 0.0f);
            sm.NewFloat(Avatar_EyeY, 0.0f);
            sm.NewFloat(Avatar_EyeLidLeft, 0.75f);
            sm.NewFloat(Avatar_EyeLidRight, 0.75f);
            sm.NewFloat(Avatar_EyeSquintLeft, 0.0f);
            sm.NewFloat(Avatar_EyeSquintRight, 0.0f);
            sm.NewFloat(Avatar_JawOpen, 0.0f);
            sm.NewFloat(Avatar_MouthClosed, 0.0f);
            sm.NewFloat(Avatar_MouthUpperUp, 0.0f);
            sm.NewFloat(Avatar_MouthLowerDown, 0.0f);
            sm.NewFloat(Avatar_SmileFrownRight, 0.0f);
            sm.NewFloat(Avatar_SmileFrownLeft, 0.0f);
            sm.NewFloat(Avatar_LipPucker, 0.0f);
            sm.NewFloat(Avatar_LipFunnel, 0.0f);
            sm.NewFloat(Avatar_JawX, 0.0f);
            sm.NewFloat(Avatar_MouthX, 0.0f);
            sm.NewFloat(Avatar_CheekPuffLeft, 0.0f);
            sm.NewFloat(Avatar_MouthRaiserLower, 0.0f);
            sm.NewFloat(Avatar_JawForward, 0.0f);
            sm.NewFloat(Avatar_TongueOut, 0.0f);
            sm.NewFloat(Avatar_NoseSneer, 0.0f);
            sm.NewFloat(Avatar_MouthStretchLeft, 0.0f);
            sm.NewFloat(Avatar_MouthStretchRight, 0.0f);
            sm.NewFloat(Avatar_BrowExpressionLeft, 0.0f);
            sm.NewFloat(Avatar_BrowExpressionRight, 0.0f);
            sm.NewFloat(Avatar_LipSuckUpper, 0.0f);
            sm.NewFloat(Avatar_LipSuckLower, 0.0f);
            sm.NewFloat(Avatar_MouthRaiserUpper, 0.0f);
            sm.NewFloat(Avatar_MouthPress, 0.0f);

            Gen124(sm, Avatar_EyeSquintLeft);
            Gen124(sm, Avatar_EyeSquintRight);
            Gen124(sm, Avatar_MouthLowerDown);
            Gen124(sm, Avatar_LipSuckUpper);
            Gen124(sm, Avatar_LipSuckLower);
            GenNeg124(sm, Avatar_SmileFrownRight);
            GenNeg124(sm, Avatar_SmileFrownLeft);
            Gen124(sm, Avatar_CheekPuffLeft);
            Gen124(sm, Avatar_LipPucker);
            Gen124(sm, Avatar_LipFunnel);
            GenNeg124(sm, Avatar_JawX);
            GenNeg1248(sm, Avatar_MouthX);
            Gen124(sm, Avatar_MouthRaiserUpper);
            Gen124(sm, Avatar_MouthRaiserLower);
            Gen124(sm, Avatar_JawForward);
            Gen124(sm, Avatar_TongueOut);
            Gen124(sm, Avatar_NoseSneer);
            Gen124(sm, Avatar_MouthStretchLeft);
            Gen124(sm, Avatar_MouthStretchRight);
            GenNeg124(sm, Avatar_BrowExpressionLeft);
            GenNeg124(sm, Avatar_BrowExpressionRight);
            Gen124(sm, Avatar_MouthPress);

            sm.NewFloat("OSCm/Local/FloatSmoothing", 0.0f);
            sm.NewFloat("OSCm/Local/FloatScaler", 15);
            sm.NewFloat("OSCm/Local/FloatMod", 0.5425f);
            sm.NewFloat("OSCm/Remote/FloatSmoothing", 0.0f);
            sm.NewFloat("OSCm/Remote/FloatScaler", 15);
            sm.NewFloat("OSCm/Remote/FloatMod", 0.05f);
            sm.NewFloat("OSCm/Remote/EyeLidSmoothing", 0.0f);
            sm.NewFloat("OSCm/Remote/EyeLidScaler", 15);
            sm.NewFloat("OSCm/Remote/EyeLidMod", 0.2f);
            sm.NewFloat("OSCm/Remote/BinarySmoothing", 0.0f);
            sm.NewFloat("OSCm/Remote/BinaryScaler", 15);
            sm.NewFloat("OSCm/Remote/BinaryMod", 0.05f);

            OSCmProxy(sm, Avatar_EyeLeftX, 0.0f);
            OSCmProxy(sm, Avatar_EyeRightX, 0.0f);
            OSCmProxy(sm, Avatar_EyeY, 0.0f);
            OSCmProxy(sm, Avatar_EyeLidLeft, 0.75f);
            OSCmProxy(sm, Avatar_EyeLidRight, 0.75f);
            OSCmProxy(sm, Avatar_EyeSquintLeft, 0.0f);
            OSCmProxy(sm, Avatar_EyeSquintRight, 0.0f);
            OSCmProxy(sm, Avatar_JawOpen, 0.0f);
            OSCmProxy(sm, Avatar_MouthClosed, 0.0f);
            OSCmProxy(sm, Avatar_MouthUpperUp, 0.0f);
            OSCmProxy(sm, Avatar_MouthLowerDown, 0.0f);
            OSCmProxy(sm, Avatar_LipSuckUpper, 0.0f);
            OSCmProxy(sm, Avatar_LipSuckLower, 0.0f);
            OSCmProxy(sm, Avatar_SmileFrownRight, 0.0f);
            OSCmProxy(sm, Avatar_SmileFrownLeft, 0.0f);
            OSCmProxy(sm, Avatar_CheekPuffLeft, 0.0f);
            OSCmProxy(sm, Avatar_LipPucker, 0.0f);
            OSCmProxy(sm, Avatar_LipFunnel, 0.0f);
            OSCmProxy(sm, Avatar_JawX, 0.0f);
            OSCmProxy(sm, Avatar_MouthX, 0.0f);
            OSCmProxy(sm, Avatar_MouthRaiserUpper, 0.0f);
            OSCmProxy(sm, Avatar_MouthRaiserLower, 0.0f);
            OSCmProxy(sm, Avatar_JawForward, 0.0f);
            OSCmProxy(sm, Avatar_TongueOut, 0.0f);
            OSCmProxy(sm, Avatar_NoseSneer, 0.0f);
            OSCmProxy(sm, Avatar_MouthStretchLeft, 0.0f);
            OSCmProxy(sm, Avatar_MouthStretchRight, 0.0f);
            OSCmProxy(sm, Avatar_BrowExpressionLeft, 0.0f);
            OSCmProxy(sm, Avatar_BrowExpressionRight, 0.0f);
            OSCmProxy(sm, Avatar_MouthPress, 0.0f);

            sm.NewFloat("OSCm/TimeSinceLoad", 0.0f);
            sm.NewFloat("OSCm/LastTimeSinceLoad", 0.0f);
            sm.NewFloat("OSCm/FrameTime", 0.0f);

            sm.Layers =
            [
                MakeTrackingStateLayer(),
                MakeFaceTrackingLayer(),
            ];
        }

        private static void OSCmProxy(AnimStateMachine sm, string name, float defaultValue)
            => sm.NewFloat(OSCmProxyName(name), defaultValue);

        public static string OSCmProxyName(string name)
            => $"OSCm/Proxy/{name}";

        private static void Gen124(AnimStateMachine sm, string name)
        {
            sm.NewFloat($"{name}1", 0.0f);
            sm.NewFloat($"{name}2", 0.0f);
            sm.NewFloat($"{name}4", 0.0f);
        }

        private static void GenNeg124(AnimStateMachine sm, string name)
        {
            sm.NewFloat($"{name}Negative", 0.0f);
            sm.NewFloat($"{name}1", 0.0f);
            sm.NewFloat($"{name}2", 0.0f);
            sm.NewFloat($"{name}4", 0.0f);
        }

        private static void GenNeg1248(AnimStateMachine sm, string name)
        {
            sm.NewFloat($"{name}Negative", 0.0f);
            sm.NewFloat($"{name}1", 0.0f);
            sm.NewFloat($"{name}2", 0.0f);
            sm.NewFloat($"{name}4", 0.0f);
            sm.NewFloat($"{name}8", 0.0f);
        }

        private static AnimLayer MakeFaceTrackingLayer()
        {
            AnimLayer layer = new();
            float tolerance = 0.008f;

            AnimTransitionCondition[] faceTrackingDisabledConditions =
            [
                new(Param_EyeTrackingActive, EComparison.LessThan, tolerance),
                new(Param_LipTrackingActive, EComparison.LessThan, tolerance),
                new(Param_StateTrackingActive, true),
                new(Param_IsLocal, EComparison.GreaterThan, 1.0f - tolerance),
            ];
            AnimTransitionCondition[] facialExpressionsDisabledConditionsBecauseEye =
            [
                new(Param_EyeTrackingActive, EComparison.GreaterThan, 1.0f - tolerance),
                new(Param_StateTrackingActive, false),
                new(Param_IsLocal, EComparison.GreaterThan, 1.0f - tolerance),
            ];
            AnimTransitionCondition[] facialExpressionsDisabledConditionsBecauseLip =
            [
                new(Param_LipTrackingActive, EComparison.GreaterThan, 1.0f - tolerance),
                new(Param_StateTrackingActive, false),
                new(Param_IsLocal, EComparison.GreaterThan, 1.0f - tolerance),
            ];
            AnimTransitionCondition[] visemesEnabledConditions =
            [
                new(Param_VisemesEnable, true),
                new(Param_StateVisemesEnable, false),
            ];
            AnimTransitionCondition[] visemesDisabledConditions =
            [
                new(Param_VisemesEnable, false),
                new(Param_StateVisemesEnable, true),
            ];
            AnimTransitionCondition[] eyeTrackingEnabledConditions =
            [
                new(Param_EyeTrackingActive, EComparison.GreaterThan, 1.0f - tolerance),
            ];
            AnimTransitionCondition[] eyeTrackingDisabledConditions =
            [
                new(Param_EyeTrackingActive, EComparison.LessThan, tolerance),
            ];

            AnimState faceTrackingDisabledState = new("Face Tracking Disabled");
            layer.AnyState.AddTransitionTo(faceTrackingDisabledState, faceTrackingDisabledConditions);

            AnimState facialExpressionsDisabledState = new("Facial Expressions Disabled");
            layer.AnyState.AddTransitionTo(facialExpressionsDisabledState, facialExpressionsDisabledConditionsBecauseEye);
            layer.AnyState.AddTransitionTo(facialExpressionsDisabledState, facialExpressionsDisabledConditionsBecauseLip);

            AnimState visemesEnabledState = new("Visemes Enabled");
            layer.AnyState.AddTransitionTo(visemesEnabledState, visemesEnabledConditions);

            AnimState visemesDisabledState = new("Visemes Disabled");
            layer.AnyState.AddTransitionTo(visemesDisabledState, visemesDisabledConditions);

            AnimState eyeTrackingEnabledState = new("Eye Tracking Enabled");
            var eyeEnabledTransition = layer.AnyState.AddTransitionTo(eyeTrackingEnabledState, eyeTrackingEnabledConditions);
            eyeEnabledTransition.CanTransitionToSelf = true;

            AnimState eyeTrackingDisabledState = new("Eye Tracking Disabled");
            var eyeDisabledTransition = layer.AnyState.AddTransitionTo(eyeTrackingDisabledState, eyeTrackingDisabledConditions);
            eyeDisabledTransition.CanTransitionToSelf = true;

            AnimState initState = new("Init");

            var faceTrackingDisabledDriver1 = faceTrackingDisabledState.AddComponent<AnimParameterDriverComponent>();
            faceTrackingDisabledDriver1.ExecuteRemotely = false;
            faceTrackingDisabledDriver1.Operation = AnimParameterDriverComponent.EOperation.Set;
            faceTrackingDisabledDriver1.DstParameterName = Param_FacialExpressionsDisabled;
            faceTrackingDisabledDriver1.ConstantValueBool = false;

            var faceTrackingDisabledDriver2 = faceTrackingDisabledState.AddComponent<AnimParameterDriverComponent>();
            faceTrackingDisabledDriver2.ExecuteRemotely = false;
            faceTrackingDisabledDriver2.Operation = AnimParameterDriverComponent.EOperation.Set;
            faceTrackingDisabledDriver2.DstParameterName = Param_StateTrackingActive;
            faceTrackingDisabledDriver2.ConstantValueBool = false;

            var facialExpressionsDisabledDriver1 = facialExpressionsDisabledState.AddComponent<AnimParameterDriverComponent>();
            facialExpressionsDisabledDriver1.Operation = AnimParameterDriverComponent.EOperation.Set;
            facialExpressionsDisabledDriver1.DstParameterName = Param_FacialExpressionsDisabled;
            facialExpressionsDisabledDriver1.ConstantValueBool = true;

            var facialExpressionsDisabledDriver2 = facialExpressionsDisabledState.AddComponent<AnimParameterDriverComponent>();
            facialExpressionsDisabledDriver2.Operation = AnimParameterDriverComponent.EOperation.Set;
            facialExpressionsDisabledDriver2.DstParameterName = Param_StateTrackingActive;
            facialExpressionsDisabledDriver2.ConstantValueBool = true;

            var visemesEnabledDriver = visemesEnabledState.AddComponent<AnimParameterDriverComponent>();
            visemesEnabledDriver.Operation = AnimParameterDriverComponent.EOperation.Set;
            visemesEnabledDriver.DstParameterName = Param_StateVisemesEnable;
            visemesEnabledDriver.ConstantValueBool = true;

            var visemesEnabledTrackingController = visemesEnabledState.AddComponent<TrackingControllerComponent>();
            visemesEnabledTrackingController.TrackingModeMouth = TrackingControllerComponent.ETrackingMode.Tracking;

            var visemesDisabledDriver = visemesDisabledState.AddComponent<AnimParameterDriverComponent>();
            visemesDisabledDriver.Operation = AnimParameterDriverComponent.EOperation.Set;
            visemesDisabledDriver.DstParameterName = Param_StateVisemesEnable;
            visemesDisabledDriver.ConstantValueBool = false;

            var visemesDisabledTrackingController = visemesDisabledState.AddComponent<TrackingControllerComponent>();
            visemesDisabledTrackingController.TrackingModeMouth = TrackingControllerComponent.ETrackingMode.Animation;

            var eyeTrackingEnabledDriver = eyeTrackingEnabledState.AddComponent<AnimParameterDriverComponent>();
            eyeTrackingEnabledDriver.Operation = AnimParameterDriverComponent.EOperation.Set;
            eyeTrackingEnabledDriver.DstParameterName = Param_StateEyeTracking;
            eyeTrackingEnabledDriver.ConstantValue = 1.0f;

            var eyeTrackingEnabledTrackingController = eyeTrackingEnabledState.AddComponent<TrackingControllerComponent>();
            eyeTrackingEnabledTrackingController.TrackingModeEyes = TrackingControllerComponent.ETrackingMode.Animation;

            var eyeTrackingDisabledDriver = eyeTrackingDisabledState.AddComponent<AnimParameterDriverComponent>();
            eyeTrackingDisabledDriver.Operation = AnimParameterDriverComponent.EOperation.Set;
            eyeTrackingDisabledDriver.DstParameterName = Param_StateEyeTracking;
            eyeTrackingDisabledDriver.ConstantValue = 0.0f;

            var eyeTrackingDisabledTrackingController = eyeTrackingDisabledState.AddComponent<TrackingControllerComponent>();
            eyeTrackingDisabledTrackingController.TrackingModeEyes = TrackingControllerComponent.ETrackingMode.Tracking;

            layer.States = 
            [
                initState,
                faceTrackingDisabledState,
                facialExpressionsDisabledState,
                visemesEnabledState,
                visemesDisabledState,
                eyeTrackingEnabledState,
                eyeTrackingDisabledState,
            ];

            layer.InitialState = initState;

            return layer;
        }

        private static AnimLayer MakeTrackingStateLayer()
        {
            AnimLayer layer = new();

            AnimState ftLocalRootState = new("FT Local Root");
            AnimState ftRemoteRootState = new("FT Remote Root");

            ftLocalRootState.AddTransitionTo(ftRemoteRootState,
            [
                new(Param_IsLocal, EComparison.LessThan, 0.5f),
            ]);
            ftLocalRootState.AddTransitionTo(ftRemoteRootState,
            [
                new(Param_RemoteModeActive, true),
            ]);

            ftRemoteRootState.AddTransitionTo(ftLocalRootState,
            [
                new(Param_IsLocal, EComparison.GreaterThan, 0.5f),
                new(Param_RemoteModeActive, false),
            ]);

            layer.States =
            [
                ftLocalRootState,
                ftRemoteRootState,
            ];

            layer.InitialState = ftLocalRootState;

            var reset = MakeResetFTAnimator();
            var driver = MakeFTBlendShapeDriver();

            ftLocalRootState.Animation = new BlendTreeDirect()
            {
                Name = "FT Local Root",
                Children = 
                [
                    new BlendTreeDirect.Child()
                    {
                        WeightParameterName = Param_DirectBlend,
                        Motion = reset,
                        Speed = 1.0f,
                    },
                    new BlendTreeDirect.Child()
                    {
                        WeightParameterName = Param_DirectBlend,
                        Motion = MakeOSCm_Local(),
                        Speed = 1.0f,
                    },
                    new BlendTreeDirect.Child()
                    {
                        WeightParameterName = Param_DirectBlend,
                        Motion = driver,
                        Speed = 1.0f,
                    },
                ]
            };
            ftRemoteRootState.Animation = new BlendTreeDirect()
            {
                Name = "FT Remote Root",
                Children =
                [
                    new BlendTreeDirect.Child()
                    {
                        WeightParameterName = Param_DirectBlend,
                        Motion = reset,
                        Speed = 1.0f,
                    },
                    new BlendTreeDirect.Child()
                    {
                        WeightParameterName = Param_DirectBlend,
                        Motion = MakeOSCm_Remote(),
                        Speed = 1.0f,
                    },
                    new BlendTreeDirect.Child()
                    {
                        WeightParameterName = Param_DirectBlend,
                        Motion = driver,
                        Speed = 1.0f,
                    },
                ]
            };

            return layer;

        }

        private static BlendTreeDirect MakeFTBlendShapeDriver()
        {
            BlendTreeDirect blendTree = new()
            {
                Name = "FT Blendshape Driver",
                Children =
                [
                    new BlendTreeDirect.Child()
                    {
                        WeightParameterName = Param_EyeTrackingActive,
                        Motion = EyeTrackingBlendTrees.RightEyeLidBlend(),
                    },
                    new BlendTreeDirect.Child()
                    {
                        WeightParameterName = Param_EyeTrackingActive,
                        Motion = EyeTrackingBlendTrees.LeftEyeLidBlend(),
                    },
                    new BlendTreeDirect.Child()
                    {
                        WeightParameterName = Param_EyeTrackingActive,
                        Motion = EyeTrackingBlendTrees.EyeLookRightBlend(),
                    },
                    new BlendTreeDirect.Child()
                    {
                        WeightParameterName = Param_EyeTrackingActive,
                        Motion = EyeTrackingBlendTrees.EyeLookLeftBlend(),
                    },
                    new BlendTreeDirect.Child()
                    {
                        WeightParameterName = Param_EyeTrackingActive,
                        Motion = EyeTrackingBlendTrees.BrowInnerUpBlend(),
                    },
                    new BlendTreeDirect.Child()
                    {
                        WeightParameterName = Param_EyeTrackingActive,
                        Motion = EyeTrackingBlendTrees.BrowOuterUpRightBlend(),
                    },
                    new BlendTreeDirect.Child()
                    {
                        WeightParameterName = Param_EyeTrackingActive,
                        Motion = EyeTrackingBlendTrees.BrowOuterUpLeftBlend(),
                    },
                    new BlendTreeDirect.Child()
                    {
                        WeightParameterName = Param_EyeTrackingActive,
                        Motion = EyeTrackingBlendTrees.BrowDownRightBlend(),
                    },
                    new BlendTreeDirect.Child()
                    {
                        WeightParameterName = Param_EyeTrackingActive,
                        Motion = EyeTrackingBlendTrees.BrowDownLeftBlend(),
                    },
                    new BlendTreeDirect.Child()
                    {
                        WeightParameterName = Param_LipTrackingActive,
                        Motion = LipTrackingBlendTrees.NoseSneerBlend(),
                    },
                    new BlendTreeDirect.Child()
                    {
                        WeightParameterName = Param_LipTrackingActive,
                        Motion = LipTrackingBlendTrees.CheekPuffBlend(),
                    },
                    new BlendTreeDirect.Child()
                    {
                        WeightParameterName = Param_LipTrackingActive,
                        Motion = LipTrackingBlendTrees.JawOpenBlend(),
                    },
                    new BlendTreeDirect.Child()
                    {
                        WeightParameterName = Param_LipTrackingActive,
                        Motion = LipTrackingBlendTrees.MouthClosedBlend(),
                    },
                    new BlendTreeDirect.Child()
                    {
                        WeightParameterName = Param_LipTrackingActive,
                        Motion = LipTrackingBlendTrees.JawForwardBlend(),
                    },
                    new BlendTreeDirect.Child()
                    {
                        WeightParameterName = Param_LipTrackingActive,
                        Motion = LipTrackingBlendTrees.MouthUpperUpRightBlend(),
                    },
                    new BlendTreeDirect.Child()
                    {
                        WeightParameterName = Param_LipTrackingActive,
                        Motion = LipTrackingBlendTrees.MouthUpperUpLeftBlend(),
                    },
                    new BlendTreeDirect.Child()
                    {
                        WeightParameterName = Param_LipTrackingActive,
                        Motion = LipTrackingBlendTrees.MouthLowerDownBlend(),
                    },
                    new BlendTreeDirect.Child()
                    {
                        WeightParameterName = Param_LipTrackingActive,
                        Motion = LipTrackingBlendTrees.MouthRollUpperBlend(),
                    },
                    new BlendTreeDirect.Child()
                    {
                        WeightParameterName = Param_LipTrackingActive,
                        Motion = LipTrackingBlendTrees.MouthRollLowerBlend(),
                    },
                    new BlendTreeDirect.Child()
                    {
                        WeightParameterName = Param_LipTrackingActive,
                        Motion = LipTrackingBlendTrees.CheekSquintRightBlend(),
                    },
                    new BlendTreeDirect.Child()
                    {
                        WeightParameterName = Param_LipTrackingActive,
                        Motion = LipTrackingBlendTrees.CheekSquintLeftBlend(),
                    },
                    new BlendTreeDirect.Child()
                    {
                        WeightParameterName = Param_LipTrackingActive,
                        Motion = LipTrackingBlendTrees.MouthSmileRightBlend(),
                    },
                    new BlendTreeDirect.Child()
                    {
                        WeightParameterName = Param_LipTrackingActive,
                        Motion = LipTrackingBlendTrees.MouthSmileLeftBlend(),
                    },
                    new BlendTreeDirect.Child()
                    {
                        WeightParameterName = Param_LipTrackingActive,
                        Motion = LipTrackingBlendTrees.MouthFrownRightBlend(),
                    },
                    new BlendTreeDirect.Child()
                    {
                        WeightParameterName = Param_LipTrackingActive,
                        Motion = LipTrackingBlendTrees.MouthFrownLeftBlend(),
                    },
                    new BlendTreeDirect.Child()
                    {
                        WeightParameterName = Param_LipTrackingActive,
                        Motion = LipTrackingBlendTrees.LipPuckerBlend(),
                    },
                    new BlendTreeDirect.Child()
                    {
                        WeightParameterName = Param_LipTrackingActive,
                        Motion = LipTrackingBlendTrees.LimitJawX_MouthX(),
                    },
                    new BlendTreeDirect.Child()
                    {
                        WeightParameterName = Param_LipTrackingActive,
                        Motion = LipTrackingBlendTrees.MouthXBlend(),
                    },
                    new BlendTreeDirect.Child()
                    {
                        WeightParameterName = Param_LipTrackingActive,
                        Motion = LipTrackingBlendTrees.MouthFunnelBlend(),
                    },
                    new BlendTreeDirect.Child()
                    {
                        WeightParameterName = Param_LipTrackingActive,
                        Motion = LipTrackingBlendTrees.MouthShrugUpperBlend(),
                    },
                    new BlendTreeDirect.Child()
                    {
                        WeightParameterName = Param_LipTrackingActive,
                        Motion = LipTrackingBlendTrees.MouthShrugLowerBlend(),
                    },
                    new BlendTreeDirect.Child()
                    {
                        WeightParameterName = Param_LipTrackingActive,
                        Motion = LipTrackingBlendTrees.TongueOutBlend(),
                    },
                    new BlendTreeDirect.Child()
                    {
                        WeightParameterName = Param_LipTrackingActive,
                        Motion = LipTrackingBlendTrees.MouthStretchLeftBlend(),
                    },
                    new BlendTreeDirect.Child()
                    {
                        WeightParameterName = Param_LipTrackingActive,
                        Motion = LipTrackingBlendTrees.MouthStretchRightBlend(),
                    },
                    new BlendTreeDirect.Child()
                    {
                        WeightParameterName = Param_LipTrackingActive,
                        Motion = LipTrackingBlendTrees.MouthPressBlend(),
                    },
                ]
            };
            return blendTree;
        }

        private static BlendTreeDirect MakeOSCm_Local()
        {
            return new BlendTreeDirect()
            {
                Name = "OSCm_Local",
                Children =
                [
                    new BlendTreeDirect.Child()
                    {
                        WeightParameterName = Param_DirectBlend,
                        Motion = OscmBlendTrees.FrameTimeCounter(),
                    },
                    new BlendTreeDirect.Child()
                    {
                        WeightParameterName = "OSCm/TimeSinceLoad",
                        Motion = OscmBlendTrees.FrameTimeIsOneAndLastTime(),
                    },
                    new BlendTreeDirect.Child()
                    {
                        WeightParameterName = "OSCm/LastTimeSinceLoad",
                        Motion = OscmBlendTrees.FrameTimeIsNegativeOne(),
                    },
                    new BlendTreeDirect.Child()
                    {
                        WeightParameterName = Param_DirectBlend,
                        Motion = OscmBlendTrees.LocalSmoothing(),
                    },
                    new BlendTreeDirect.Child()
                    {
                        WeightParameterName = Param_DirectBlend,
                        Motion = OscmBlendTrees.SmoothingCutoffLocal(),
                    },
                    new BlendTreeDirect.Child()
                    {
                        WeightParameterName = Param_DirectBlend,
                        Motion = OscmBlendTrees.RemoteSmoothingOff(),
                    },
                    new BlendTreeDirect.Child()
                    {
                        WeightParameterName = Param_EyeTrackingActive,
                        Motion = OscmBlendTrees.EyeTrackingSmoothing(),
                    },
                    new BlendTreeDirect.Child()
                    {
                        WeightParameterName = Param_EyeDilationEnable,
                        Motion = OscmBlendTrees.PupilDilationSmoothing(),
                    },
                    new BlendTreeDirect.Child()
                    {
                        WeightParameterName = Param_LipTrackingActive,
                        Motion = OscmBlendTrees.LipTrackingSmoothing(),
                    },
                ]
            };
        }
        private static BlendTreeDirect MakeOSCm_Remote()
        {
            return new BlendTreeDirect()
            {
                Name = "OSCm_Remote",
                Children =
                [
                    new BlendTreeDirect.Child()
                    {
                        WeightParameterName = Param_DirectBlend,
                        Motion = OscmBlendTrees.FrameTimeCounter(),
                    },
                    new BlendTreeDirect.Child()
                    {
                        WeightParameterName = "OSCm/TimeSinceLoad",
                        Motion = OscmBlendTrees.FrameTimeIsOneAndLastTime(),
                    },
                    new BlendTreeDirect.Child()
                    {
                        WeightParameterName = "OSCm/LastTimeSinceLoad",
                        Motion = OscmBlendTrees.FrameTimeIsNegativeOne(),
                    },
                    new BlendTreeDirect.Child()
                    {
                        WeightParameterName = Param_DirectBlend,
                        Motion = OscmBlendTrees.SmoothingCutoffRemote(),
                    },
                    new BlendTreeDirect.Child()
                    {
                        WeightParameterName = Param_DirectBlend,
                        Motion = OscmBlendTrees.LocalSmoothingOff(),
                    },
                    new BlendTreeDirect.Child()
                    {
                        WeightParameterName = Param_EyeTrackingActive,
                        Motion = OscmBlendTrees.EyeTrackingFloatSmoothing(),
                    },
                    new BlendTreeDirect.Child()
                    {
                        WeightParameterName = Param_EyeTrackingActive,
                        Motion = OscmBlendTrees.EyeTrackingBinarySmoothing(),
                    },
                    new BlendTreeDirect.Child()
                    {
                        WeightParameterName = Param_EyeDilationEnable,
                        Motion = OscmBlendTrees.PupilDilationBinarySmoothing(),
                    },
                    new BlendTreeDirect.Child()
                    {
                        WeightParameterName = Param_LipTrackingActive,
                        Motion = OscmBlendTrees.LipTrackingFloatSmoothing(),
                    },
                    new BlendTreeDirect.Child()
                    {
                        WeightParameterName = Param_LipTrackingActive,
                        Motion = OscmBlendTrees.LipTrackingBinarySmoothing(),
                    },
                ]
            };
        }

        private static AnimationClip MakeResetFTAnimator()
        {
            AnimationClip clip = new()
            {
                Name = "Reset_FT_Animator"
            };

            return clip;
        }
    }

    public static class OscmBlendTrees
    {
        internal static MotionBase EyeTrackingBinarySmoothing()
        {
            throw new NotImplementedException();
        }

        internal static MotionBase EyeTrackingFloatSmoothing()
        {
            throw new NotImplementedException();
        }

        internal static MotionBase EyeTrackingSmoothing()
        {
            throw new NotImplementedException();
        }

        internal static MotionBase FrameTimeCounter()
        {
            throw new NotImplementedException();
        }

        internal static MotionBase FrameTimeIsNegativeOne()
        {
            throw new NotImplementedException();
        }

        internal static MotionBase FrameTimeIsOneAndLastTime()
        {
            throw new NotImplementedException();
        }

        internal static MotionBase LipTrackingBinarySmoothing()
        {
            throw new NotImplementedException();
        }

        internal static MotionBase LipTrackingFloatSmoothing()
        {
            throw new NotImplementedException();
        }

        internal static MotionBase LipTrackingSmoothing()
        {
            throw new NotImplementedException();
        }

        internal static MotionBase LocalSmoothing()
        {
            throw new NotImplementedException();
        }

        internal static MotionBase LocalSmoothingOff()
        {
            throw new NotImplementedException();
        }

        internal static MotionBase PupilDilationBinarySmoothing()
        {
            throw new NotImplementedException();
        }

        internal static MotionBase PupilDilationSmoothing()
        {
            throw new NotImplementedException();
        }

        internal static MotionBase RemoteSmoothingOff()
        {
            throw new NotImplementedException();
        }

        internal static MotionBase SmoothingCutoffLocal()
        {
            throw new NotImplementedException();
        }

        internal static MotionBase SmoothingCutoffRemote()
        {
            throw new NotImplementedException();
        }
    }
}
