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
            CalibrationClipName = ReadString(root, "CalibrationClipName") ?? string.Empty,
            AvatarSettings = ReadAvatarSettings(root["AvatarSettings"] as JObject),
            BodyAxes = ReadBodyAxes(root["BodyAxes"] as JObject),
        };

        ReadNeutralPose(root, profile);
        ReadMuscleResponses(root, profile);
        ReadCoupledMuscleCalibrations(root, profile);
        ReadAvatarRoles(root, profile);
        ReadTwistChains(root, profile);

        if (profile.NeutralPoseBoneRotations.Count == 0)
            throw new InvalidDataException($"Unity humanoid avatar profile '{fullPath}' contains no neutral bone rotations.");
        if (profile.BoneResponses.Count == 0)
            throw new InvalidDataException($"Unity humanoid avatar profile '{fullPath}' contains no muscle responses.");
        if (!float.IsFinite(profile.HumanScale) || profile.HumanScale <= 0.0f)
            throw new InvalidDataException($"Unity humanoid avatar profile '{fullPath}' has an invalid human scale.");
        if (!profile.BodyAxes.IsFiniteOrthonormal())
            throw new InvalidDataException($"Unity humanoid avatar profile '{fullPath}' has invalid body axes.");

        ValidateRequiredRoles(profile);
        profile.BuildDenseLookups();

        return profile;
    }

    private static UnityHumanoidBodyAxes ReadBodyAxes(JObject? axes)
    {
        if (axes is null)
            return new UnityHumanoidBodyAxes();

        return new UnityHumanoidBodyAxes
        {
            Right = NormalizeOrFallback(
                ConvertUnitySemanticVector(ReadVector3(axes["Right"] as JObject)),
                -Vector3.UnitX),
            Up = NormalizeOrFallback(
                ConvertUnitySemanticVector(ReadVector3(axes["Up"] as JObject)),
                Vector3.UnitY),
            Forward = NormalizeOrFallback(
                ConvertUnitySemanticVector(ReadVector3(axes["Forward"] as JObject)),
                Vector3.UnitZ),
        };
    }

    private static void ReadAvatarRoles(JObject root, UnityHumanoidAvatarProfile profile)
    {
        var seen = new bool[(int)EUnityHumanoidAvatarRole.Count];
        if (root["AvatarRoles"] is JArray roles)
        {
            foreach (JObject roleValue in roles.OfType<JObject>())
            {
                string? humanName = ReadString(roleValue, "HumanName") ?? ReadString(roleValue, "Role");
                if (humanName is null || !UnityHumanoidAvatarProfile.TryParseRole(humanName, out EUnityHumanoidAvatarRole role))
                    continue;

                int index = (int)role;
                if (seen[index])
                    throw new InvalidDataException($"Unity humanoid avatar profile '{profile.SourcePath}' maps role '{role}' more than once.");

                seen[index] = true;
                profile.Roles.Add(new UnityHumanoidAvatarRoleProfile
                {
                    Role = role,
                    HumanName = humanName,
                    TransformName = ReadString(roleValue, "TransformName") ?? string.Empty,
                    Required = ReadBool(roleValue, "Required"),
                });
            }
        }

        foreach (string boneName in profile.NeutralPoseBoneRotations.Keys)
        {
            if (!UnityHumanoidAvatarProfile.TryParseRole(boneName, out EUnityHumanoidAvatarRole role)
                || seen[(int)role])
                continue;

            seen[(int)role] = true;
            profile.Roles.Add(new UnityHumanoidAvatarRoleProfile
            {
                Role = role,
                HumanName = boneName,
                TransformName = boneName,
                Required = IsRequiredRole(role),
            });
        }
    }

    private static void ReadTwistChains(JObject root, UnityHumanoidAvatarProfile profile)
    {
        if (root["TwistChains"] is JArray chains)
        {
            foreach (JObject chain in chains.OfType<JObject>())
            {
                if (!TryReadRole(chain, "ProximalRole", out EUnityHumanoidAvatarRole proximal)
                    || !TryReadRole(chain, "DistalRole", out EUnityHumanoidAvatarRole distal)
                    || !TryReadRole(chain, "EndRole", out EUnityHumanoidAvatarRole end))
                    continue;

                profile.TwistChains.Add(new UnityHumanoidTwistChainProfile
                {
                    Name = ReadString(chain, "Name") ?? string.Empty,
                    ProximalRole = proximal,
                    DistalRole = distal,
                    EndRole = end,
                    ProximalDistribution = ReadFloat(chain, "ProximalDistribution", 0.5f),
                    DistalDistribution = ReadFloat(chain, "DistalDistribution", 0.5f),
                });
            }
        }

        if (profile.TwistChains.Count != 0)
            return;

        profile.TwistChains.Add(new UnityHumanoidTwistChainProfile
        {
            Name = "LeftArm",
            ProximalRole = EUnityHumanoidAvatarRole.LeftUpperArm,
            DistalRole = EUnityHumanoidAvatarRole.LeftLowerArm,
            EndRole = EUnityHumanoidAvatarRole.LeftHand,
            ProximalDistribution = profile.AvatarSettings.UpperArmTwist,
            DistalDistribution = profile.AvatarSettings.LowerArmTwist,
        });
        profile.TwistChains.Add(new UnityHumanoidTwistChainProfile
        {
            Name = "RightArm",
            ProximalRole = EUnityHumanoidAvatarRole.RightUpperArm,
            DistalRole = EUnityHumanoidAvatarRole.RightLowerArm,
            EndRole = EUnityHumanoidAvatarRole.RightHand,
            ProximalDistribution = profile.AvatarSettings.UpperArmTwist,
            DistalDistribution = profile.AvatarSettings.LowerArmTwist,
        });
        profile.TwistChains.Add(new UnityHumanoidTwistChainProfile
        {
            Name = "LeftLeg",
            ProximalRole = EUnityHumanoidAvatarRole.LeftUpperLeg,
            DistalRole = EUnityHumanoidAvatarRole.LeftLowerLeg,
            EndRole = EUnityHumanoidAvatarRole.LeftFoot,
            ProximalDistribution = profile.AvatarSettings.UpperLegTwist,
            DistalDistribution = profile.AvatarSettings.LowerLegTwist,
        });
        profile.TwistChains.Add(new UnityHumanoidTwistChainProfile
        {
            Name = "RightLeg",
            ProximalRole = EUnityHumanoidAvatarRole.RightUpperLeg,
            DistalRole = EUnityHumanoidAvatarRole.RightLowerLeg,
            EndRole = EUnityHumanoidAvatarRole.RightFoot,
            ProximalDistribution = profile.AvatarSettings.UpperLegTwist,
            DistalDistribution = profile.AvatarSettings.LowerLegTwist,
        });
    }

    private static void ValidateRequiredRoles(UnityHumanoidAvatarProfile profile)
    {
        var present = new bool[(int)EUnityHumanoidAvatarRole.Count];
        for (int i = 0; i < profile.Roles.Count; i++)
            present[(int)profile.Roles[i].Role] = true;

        for (int i = 0; i < present.Length; i++)
        {
            var role = (EUnityHumanoidAvatarRole)i;
            if (IsRequiredRole(role) && !present[i])
                throw new InvalidDataException($"Unity humanoid avatar profile '{profile.SourcePath}' is missing required role '{role}'.");
        }
    }

    private static bool IsRequiredRole(EUnityHumanoidAvatarRole role)
        => role is EUnityHumanoidAvatarRole.Hips
        or EUnityHumanoidAvatarRole.Spine
        or EUnityHumanoidAvatarRole.Head
        or EUnityHumanoidAvatarRole.LeftUpperArm
        or EUnityHumanoidAvatarRole.LeftLowerArm
        or EUnityHumanoidAvatarRole.LeftHand
        or EUnityHumanoidAvatarRole.RightUpperArm
        or EUnityHumanoidAvatarRole.RightLowerArm
        or EUnityHumanoidAvatarRole.RightHand
        or EUnityHumanoidAvatarRole.LeftUpperLeg
        or EUnityHumanoidAvatarRole.LeftLowerLeg
        or EUnityHumanoidAvatarRole.LeftFoot
        or EUnityHumanoidAvatarRole.RightUpperLeg
        or EUnityHumanoidAvatarRole.RightLowerLeg
        or EUnityHumanoidAvatarRole.RightFoot;

    private static bool TryReadRole(JObject value, string propertyName, out EUnityHumanoidAvatarRole role)
        => UnityHumanoidAvatarProfile.TryParseRole(ReadString(value, propertyName) ?? string.Empty, out role);

    private static void ReadCoupledMuscleCalibrations(JObject root, UnityHumanoidAvatarProfile profile)
    {
        if (root["CoupledMuscleCalibrations"] is not JArray calibrations)
            return;

        foreach (JObject calibration in calibrations.OfType<JObject>())
        {
            string? boneName = ReadString(calibration, "BoneName");
            if (string.IsNullOrWhiteSpace(boneName))
                continue;

            var model = new UnityHumanoidCoupledBoneModel
            {
                BoneName = boneName,
                Muscles = ReadMuscles(calibration),
                MaximumPolynomialDegree = ReadInt(calibration, "MaximumPolynomialDegree", 3),
                NegativeEndpointRotations = ReadQuaternionArray(calibration, "NegativeEndpointRotations"),
                PositiveEndpointRotations = ReadQuaternionArray(calibration, "PositiveEndpointRotations"),
                NegativeEndpointPositionDeltas = ReadPositionArray(calibration, "NegativeEndpointPositionDeltas"),
                PositiveEndpointPositionDeltas = ReadPositionArray(calibration, "PositiveEndpointPositionDeltas"),
                RotationResidualCoefficients = ReadCoefficientVectors(
                    calibration,
                    "XCoefficients",
                    "YCoefficients",
                    "ZCoefficients",
                    isPosition: false),
                PositionResidualCoefficients = ReadCoefficientVectors(
                    calibration,
                    "PositionXCoefficients",
                    "PositionYCoefficients",
                    "PositionZCoefficients",
                    isPosition: true),
                MeanAngularErrorDegrees = ReadFloat(calibration, "MeanAngularErrorDegrees", 0.0f),
                MaxAngularErrorDegrees = ReadFloat(calibration, "MaxAngularErrorDegrees", 0.0f),
                MeanPositionError = ReadFloat(calibration, "MeanPositionError", 0.0f),
                MaxPositionError = ReadFloat(calibration, "MaxPositionError", 0.0f),
                ProjectedRootYCoefficients = ReadFloatArray(calibration, "ProjectedRootYCoefficients"),
                ProjectedRootYZeroOffset = ReadFloat(calibration, "ProjectedRootYZeroOffset", 0.0f),
            };

            int exportedFeatureCount = ReadInt(calibration, "FeatureCount", model.ExpectedFeatureCount);
            if (!model.IsValid || exportedFeatureCount != model.ExpectedFeatureCount)
            {
                throw new InvalidDataException(
                    $"Unity humanoid avatar profile '{profile.SourcePath}' has an invalid coupled-muscle model for '{boneName}'.");
            }

            profile.CoupledBoneModels[boneName] = model;
        }
    }

    private static EHumanoidValue[] ReadMuscles(JObject calibration)
    {
        if (calibration["MuscleNames"] is not JArray names)
            return [];

        var muscles = new List<EHumanoidValue>(names.Count);
        foreach (JToken token in names)
        {
            string? name = token.Value<string>();
            if (name is not null && UnityHumanoidMuscleMap.TryGetValue(name, out EHumanoidValue muscle))
                muscles.Add(muscle);
        }
        return [.. muscles];
    }

    private static Quaternion[] ReadQuaternionArray(JObject value, string propertyName)
    {
        if (value[propertyName] is not JArray values)
            return [];

        var result = new Quaternion[values.Count];
        for (int i = 0; i < values.Count; i++)
            result[i] = values[i] is JObject rotation
                ? ConvertUnityLocalRotation(ReadQuaternion(rotation))
                : Quaternion.Identity;
        return result;
    }

    private static Vector3[] ReadPositionArray(JObject value, string propertyName)
    {
        if (value[propertyName] is not JArray values)
            return [];

        var result = new Vector3[values.Count];
        for (int i = 0; i < values.Count; i++)
            result[i] = values[i] is JObject position
                ? ConvertUnityLocalPositionDelta(ReadVector3(position))
                : Vector3.Zero;
        return result;
    }

    private static Vector3[] ReadCoefficientVectors(
        JObject value,
        string xPropertyName,
        string yPropertyName,
        string zPropertyName,
        bool isPosition)
    {
        if (value[xPropertyName] is not JArray xValues
            || value[yPropertyName] is not JArray yValues
            || value[zPropertyName] is not JArray zValues
            || xValues.Count != yValues.Count
            || xValues.Count != zValues.Count)
            return [];

        var result = new Vector3[xValues.Count];
        for (int i = 0; i < result.Length; i++)
        {
            Vector3 unityValue = new(
                xValues[i]!.Value<float>(),
                yValues[i]!.Value<float>(),
                zValues[i]!.Value<float>());
            result[i] = isPosition
                ? ConvertUnityLocalPositionDelta(unityValue)
                : ConvertUnityLocalRotationVector(unityValue);
        }
        return result;
    }

    private static float[] ReadFloatArray(JObject value, string propertyName)
    {
        if (value[propertyName] is not JArray values)
            return [];

        var result = new float[values.Count];
        for (int i = 0; i < values.Count; i++)
            result[i] = values[i]!.Value<float>();
        return result;
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
            profile.UnityNeutralBoneLocalPositions[boneName] = ConvertUnityLocalPosition(ReadVector3(localPosition));
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

    private static Vector3 ReadVector3(JObject? value)
        => new(
            ReadFloat(value, "X", ReadFloat(value, "x", 0.0f)),
            ReadFloat(value, "Y", ReadFloat(value, "y", 0.0f)),
            ReadFloat(value, "Z", ReadFloat(value, "z", 0.0f)));

    private static Vector3 ConvertUnitySemanticVector(Vector3 unityVector)
        => new(-unityVector.X, unityVector.Y, unityVector.Z);

    private static Vector3 NormalizeOrFallback(Vector3 value, Vector3 fallback)
        => float.IsFinite(value.X)
            && float.IsFinite(value.Y)
            && float.IsFinite(value.Z)
            && value.LengthSquared() > 1e-12f
                ? Vector3.Normalize(value)
                : fallback;

    private static Quaternion ConvertUnityLocalRotation(Quaternion unityRotation)
        => Quaternion.Normalize(new Quaternion(
            unityRotation.X,
            -unityRotation.Y,
            -unityRotation.Z,
            unityRotation.W));

    private static Vector3 ConvertUnityLocalRotationVector(Vector3 unityRotationVector)
        => new(unityRotationVector.X, -unityRotationVector.Y, -unityRotationVector.Z);

    private static Vector3 ConvertUnityLocalPosition(Vector3 unityPosition)
        => new(-unityPosition.X, unityPosition.Y, unityPosition.Z);

    private static Vector3 ConvertUnityLocalPositionDelta(Vector3 unityPositionDelta)
        => ConvertUnityLocalPosition(unityPositionDelta);

    private static string? ReadString(JObject? value, string name)
        => value?[name]?.Value<string>();

    private static int ReadInt(JObject? value, string name, int fallback)
        => value?[name]?.Value<int?>() ?? fallback;

    private static float ReadFloat(JObject? value, string name, float fallback)
        => value?[name]?.Value<float?>() ?? fallback;

    private static bool ReadBool(JObject? value, string name)
        => value?[name]?.Value<bool?>() ?? false;
}
