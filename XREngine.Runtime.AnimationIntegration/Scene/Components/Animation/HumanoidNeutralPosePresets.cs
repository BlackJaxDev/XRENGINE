using System;
using System.Collections.Generic;
using System.Numerics;

namespace XREngine.Components.Animation
{
    public enum EHumanoidNeutralPosePreset
    {
        None,
        UnityMecanim,
    }

    public static class HumanoidNeutralPosePresets
    {
        private static readonly IReadOnlyDictionary<string, Quaternion> Empty =
            new Dictionary<string, Quaternion>(0, StringComparer.Ordinal);

        // Populate this table from the Unity HumanoidFullPoseExporter output.
        // All entries use the original boneNeutralRotation (Unity local space),
        // NOT the unityToOpenGLRotation values. The engine's bind poses are already
        // in the correct space, so using neutral rotations maintains a consistent
        // parent-child rotation chain.
        // Keys should match Unity HumanBodyBones enum names.
        private static readonly IReadOnlyDictionary<string, Quaternion> UnityMecanimRotations =
            new Dictionary<string, Quaternion>(StringComparer.Ordinal)
            {
            { "Hips", Quaternion.Normalize(new Quaternion(0.707106709f, -5.5577253e-08f, -4.50044837e-08f, 0.707106948f)) },
            { "LeftUpperLeg", Quaternion.Normalize(new Quaternion(0.96636939f, -0.00854578521f, -0.00493180705f, 0.256968826f)) },
            { "RightUpperLeg", Quaternion.Normalize(new Quaternion(0.966370046f, 0.00850888994f, 0.00479289843f, 0.256970048f)) },
            { "LeftLowerLeg", Quaternion.Normalize(new Quaternion(0.706381559f, 0.000519764144f, -0.0138644539f, 0.707695365f)) },
            { "RightLowerLeg", Quaternion.Normalize(new Quaternion(0.706383586f, -0.000418393873f, 0.0137626491f, 0.707695365f)) },
            { "LeftFoot", Quaternion.Normalize(new Quaternion(-0.358331442f, -0.00196558167f, -0.0028612474f, 0.933588028f)) },
            { "RightFoot", Quaternion.Normalize(new Quaternion(-0.35833174f, 0.00196558191f, 0.00286132144f, 0.933587909f)) },
            { "Spine", Quaternion.Normalize(new Quaternion(-0.0227929503f, -0.000264644623f, -0.000274538994f, 0.999740183f)) },
            { "Chest", Quaternion.Normalize(new Quaternion(-0.0155844558f, 0.000254049926f, 0.00028464201f, 0.999878526f)) },
            { "Neck", Quaternion.Normalize(new Quaternion(0.0162689108f, 0f, 0f, 0.999867678f)) },
            { "Head", Quaternion.Normalize(new Quaternion(0.0221088082f, 0f, 1.49011612e-08f, 0.999755621f)) },
            { "LeftShoulder", Quaternion.Normalize(new Quaternion(0.610601187f, -0.462940216f, -0.499911487f, -0.403659672f)) },
            { "RightShoulder", Quaternion.Normalize(new Quaternion(0.610601008f, 0.462939769f, 0.499911785f, -0.403660029f)) },
            { "LeftUpperArm", Quaternion.Normalize(new Quaternion(-0.294541091f, 0.175574958f, 0.104280844f, 0.933565497f)) },
            { "RightUpperArm", Quaternion.Normalize(new Quaternion(-0.294541121f, -0.175574794f, -0.104281209f, 0.933565497f)) },
            { "LeftLowerArm", Quaternion.Normalize(new Quaternion(-0.461033821f, 0.00238569081f, 0.500023484f, 0.733088493f)) },
            { "RightLowerArm", Quaternion.Normalize(new Quaternion(-0.461033225f, -0.00238548941f, -0.50002408f, 0.733088434f)) },
            { "LeftHand", Quaternion.Normalize(new Quaternion(-0.0322547778f, 0.0347961895f, -0.0134811597f, -0.998782814f)) },
            { "RightHand", Quaternion.Normalize(new Quaternion(0.0322548114f, 0.0347962528f, -0.0134812035f, 0.998782814f)) },
            { "LeftEye", Quaternion.Normalize(new Quaternion(-0.707106709f, -1.85221154e-08f, -5.96046448e-08f, 0.707106888f)) },
            { "RightEye", Quaternion.Normalize(new Quaternion(-0.707106709f, -1.85221154e-08f, -5.96046448e-08f, 0.707106888f)) },
            { "Jaw", Quaternion.Normalize(new Quaternion(0.88632524f, 0.455163717f, -0.0778599903f, -0.0345179699f)) },
            { "LeftThumbProximal", Quaternion.Normalize(new Quaternion(0.467552453f, -0.234344974f, -0.174370408f, -0.833430934f)) },
            { "LeftThumbIntermediate", Quaternion.Normalize(new Quaternion(0.211843565f, 0.00889089797f, -0.096315071f, 0.972505391f)) },
            { "LeftThumbDistal", Quaternion.Normalize(new Quaternion(0.196464613f, -0.0339579433f, -0.114743605f, 0.973181605f)) },
            { "LeftIndexProximal", Quaternion.Normalize(new Quaternion(0.272980094f, -0.0404032841f, 0.171944618f, -0.945666194f)) },
            { "LeftIndexIntermediate", Quaternion.Normalize(new Quaternion(-0.225680172f, -0.0128414091f, -0.212901026f, 0.95056653f)) },
            { "LeftIndexDistal", Quaternion.Normalize(new Quaternion(-0.229670674f, -0.00902262330f, -0.210021168f, 0.950295329f)) },
            { "LeftMiddleProximal", Quaternion.Normalize(new Quaternion(0.259909719f, -0.0550650284f, 0.204470456f, -0.942128837f)) },
            { "LeftMiddleIntermediate", Quaternion.Normalize(new Quaternion(-0.226706281f, -0.00324137462f, -0.208271176f, 0.95142889f)) },
            { "LeftMiddleDistal", Quaternion.Normalize(new Quaternion(0.207640380f, 0.0310541466f, 0.183694601f, -0.960300684f)) },
            { "LeftRingProximal", Quaternion.Normalize(new Quaternion(0.219384238f, -0.0550778136f, 0.253593892f, -0.940493107f)) },
            { "LeftRingIntermediate", Quaternion.Normalize(new Quaternion(-0.230019286f, 0.00375354989f, -0.203174144f, 0.951733828f)) },
            { "LeftRingDistal", Quaternion.Normalize(new Quaternion(0.219594643f, 0.00391732110f, 0.196598336f, -0.955568910f)) },
            { "LeftLittleProximal", Quaternion.Normalize(new Quaternion(0.205665857f, -0.0514465868f, 0.251158327f, -0.944444001f)) },
            { "LeftLittleIntermediate", Quaternion.Normalize(new Quaternion(0.229531035f, 0.0130050024f, 0.162758365f, -0.959508300f)) },
            { "LeftLittleDistal", Quaternion.Normalize(new Quaternion(0.279452533f, -0.114302412f, 0.251392812f, -0.919588447f)) },
            { "RightThumbProximal", Quaternion.Normalize(new Quaternion(0.471061349f, 0.232775375f, 0.170707345f, -0.833532214f)) },
            { "RightThumbIntermediate", Quaternion.Normalize(new Quaternion(-0.204498231f, 0.0151288826f, -0.103319138f, -0.973281503f)) },
            { "RightThumbDistal", Quaternion.Normalize(new Quaternion(-0.193731472f, -0.0367096961f, -0.0659975260f, -0.978143573f)) },
            { "RightIndexProximal", Quaternion.Normalize(new Quaternion(0.282484114f, 0.0428318195f, -0.169403896f, -0.943223476f)) },
            { "RightIndexIntermediate", Quaternion.Normalize(new Quaternion(0.216359958f, -0.0112764984f, -0.220021665f, -0.951131821f)) },
            { "RightIndexDistal", Quaternion.Normalize(new Quaternion(0.217363030f, -0.0171470772f, -0.223419726f, -0.950022578f)) },
            { "RightMiddleProximal", Quaternion.Normalize(new Quaternion(0.262097359f, 0.0552427769f, -0.205517501f, -0.941284120f)) },
            { "RightMiddleIntermediate", Quaternion.Normalize(new Quaternion(0.224328011f, -0.00392608298f, -0.210699618f, -0.951455355f)) },
            { "RightMiddleDistal", Quaternion.Normalize(new Quaternion(0.207705155f, -0.0328301452f, -0.183669820f, -0.960232377f)) },
            { "RightRingProximal", Quaternion.Normalize(new Quaternion(0.218610138f, 0.0546575487f, -0.254490316f, -0.940455675f)) },
            { "RightRingIntermediate", Quaternion.Normalize(new Quaternion(0.218760639f, -0.00231274939f, -0.215137810f, -0.951763749f)) },
            { "RightRingDistal", Quaternion.Normalize(new Quaternion(0.243387282f, -0.00390459597f, -0.170421064f, -0.954832017f)) },
            { "RightLittleProximal", Quaternion.Normalize(new Quaternion(0.194079623f, 0.0456279777f, -0.263690174f, -0.943778932f)) },
            { "RightLittleIntermediate", Quaternion.Normalize(new Quaternion(0.223200098f, -0.0245021861f, -0.165732801f, -0.960267723f)) },
            { "RightLittleDistal", Quaternion.Normalize(new Quaternion(0.304357499f, 0.109852307f, -0.226735264f, -0.918634951f)) },
            };

        public static IReadOnlyDictionary<string, Quaternion> GetRotations(EHumanoidNeutralPosePreset preset)
            => preset switch
            {
                EHumanoidNeutralPosePreset.UnityMecanim => UnityMecanimRotations,
                _ => Empty,
            };

        public static int GetRotationCount(EHumanoidNeutralPosePreset preset)
            => GetRotations(preset).Count;
    }
}