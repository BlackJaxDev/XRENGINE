using System.Collections.ObjectModel;

namespace XREngine.Components.Animation
{
    public static class UnityHumanoidMuscleMap
    {
        private static readonly (EHumanoidValue value, string unityName)[] Entries =
        [
            (EHumanoidValue.LeftEyeDownUp, "Left Eye Down-Up"),
            (EHumanoidValue.LeftEyeInOut, "Left Eye In-Out"),
            (EHumanoidValue.RightEyeDownUp, "Right Eye Down-Up"),
            (EHumanoidValue.RightEyeInOut, "Right Eye In-Out"),

            (EHumanoidValue.SpineFrontBack, "Spine Front-Back"),
            (EHumanoidValue.SpineLeftRight, "Spine Left-Right"),
            (EHumanoidValue.SpineTwistLeftRight, "Spine Twist Left-Right"),
            (EHumanoidValue.ChestFrontBack, "Chest Front-Back"),
            (EHumanoidValue.ChestLeftRight, "Chest Left-Right"),
            (EHumanoidValue.ChestTwistLeftRight, "Chest Twist Left-Right"),
            (EHumanoidValue.UpperChestFrontBack, "UpperChest Front-Back"),
            (EHumanoidValue.UpperChestLeftRight, "UpperChest Left-Right"),
            (EHumanoidValue.UpperChestTwistLeftRight, "UpperChest Twist Left-Right"),
            (EHumanoidValue.NeckNodDownUp, "Neck Nod Down-Up"),
            (EHumanoidValue.NeckTiltLeftRight, "Neck Tilt Left-Right"),
            (EHumanoidValue.NeckTurnLeftRight, "Neck Turn Left-Right"),
            (EHumanoidValue.HeadNodDownUp, "Head Nod Down-Up"),
            (EHumanoidValue.HeadTiltLeftRight, "Head Tilt Left-Right"),
            (EHumanoidValue.HeadTurnLeftRight, "Head Turn Left-Right"),
            (EHumanoidValue.JawClose, "Jaw Close"),
            (EHumanoidValue.JawLeftRight, "Jaw Left-Right"),

            (EHumanoidValue.LeftShoulderDownUp, "Left Shoulder Down-Up"),
            (EHumanoidValue.LeftShoulderFrontBack, "Left Shoulder Front-Back"),
            (EHumanoidValue.LeftArmDownUp, "Left Arm Down-Up"),
            (EHumanoidValue.LeftArmFrontBack, "Left Arm Front-Back"),
            (EHumanoidValue.LeftArmTwistInOut, "Left Arm Twist In-Out"),
            (EHumanoidValue.LeftForearmStretch, "Left Forearm Stretch"),
            (EHumanoidValue.LeftForearmTwistInOut, "Left Forearm Twist In-Out"),
            (EHumanoidValue.LeftHandDownUp, "Left Hand Down-Up"),
            (EHumanoidValue.LeftHandInOut, "Left Hand In-Out"),
            (EHumanoidValue.LeftUpperLegFrontBack, "Left Upper Leg Front-Back"),
            (EHumanoidValue.LeftUpperLegInOut, "Left Upper Leg In-Out"),
            (EHumanoidValue.LeftUpperLegTwistInOut, "Left Upper Leg Twist In-Out"),
            (EHumanoidValue.LeftLowerLegStretch, "Left Lower Leg Stretch"),
            (EHumanoidValue.LeftLowerLegTwistInOut, "Left Lower Leg Twist In-Out"),
            (EHumanoidValue.LeftFootUpDown, "Left Foot Up-Down"),
            (EHumanoidValue.LeftFootTwistInOut, "Left Foot Twist In-Out"),
            (EHumanoidValue.LeftToesUpDown, "Left Toes Up-Down"),

            (EHumanoidValue.RightShoulderDownUp, "Right Shoulder Down-Up"),
            (EHumanoidValue.RightShoulderFrontBack, "Right Shoulder Front-Back"),
            (EHumanoidValue.RightArmDownUp, "Right Arm Down-Up"),
            (EHumanoidValue.RightArmFrontBack, "Right Arm Front-Back"),
            (EHumanoidValue.RightArmTwistInOut, "Right Arm Twist In-Out"),
            (EHumanoidValue.RightForearmStretch, "Right Forearm Stretch"),
            (EHumanoidValue.RightForearmTwistInOut, "Right Forearm Twist In-Out"),
            (EHumanoidValue.RightHandDownUp, "Right Hand Down-Up"),
            (EHumanoidValue.RightHandInOut, "Right Hand In-Out"),
            (EHumanoidValue.RightUpperLegFrontBack, "Right Upper Leg Front-Back"),
            (EHumanoidValue.RightUpperLegInOut, "Right Upper Leg In-Out"),
            (EHumanoidValue.RightUpperLegTwistInOut, "Right Upper Leg Twist In-Out"),
            (EHumanoidValue.RightLowerLegStretch, "Right Lower Leg Stretch"),
            (EHumanoidValue.RightLowerLegTwistInOut, "Right Lower Leg Twist In-Out"),
            (EHumanoidValue.RightFootUpDown, "Right Foot Up-Down"),
            (EHumanoidValue.RightFootTwistInOut, "Right Foot Twist In-Out"),
            (EHumanoidValue.RightToesUpDown, "Right Toes Up-Down"),

            (EHumanoidValue.LeftHandIndexSpread, "LeftHand.Index.Spread"),
            (EHumanoidValue.LeftHandIndex1Stretched, "LeftHand.Index.1 Stretched"),
            (EHumanoidValue.LeftHandIndex2Stretched, "LeftHand.Index.2 Stretched"),
            (EHumanoidValue.LeftHandIndex3Stretched, "LeftHand.Index.3 Stretched"),
            (EHumanoidValue.LeftHandMiddleSpread, "LeftHand.Middle.Spread"),
            (EHumanoidValue.LeftHandMiddle1Stretched, "LeftHand.Middle.1 Stretched"),
            (EHumanoidValue.LeftHandMiddle2Stretched, "LeftHand.Middle.2 Stretched"),
            (EHumanoidValue.LeftHandMiddle3Stretched, "LeftHand.Middle.3 Stretched"),
            (EHumanoidValue.LeftHandRingSpread, "LeftHand.Ring.Spread"),
            (EHumanoidValue.LeftHandRing1Stretched, "LeftHand.Ring.1 Stretched"),
            (EHumanoidValue.LeftHandRing2Stretched, "LeftHand.Ring.2 Stretched"),
            (EHumanoidValue.LeftHandRing3Stretched, "LeftHand.Ring.3 Stretched"),
            (EHumanoidValue.LeftHandLittleSpread, "LeftHand.Little.Spread"),
            (EHumanoidValue.LeftHandLittle1Stretched, "LeftHand.Little.1 Stretched"),
            (EHumanoidValue.LeftHandLittle2Stretched, "LeftHand.Little.2 Stretched"),
            (EHumanoidValue.LeftHandLittle3Stretched, "LeftHand.Little.3 Stretched"),
            (EHumanoidValue.LeftHandThumbSpread, "LeftHand.Thumb.Spread"),
            (EHumanoidValue.LeftHandThumb1Stretched, "LeftHand.Thumb.1 Stretched"),
            (EHumanoidValue.LeftHandThumb2Stretched, "LeftHand.Thumb.2 Stretched"),
            (EHumanoidValue.LeftHandThumb3Stretched, "LeftHand.Thumb.3 Stretched"),

            (EHumanoidValue.RightHandIndexSpread, "RightHand.Index.Spread"),
            (EHumanoidValue.RightHandIndex1Stretched, "RightHand.Index.1 Stretched"),
            (EHumanoidValue.RightHandIndex2Stretched, "RightHand.Index.2 Stretched"),
            (EHumanoidValue.RightHandIndex3Stretched, "RightHand.Index.3 Stretched"),
            (EHumanoidValue.RightHandMiddleSpread, "RightHand.Middle.Spread"),
            (EHumanoidValue.RightHandMiddle1Stretched, "RightHand.Middle.1 Stretched"),
            (EHumanoidValue.RightHandMiddle2Stretched, "RightHand.Middle.2 Stretched"),
            (EHumanoidValue.RightHandMiddle3Stretched, "RightHand.Middle.3 Stretched"),
            (EHumanoidValue.RightHandRingSpread, "RightHand.Ring.Spread"),
            (EHumanoidValue.RightHandRing1Stretched, "RightHand.Ring.1 Stretched"),
            (EHumanoidValue.RightHandRing2Stretched, "RightHand.Ring.2 Stretched"),
            (EHumanoidValue.RightHandRing3Stretched, "RightHand.Ring.3 Stretched"),
            (EHumanoidValue.RightHandLittleSpread, "RightHand.Little.Spread"),
            (EHumanoidValue.RightHandLittle1Stretched, "RightHand.Little.1 Stretched"),
            (EHumanoidValue.RightHandLittle2Stretched, "RightHand.Little.2 Stretched"),
            (EHumanoidValue.RightHandLittle3Stretched, "RightHand.Little.3 Stretched"),
            (EHumanoidValue.RightHandThumbSpread, "RightHand.Thumb.Spread"),
            (EHumanoidValue.RightHandThumb1Stretched, "RightHand.Thumb.1 Stretched"),
            (EHumanoidValue.RightHandThumb2Stretched, "RightHand.Thumb.2 Stretched"),
            (EHumanoidValue.RightHandThumb3Stretched, "RightHand.Thumb.3 Stretched"),
        ];

        private static readonly ReadOnlyDictionary<string, EHumanoidValue> NameToValue =
            new(Entries.ToDictionary(static x => x.unityName, static x => x.value, StringComparer.Ordinal));

        private static readonly ReadOnlyDictionary<EHumanoidValue, string> ValueToName =
            new(Entries.ToDictionary(static x => x.value, static x => x.unityName));

        public static IReadOnlyList<(EHumanoidValue Value, string UnityName)> OrderedEntries { get; } =
            Array.AsReadOnly(Entries.Select(static x => (x.value, x.unityName)).ToArray());

        public static bool TryGetValue(string unityName, out EHumanoidValue value)
            => NameToValue.TryGetValue(unityName, out value);

        public static bool TryGetUnityName(EHumanoidValue value, out string unityName)
            => ValueToName.TryGetValue(value, out unityName!);
    }
}
