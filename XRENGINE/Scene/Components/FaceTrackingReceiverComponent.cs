using OscCore;
using XREngine.Components;

namespace XREngine.Data.Components
{
    public class FaceTrackingReceiverComponent : OscReceiverComponent
    {
        private const string Tracking_Eye_CenterPitchYaw = "/tracking/eye/CenterPitchYaw";
        private const string Tracking_Eye_EyesClosedAmount = "/tracking/eye/EyesClosedAmount";
        private const string Avatar_EyeX = "/avatar/parameters/v2/EyeX";
        private const string Avatar_EyeY = "/avatar/parameters/v2/EyeY";
        private const string Avatar_EyeLeftX = "/avatar/parameters/v2/EyeLeftX";
        private const string Avatar_EyeLeftY = "/avatar/parameters/v2/EyeLeftY";
        private const string Avatar_EyeRightX = "/avatar/parameters/v2/EyeRightX";
        private const string Avatar_EyeRightY = "/avatar/parameters/v2/EyeRightY";
        private const string Avatar_EyeOpenLeft = "/avatar/parameters/v2/EyeOpenLeft";
        private const string Avatar_EyeOpenRight = "/avatar/parameters/v2/EyeOpenRight";
        private const string Avatar_EyeOpen = "/avatar/parameters/v2/EyeOpen";
        private const string Avatar_EyeClosedLeft = "/avatar/parameters/v2/EyeClosedLeft";
        private const string Avatar_EyeClosedRight = "/avatar/parameters/v2/EyeClosedRight";
        private const string Avatar_EyeClosed = "/avatar/parameters/v2/EyeClosed";
        private const string Avatar_EyeLidLeft = "/avatar/parameters/v2/EyeLidLeft";
        private const string Avatar_EyeLidRight = "/avatar/parameters/v2/EyeLidRight";
        private const string Avatar_EyeLid = "/avatar/parameters/v2/EyeLid";
        private const string Avatar_EyeSquint = "/avatar/parameters/v2/EyeSquint";
        private const string Avatar_EyesSquint = "/avatar/parameters/v2/EyesSquint";
        private const string Avatar_EyeSquintRight = "/avatar/parameters/v2/EyeSquintRight";
        private const string Avatar_EyeSquintLeft = "/avatar/parameters/v2/EyeSquintLeft";

        private const string Avatar_BrowPinchRight = "/avatar/parameters/v2/BrowPinchRight";
        private const string Avatar_BrowPinchLeft = "/avatar/parameters/v2/BrowPinchLeft";
        private const string Avatar_BrowLowererRight = "/avatar/parameters/v2/BrowLowererRight";
        private const string Avatar_BrowLowererLeft = "/avatar/parameters/v2/BrowLowererLeft";
        private const string Avatar_BrowInnerUpRight = "/avatar/parameters/v2/BrowInnerUpRight";
        private const string Avatar_BrowInnerUpLeft = "/avatar/parameters/v2/BrowInnerUpLeft";
        private const string Avatar_BrowUp = "/avatar/parameters/v2/BrowUp";
        private const string Avatar_BrowDown = "/avatar/parameters/v2/BrowDown";
        private const string Avatar_BrowInnerUp = "/avatar/parameters/v2/BrowInnerUp";
        private const string Avatar_BrowUpRight = "/avatar/parameters/v2/BrowUpRight";
        private const string Avatar_BrowUpLeft = "/avatar/parameters/v2/BrowUpLeft";
        private const string Avatar_BrowDownRight = "/avatar/parameters/v2/BrowDownRight";
        private const string Avatar_BrowDownLeft = "/avatar/parameters/v2/BrowDownLeft";
        private const string Avatar_BrowExpressionRight = "/avatar/parameters/v2/BrowExpressionRight";
        private const string Avatar_BrowExpressionLeft = "/avatar/parameters/v2/BrowExpressionLeft";
        private const string Avatar_BrowExpression = "/avatar/parameters/v2/BrowExpression";

        private const string Avatar_CheekSquintRight = "/avatar/parameters/v2/CheekSquintRight";
        private const string Avatar_CheekSquintLeft = "/avatar/parameters/v2/CheekSquintLeft";
        private const string Avatar_CheekPuffRight = "/avatar/parameters/v2/CheekPuffRight";
        private const string Avatar_CheekPuffLeft = "/avatar/parameters/v2/CheekPuffLeft";

        private const string Avatar_JawOpen = "/avatar/parameters/v2/JawOpen";
        private const string Avatar_JawRight = "/avatar/parameters/v2/JawRight";
        private const string Avatar_JawForward = "/avatar/parameters/v2/JawForward";
        private const string Avatar_JawX = "/avatar/parameters/v2/JawX";
        private const string Avatar_JawZ = "/avatar/parameters/v2/JawZ";

        private const string Avatar_MouthClosed = "/avatar/parameters/v2/MouthClosed";
        private const string Avatar_MouthUpperUpRight = "/avatar/parameters/v2/MouthUpperUpRight";
        private const string Avatar_MouthUpperUpLeft = "/avatar/parameters/v2/MouthUpperUpLeft";
        private const string Avatar_MouthUpperDeepenRight = "/avatar/parameters/v2/MouthUpperDeepenRight";
        private const string Avatar_MouthUpperDeepenLeft = "/avatar/parameters/v2/MouthUpperDeepenLeft";
        private const string Avatar_MouthLowerDownRight = "/avatar/parameters/v2/MouthLowerDownRight";
        private const string Avatar_MouthLowerDownLeft = "/avatar/parameters/v2/MouthLowerDownLeft";
        private const string Avatar_MouthUpperRight = "/avatar/parameters/v2/MouthUpperRight";
        private const string Avatar_MouthLowerRight = "/avatar/parameters/v2/MouthLowerRight";
        private const string Avatar_MouthFrownRight = "/avatar/parameters/v2/MouthFrownRight";
        private const string Avatar_MouthFrownLeft = "/avatar/parameters/v2/MouthFrownLeft";
        private const string Avatar_MouthStretchRight = "/avatar/parameters/v2/MouthStretchRight";
        private const string Avatar_MouthStretchLeft = "/avatar/parameters/v2/MouthStretchLeft";
        private const string Avatar_MouthDimpleRight = "/avatar/parameters/v2/MouthDimpleRight";
        private const string Avatar_MouthDimpleLeft = "/avatar/parameters/v2/MouthDimpleLeft";
        private const string Avatar_MouthRaiserUpper = "/avatar/parameters/v2/MouthRaiserUpper";
        private const string Avatar_MouthRaiserLower = "/avatar/parameters/v2/MouthRaiserLower";
        private const string Avatar_MouthPressRight = "/avatar/parameters/v2/MouthPressRight";
        private const string Avatar_MouthPressLeft = "/avatar/parameters/v2/MouthPressLeft";
        private const string Avatar_MouthSadRight = "/avatar/parameters/v2/MouthSadRight";
        private const string Avatar_MouthSadLeft = "/avatar/parameters/v2/MouthSadLeft";

        private const string Avatar_LipSuckUpperRight = "/avatar/parameters/v2/LipSuckUpperRight";
        private const string Avatar_LipSuckUpperLeft = "/avatar/parameters/v2/LipSuckUpperLeft";
        private const string Avatar_LipSuckLowerRight = "/avatar/parameters/v2/LipSuckLowerRight";
        private const string Avatar_LipSuckLowerLeft = "/avatar/parameters/v2/LipSuckLowerLeft";
        private const string Avatar_LipFunnelUpperRight = "/avatar/parameters/v2/LipFunnelUpperRight";
        private const string Avatar_LipFunnelUpperLeft = "/avatar/parameters/v2/LipFunnelUpperLeft";
        private const string Avatar_LipFunnelLowerRight = "/avatar/parameters/v2/LipFunnelLowerRight";
        private const string Avatar_LipFunnelLowerLeft = "/avatar/parameters/v2/LipFunnelLowerLeft";
        private const string Avatar_LipPuckerUpperRight = "/avatar/parameters/v2/LipPuckerUpperRight";
        private const string Avatar_LipPuckerUpperLeft = "/avatar/parameters/v2/LipPuckerUpperLeft";
        private const string Avatar_LipPuckerLowerRight = "/avatar/parameters/v2/LipPuckerLowerRight";
        private const string Avatar_LipPuckerLowerLeft = "/avatar/parameters/v2/LipPuckerLowerLeft";

        private const string Avatar_NoseSneerRight = "/avatar/parameters/v2/NoseSneerRight";
        private const string Avatar_NoseSneerLeft = "/avatar/parameters/v2/NoseSneerLeft";

        private const string Avatar_TongueOut = "/avatar/parameters/v2/TongueOut";

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
    }
}
