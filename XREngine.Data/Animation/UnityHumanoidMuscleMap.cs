using System.Collections.ObjectModel;

namespace XREngine.Components.Animation
{
    public static class UnityHumanoidMuscleMap
    {
        public readonly record struct MuscleEntry(
            EHumanoidValue Value,
            string CurveAttributeName,
            string HumanTraitName);

        private static readonly MuscleEntry[] Entries =
        [
            new(EHumanoidValue.SpineFrontBack, "Spine Front-Back", "Spine Front-Back"),
            new(EHumanoidValue.SpineLeftRight, "Spine Left-Right", "Spine Left-Right"),
            new(EHumanoidValue.SpineTwistLeftRight, "Spine Twist Left-Right", "Spine Twist Left-Right"),
            new(EHumanoidValue.ChestFrontBack, "Chest Front-Back", "Chest Front-Back"),
            new(EHumanoidValue.ChestLeftRight, "Chest Left-Right", "Chest Left-Right"),
            new(EHumanoidValue.ChestTwistLeftRight, "Chest Twist Left-Right", "Chest Twist Left-Right"),
            new(EHumanoidValue.UpperChestFrontBack, "UpperChest Front-Back", "UpperChest Front-Back"),
            new(EHumanoidValue.UpperChestLeftRight, "UpperChest Left-Right", "UpperChest Left-Right"),
            new(EHumanoidValue.UpperChestTwistLeftRight, "UpperChest Twist Left-Right", "UpperChest Twist Left-Right"),
            new(EHumanoidValue.NeckNodDownUp, "Neck Nod Down-Up", "Neck Nod Down-Up"),
            new(EHumanoidValue.NeckTiltLeftRight, "Neck Tilt Left-Right", "Neck Tilt Left-Right"),
            new(EHumanoidValue.NeckTurnLeftRight, "Neck Turn Left-Right", "Neck Turn Left-Right"),
            new(EHumanoidValue.HeadNodDownUp, "Head Nod Down-Up", "Head Nod Down-Up"),
            new(EHumanoidValue.HeadTiltLeftRight, "Head Tilt Left-Right", "Head Tilt Left-Right"),
            new(EHumanoidValue.HeadTurnLeftRight, "Head Turn Left-Right", "Head Turn Left-Right"),
            new(EHumanoidValue.LeftEyeDownUp, "Left Eye Down-Up", "Left Eye Down-Up"),
            new(EHumanoidValue.LeftEyeInOut, "Left Eye In-Out", "Left Eye In-Out"),
            new(EHumanoidValue.RightEyeDownUp, "Right Eye Down-Up", "Right Eye Down-Up"),
            new(EHumanoidValue.RightEyeInOut, "Right Eye In-Out", "Right Eye In-Out"),
            new(EHumanoidValue.JawClose, "Jaw Close", "Jaw Close"),
            new(EHumanoidValue.JawLeftRight, "Jaw Left-Right", "Jaw Left-Right"),

            new(EHumanoidValue.LeftUpperLegFrontBack, "Left Upper Leg Front-Back", "Left Upper Leg Front-Back"),
            new(EHumanoidValue.LeftUpperLegInOut, "Left Upper Leg In-Out", "Left Upper Leg In-Out"),
            new(EHumanoidValue.LeftUpperLegTwistInOut, "Left Upper Leg Twist In-Out", "Left Upper Leg Twist In-Out"),
            new(EHumanoidValue.LeftLowerLegStretch, "Left Lower Leg Stretch", "Left Lower Leg Stretch"),
            new(EHumanoidValue.LeftLowerLegTwistInOut, "Left Lower Leg Twist In-Out", "Left Lower Leg Twist In-Out"),
            new(EHumanoidValue.LeftFootUpDown, "Left Foot Up-Down", "Left Foot Up-Down"),
            new(EHumanoidValue.LeftFootTwistInOut, "Left Foot Twist In-Out", "Left Foot Twist In-Out"),
            new(EHumanoidValue.LeftToesUpDown, "Left Toes Up-Down", "Left Toes Up-Down"),
            new(EHumanoidValue.RightUpperLegFrontBack, "Right Upper Leg Front-Back", "Right Upper Leg Front-Back"),
            new(EHumanoidValue.RightUpperLegInOut, "Right Upper Leg In-Out", "Right Upper Leg In-Out"),
            new(EHumanoidValue.RightUpperLegTwistInOut, "Right Upper Leg Twist In-Out", "Right Upper Leg Twist In-Out"),
            new(EHumanoidValue.RightLowerLegStretch, "Right Lower Leg Stretch", "Right Lower Leg Stretch"),
            new(EHumanoidValue.RightLowerLegTwistInOut, "Right Lower Leg Twist In-Out", "Right Lower Leg Twist In-Out"),
            new(EHumanoidValue.RightFootUpDown, "Right Foot Up-Down", "Right Foot Up-Down"),
            new(EHumanoidValue.RightFootTwistInOut, "Right Foot Twist In-Out", "Right Foot Twist In-Out"),
            new(EHumanoidValue.RightToesUpDown, "Right Toes Up-Down", "Right Toes Up-Down"),

            new(EHumanoidValue.LeftShoulderDownUp, "Left Shoulder Down-Up", "Left Shoulder Down-Up"),
            new(EHumanoidValue.LeftShoulderFrontBack, "Left Shoulder Front-Back", "Left Shoulder Front-Back"),
            new(EHumanoidValue.LeftArmDownUp, "Left Arm Down-Up", "Left Arm Down-Up"),
            new(EHumanoidValue.LeftArmFrontBack, "Left Arm Front-Back", "Left Arm Front-Back"),
            new(EHumanoidValue.LeftArmTwistInOut, "Left Arm Twist In-Out", "Left Arm Twist In-Out"),
            new(EHumanoidValue.LeftForearmStretch, "Left Forearm Stretch", "Left Forearm Stretch"),
            new(EHumanoidValue.LeftForearmTwistInOut, "Left Forearm Twist In-Out", "Left Forearm Twist In-Out"),
            new(EHumanoidValue.LeftHandDownUp, "Left Hand Down-Up", "Left Hand Down-Up"),
            new(EHumanoidValue.LeftHandInOut, "Left Hand In-Out", "Left Hand In-Out"),
            new(EHumanoidValue.RightShoulderDownUp, "Right Shoulder Down-Up", "Right Shoulder Down-Up"),
            new(EHumanoidValue.RightShoulderFrontBack, "Right Shoulder Front-Back", "Right Shoulder Front-Back"),
            new(EHumanoidValue.RightArmDownUp, "Right Arm Down-Up", "Right Arm Down-Up"),
            new(EHumanoidValue.RightArmFrontBack, "Right Arm Front-Back", "Right Arm Front-Back"),
            new(EHumanoidValue.RightArmTwistInOut, "Right Arm Twist In-Out", "Right Arm Twist In-Out"),
            new(EHumanoidValue.RightForearmStretch, "Right Forearm Stretch", "Right Forearm Stretch"),
            new(EHumanoidValue.RightForearmTwistInOut, "Right Forearm Twist In-Out", "Right Forearm Twist In-Out"),
            new(EHumanoidValue.RightHandDownUp, "Right Hand Down-Up", "Right Hand Down-Up"),
            new(EHumanoidValue.RightHandInOut, "Right Hand In-Out", "Right Hand In-Out"),

            new(EHumanoidValue.LeftHandThumb1Stretched, "LeftHand.Thumb.1 Stretched", "Left Thumb 1 Stretched"),
            new(EHumanoidValue.LeftHandThumbSpread, "LeftHand.Thumb.Spread", "Left Thumb Spread"),
            new(EHumanoidValue.LeftHandThumb2Stretched, "LeftHand.Thumb.2 Stretched", "Left Thumb 2 Stretched"),
            new(EHumanoidValue.LeftHandThumb3Stretched, "LeftHand.Thumb.3 Stretched", "Left Thumb 3 Stretched"),
            new(EHumanoidValue.LeftHandIndex1Stretched, "LeftHand.Index.1 Stretched", "Left Index 1 Stretched"),
            new(EHumanoidValue.LeftHandIndexSpread, "LeftHand.Index.Spread", "Left Index Spread"),
            new(EHumanoidValue.LeftHandIndex2Stretched, "LeftHand.Index.2 Stretched", "Left Index 2 Stretched"),
            new(EHumanoidValue.LeftHandIndex3Stretched, "LeftHand.Index.3 Stretched", "Left Index 3 Stretched"),
            new(EHumanoidValue.LeftHandMiddle1Stretched, "LeftHand.Middle.1 Stretched", "Left Middle 1 Stretched"),
            new(EHumanoidValue.LeftHandMiddleSpread, "LeftHand.Middle.Spread", "Left Middle Spread"),
            new(EHumanoidValue.LeftHandMiddle2Stretched, "LeftHand.Middle.2 Stretched", "Left Middle 2 Stretched"),
            new(EHumanoidValue.LeftHandMiddle3Stretched, "LeftHand.Middle.3 Stretched", "Left Middle 3 Stretched"),
            new(EHumanoidValue.LeftHandRing1Stretched, "LeftHand.Ring.1 Stretched", "Left Ring 1 Stretched"),
            new(EHumanoidValue.LeftHandRingSpread, "LeftHand.Ring.Spread", "Left Ring Spread"),
            new(EHumanoidValue.LeftHandRing2Stretched, "LeftHand.Ring.2 Stretched", "Left Ring 2 Stretched"),
            new(EHumanoidValue.LeftHandRing3Stretched, "LeftHand.Ring.3 Stretched", "Left Ring 3 Stretched"),
            new(EHumanoidValue.LeftHandLittle1Stretched, "LeftHand.Little.1 Stretched", "Left Little 1 Stretched"),
            new(EHumanoidValue.LeftHandLittleSpread, "LeftHand.Little.Spread", "Left Little Spread"),
            new(EHumanoidValue.LeftHandLittle2Stretched, "LeftHand.Little.2 Stretched", "Left Little 2 Stretched"),
            new(EHumanoidValue.LeftHandLittle3Stretched, "LeftHand.Little.3 Stretched", "Left Little 3 Stretched"),

            new(EHumanoidValue.RightHandThumb1Stretched, "RightHand.Thumb.1 Stretched", "Right Thumb 1 Stretched"),
            new(EHumanoidValue.RightHandThumbSpread, "RightHand.Thumb.Spread", "Right Thumb Spread"),
            new(EHumanoidValue.RightHandThumb2Stretched, "RightHand.Thumb.2 Stretched", "Right Thumb 2 Stretched"),
            new(EHumanoidValue.RightHandThumb3Stretched, "RightHand.Thumb.3 Stretched", "Right Thumb 3 Stretched"),
            new(EHumanoidValue.RightHandIndex1Stretched, "RightHand.Index.1 Stretched", "Right Index 1 Stretched"),
            new(EHumanoidValue.RightHandIndexSpread, "RightHand.Index.Spread", "Right Index Spread"),
            new(EHumanoidValue.RightHandIndex2Stretched, "RightHand.Index.2 Stretched", "Right Index 2 Stretched"),
            new(EHumanoidValue.RightHandIndex3Stretched, "RightHand.Index.3 Stretched", "Right Index 3 Stretched"),
            new(EHumanoidValue.RightHandMiddle1Stretched, "RightHand.Middle.1 Stretched", "Right Middle 1 Stretched"),
            new(EHumanoidValue.RightHandMiddleSpread, "RightHand.Middle.Spread", "Right Middle Spread"),
            new(EHumanoidValue.RightHandMiddle2Stretched, "RightHand.Middle.2 Stretched", "Right Middle 2 Stretched"),
            new(EHumanoidValue.RightHandMiddle3Stretched, "RightHand.Middle.3 Stretched", "Right Middle 3 Stretched"),
            new(EHumanoidValue.RightHandRing1Stretched, "RightHand.Ring.1 Stretched", "Right Ring 1 Stretched"),
            new(EHumanoidValue.RightHandRingSpread, "RightHand.Ring.Spread", "Right Ring Spread"),
            new(EHumanoidValue.RightHandRing2Stretched, "RightHand.Ring.2 Stretched", "Right Ring 2 Stretched"),
            new(EHumanoidValue.RightHandRing3Stretched, "RightHand.Ring.3 Stretched", "Right Ring 3 Stretched"),
            new(EHumanoidValue.RightHandLittle1Stretched, "RightHand.Little.1 Stretched", "Right Little 1 Stretched"),
            new(EHumanoidValue.RightHandLittleSpread, "RightHand.Little.Spread", "Right Little Spread"),
            new(EHumanoidValue.RightHandLittle2Stretched, "RightHand.Little.2 Stretched", "Right Little 2 Stretched"),
            new(EHumanoidValue.RightHandLittle3Stretched, "RightHand.Little.3 Stretched", "Right Little 3 Stretched"),
        ];

        private static readonly ReadOnlyDictionary<string, EHumanoidValue> NameToValue = BuildNameToValue();
        private static readonly ReadOnlyDictionary<EHumanoidValue, string> ValueToHumanTraitName =
            new(Entries.ToDictionary(static x => x.Value, static x => x.HumanTraitName));
        private static readonly ReadOnlyDictionary<EHumanoidValue, string> ValueToCurveAttributeName =
            new(Entries.ToDictionary(static x => x.Value, static x => x.CurveAttributeName));

        public static IReadOnlyList<(EHumanoidValue Value, string UnityName)> OrderedEntries { get; } =
            Array.AsReadOnly(Entries.Select(static x => (x.Value, x.HumanTraitName)).ToArray());

        public static IReadOnlyList<MuscleEntry> OrderedMuscleEntries { get; } =
            Array.AsReadOnly(Entries);

        public static bool TryGetValue(string unityName, out EHumanoidValue value)
            => NameToValue.TryGetValue(unityName, out value);

        public static bool TryGetUnityName(EHumanoidValue value, out string unityName)
            => ValueToHumanTraitName.TryGetValue(value, out unityName!);

        public static bool TryGetHumanTraitName(EHumanoidValue value, out string humanTraitName)
            => ValueToHumanTraitName.TryGetValue(value, out humanTraitName!);

        public static bool TryGetCurveAttributeName(EHumanoidValue value, out string curveAttributeName)
            => ValueToCurveAttributeName.TryGetValue(value, out curveAttributeName!);

        private static ReadOnlyDictionary<string, EHumanoidValue> BuildNameToValue()
        {
            var map = new Dictionary<string, EHumanoidValue>(StringComparer.Ordinal);
            foreach (MuscleEntry entry in Entries)
            {
                map.TryAdd(entry.CurveAttributeName, entry.Value);
                map.TryAdd(entry.HumanTraitName, entry.Value);
            }

            return new ReadOnlyDictionary<string, EHumanoidValue>(map);
        }
    }
}
