using System.Numerics;
using Newtonsoft.Json.Linq;

namespace XREngine.Components.Animation;

/// <summary>
/// Imports either the compact Unity avatar-profile sidecar or a schema-6 Unity
/// humanoid pose-audit report. Unity local bone coordinates are reflected into
/// XRENGINE's FBX local convention during import.
/// </summary>
public static class UnityHumanoidAvatarProfileImporter
{
    public static UnityHumanoidAvatarProfile Import(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        string fullPath = Path.GetFullPath(filePath);
        JObject root = JObject.Parse(File.ReadAllText(fullPath));

        var profile = new UnityHumanoidAvatarProfile
        {
            SchemaVersion = ReadInt(root, "SchemaVersion", UnityHumanoidAvatarProfile.CurrentSchemaVersion),
            Source = ReadString(root, "Source") ?? "UnityMecanim",
            AvatarName = ReadString(root, "AvatarName") ?? string.Empty,
            SourcePath = fullPath,
            HumanScale = ReadFloat(root, "HumanScale", ReadFloat(root, "AvatarHumanScale", 0.0f)),
            AvatarSettings = ReadAvatarSettings(root["AvatarSettings"] as JObject),
        };

        ReadNeutralPose(root, profile);
        ReadMuscleResponses(root, profile);

        if (profile.NeutralPoseBoneRotations.Count == 0)
            throw new InvalidDataException($"Unity humanoid avatar profile '{fullPath}' contains no neutral bone rotations.");
        if (profile.BoneResponses.Count == 0)
            throw new InvalidDataException($"Unity humanoid avatar profile '{fullPath}' contains no muscle responses.");
        if (!float.IsFinite(profile.HumanScale) || profile.HumanScale <= 0.0f)
            throw new InvalidDataException($"Unity humanoid avatar profile '{fullPath}' has an invalid human scale.");

        return profile;
    }

    private static UnityHumanoidAvatarDescription ReadAvatarSettings(JObject? settings)
        => new()
        {
            UpperArmTwist = ReadFloat(settings, "UpperArmTwist", 0.5f),
            LowerArmTwist = ReadFloat(settings, "LowerArmTwist", 0.5f),
            UpperLegTwist = ReadFloat(settings, "UpperLegTwist", 0.5f),
            LowerLegTwist = ReadFloat(settings, "LowerLegTwist", 0.5f),
            ArmStretch = ReadFloat(settings, "ArmStretch", 0.05f),
            LegStretch = ReadFloat(settings, "LegStretch", 0.05f),
            FeetSpacing = ReadFloat(settings, "FeetSpacing", 0.0f),
            HasTranslationDoF = ReadBool(settings, "HasTranslationDoF"),
        };

    private static void ReadNeutralPose(JObject root, UnityHumanoidAvatarProfile profile)
    {
        JArray? compactBones = root["NeutralPoseBoneRotations"] as JArray;
        if (compactBones is not null)
        {
            foreach (JObject bone in compactBones.OfType<JObject>())
                AddNeutralBone(profile, bone, "BindRelativeRotation");
            return;
        }

        JArray? auditBones = root["DefaultMusclePose"]?["Bones"] as JArray;
        if (auditBones is null)
            return;

        foreach (JObject bone in auditBones.OfType<JObject>())
            AddNeutralBone(profile, bone, "BindRelativeRotation");
    }

    private static void AddNeutralBone(
        UnityHumanoidAvatarProfile profile,
        JObject bone,
        string rotationPropertyName)
    {
        string? boneName = ReadString(bone, "Name");
        if (string.IsNullOrWhiteSpace(boneName) || bone[rotationPropertyName] is not JObject rotation)
            return;

        profile.NeutralPoseBoneRotations[boneName] = ConvertUnityLocalRotation(ReadQuaternion(rotation));
        if (bone["LocalPosition"] is JObject localPosition)
            profile.UnityNeutralBoneLocalPositions[boneName] = ReadVector3(localPosition);
    }

    private static void ReadMuscleResponses(JObject root, UnityHumanoidAvatarProfile profile)
    {
        var responsesByBone = new Dictionary<string, List<UnityHumanoidMuscleResponse>>(StringComparer.Ordinal);
        if (root["MuscleResponses"] is JArray compactResponses)
        {
            foreach (JObject response in compactResponses.OfType<JObject>())
            {
                AddMuscleResponse(
                    responsesByBone,
                    ReadString(response, "MuscleName"),
                    ReadString(response, "BoneName"),
                    response["NegativePoseDeltaFromNeutralRotation"] as JObject,
                    response["PositivePoseDeltaFromNeutralRotation"] as JObject);
            }
        }
        else if (root["MuscleProbes"] is JArray probes)
        {
            foreach (JObject probe in probes.OfType<JObject>())
            {
                string? muscleName = ReadString(probe, "Name");
                if (probe["Bones"] is not JArray bones)
                    continue;

                foreach (JObject bone in bones.OfType<JObject>())
                {
                    AddMuscleResponse(
                        responsesByBone,
                        muscleName,
                        ReadString(bone, "Name"),
                        bone["NegativePoseDeltaFromNeutralRotation"] as JObject,
                        bone["PositivePoseDeltaFromNeutralRotation"] as JObject);
                }
            }
        }

        foreach ((string boneName, List<UnityHumanoidMuscleResponse> responses) in responsesByBone)
        {
            responses.Sort((a, b) =>
                GetCompositionOrder(boneName, a.Muscle).CompareTo(GetCompositionOrder(boneName, b.Muscle)));
            profile.BoneResponses[boneName] = new UnityHumanoidBoneResponseProfile
            {
                BoneName = boneName,
                Responses = [.. responses],
            };
        }
    }

    private static int GetCompositionOrder(string boneName, EHumanoidValue muscle)
        => (boneName, muscle) switch
        {
            ("Spine", EHumanoidValue.SpineLeftRight) => 0,
            ("Spine", EHumanoidValue.SpineFrontBack) => 1,
            ("Spine", EHumanoidValue.SpineTwistLeftRight) => 2,
            ("Neck", EHumanoidValue.NeckTiltLeftRight) => 0,
            ("Neck", EHumanoidValue.NeckNodDownUp) => 1,
            ("Neck", EHumanoidValue.NeckTurnLeftRight) => 2,
            ("Head", EHumanoidValue.HeadTiltLeftRight) => 0,
            ("Head", EHumanoidValue.HeadNodDownUp) => 1,
            ("Head", EHumanoidValue.HeadTurnLeftRight) => 2,
            ("LeftShoulder", EHumanoidValue.LeftShoulderFrontBack) => 0,
            ("LeftShoulder", EHumanoidValue.LeftShoulderDownUp) => 1,
            ("RightShoulder", EHumanoidValue.RightShoulderFrontBack) => 0,
            ("RightShoulder", EHumanoidValue.RightShoulderDownUp) => 1,
            ("LeftUpperArm", EHumanoidValue.LeftArmFrontBack) => 0,
            ("LeftUpperArm", EHumanoidValue.LeftArmDownUp) => 1,
            ("LeftUpperArm", EHumanoidValue.LeftArmTwistInOut) => 2,
            ("RightUpperArm", EHumanoidValue.RightArmFrontBack) => 0,
            ("RightUpperArm", EHumanoidValue.RightArmDownUp) => 1,
            ("RightUpperArm", EHumanoidValue.RightArmTwistInOut) => 2,
            ("LeftHand", EHumanoidValue.LeftForearmTwistInOut) => 0,
            ("LeftHand", EHumanoidValue.LeftHandInOut) => 1,
            ("LeftHand", EHumanoidValue.LeftHandDownUp) => 2,
            ("RightHand", EHumanoidValue.RightForearmTwistInOut) => 0,
            ("RightHand", EHumanoidValue.RightHandInOut) => 1,
            ("RightHand", EHumanoidValue.RightHandDownUp) => 2,
            ("RightUpperLeg", EHumanoidValue.RightUpperLegInOut) => 0,
            ("RightUpperLeg", EHumanoidValue.RightUpperLegFrontBack) => 1,
            ("RightUpperLeg", EHumanoidValue.RightUpperLegTwistInOut) => 2,
            ("LeftFoot", EHumanoidValue.LeftLowerLegTwistInOut) => 0,
            ("LeftFoot", EHumanoidValue.LeftFootTwistInOut) => 1,
            ("LeftFoot", EHumanoidValue.LeftFootUpDown) => 2,
            _ => (int)muscle,
        };

    private static void AddMuscleResponse(
        Dictionary<string, List<UnityHumanoidMuscleResponse>> responsesByBone,
        string? muscleName,
        string? boneName,
        JObject? negative,
        JObject? positive)
    {
        if (string.IsNullOrWhiteSpace(muscleName)
            || string.IsNullOrWhiteSpace(boneName)
            || negative is null
            || positive is null
            || !UnityHumanoidMuscleMap.TryGetValue(muscleName, out EHumanoidValue muscle))
            return;

        if (!responsesByBone.TryGetValue(boneName, out List<UnityHumanoidMuscleResponse>? responses))
        {
            responses = [];
            responsesByBone.Add(boneName, responses);
        }

        responses.Add(new UnityHumanoidMuscleResponse
        {
            Muscle = muscle,
            NegativeRotation = ConvertUnityLocalRotation(ReadQuaternion(negative)),
            PositiveRotation = ConvertUnityLocalRotation(ReadQuaternion(positive)),
        });
    }

    private static Quaternion ReadQuaternion(JObject value)
    {
        Quaternion rotation = new(
            ReadFloat(value, "X", ReadFloat(value, "x", 0.0f)),
            ReadFloat(value, "Y", ReadFloat(value, "y", 0.0f)),
            ReadFloat(value, "Z", ReadFloat(value, "z", 0.0f)),
            ReadFloat(value, "W", ReadFloat(value, "w", 1.0f)));
        return rotation.LengthSquared() > 1e-12f ? Quaternion.Normalize(rotation) : Quaternion.Identity;
    }

    private static Vector3 ReadVector3(JObject value)
        => new(
            ReadFloat(value, "X", ReadFloat(value, "x", 0.0f)),
            ReadFloat(value, "Y", ReadFloat(value, "y", 0.0f)),
            ReadFloat(value, "Z", ReadFloat(value, "z", 0.0f)));

    private static Quaternion ConvertUnityLocalRotation(Quaternion unityRotation)
        => Quaternion.Normalize(new Quaternion(
            unityRotation.X,
            -unityRotation.Y,
            -unityRotation.Z,
            unityRotation.W));

    private static string? ReadString(JObject? value, string name)
        => value?[name]?.Value<string>();

    private static int ReadInt(JObject? value, string name, int fallback)
        => value?[name]?.Value<int?>() ?? fallback;

    private static float ReadFloat(JObject? value, string name, float fallback)
        => value?[name]?.Value<float?>() ?? fallback;

    private static bool ReadBool(JObject? value, string name)
        => value?[name]?.Value<bool?>() ?? false;
}
