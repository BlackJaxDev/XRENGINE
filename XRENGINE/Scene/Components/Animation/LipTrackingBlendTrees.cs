using XREngine.Animation;
using XREngine.Data.Components;

namespace XREngine.Components.Animation
{
    public static class LipTrackingBlendTrees
    {
        #region Cheek Puff
        public static BlendTree1D CheekPuffBlend() => new()
        {
            ParameterName = FaceTrackingReceiverComponent.OSCmProxyName(FaceTrackingReceiverComponent.Avatar_CheekPuffLeft),
            Children =
            [
                new BlendTree1D.Child()
                {
                    Motion = CheekPuff0(),
                    Threshold = 0.0f,
                },
                new BlendTree1D.Child()
                {
                    Motion = CheekPuff(),
                    Threshold = 1.0f,
                },
            ]
        };
        private static AnimationClip CheekPuff() => new()
        {
            Name = "Cheek Puff",
            LengthInSeconds = 0.0f,
            Looped = false,
            RootMember = AnimationMember.SetNormalizedBlendshapeValuesByModelNodeName(FaceTrackingReceiverComponent.DefaultFaceTrackedNodeName,
                (ARKitBlendshapeNames.CheekPuff, 1.0f))
        };
        private static AnimationClip CheekPuff0() => new()
        {
            Name = "Cheek Puff 0",
            LengthInSeconds = 0.0f,
            Looped = false,
            RootMember = AnimationMember.SetNormalizedBlendshapeValuesByModelNodeName(FaceTrackingReceiverComponent.DefaultFaceTrackedNodeName,
                (ARKitBlendshapeNames.CheekPuff, 0.0f))
        };
        #endregion

        #region Cheek Squint Left
        public static BlendTree1D CheekSquintLeftBlend() => new()
        {
            ParameterName = FaceTrackingReceiverComponent.OSCmProxyName(FaceTrackingReceiverComponent.Avatar_SmileFrownLeft),
            Children =
            [
                new BlendTree1D.Child()
                {
                    Motion = CheekSquintLeft0(),
                    Threshold = 0.0f,
                },
                new BlendTree1D.Child()
                {
                    Motion = CheekSquintLeft(),
                    Threshold = 2.0f,
                },
            ]
        };
        private static AnimationClip CheekSquintLeft() => new()
        {
            Name = "Cheek Squint Left",
            LengthInSeconds = 0.0f,
            Looped = false,
            RootMember = AnimationMember.SetNormalizedBlendshapeValuesByModelNodeName(FaceTrackingReceiverComponent.DefaultFaceTrackedNodeName,
                (ARKitBlendshapeNames.CheekSquintLeft, 1.0f))
        };
        private static AnimationClip CheekSquintLeft0() => new()
        {
            Name = "Cheek Squint Left 0",
            LengthInSeconds = 0.0f,
            Looped = false,
            RootMember = AnimationMember.SetNormalizedBlendshapeValuesByModelNodeName(FaceTrackingReceiverComponent.DefaultFaceTrackedNodeName,
                (ARKitBlendshapeNames.CheekSquintLeft, 0.0f))
        };
        #endregion

        #region Cheek Squint Right
        public static BlendTree1D CheekSquintRightBlend() => new()
        {
            ParameterName = FaceTrackingReceiverComponent.OSCmProxyName(FaceTrackingReceiverComponent.Avatar_SmileFrownRight),
            Children =
            [
                new BlendTree1D.Child()
                {
                    Motion = CheekSquintRight0(),
                    Threshold = 0.0f,
                },
                new BlendTree1D.Child()
                {
                    Motion = CheekSquintRight(),
                    Threshold = 2.0f,
                },
            ]
        };
        private static AnimationClip CheekSquintRight() => new()
        {
            Name = "Cheek Squint Right",
            LengthInSeconds = 0.0f,
            Looped = false,
            RootMember = AnimationMember.SetNormalizedBlendshapeValuesByModelNodeName(FaceTrackingReceiverComponent.DefaultFaceTrackedNodeName,
                (ARKitBlendshapeNames.CheekSquintRight, 1.0f))
        };
        private static AnimationClip CheekSquintRight0() => new()
        {
            Name = "Cheek Squint Right 0",
            LengthInSeconds = 0.0f,
            Looped = false,
            RootMember = AnimationMember.SetNormalizedBlendshapeValuesByModelNodeName(FaceTrackingReceiverComponent.DefaultFaceTrackedNodeName,
                (ARKitBlendshapeNames.CheekSquintRight, 0.0f))
        };
        #endregion

        #region Jaw Forward
        public static BlendTree1D JawForwardBlend() => new()
        {
            ParameterName = FaceTrackingReceiverComponent.OSCmProxyName(FaceTrackingReceiverComponent.Avatar_JawForward),
            Children =
            [
                new BlendTree1D.Child()
                {
                    Motion = JawForward0(),
                    Threshold = 0.0f,
                },
                new BlendTree1D.Child()
                {
                    Motion = JawForward(),
                    Threshold = 1.0f,
                },
            ]
        };
        private static AnimationClip JawForward() => new()
        {
            Name = "Jaw Forward",
            LengthInSeconds = 0.0f,
            Looped = false,
            RootMember = AnimationMember.SetNormalizedBlendshapeValuesByModelNodeName(FaceTrackingReceiverComponent.DefaultFaceTrackedNodeName,
                (ARKitBlendshapeNames.JawForward, 1.0f))
        };
        private static AnimationClip JawForward0() => new()
        {
            Name = "Jaw Forward 0",
            LengthInSeconds = 0.0f,
            Looped = false,
            RootMember = AnimationMember.SetNormalizedBlendshapeValuesByModelNodeName(FaceTrackingReceiverComponent.DefaultFaceTrackedNodeName,
                (ARKitBlendshapeNames.JawForward, 0.0f))
        };
        #endregion

        #region Jaw Open
        public static BlendTree1D JawOpenBlend() => new()
        {
            ParameterName = FaceTrackingReceiverComponent.OSCmProxyName(FaceTrackingReceiverComponent.Avatar_JawOpen),
            Children =
            [
                new BlendTree1D.Child()
                {
                    Motion = JawOpenHelper(),
                    Threshold = 0.0f,
                },
                new BlendTree1D.Child()
                {
                    Motion = JawOpen(),
                    Threshold = 1.0f,
                },
            ]
        };
        private static BlendTree1D JawOpenHelper() => new()
        {
            ParameterName = FaceTrackingReceiverComponent.OSCmProxyName(FaceTrackingReceiverComponent.Avatar_TongueOut),
            Children =
            [
                new BlendTree1D.Child()
                {
                    Motion = JawOpen0(),
                    Threshold = 0.0f,
                },
                new BlendTree1D.Child()
                {
                    Motion = JawOpen(),
                    Threshold = 4.0f,
                },
            ]
        };
        private static AnimationClip JawOpen0() => new()
        {
            Name = "Jaw Open 0",
            LengthInSeconds = 0.0f,
            Looped = false,
            RootMember = AnimationMember.SetNormalizedBlendshapeValuesByModelNodeName(FaceTrackingReceiverComponent.DefaultFaceTrackedNodeName,
                (ARKitBlendshapeNames.JawOpen, 0.0f))
        };
        private static AnimationClip JawOpen() => new()
        {
            Name = "Jaw Open",
            LengthInSeconds = 0.0f,
            Looped = false,
            RootMember = AnimationMember.SetNormalizedBlendshapeValuesByModelNodeName(FaceTrackingReceiverComponent.DefaultFaceTrackedNodeName,
                (ARKitBlendshapeNames.JawOpen, 1.0f))
        };
        #endregion

        #region Limit JawX (MouthX)
        public static BlendTree1D LimitJawX_MouthX() => new()
        {
            ParameterName = FaceTrackingReceiverComponent.OSCmProxyName(FaceTrackingReceiverComponent.Avatar_MouthX),
            Children =
            [
                new BlendTree1D.Child()
                {
                    Motion = JawX0(),
                    Threshold = -2.0f,
                },
                new BlendTree1D.Child()
                {
                    Motion = JawXBlend(),
                    Threshold = 0.0f,
                },
                new BlendTree1D.Child()
                {
                    Motion = JawX0(),
                    Threshold = 2.0f,
                },
            ]
        };
        private static BlendTree1D JawXBlend() => new()
        {
            ParameterName = FaceTrackingReceiverComponent.OSCmProxyName(FaceTrackingReceiverComponent.Avatar_JawX),
            Children =
            [
                new BlendTree1D.Child()
                {
                    Motion = JawLeft(),
                    Threshold = -1.0f,
                },
                new BlendTree1D.Child()
                {
                    Motion = JawX0(),
                    Threshold = 0.0f,
                },
                new BlendTree1D.Child()
                {
                    Motion = JawRight(),
                    Threshold = 1.0f,
                },
            ]
        };
        private static AnimationClip JawRight() => new()
        {
            Name = "Jaw Right",
            LengthInSeconds = 0.0f,
            Looped = false,
            RootMember = AnimationMember.SetNormalizedBlendshapeValuesByModelNodeName(FaceTrackingReceiverComponent.DefaultFaceTrackedNodeName,
                (ARKitBlendshapeNames.JawRight, 1.0f),
                (ARKitBlendshapeNames.JawLeft, 0.0f))
        };
        private static AnimationClip JawLeft() => new()
        {
            Name = "Jaw Left",
            LengthInSeconds = 0.0f,
            Looped = false,
            RootMember = AnimationMember.SetNormalizedBlendshapeValuesByModelNodeName(FaceTrackingReceiverComponent.DefaultFaceTrackedNodeName,
                (ARKitBlendshapeNames.JawLeft, 1.0f),
                (ARKitBlendshapeNames.JawRight, 0.0f))
        };
        private static AnimationClip JawX0() => new()
        {
            Name = "Jaw X 0",
            LengthInSeconds = 0.0f,
            Looped = false,
            RootMember = AnimationMember.SetNormalizedBlendshapeValuesByModelNodeName(FaceTrackingReceiverComponent.DefaultFaceTrackedNodeName,
                (ARKitBlendshapeNames.JawLeft, 0.0f),
                (ARKitBlendshapeNames.JawRight, 0.0f))
        };
        #endregion

        #region Lip Pucker
        public static BlendTree1D LipPuckerBlend() => new()
        {
            ParameterName = FaceTrackingReceiverComponent.OSCmProxyName(FaceTrackingReceiverComponent.Avatar_LipPucker),
            Children =
            [
                new BlendTree1D.Child()
                {
                    Motion = MouthPucker0(),
                    Threshold = 0.0f,
                },
                new BlendTree1D.Child()
                {
                    Motion = LimitMouthPucker_LipFunnel(),
                    Threshold = 1.0f,
                },
            ]
        };
        private static BlendTree1D LimitMouthPucker_LipFunnel() => new()
        {
            ParameterName = FaceTrackingReceiverComponent.OSCmProxyName(FaceTrackingReceiverComponent.Avatar_LipFunnel),
            Children =
            [
                new BlendTree1D.Child()
                {
                    Motion = MouthPucker(),
                    Threshold = 0.0f,
                },
                new BlendTree1D.Child()
                {
                    Motion = MouthPucker0(),
                    Threshold = 1.0f,
                },
            ]
        };
        private static AnimationClip MouthPucker() => new()
        {
            Name = "Mouth Pucker",
            LengthInSeconds = 0.0f,
            Looped = false,
            RootMember = AnimationMember.SetNormalizedBlendshapeValuesByModelNodeName(FaceTrackingReceiverComponent.DefaultFaceTrackedNodeName,
                (ARKitBlendshapeNames.MouthPucker, 1.0f))
        };
        private static AnimationClip MouthPucker0() => new()
        {
            Name = "Mouth Pucker 0",
            LengthInSeconds = 0.0f,
            Looped = false,
            RootMember = AnimationMember.SetNormalizedBlendshapeValuesByModelNodeName(FaceTrackingReceiverComponent.DefaultFaceTrackedNodeName,
                (ARKitBlendshapeNames.MouthPucker, 0.0f))
        };
        #endregion

        #region Mouth Closed
        public static BlendTree1D MouthClosedBlend() => new()
        {
            ParameterName = FaceTrackingReceiverComponent.OSCmProxyName(FaceTrackingReceiverComponent.Avatar_MouthClosed),
            Children =
            [
                new BlendTree1D.Child()
                {
                    Motion = MouthClosed0(),
                    Threshold = 0.0f,
                },
                new BlendTree1D.Child()
                {
                    Motion = MouthClosed(),
                    Threshold = 1.0f,
                },
            ]
        };
        private static AnimationClip MouthClosed() => new()
        {
            Name = "Mouth Closed",
            LengthInSeconds = 0.0f,
            Looped = false,
            RootMember = AnimationMember.SetNormalizedBlendshapeValuesByModelNodeName(FaceTrackingReceiverComponent.DefaultFaceTrackedNodeName,
                (ARKitBlendshapeNames.MouthClose, 1.0f))
        };
        private static AnimationClip MouthClosed0() => new()
        {
            Name = "Mouth Closed 0",
            LengthInSeconds = 0.0f,
            Looped = false,
            RootMember = AnimationMember.SetNormalizedBlendshapeValuesByModelNodeName(FaceTrackingReceiverComponent.DefaultFaceTrackedNodeName,
                (ARKitBlendshapeNames.MouthClose, 0.0f))
        };
        #endregion

        #region Mouth Frown Left
        public static BlendTree1D MouthFrownLeftBlend() => new()
        {
            ParameterName = FaceTrackingReceiverComponent.OSCmProxyName(FaceTrackingReceiverComponent.Avatar_SmileFrownLeft),
            Children =
            [
                new BlendTree1D.Child()
                {
                    Motion = LimitMouthFrownLeft_MouthXLeft(),
                    Threshold = -1.0f,
                },
                new BlendTree1D.Child()
                {
                    Motion = MouthFrownLeft0(),
                    Threshold = 0.0f,
                },
            ]
        };
        private static AnimationClip MouthFrownLeft0() => new()
        {
            Name = "Mouth Frown Left 0",
            LengthInSeconds = 0.0f,
            Looped = false,
            RootMember = AnimationMember.SetNormalizedBlendshapeValuesByModelNodeName(FaceTrackingReceiverComponent.DefaultFaceTrackedNodeName,
                (ARKitBlendshapeNames.MouthFrownLeft, 0.0f))
        };
        private static BlendTree1D LimitMouthFrownLeft_MouthXLeft() => new()
        {
            ParameterName = FaceTrackingReceiverComponent.OSCmProxyName(FaceTrackingReceiverComponent.Avatar_MouthX),
            Children =
            [
                new BlendTree1D.Child()
                {
                    Motion = MouthFrownLeft0(),
                    Threshold = -3.0f,
                },
                new BlendTree1D.Child()
                {
                    Motion = MouthFrownLeft(),
                    Threshold = 0.0f,
                },
            ]
        };
        private static AnimationClip MouthFrownLeft() => new()
        {
            Name = "Mouth Frown Left",
            LengthInSeconds = 0.0f,
            Looped = false,
            RootMember = AnimationMember.SetNormalizedBlendshapeValuesByModelNodeName(FaceTrackingReceiverComponent.DefaultFaceTrackedNodeName,
                (ARKitBlendshapeNames.MouthFrownLeft, 1.0f))
        };
        #endregion

        #region Mouth Frown Right
        public static BlendTree1D MouthFrownRightBlend() => new()
        {
            ParameterName = FaceTrackingReceiverComponent.OSCmProxyName(FaceTrackingReceiverComponent.Avatar_SmileFrownRight),
            Children =
            [
                new BlendTree1D.Child()
                {
                    Motion = LimitMouthFrownRight_MouthXRight(),
                    Threshold = -1.0f,
                },
                new BlendTree1D.Child()
                {
                    Motion = MouthFrownRight0(),
                    Threshold = 0.0f,
                },
            ]
        };
        private static BlendTree1D LimitMouthFrownRight_MouthXRight() => new()
        {
            ParameterName = FaceTrackingReceiverComponent.OSCmProxyName(FaceTrackingReceiverComponent.Avatar_MouthX),
            Children =
            [
                new BlendTree1D.Child()
                {
                    Motion = MouthFrownRight(),
                    Threshold = 0.0f,
                },
                new BlendTree1D.Child()
                {
                    Motion = MouthFrownRight0(),
                    Threshold = 3.0f,
                },
            ]
        };
        private static AnimationClip MouthFrownRight() => new()
        {
            Name = "Mouth Frown Right",
            LengthInSeconds = 0.0f,
            Looped = false,
            RootMember = AnimationMember.SetNormalizedBlendshapeValuesByModelNodeName(FaceTrackingReceiverComponent.DefaultFaceTrackedNodeName,
                (ARKitBlendshapeNames.MouthFrownRight, 1.0f))
        };
        private static AnimationClip MouthFrownRight0() => new()
        {
            Name = "Mouth Frown Right 0",
            LengthInSeconds = 0.0f,
            Looped = false,
            RootMember = AnimationMember.SetNormalizedBlendshapeValuesByModelNodeName(FaceTrackingReceiverComponent.DefaultFaceTrackedNodeName,
                (ARKitBlendshapeNames.MouthFrownRight, 0.0f))
        };
        #endregion

        #region Mouth Funnel
        public static BlendTree1D MouthFunnelBlend() => new()
        {
            ParameterName = FaceTrackingReceiverComponent.OSCmProxyName(FaceTrackingReceiverComponent.Avatar_LipFunnel),
            Children =
            [
                new BlendTree1D.Child()
                {
                    Motion = MouthFunnel0(),
                    Threshold = 0.0f,
                },
                new BlendTree1D.Child()
                {
                    Motion = MouthFunnel(),
                    Threshold = 1.0f,
                },
            ]
        };
        private static AnimationClip MouthFunnel() => new()
        {
            Name = "Mouth Funnel",
            LengthInSeconds = 0.0f,
            Looped = false,
            RootMember = AnimationMember.SetNormalizedBlendshapeValuesByModelNodeName(FaceTrackingReceiverComponent.DefaultFaceTrackedNodeName,
                (ARKitBlendshapeNames.MouthFunnel, 1.0f))
        };
        private static AnimationClip MouthFunnel0() => new()
        {
            Name = "Mouth Funnel 0",
            LengthInSeconds = 0.0f,
            Looped = false,
            RootMember = AnimationMember.SetNormalizedBlendshapeValuesByModelNodeName(FaceTrackingReceiverComponent.DefaultFaceTrackedNodeName,
                (ARKitBlendshapeNames.MouthFunnel, 0.0f))
        };
        #endregion

        #region Mouth Lower Down
        public static BlendTree1D MouthLowerDownBlend() => new()
        {
            ParameterName = FaceTrackingReceiverComponent.OSCmProxyName(FaceTrackingReceiverComponent.Avatar_MouthLowerDown),
            Children =
            [
                new BlendTree1D.Child()
                {
                    Motion = MouthLowerDown0(),
                    Threshold = 0.0f,
                },
                new BlendTree1D.Child()
                {
                    Motion = LimitMouthLowerDown_LipFunnel(),
                    Threshold = 1.0f,
                },
            ]
        };
        private static BlendTree1D LimitMouthLowerDown_LipFunnel() => new()
        {
            ParameterName = FaceTrackingReceiverComponent.OSCmProxyName(FaceTrackingReceiverComponent.Avatar_LipFunnel),
            Children =
            [
                new BlendTree1D.Child()
                {
                    Motion = MouthLowerDown(),
                    Threshold = 0.0f,
                },
                new BlendTree1D.Child()
                {
                    Motion = MouthLowerDown0(),
                    Threshold = 1.0f,
                },
            ]
        };
        private static AnimationClip MouthLowerDown() => new()
        {
            Name = "Mouth Lower Down",
            LengthInSeconds = 0.0f,
            Looped = false,
            RootMember = AnimationMember.SetNormalizedBlendshapeValuesByModelNodeName(FaceTrackingReceiverComponent.DefaultFaceTrackedNodeName,
                (ARKitBlendshapeNames.MouthLowerDownLeft, 1.0f),
                (ARKitBlendshapeNames.MouthLowerDownRight, 1.0f))
        };
        private static AnimationClip MouthLowerDown0() => new()
        {
            Name = "Mouth Lower Down 0",
            LengthInSeconds = 0.0f,
            Looped = false,
            RootMember = AnimationMember.SetNormalizedBlendshapeValuesByModelNodeName(FaceTrackingReceiverComponent.DefaultFaceTrackedNodeName,
                (ARKitBlendshapeNames.MouthLowerDownLeft, 0.0f),
                (ARKitBlendshapeNames.MouthLowerDownRight, 0.0f))
        };
        #endregion

        #region Mouth Press
        public static BlendTree1D MouthPressBlend() => new()
        {
            ParameterName = FaceTrackingReceiverComponent.OSCmProxyName(FaceTrackingReceiverComponent.Avatar_MouthPress),
            Children =
            [
                new BlendTree1D.Child()
                {
                    Motion = MouthPress0(),
                    Threshold = 0.0f,
                },
                new BlendTree1D.Child()
                {
                    Motion = MouthPress(),
                    Threshold = 1.0f,
                },
            ]
        };
        private static AnimationClip MouthPress() => new()
        {
            Name = "Mouth Press",
            LengthInSeconds = 0.0f,
            Looped = false,
            RootMember = AnimationMember.SetNormalizedBlendshapeValuesByModelNodeName(FaceTrackingReceiverComponent.DefaultFaceTrackedNodeName,
                (ARKitBlendshapeNames.MouthPressLeft, 1.0f),
                (ARKitBlendshapeNames.MouthPressRight, 1.0f))
        };
        private static AnimationClip MouthPress0() => new()
        {
            Name = "Mouth Press 0",
            LengthInSeconds = 0.0f,
            Looped = false,
            RootMember = AnimationMember.SetNormalizedBlendshapeValuesByModelNodeName(FaceTrackingReceiverComponent.DefaultFaceTrackedNodeName,
                (ARKitBlendshapeNames.MouthPressLeft, 0.0f),
                (ARKitBlendshapeNames.MouthPressRight, 0.0f))
        };
        #endregion

        #region Mouth Roll Lower
        public static BlendTree1D MouthRollLowerBlend() => new()
        {
            ParameterName = FaceTrackingReceiverComponent.OSCmProxyName(FaceTrackingReceiverComponent.Avatar_LipSuckLower),
            Children =
            [
                new BlendTree1D.Child()
                {
                    Motion = MouthRollLower0(),
                    Threshold = 0.0f,
                },
                new BlendTree1D.Child()
                {
                    Motion = LimitMouthRollLower_MouthClosed(),
                    Threshold = 1.0f,
                },
            ]
        };
        private static BlendTree1D LimitMouthRollLower_MouthClosed() => new()
        {
            ParameterName = FaceTrackingReceiverComponent.OSCmProxyName(FaceTrackingReceiverComponent.Avatar_MouthClosed),
            Children =
            [
                new BlendTree1D.Child()
                {
                    Motion = MouthRollLower(),
                    Threshold = 0.0f,
                },
                new BlendTree1D.Child()
                {
                    Motion = MouthRollLower0(),
                    Threshold = 3.0f,
                },
            ]
        };
        private static AnimationClip MouthRollLower() => new()
        {
            Name = "Mouth Roll Lower",
            LengthInSeconds = 0.0f,
            Looped = false,
            RootMember = AnimationMember.SetNormalizedBlendshapeValuesByModelNodeName(FaceTrackingReceiverComponent.DefaultFaceTrackedNodeName,
                (ARKitBlendshapeNames.MouthRollLower, 1.0f))
        };
        private static AnimationClip MouthRollLower0() => new()
        {
            Name = "Mouth Roll Lower 0",
            LengthInSeconds = 0.0f,
            Looped = false,
            RootMember = AnimationMember.SetNormalizedBlendshapeValuesByModelNodeName(FaceTrackingReceiverComponent.DefaultFaceTrackedNodeName,
                (ARKitBlendshapeNames.MouthRollLower, 0.0f))
        };
        #endregion

        #region Mouth Roll Upper
        public static BlendTree1D MouthRollUpperBlend() => new()
        {
            ParameterName = FaceTrackingReceiverComponent.OSCmProxyName(FaceTrackingReceiverComponent.Avatar_LipSuckUpper),
            Children =
            [
                new BlendTree1D.Child()
                {
                    Motion = MouthRollUpper0(),
                    Threshold = 0.0f,
                },
                new BlendTree1D.Child()
                {
                    Motion = LimitMouthRollUpper_MouthClosed(),
                    Threshold = 1.0f,
                },
            ]
        };
        private static BlendTree1D LimitMouthRollUpper_MouthClosed() => new()
        {
            ParameterName = FaceTrackingReceiverComponent.OSCmProxyName(FaceTrackingReceiverComponent.Avatar_MouthClosed),
            Children =
            [
                new BlendTree1D.Child()
                {
                    Motion = MouthRollUpper(),
                    Threshold = 0.0f,
                },
                new BlendTree1D.Child()
                {
                    Motion = MouthRollUpper0(),
                    Threshold = 3.0f,
                },
            ]
        };
        private static AnimationClip MouthRollUpper() => new()
        {
            Name = "Mouth Roll Upper",
            LengthInSeconds = 0.0f,
            Looped = false,
            RootMember = AnimationMember.SetNormalizedBlendshapeValuesByModelNodeName(FaceTrackingReceiverComponent.DefaultFaceTrackedNodeName,
                (ARKitBlendshapeNames.MouthRollUpper, 1.0f))
        };
        private static AnimationClip MouthRollUpper0() => new()
        {
            Name = "Mouth Roll Upper 0",
            LengthInSeconds = 0.0f,
            Looped = false,
            RootMember = AnimationMember.SetNormalizedBlendshapeValuesByModelNodeName(FaceTrackingReceiverComponent.DefaultFaceTrackedNodeName,
                (ARKitBlendshapeNames.MouthRollUpper, 0.0f))
        };
        #endregion

        #region Mouth Shrug Lower
        public static BlendTree1D MouthShrugLowerBlend() => new()
        {
            ParameterName = FaceTrackingReceiverComponent.OSCmProxyName(FaceTrackingReceiverComponent.Avatar_MouthRaiserLower),
            Children =
            [
                new BlendTree1D.Child()
                {
                    Motion = MouthShrugLower0(),
                    Threshold = 0.0f,
                },
                new BlendTree1D.Child()
                {
                    Motion = LimitMouthShrugLower_MouthClosed(),
                    Threshold = 1.0f,
                },
            ]
        };
        private static BlendTree1D LimitMouthShrugLower_MouthClosed() => new()
        {
            ParameterName = FaceTrackingReceiverComponent.OSCmProxyName(FaceTrackingReceiverComponent.Avatar_MouthClosed),
            Children =
            [
                new BlendTree1D.Child()
                {
                    Motion = MouthShrugLower(),
                    Threshold = 0.0f,
                },
                new BlendTree1D.Child()
                {
                    Motion = MouthShrugLower0(),
                    Threshold = 1.0f,
                },
            ]
        };
        private static AnimationClip MouthShrugLower() => new()
        {
            Name = "Mouth Shrug Lower",
            LengthInSeconds = 0.0f,
            Looped = false,
            RootMember = AnimationMember.SetNormalizedBlendshapeValuesByModelNodeName(FaceTrackingReceiverComponent.DefaultFaceTrackedNodeName,
                (ARKitBlendshapeNames.MouthShrugLower, 1.0f))
        };
        private static AnimationClip MouthShrugLower0() => new()
        {
            Name = "Mouth Shrug Lower 0",
            LengthInSeconds = 0.0f,
            Looped = false,
            RootMember = AnimationMember.SetNormalizedBlendshapeValuesByModelNodeName(FaceTrackingReceiverComponent.DefaultFaceTrackedNodeName,
                (ARKitBlendshapeNames.MouthShrugLower, 0.0f))
        };
        #endregion

        #region Mouth Shrug Upper
        public static BlendTree1D MouthShrugUpperBlend() => new()
        {
            ParameterName = FaceTrackingReceiverComponent.OSCmProxyName(FaceTrackingReceiverComponent.Avatar_MouthRaiserUpper),
            Children =
            [
                new BlendTree1D.Child()
                {
                    Motion = MouthShrugUpper0(),
                    Threshold = 0.0f,
                },
                new BlendTree1D.Child()
                {
                    Motion = LimitMouthShrugUpper_MouthClosed(),
                    Threshold = 1.0f,
                },
            ]
        };
        private static BlendTree1D LimitMouthShrugUpper_MouthClosed() => new()
        {
            ParameterName = FaceTrackingReceiverComponent.OSCmProxyName(FaceTrackingReceiverComponent.Avatar_MouthClosed),
            Children =
            [
                new BlendTree1D.Child()
                {
                    Motion = MouthShrugUpper(),
                    Threshold = 0.0f,
                },
                new BlendTree1D.Child()
                {
                    Motion = MouthShrugUpper0(),
                    Threshold = 1.0f,
                },
            ]
        };
        private static AnimationClip MouthShrugUpper() => new()
        {
            Name = "Mouth Shrug Upper",
            LengthInSeconds = 0.0f,
            Looped = false,
            RootMember = AnimationMember.SetNormalizedBlendshapeValuesByModelNodeName(FaceTrackingReceiverComponent.DefaultFaceTrackedNodeName,
                (ARKitBlendshapeNames.MouthShrugUpper, 1.0f))
        };
        private static AnimationClip MouthShrugUpper0() => new()
        {
            Name = "Mouth Shrug Upper 0",
            LengthInSeconds = 0.0f,
            Looped = false,
            RootMember = AnimationMember.SetNormalizedBlendshapeValuesByModelNodeName(FaceTrackingReceiverComponent.DefaultFaceTrackedNodeName,
                (ARKitBlendshapeNames.MouthShrugUpper, 0.0f))
        };
        #endregion

        #region Mouth Smile Left
        public static BlendTree1D MouthSmileLeftBlend() => new()
        {
            ParameterName = FaceTrackingReceiverComponent.OSCmProxyName(FaceTrackingReceiverComponent.Avatar_SmileFrownLeft),
            Children =
            [
                new BlendTree1D.Child()
                {
                    Motion = MouthSmileLeft0(),
                    Threshold = 0.0f,
                },
                new BlendTree1D.Child()
                {
                    Motion = MouthSmileLeft(),
                    Threshold = 1.0f,
                },
            ]
        };
        private static AnimationClip MouthSmileLeft() => new()
        {
            Name = "Mouth Smile Left",
            LengthInSeconds = 0.0f,
            Looped = false,
            RootMember = AnimationMember.SetNormalizedBlendshapeValuesByModelNodeName(FaceTrackingReceiverComponent.DefaultFaceTrackedNodeName,
                (ARKitBlendshapeNames.MouthSmileLeft, 1.0f))
        };
        private static AnimationClip MouthSmileLeft0() => new()
        {
            Name = "Mouth Smile Left 0",
            LengthInSeconds = 0.0f,
            Looped = false,
            RootMember = AnimationMember.SetNormalizedBlendshapeValuesByModelNodeName(FaceTrackingReceiverComponent.DefaultFaceTrackedNodeName,
                (ARKitBlendshapeNames.MouthSmileLeft, 0.0f))
        };
        #endregion

        #region Mouth Smile Right
        public static BlendTree1D MouthSmileRightBlend() => new()
        {
            ParameterName = FaceTrackingReceiverComponent.OSCmProxyName(FaceTrackingReceiverComponent.Avatar_SmileFrownRight),
            Children =
            [
                new BlendTree1D.Child()
                {
                    Motion = MouthSmileRight0(),
                    Threshold = 0.0f,
                },
                new BlendTree1D.Child()
                {
                    Motion = MouthSmileRight(),
                    Threshold = 1.0f,
                },
            ]
        };
        private static AnimationClip MouthSmileRight() => new()
        {
            Name = "Mouth Smile Right",
            LengthInSeconds = 0.0f,
            Looped = false,
            RootMember = AnimationMember.SetNormalizedBlendshapeValuesByModelNodeName(FaceTrackingReceiverComponent.DefaultFaceTrackedNodeName,
                (ARKitBlendshapeNames.MouthSmileRight, 1.0f))
        };
        private static AnimationClip MouthSmileRight0() => new()
        {
            Name = "Mouth Smile Right 0",
            LengthInSeconds = 0.0f,
            Looped = false,
            RootMember = AnimationMember.SetNormalizedBlendshapeValuesByModelNodeName(FaceTrackingReceiverComponent.DefaultFaceTrackedNodeName,
                (ARKitBlendshapeNames.MouthSmileRight, 0.0f))
        };
        #endregion

        #region Mouth Stretch Left
        public static BlendTree1D MouthStretchLeftBlend() => new()
        {
            ParameterName = FaceTrackingReceiverComponent.OSCmProxyName(FaceTrackingReceiverComponent.Avatar_MouthStretchLeft),
            Children =
            [
                new BlendTree1D.Child()
                {
                    Motion = MouthStretchLeft0(),
                    Threshold = 0.0f,
                },
                new BlendTree1D.Child()
                {
                    Motion = MouthStretchLeft(),
                    Threshold = 1.0f,
                },
            ]
        };
        private static AnimationClip MouthStretchLeft() => new()
        {
            Name = "Mouth Stretch Left",
            LengthInSeconds = 0.0f,
            Looped = false,
            RootMember = AnimationMember.SetNormalizedBlendshapeValuesByModelNodeName(FaceTrackingReceiverComponent.DefaultFaceTrackedNodeName,
                (ARKitBlendshapeNames.MouthStretchLeft, 1.0f))
        };
        private static AnimationClip MouthStretchLeft0() => new()
        {
            Name = "Mouth Stretch Left 0",
            LengthInSeconds = 0.0f,
            Looped = false,
            RootMember = AnimationMember.SetNormalizedBlendshapeValuesByModelNodeName(FaceTrackingReceiverComponent.DefaultFaceTrackedNodeName,
                (ARKitBlendshapeNames.MouthStretchLeft, 0.0f))
        };
        #endregion

        #region Mouth Stretch Right
        public static BlendTree1D MouthStretchRightBlend() => new()
        {
            ParameterName = FaceTrackingReceiverComponent.OSCmProxyName(FaceTrackingReceiverComponent.Avatar_MouthStretchRight),
            Children =
            [
                new BlendTree1D.Child()
                {
                    Motion = MouthStretchRight0(),
                    Threshold = 0.0f,
                },
                new BlendTree1D.Child()
                {
                    Motion = MouthStretchRight(),
                    Threshold = 1.0f,
                },
            ]
        };
        private static AnimationClip MouthStretchRight() => new()
        {
            Name = "Mouth Stretch Right",
            LengthInSeconds = 0.0f,
            Looped = false,
            RootMember = AnimationMember.SetNormalizedBlendshapeValuesByModelNodeName(FaceTrackingReceiverComponent.DefaultFaceTrackedNodeName,
                (ARKitBlendshapeNames.MouthStretchRight, 1.0f))
        };
        private static AnimationClip MouthStretchRight0() => new()
        {
            Name = "Mouth Stretch Right 0",
            LengthInSeconds = 0.0f,
            Looped = false,
            RootMember = AnimationMember.SetNormalizedBlendshapeValuesByModelNodeName(FaceTrackingReceiverComponent.DefaultFaceTrackedNodeName,
                (ARKitBlendshapeNames.MouthStretchRight, 0.0f))
        };
        #endregion

        #region Mouth Upper Up Left
        public static BlendTree1D MouthUpperUpLeftBlend() => new()
        {
            ParameterName = FaceTrackingReceiverComponent.OSCmProxyName(FaceTrackingReceiverComponent.Avatar_MouthUpperUp),
            Children =
            [
                new BlendTree1D.Child()
                {
                    Motion = MouthUpperUpLeft0(),
                    Threshold = 0.0f,
                },
                new BlendTree1D.Child()
                {
                    Motion = MouthRight(),
                    Threshold = 1.0f,
                },
            ]
        };
        private static BlendTree1D MouthRight() => new()
        {
            ParameterName = FaceTrackingReceiverComponent.OSCmProxyName(FaceTrackingReceiverComponent.Avatar_MouthX),
            Children =
            [
                new BlendTree1D.Child()
                {
                    Motion = LimitMouthUpperUpLeft_LipFunnel(),
                    Threshold = 0.0f,
                },
                new BlendTree1D.Child()
                {
                    Motion = MouthUpperUpLeft0(),
                    Threshold = 4.0f,
                },
            ]
        };
        private static BlendTree1D LimitMouthUpperUpLeft_LipFunnel() => new()
        {
            ParameterName = FaceTrackingReceiverComponent.OSCmProxyName(FaceTrackingReceiverComponent.Avatar_LipFunnel),
            Children =
            [
                new BlendTree1D.Child()
                {
                    Motion = MouthUpperUpLeft(),
                    Threshold = 0.0f,
                },
                new BlendTree1D.Child()
                {
                    Motion = MouthUpperUpLeft0(),
                    Threshold = 1.0f,
                },
            ]
        };
        private static AnimationClip MouthUpperUpLeft() => new()
        {
            Name = "Mouth Upper Up Left",
            LengthInSeconds = 0.0f,
            Looped = false,
            RootMember = AnimationMember.SetNormalizedBlendshapeValuesByModelNodeName(FaceTrackingReceiverComponent.DefaultFaceTrackedNodeName,
                (ARKitBlendshapeNames.MouthUpperUpLeft, 1.0f))
        };
        private static AnimationClip MouthUpperUpLeft0() => new()
        {
            Name = "Mouth Upper Up Left 0",
            LengthInSeconds = 0.0f,
            Looped = false,
            RootMember = AnimationMember.SetNormalizedBlendshapeValuesByModelNodeName(FaceTrackingReceiverComponent.DefaultFaceTrackedNodeName,
                (ARKitBlendshapeNames.MouthUpperUpLeft, 0.0f))
        };
        #endregion

        #region Mouth Upper Up Right
        public static BlendTree1D MouthUpperUpRightBlend() => new()
        {
            ParameterName = FaceTrackingReceiverComponent.OSCmProxyName(FaceTrackingReceiverComponent.Avatar_MouthUpperUp),
            Children =
            [
                new BlendTree1D.Child()
                {
                    Motion = MouthUpperUpRight0(),
                    Threshold = 0.0f,
                },
                new BlendTree1D.Child()
                {
                    Motion = MouthLeft(),
                    Threshold = 1.0f,
                },
            ]
        };
        private static BlendTree1D MouthLeft() => new()
        {
            ParameterName = FaceTrackingReceiverComponent.OSCmProxyName(FaceTrackingReceiverComponent.Avatar_MouthX),
            Children =
            [
                new BlendTree1D.Child()
                {
                    Motion = MouthUpperUpRight0(),
                    Threshold = -4.0f,
                },
                new BlendTree1D.Child()
                {
                    Motion = LimitMouthUpperUpRight_LipFunnel(),
                    Threshold = 0.0f,
                },
            ]
        };
        private static BlendTree1D LimitMouthUpperUpRight_LipFunnel() => new()
        {
            ParameterName = FaceTrackingReceiverComponent.OSCmProxyName(FaceTrackingReceiverComponent.Avatar_LipFunnel),
            Children =
            [
                new BlendTree1D.Child()
                {
                    Motion = MouthUpperUpRight(),
                    Threshold = 0.0f,
                },
                new BlendTree1D.Child()
                {
                    Motion = MouthUpperUpRight0(),
                    Threshold = 1.0f,
                },
            ]
        };
        private static AnimationClip MouthUpperUpRight() => new()
        {
            Name = "Mouth Upper Up Right",
            LengthInSeconds = 0.0f,
            Looped = false,
            RootMember = AnimationMember.SetNormalizedBlendshapeValuesByModelNodeName(FaceTrackingReceiverComponent.DefaultFaceTrackedNodeName,
                (ARKitBlendshapeNames.MouthUpperUpRight, 1.0f))
        };
        private static AnimationClip MouthUpperUpRight0() => new()
        {
            Name = "Mouth Upper Up Right 0",
            LengthInSeconds = 0.0f,
            Looped = false,
            RootMember = AnimationMember.SetNormalizedBlendshapeValuesByModelNodeName(FaceTrackingReceiverComponent.DefaultFaceTrackedNodeName,
                (ARKitBlendshapeNames.MouthUpperUpRight, 0.0f))
        };
        #endregion

        #region MouthX
        public static BlendTree1D MouthXBlend() => new()
        {
            ParameterName = FaceTrackingReceiverComponent.OSCmProxyName(FaceTrackingReceiverComponent.Avatar_MouthX),
            Children =
            [
                new BlendTree1D.Child()
                {
                    Motion = MouthLeftClip(),
                    Threshold = -1.0f,
                },
                new BlendTree1D.Child()
                {
                    Motion = MouthX0(),
                    Threshold = 0.0f,
                },
                new BlendTree1D.Child()
                {
                    Motion = MouthRightClip(),
                    Threshold = 1.0f,
                },
            ]
        };
        private static AnimationClip MouthRightClip() => new()
        {
            Name = "Mouth Right",
            LengthInSeconds = 0.0f,
            Looped = false,
            RootMember = AnimationMember.SetNormalizedBlendshapeValuesByModelNodeName(FaceTrackingReceiverComponent.DefaultFaceTrackedNodeName,
                (ARKitBlendshapeNames.MouthRight, 1.0f),
                (ARKitBlendshapeNames.MouthLeft, 0.0f))
        };
        private static AnimationClip MouthLeftClip() => new()
        {
            Name = "Mouth Left",
            LengthInSeconds = 0.0f,
            Looped = false,
            RootMember = AnimationMember.SetNormalizedBlendshapeValuesByModelNodeName(FaceTrackingReceiverComponent.DefaultFaceTrackedNodeName,
                (ARKitBlendshapeNames.MouthLeft, 1.0f),
                (ARKitBlendshapeNames.MouthRight, 0.0f))
        };
        private static AnimationClip MouthX0() => new()
        {
            Name = "Mouth X 0",
            LengthInSeconds = 0.0f,
            Looped = false,
            RootMember = AnimationMember.SetNormalizedBlendshapeValuesByModelNodeName(FaceTrackingReceiverComponent.DefaultFaceTrackedNodeName,
                (ARKitBlendshapeNames.MouthLeft, 0.0f),
                (ARKitBlendshapeNames.MouthRight, 0.0f))
        };
        #endregion

        #region Nose Sneer
        public static BlendTree1D NoseSneerBlend() => new()
        {
            ParameterName = FaceTrackingReceiverComponent.OSCmProxyName(FaceTrackingReceiverComponent.Avatar_NoseSneer),
            Children =
            [
                new BlendTree1D.Child()
                {
                    Motion = NoseSneer0(),
                    Threshold = 0.0f,
                },
                new BlendTree1D.Child()
                {
                    Motion = NoseSneer(),
                    Threshold = 1.0f,
                },
            ]
        };
        private static AnimationClip NoseSneer() => new()
        {
            Name = "Nose Sneer",
            LengthInSeconds = 0.0f,
            Looped = false,
            RootMember = AnimationMember.SetNormalizedBlendshapeValuesByModelNodeName(FaceTrackingReceiverComponent.DefaultFaceTrackedNodeName,
                (ARKitBlendshapeNames.NoseSneerLeft, 1.0f),
                (ARKitBlendshapeNames.NoseSneerRight, 1.0f))
        };
        private static AnimationClip NoseSneer0() => new()
        {
            Name = "Nose Sneer 0",
            LengthInSeconds = 0.0f,
            Looped = false,
            RootMember = AnimationMember.SetNormalizedBlendshapeValuesByModelNodeName(FaceTrackingReceiverComponent.DefaultFaceTrackedNodeName,
                (ARKitBlendshapeNames.NoseSneerLeft, 0.0f),
                (ARKitBlendshapeNames.NoseSneerRight, 0.0f))
        };
        #endregion

        #region Tongue Out
        public static BlendTree1D TongueOutBlend() => new()
        {
            ParameterName = FaceTrackingReceiverComponent.OSCmProxyName(FaceTrackingReceiverComponent.Avatar_TongueOut),
            Children =
            [
                new BlendTree1D.Child()
                {
                    Motion = TongueOut0(),
                    Threshold = 0.0f,
                },
                new BlendTree1D.Child()
                {
                    Motion = TongueOut(),
                    Threshold = 1.0f,
                },
            ]
        };
        private static AnimationClip TongueOut() => new()
        {
            Name = "Tongue Out",
            LengthInSeconds = 0.0f,
            Looped = false,
            RootMember = AnimationMember.SetNormalizedBlendshapeValuesByModelNodeName(FaceTrackingReceiverComponent.DefaultFaceTrackedNodeName,
                (ARKitBlendshapeNames.TongueOut, 1.0f))
        };
        private static AnimationClip TongueOut0() => new()
        {
            Name = "Tongue Out 0",
            LengthInSeconds = 0.0f,
            Looped = false,
            RootMember = AnimationMember.SetNormalizedBlendshapeValuesByModelNodeName(FaceTrackingReceiverComponent.DefaultFaceTrackedNodeName,
                (ARKitBlendshapeNames.TongueOut, 0.0f))
        };
        #endregion
    }
}
