using XREngine.Animation;

namespace XREngine.Data.Components
{
    public static class EyeTrackingBlendTrees
    {
        #region Right Eyelid
        public static BlendTree2D RightEyeLidBlend() => new()
        {
            Name = "Right Eyelid Blend",
            BlendType = BlendTree2D.EBlendType.Cartesian,
            XParameterName = FaceTrackingReceiverComponent.OSCmProxyName(FaceTrackingReceiverComponent.Avatar_EyeLidRight),
            YParameterName = FaceTrackingReceiverComponent.OSCmProxyName(FaceTrackingReceiverComponent.Avatar_EyeSquintRight),
            Children =
            {
                new BlendTree2D.Child
                {
                    Motion = EyelidBlinkRight(),
                    PositionX = 0.0f,
                    PositionY = 0.0f,
                },
                new BlendTree2D.Child
                {
                    Motion = EyelidNeutralRight(),
                    PositionX = 0.75f,
                    PositionY = 0.0f,
                },
                new BlendTree2D.Child
                {
                    Motion = EyelidWideRight(),
                    PositionX = 1.0f,
                    PositionY = 0.0f,
                },
                new BlendTree2D.Child
                {
                    Motion = EyelidSquintRight(),
                    PositionX = 0.25f,
                    PositionY = 1.0f,
                },
                new BlendTree2D.Child
                {
                    Motion = EyelidSquintRight(),
                    PositionX = 0.25f,
                    PositionY = 0.75f,
                },
                new BlendTree2D.Child
                {
                    Motion = EyeOpenSquintRight(),
                    PositionX = 0.75f,
                    PositionY = 1.0f,
                },
                new BlendTree2D.Child
                {
                    Motion = EyeOpenSquintRight(),
                    PositionX = 0.75f,
                    PositionY = 0.75f,
                },
            }
        };
        private static AnimationClip EyeOpenSquintRight() => new()
        {
            Name = "Eyelid Open Squint Right",
            LengthInSeconds = 0.0f,
            Looped = false,
            RootMember = AnimationMember.SetNormalizedBlendshapeValuesByModelNodeName(FaceTrackingReceiverComponent.DefaultFaceTrackedNodeName,
                (ARKitBlendshapeNames.EyeBlinkRight, 0.0f),
                (ARKitBlendshapeNames.EyeSquintRight, 1.0f),
                (ARKitBlendshapeNames.EyeWideRight, 0.0f))
        };
        private static AnimationClip EyelidSquintRight() => new()
        {
            Name = "Eyelid Squint Right",
            LengthInSeconds = 0.0f,
            Looped = false,
            RootMember = AnimationMember.SetNormalizedBlendshapeValuesByModelNodeName(FaceTrackingReceiverComponent.DefaultFaceTrackedNodeName,
                (ARKitBlendshapeNames.EyeBlinkRight, 0.9f),
                (ARKitBlendshapeNames.EyeSquintRight, 1.0f),
                (ARKitBlendshapeNames.EyeWideRight, 0.0f))
        };
        private static AnimationClip EyelidWideRight() => new()
        {
            Name = "Eyelid Wide Right",
            LengthInSeconds = 0.0f,
            Looped = false,
            RootMember = AnimationMember.SetNormalizedBlendshapeValuesByModelNodeName(FaceTrackingReceiverComponent.DefaultFaceTrackedNodeName,
                (ARKitBlendshapeNames.EyeBlinkRight, 0.0f),
                (ARKitBlendshapeNames.EyeSquintRight, 0.0f),
                (ARKitBlendshapeNames.EyeWideRight, 1.0f))
        };
        private static AnimationClip EyelidNeutralRight() => new()
        {
            Name = "Eyelid Neutral Right",
            LengthInSeconds = 0.0f,
            Looped = false,
            RootMember = AnimationMember.SetNormalizedBlendshapeValuesByModelNodeName(FaceTrackingReceiverComponent.DefaultFaceTrackedNodeName,
                (ARKitBlendshapeNames.EyeBlinkRight, 0.0f),
                (ARKitBlendshapeNames.EyeSquintRight, 0.0f),
                (ARKitBlendshapeNames.EyeWideRight, 0.0f))
        };
        private static AnimationClip EyelidBlinkRight() => new()
        {
            Name = "Eyelid Blink Right",
            LengthInSeconds = 0.0f,
            Looped = false,
            RootMember = AnimationMember.SetNormalizedBlendshapeValuesByModelNodeName(FaceTrackingReceiverComponent.DefaultFaceTrackedNodeName,
                (ARKitBlendshapeNames.EyeBlinkRight, 1.0f),
                (ARKitBlendshapeNames.EyeSquintRight, 0.0f),
                (ARKitBlendshapeNames.EyeWideRight, 0.0f))
        };
        #endregion

        #region Left Eyelid
        public static BlendTree2D LeftEyeLidBlend() => new()
        {
            Name = "Left Eyelid Blend",
            BlendType = BlendTree2D.EBlendType.Cartesian,
            XParameterName = FaceTrackingReceiverComponent.OSCmProxyName(FaceTrackingReceiverComponent.Avatar_EyeLidLeft),
            YParameterName = FaceTrackingReceiverComponent.OSCmProxyName(FaceTrackingReceiverComponent.Avatar_EyeSquintLeft),
            Children =
            {
                new BlendTree2D.Child
                {
                    Motion = EyelidBlinkLeft(),
                    PositionX = 0.0f,
                    PositionY = 0.0f,
                },
                new BlendTree2D.Child
                {
                    Motion = EyelidNeutralLeft(),
                    PositionX = 0.75f,
                    PositionY = 0.0f,
                },
                new BlendTree2D.Child
                {
                    Motion = EyelidWideLeft(),
                    PositionX = 1.0f,
                    PositionY = 0.0f,
                },
                new BlendTree2D.Child
                {
                    Motion = EyelidSquintLeft(),
                    PositionX = 0.25f,
                    PositionY = 1.0f,
                },
                new BlendTree2D.Child
                {
                    Motion = EyelidSquintLeft(),
                    PositionX = 0.25f,
                    PositionY = 0.75f,
                },
                new BlendTree2D.Child
                {
                    Motion = EyeOpenSquintLeft(),
                    PositionX = 0.75f,
                    PositionY = 1.0f,
                },
                new BlendTree2D.Child
                {
                    Motion = EyeOpenSquintLeft(),
                    PositionX = 0.75f,
                    PositionY = 0.75f,
                },
            }
        };
        private static AnimationClip EyeOpenSquintLeft() => new()
        {
            Name = "Eyelid Open Squint Left",
            LengthInSeconds = 0.0f,
            Looped = false,
            RootMember = AnimationMember.SetNormalizedBlendshapeValuesByModelNodeName(FaceTrackingReceiverComponent.DefaultFaceTrackedNodeName,
                (ARKitBlendshapeNames.EyeBlinkLeft, 0.0f),
                (ARKitBlendshapeNames.EyeSquintLeft, 1.0f),
                (ARKitBlendshapeNames.EyeWideLeft, 0.0f))
        };
        private static AnimationClip EyelidSquintLeft() => new()
        {
            Name = "Eyelid Squint Left",
            LengthInSeconds = 0.0f,
            Looped = false,
            RootMember = AnimationMember.SetNormalizedBlendshapeValuesByModelNodeName(FaceTrackingReceiverComponent.DefaultFaceTrackedNodeName,
                (ARKitBlendshapeNames.EyeBlinkLeft, 0.9f),
                (ARKitBlendshapeNames.EyeSquintLeft, 1.0f),
                (ARKitBlendshapeNames.EyeWideLeft, 0.0f))
        };
        private static AnimationClip EyelidWideLeft() => new()
        {
            Name = "Eyelid Wide Left",
            LengthInSeconds = 0.0f,
            Looped = false,
            RootMember = AnimationMember.SetNormalizedBlendshapeValuesByModelNodeName(FaceTrackingReceiverComponent.DefaultFaceTrackedNodeName,
                (ARKitBlendshapeNames.EyeBlinkLeft, 0.0f),
                (ARKitBlendshapeNames.EyeSquintLeft, 0.0f),
                (ARKitBlendshapeNames.EyeWideLeft, 1.0f))
        };
        private static AnimationClip EyelidNeutralLeft() => new()
        {
            Name = "Eyelid Neutral Left",
            LengthInSeconds = 0.0f,
            Looped = false,
            RootMember = AnimationMember.SetNormalizedBlendshapeValuesByModelNodeName(FaceTrackingReceiverComponent.DefaultFaceTrackedNodeName,
                (ARKitBlendshapeNames.EyeBlinkLeft, 0.0f),
                (ARKitBlendshapeNames.EyeSquintLeft, 0.0f),
                (ARKitBlendshapeNames.EyeWideLeft, 0.0f))
        };
        private static AnimationClip EyelidBlinkLeft() => new()
        {
            Name = "Eyelid Blink Left",
            LengthInSeconds = 0.0f,
            Looped = false,
            RootMember = AnimationMember.SetNormalizedBlendshapeValuesByModelNodeName(FaceTrackingReceiverComponent.DefaultFaceTrackedNodeName,
                (ARKitBlendshapeNames.EyeBlinkLeft, 1.0f),
                (ARKitBlendshapeNames.EyeSquintLeft, 0.0f),
                (ARKitBlendshapeNames.EyeWideLeft, 0.0f))
        };
        #endregion

        #region Brow Inner Up
        public static BlendTreeDirect BrowInnerUpBlend() => new()
        {
            Name = "Brow Inner Up Blend",
            Children =
            {
                new BlendTreeDirect.Child
                {
                    Motion = BrowInnerUpBlend2(),
                    WeightParameterName = FaceTrackingReceiverComponent.Param_DirectBlend,
                },
                new BlendTreeDirect.Child
                {
                    Motion = LimitBrowSad_MouthClosed(),
                    WeightParameterName = FaceTrackingReceiverComponent.Param_FaceTrackingEmulation,
                },
            }
        };
        private static BlendTree1D LimitBrowSad_MouthClosed() => new()
        {
            Name = "Limit Brow Sad (Mouth Closed)",
            ParameterName = FaceTrackingReceiverComponent.OSCmProxyName(FaceTrackingReceiverComponent.Avatar_MouthClosed),
            Children =
            {
                new BlendTree1D.Child
                {
                    Motion = BrowSadEmulation(),
                    Threshold = 0.0f,
                },
                new BlendTree1D.Child
                {
                    Motion = BrowInnerUp0(),
                    Threshold = 2.0f,
                },
            }
        };
        private static BlendTree2D BrowSadEmulation() => new()
        {
            Name = "Brow Sad Emulation",
            BlendType = BlendTree2D.EBlendType.Cartesian,
            XParameterName = FaceTrackingReceiverComponent.OSCmProxyName(FaceTrackingReceiverComponent.Avatar_SmileFrownLeft),
            YParameterName = FaceTrackingReceiverComponent.OSCmProxyName(FaceTrackingReceiverComponent.Avatar_SmileFrownRight),
            Children =
            {
                new BlendTree2D.Child
                {
                    Motion = BrowInnerUp0(),
                    PositionX = 0.0f,
                    PositionY = 0.0f,
                },
                new BlendTree2D.Child
                {
                    Motion = BrowInnerUp(),
                    PositionX = -0.7f,
                    PositionY = 0.7f,
                },
            }
        };
        private static BlendTree2D BrowInnerUpBlend2() => new()
        {
            Name = "Brow Inner Up Blend",
            BlendType = BlendTree2D.EBlendType.Cartesian,
            XParameterName = FaceTrackingReceiverComponent.OSCmProxyName(FaceTrackingReceiverComponent.Avatar_BrowExpressionLeft),
            YParameterName = FaceTrackingReceiverComponent.OSCmProxyName(FaceTrackingReceiverComponent.Avatar_BrowExpressionRight),
            Children =
            {
                new BlendTree2D.Child
                {
                    Motion = BrowInnerUp0(),
                    PositionX = 0.0f,
                    PositionY = 0.0f,
                },
                new BlendTree2D.Child
                {
                    Motion = BrowInnerUp(),
                    PositionX = 1.0f,
                    PositionY = 1.0f,
                },
            }
        };
        private static AnimationClip BrowInnerUp() => new()
        {
            Name = "Brow Inner Up",
            LengthInSeconds = 0.0f,
            Looped = false,
            RootMember = AnimationMember.SetNormalizedBlendshapeValuesByModelNodeName(FaceTrackingReceiverComponent.DefaultFaceTrackedNodeName, 
                (ARKitBlendshapeNames.BrowInnerUp, 1.0f))
        };
        private static AnimationClip BrowInnerUp0() => new()
        {
            Name = "Brow Inner Up 0",
            LengthInSeconds = 0.0f,
            Looped = false,
            RootMember = AnimationMember.SetNormalizedBlendshapeValuesByModelNodeName(FaceTrackingReceiverComponent.DefaultFaceTrackedNodeName,
                (ARKitBlendshapeNames.BrowInnerUp, 0.0f))
        };
        #endregion

        #region Eye Look Left
        public static BlendTree2D EyeLookLeftBlend()
        {
            return new BlendTree2D
            {
                Name = "Eye Look Left Blend",
                BlendType = BlendTree2D.EBlendType.Directional,
                XParameterName = FaceTrackingReceiverComponent.OSCmProxyName(FaceTrackingReceiverComponent.Avatar_EyeLeftX),
                YParameterName = FaceTrackingReceiverComponent.OSCmProxyName(FaceTrackingReceiverComponent.Avatar_EyeY),
                Children =
                {
                    new BlendTree2D.Child
                    {
                        Motion = EyeLookNeutralLeft(),
                        PositionX = 0.0f,
                        PositionY = 0.0f,
                    },
                    new BlendTree2D.Child
                    {
                        Motion = EyeLookInLeft(),
                        PositionX = 0.7f,
                        PositionY = 0.0f,
                    },
                    new BlendTree2D.Child
                    {
                        Motion = EyeLookOutLeft(),
                        PositionX = -0.7f,
                        PositionY = 0.0f,
                    },
                    new BlendTree2D.Child
                    {
                        Motion = EyeLookUpLeft(),
                        PositionX = 0.0f,
                        PositionY = 0.7f,
                    },
                    new BlendTree2D.Child
                    {
                        Motion = EyeLookDownLeft(),
                        PositionX = 0.0f,
                        PositionY = -0.7f,
                    },
                }
            };
        }
        private static AnimationClip EyeLookDownLeft() => new()
        {
            Name = "Eye Look Down Left",
            LengthInSeconds = 0.0f,
            Looped = false,
            RootMember = AnimationMember.SetNormalizedBlendshapeValuesByModelNodeName(FaceTrackingReceiverComponent.DefaultFaceTrackedNodeName,
                (ARKitBlendshapeNames.EyeLookDownLeft, 1.0f),
                (ARKitBlendshapeNames.EyeLookInLeft, 0.0f),
                (ARKitBlendshapeNames.EyeLookOutLeft, 0.0f),
                (ARKitBlendshapeNames.EyeLookUpLeft, 0.0f))
        };
        private static AnimationClip EyeLookUpLeft() => new()
        {
            Name = "Eye Look Up Left",
            LengthInSeconds = 0.0f,
            Looped = false,
            RootMember = AnimationMember.SetNormalizedBlendshapeValuesByModelNodeName(FaceTrackingReceiverComponent.DefaultFaceTrackedNodeName,
                (ARKitBlendshapeNames.EyeLookDownLeft, 0.0f),
                (ARKitBlendshapeNames.EyeLookInLeft, 0.0f),
                (ARKitBlendshapeNames.EyeLookOutLeft, 0.0f),
                (ARKitBlendshapeNames.EyeLookUpLeft, 1.0f))
        };
        private static AnimationClip EyeLookOutLeft() => new()
        {
            Name = "Eye Look Out Left",
            LengthInSeconds = 0.0f,
            Looped = false,
            RootMember = AnimationMember.SetNormalizedBlendshapeValuesByModelNodeName(FaceTrackingReceiverComponent.DefaultFaceTrackedNodeName,
                (ARKitBlendshapeNames.EyeLookDownLeft, 0.0f),
                (ARKitBlendshapeNames.EyeLookInLeft, 0.0f),
                (ARKitBlendshapeNames.EyeLookOutLeft, 1.0f),
                (ARKitBlendshapeNames.EyeLookUpLeft, 0.0f))
        };
        private static AnimationClip EyeLookInLeft() => new()
        {
            Name = "Eye Look In Left",
            LengthInSeconds = 0.0f,
            Looped = false,
            RootMember = AnimationMember.SetNormalizedBlendshapeValuesByModelNodeName(FaceTrackingReceiverComponent.DefaultFaceTrackedNodeName,
                (ARKitBlendshapeNames.EyeLookDownLeft, 0.0f),
                (ARKitBlendshapeNames.EyeLookInLeft, 1.0f),
                (ARKitBlendshapeNames.EyeLookOutLeft, 0.0f),
                (ARKitBlendshapeNames.EyeLookUpLeft, 0.0f))
        };
        private static AnimationClip EyeLookNeutralLeft() => new()
        {
            Name = "Eye Look Neutral Left",
            LengthInSeconds = 0.0f,
            Looped = false,
            RootMember = AnimationMember.SetNormalizedBlendshapeValuesByModelNodeName(FaceTrackingReceiverComponent.DefaultFaceTrackedNodeName,
                (ARKitBlendshapeNames.EyeLookDownLeft, 0.0f),
                (ARKitBlendshapeNames.EyeLookInLeft, 0.0f),
                (ARKitBlendshapeNames.EyeLookOutLeft, 0.0f),
                (ARKitBlendshapeNames.EyeLookUpLeft, 0.0f))
        };
        #endregion

        #region Eye Look Right
        public static BlendTree2D EyeLookRightBlend() => new()
        {
            Name = "Eye Look Right Blend",
            BlendType = BlendTree2D.EBlendType.Directional,
            XParameterName = FaceTrackingReceiverComponent.OSCmProxyName(FaceTrackingReceiverComponent.Avatar_EyeRightX),
            YParameterName = FaceTrackingReceiverComponent.OSCmProxyName(FaceTrackingReceiverComponent.Avatar_EyeY),
            Children =
                {
                    new BlendTree2D.Child
                    {
                        Motion = EyeLookNeutralRight(),
                        PositionX = 0.0f,
                        PositionY = 0.0f,
                    },
                    new BlendTree2D.Child
                    {
                        Motion = EyeLookInRight(),
                        PositionX = -0.7f,
                        PositionY = 0.0f,
                    },
                    new BlendTree2D.Child
                    {
                        Motion = EyeLookOutRight(),
                        PositionX = 0.7f,
                        PositionY = 0.0f,
                    },
                    new BlendTree2D.Child
                    {
                        Motion = EyeLookUpRight(),
                        PositionX = 0.0f,
                        PositionY = 0.7f,
                    },
                    new BlendTree2D.Child
                    {
                        Motion = EyeLookDownRight(),
                        PositionX = 0.0f,
                        PositionY = -0.7f,
                    },
                }
        };
        private static AnimationClip EyeLookDownRight() => new()
        {
            Name = "Eye Look Down Right",
            LengthInSeconds = 0.0f,
            Looped = false,
            RootMember = AnimationMember.SetNormalizedBlendshapeValuesByModelNodeName(FaceTrackingReceiverComponent.DefaultFaceTrackedNodeName,
                (ARKitBlendshapeNames.EyeLookDownRight, 1.0f),
                (ARKitBlendshapeNames.EyeLookInRight, 0.0f),
                (ARKitBlendshapeNames.EyeLookOutRight, 0.0f),
                (ARKitBlendshapeNames.EyeLookUpRight, 0.0f))
        };
        private static AnimationClip EyeLookUpRight() => new()
        {
            Name = "Eye Look Up Right",
            LengthInSeconds = 0.0f,
            Looped = false,
            RootMember = AnimationMember.SetNormalizedBlendshapeValuesByModelNodeName(FaceTrackingReceiverComponent.DefaultFaceTrackedNodeName,
                (ARKitBlendshapeNames.EyeLookDownRight, 0.0f),
                (ARKitBlendshapeNames.EyeLookInRight, 0.0f),
                (ARKitBlendshapeNames.EyeLookOutRight, 0.0f),
                (ARKitBlendshapeNames.EyeLookUpRight, 1.0f))
        };
        private static AnimationClip EyeLookOutRight() => new()
        {
            Name = "Eye Look Out Right",
            LengthInSeconds = 0.0f,
            Looped = false,
            RootMember = AnimationMember.SetNormalizedBlendshapeValuesByModelNodeName(FaceTrackingReceiverComponent.DefaultFaceTrackedNodeName,
                (ARKitBlendshapeNames.EyeLookDownRight, 0.0f),
                (ARKitBlendshapeNames.EyeLookInRight, 0.0f),
                (ARKitBlendshapeNames.EyeLookOutRight, 1.0f),
                (ARKitBlendshapeNames.EyeLookUpRight, 0.0f))
        };
        private static AnimationClip EyeLookInRight() => new()
        {
            Name = "Eye Look In Right",
            LengthInSeconds = 0.0f,
            Looped = false,
            RootMember = AnimationMember.SetNormalizedBlendshapeValuesByModelNodeName(FaceTrackingReceiverComponent.DefaultFaceTrackedNodeName,
                (ARKitBlendshapeNames.EyeLookDownRight, 0.0f),
                (ARKitBlendshapeNames.EyeLookInRight, 1.0f),
                (ARKitBlendshapeNames.EyeLookOutRight, 0.0f),
                (ARKitBlendshapeNames.EyeLookUpRight, 0.0f))
        };
        private static AnimationClip EyeLookNeutralRight() => new()
        {
            Name = "Eye Look Neutral Right",
            LengthInSeconds = 0.0f,
            Looped = false,
            RootMember = AnimationMember.SetNormalizedBlendshapeValuesByModelNodeName(FaceTrackingReceiverComponent.DefaultFaceTrackedNodeName,
                (ARKitBlendshapeNames.EyeLookDownRight, 0.0f),
                (ARKitBlendshapeNames.EyeLookInRight, 0.0f),
                (ARKitBlendshapeNames.EyeLookOutRight, 0.0f),
                (ARKitBlendshapeNames.EyeLookUpRight, 0.0f))
        };
        #endregion

        #region Brow Down Left
        public static BlendTreeDirect BrowDownLeftBlend() => new()
        {
            Name = "Brow Down Left Blend",
            Children =
            {
                new BlendTreeDirect.Child
                {
                    Motion = BrowDownLeftBlend2(),
                    WeightParameterName = FaceTrackingReceiverComponent.Param_DirectBlend,
                },
                new BlendTreeDirect.Child
                {
                    Motion = BrowAngry_MouthRaiserLower_Emulation_Left(),
                    WeightParameterName = FaceTrackingReceiverComponent.Param_FaceTrackingEmulation,
                },
                new BlendTreeDirect.Child
                {
                    Motion = BrowAngry_NoseSneer_Emulation_Left(),
                    WeightParameterName = FaceTrackingReceiverComponent.Param_FaceTrackingEmulation,
                },
            }
        };
        private static BlendTree1D BrowAngry_NoseSneer_Emulation_Left() => new()
        {
            Name = "Brow Angry (Nose Sneer) Emulation Left",
            ParameterName = FaceTrackingReceiverComponent.OSCmProxyName(FaceTrackingReceiverComponent.Avatar_NoseSneer),
            Children =
            {
                new BlendTree1D.Child
                {
                    Motion = BrowDownLeft0(),
                    Threshold = 0.0f,
                },
                new BlendTree1D.Child
                {
                    Motion = BrowDownLeft(),
                    Threshold = 1.0f,
                },
            }
        };
        private static BlendTree1D BrowAngry_MouthRaiserLower_Emulation_Left() => new()
        {
            Name = "Brow Angry (Mouth Raiser Lower) Emulation Left",
            ParameterName = FaceTrackingReceiverComponent.OSCmProxyName(FaceTrackingReceiverComponent.Avatar_MouthRaiserLower),
            Children =
            {
                new BlendTree1D.Child
                {
                    Motion = BrowDownLeft0(),
                    Threshold = 0.0f,
                },
                new BlendTree1D.Child
                {
                    Motion = BrowDownLeft(),
                    Threshold = 1.0f,
                },
            }
        };
        private static BlendTree1D BrowDownLeftBlend2() => new()
        {
            Name = "Brow Down Left Blend",
            ParameterName = FaceTrackingReceiverComponent.OSCmProxyName(FaceTrackingReceiverComponent.Avatar_BrowExpressionLeft),
            Children =
            {
                new BlendTree1D.Child
                {
                    Motion = BrowDownLeft(),
                    Threshold = -1.0f,
                },
                new BlendTree1D.Child
                {
                    Motion = BrowDownLeft0(),
                    Threshold = 0.0f,
                },
            }
        };
        private static AnimationClip BrowDownLeft0() => new()
        {
            Name = "Brow Down Left 0",
            LengthInSeconds = 0.0f,
            Looped = false,
            RootMember = AnimationMember.SetNormalizedBlendshapeValuesByModelNodeName(FaceTrackingReceiverComponent.DefaultFaceTrackedNodeName,
                (ARKitBlendshapeNames.BrowDownLeft, 0.0f))
        };
        private static AnimationClip BrowDownLeft() => new()
        {
            Name = "Brow Down Left",
            LengthInSeconds = 0.0f,
            Looped = false,
            RootMember = AnimationMember.SetNormalizedBlendshapeValuesByModelNodeName(FaceTrackingReceiverComponent.DefaultFaceTrackedNodeName,
                (ARKitBlendshapeNames.BrowDownLeft, 1.0f))
        };
        #endregion

        #region Brow Down Right
        public static BlendTreeDirect BrowDownRightBlend() => new()
        {
            Name = "Brow Down Right Blend",
            Children =
            {
                new BlendTreeDirect.Child
                {
                    Motion = BrowDownRightBlend2(),
                    WeightParameterName = FaceTrackingReceiverComponent.Param_DirectBlend,
                },
                new BlendTreeDirect.Child
                {
                    Motion = BrowAngry_MouthRaiserLower_Emulation_Right(),
                    WeightParameterName = FaceTrackingReceiverComponent.Param_FaceTrackingEmulation,
                },
                new BlendTreeDirect.Child
                {
                    Motion = BrowAngry_NoseSneer_Emulation_Right(),
                    WeightParameterName = FaceTrackingReceiverComponent.Param_FaceTrackingEmulation,
                },
            }
        };
        private static BlendTree1D BrowAngry_NoseSneer_Emulation_Right() => new()
        {
            Name = "Brow Angry (Nose Sneer) Emulation Right",
            ParameterName = FaceTrackingReceiverComponent.OSCmProxyName(FaceTrackingReceiverComponent.Avatar_NoseSneer),
            Children =
            {
                new BlendTree1D.Child
                {
                    Motion = BrowDownRight0(),
                    Threshold = 0.0f,
                },
                new BlendTree1D.Child
                {
                    Motion = BrowDownRight(),
                    Threshold = 1.0f,
                },
            }
        };
        private static BlendTree1D BrowAngry_MouthRaiserLower_Emulation_Right() => new()
        {
            Name = "Brow Angry (Mouth Raiser Lower) Emulation Right",
            ParameterName = FaceTrackingReceiverComponent.OSCmProxyName(FaceTrackingReceiverComponent.Avatar_MouthRaiserLower),
            Children =
            {
                new BlendTree1D.Child
                {
                    Motion = BrowDownRight0(),
                    Threshold = 0.0f,
                },
                new BlendTree1D.Child
                {
                    Motion = BrowDownRight(),
                    Threshold = 1.0f,
                },
            }
        };
        private static BlendTree1D BrowDownRightBlend2() => new()
        {
            Name = "Brow Down Right Blend",
            ParameterName = FaceTrackingReceiverComponent.OSCmProxyName(FaceTrackingReceiverComponent.Avatar_BrowExpressionRight),
            Children =
            {
                new BlendTree1D.Child
                {
                    Motion = BrowDownRight(),
                    Threshold = -1.0f,
                },
                new BlendTree1D.Child
                {
                    Motion = BrowDownRight0(),
                    Threshold = 0.0f,
                },
            }
        };
        private static AnimationClip BrowDownRight0() => new()
        {
            Name = "Brow Down Right 0",
            LengthInSeconds = 0.0f,
            Looped = false,
            RootMember = AnimationMember.SetNormalizedBlendshapeValuesByModelNodeName(FaceTrackingReceiverComponent.DefaultFaceTrackedNodeName,
                (ARKitBlendshapeNames.BrowDownRight, 0.0f))
        };
        private static AnimationClip BrowDownRight() => new()
        {
            Name = "Brow Down Right",
            LengthInSeconds = 0.0f,
            Looped = false,
            RootMember = AnimationMember.SetNormalizedBlendshapeValuesByModelNodeName(FaceTrackingReceiverComponent.DefaultFaceTrackedNodeName,
                (ARKitBlendshapeNames.BrowDownRight, 1.0f))
        };
        #endregion

        #region Brow Outer Up Left
        public static BlendTreeDirect BrowOuterUpLeftBlend() => new()
        {
            Name = "Brow Outer Up Left Blend",
            Children =
            {
                new BlendTreeDirect.Child
                {
                    Motion = BrowOuterUpLeftBlend2(),
                    WeightParameterName = FaceTrackingReceiverComponent.Param_DirectBlend,
                },
                new BlendTreeDirect.Child
                {
                    Motion = BrowWideLeftEmulation(),
                    WeightParameterName = FaceTrackingReceiverComponent.Param_FaceTrackingEmulation,
                },
            }
        };
        private static BlendTree1D BrowWideLeftEmulation() => new()
        {
            Name = "Brow Wide Left Emulation",
            ParameterName = FaceTrackingReceiverComponent.OSCmProxyName(FaceTrackingReceiverComponent.Avatar_EyeLidLeft),
            Children =
            {
                new BlendTree1D.Child
                {
                    Motion = BrowOuterUpLeft0(),
                    Threshold = 0.75f,
                },
                new BlendTree1D.Child
                {
                    Motion = BrowOuterUpLeft(),
                    Threshold = 1.5f,
                },
            }
        };
        private static BlendTree1D BrowOuterUpLeftBlend2() => new()
        {
            Name = "Brow Outer Up Left Blend",
            ParameterName = FaceTrackingReceiverComponent.OSCmProxyName(FaceTrackingReceiverComponent.Avatar_BrowExpressionLeft),
            Children =
            {
                new BlendTree1D.Child
                {
                    Motion = BrowOuterUpLeft0(),
                    Threshold = 0.0f,
                },
                new BlendTree1D.Child
                {
                    Motion = BrowOuterUpLeft(),
                    Threshold = 1.0f,
                },
            }
        };
        private static AnimationClip BrowOuterUpLeft() => new()
        {
            Name = "Brow Outer Up Left",
            LengthInSeconds = 0.0f,
            Looped = false,
            RootMember = AnimationMember.SetNormalizedBlendshapeValuesByModelNodeName(FaceTrackingReceiverComponent.DefaultFaceTrackedNodeName,
                (ARKitBlendshapeNames.BrowOuterUpLeft, 1.0f))
        };
        private static AnimationClip BrowOuterUpLeft0() => new()
        {
            Name = "Brow Outer Up Left 0",
            LengthInSeconds = 0.0f,
            Looped = false,
            RootMember = AnimationMember.SetNormalizedBlendshapeValuesByModelNodeName(FaceTrackingReceiverComponent.DefaultFaceTrackedNodeName,
                (ARKitBlendshapeNames.BrowOuterUpLeft, 0.0f))
        };
        #endregion

        #region Brow Outer Up Right
        public static BlendTreeDirect BrowOuterUpRightBlend() => new()
        {
            Name = "Brow Outer Up Right Blend",
            Children =
            {
                new BlendTreeDirect.Child
                {
                    Motion = BrowOuterUpRightBlend2(),
                    WeightParameterName = FaceTrackingReceiverComponent.Param_DirectBlend,
                },
                new BlendTreeDirect.Child
                {
                    Motion = BrowWideRightEmulation(),
                    WeightParameterName = FaceTrackingReceiverComponent.Param_FaceTrackingEmulation,
                },
            }
        };
        private static BlendTree1D BrowWideRightEmulation() => new()
        {
            Name = "Brow Wide Right Emulation",
            ParameterName = FaceTrackingReceiverComponent.OSCmProxyName(FaceTrackingReceiverComponent.Avatar_EyeLidRight),
            Children =
            {
                new BlendTree1D.Child
                {
                    Motion = BrowOuterUpRight0(),
                    Threshold = 0.75f,
                },
                new BlendTree1D.Child
                {
                    Motion = BrowOuterUpRight(),
                    Threshold = 1.5f,
                },
            }
        };
        private static BlendTree1D BrowOuterUpRightBlend2() => new()
        {
            Name = "Brow Outer Up Right Blend",
            ParameterName = FaceTrackingReceiverComponent.OSCmProxyName(FaceTrackingReceiverComponent.Avatar_BrowExpressionRight),
            Children =
            {
                new BlendTree1D.Child
                {
                    Motion = BrowOuterUpRight0(),
                    Threshold = 0.0f,
                },
                new BlendTree1D.Child
                {
                    Motion = BrowOuterUpRight(),
                    Threshold = 1.0f,
                },
            }
        };
        private static AnimationClip BrowOuterUpRight() => new()
        {
            Name = "Brow Outer Up Right",
            LengthInSeconds = 0.0f,
            Looped = false,
            RootMember = AnimationMember.SetNormalizedBlendshapeValuesByModelNodeName(FaceTrackingReceiverComponent.DefaultFaceTrackedNodeName,
                (ARKitBlendshapeNames.BrowOuterUpRight, 1.0f))
        };
        private static AnimationClip BrowOuterUpRight0() => new()
        {
            Name = "Brow Outer Up Right 0",
            LengthInSeconds = 0.0f,
            Looped = false,
            RootMember = AnimationMember.SetNormalizedBlendshapeValuesByModelNodeName(FaceTrackingReceiverComponent.DefaultFaceTrackedNodeName,
                (ARKitBlendshapeNames.BrowOuterUpRight, 0.0f))
        };
        #endregion
    }
}
