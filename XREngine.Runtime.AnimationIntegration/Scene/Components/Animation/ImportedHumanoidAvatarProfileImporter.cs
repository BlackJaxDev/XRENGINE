using System.Numerics;
using Newtonsoft.Json.Linq;
using XREngine.Animation.Importers;

namespace XREngine.Components.Animation;

/// <summary>
/// Imports either the compact Unity avatar-profile sidecar or a schema-6 Unity
/// humanoid pose-audit report. Unity local bone coordinates are reflected into
/// XRENGINE's FBX local convention during import.
/// </summary>
public static class ImportedHumanoidAvatarProfileImporter
{
    public static ImportedHumanoidAvatarProfile Import(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        string fullPath = Path.GetFullPath(filePath);
        JObject root = JObject.Parse(File.ReadAllText(fullPath));

        var profile = new ImportedHumanoidAvatarProfile
        {
            SchemaVersion = ReadInt(root, "SchemaVersion", ImportedHumanoidAvatarProfile.CurrentSchemaVersion),
            Source = ReadString(root, "Source") ?? "UnityMecanim",
            AvatarName = ReadString(root, "AvatarName") ?? string.Empty,
            SourcePath = fullPath,
            HumanScale = ReadFloat(root, "HumanScale", ReadFloat(root, "AvatarHumanScale", 0.0f)),
            CalibrationClipName = ReadString(root, "CalibrationClipName") ?? string.Empty,
            CalibrationRootMotionSettings = ReadRootMotionSettings(
                root["CalibrationRootMotionSettings"] as JObject
                    ?? root["RootMotionSettings"] as JObject),
            RootAllocationFrame = ReadRootAllocationFrame(root["RootAllocationFrame"] as JObject),
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

    private static ImportedHumanoidBodyAxes ReadBodyAxes(JObject? axes)
    {
        if (axes is null)
            return new ImportedHumanoidBodyAxes();

        return new ImportedHumanoidBodyAxes
        {
            Right = NormalizeOrFallback(
                ConvertSourceSemanticVector(ReadVector3(axes["Right"] as JObject)),
                -Vector3.UnitX),
            Up = NormalizeOrFallback(
                ConvertSourceSemanticVector(ReadVector3(axes["Up"] as JObject)),
                Vector3.UnitY),
            Forward = NormalizeOrFallback(
                ConvertSourceSemanticVector(ReadVector3(axes["Forward"] as JObject)),
                Vector3.UnitZ),
        };
    }

    private static void ReadAvatarRoles(JObject root, ImportedHumanoidAvatarProfile profile)
    {
        var seen = new bool[(int)EHumanoidAvatarRole.Count];
        if (root["AvatarRoles"] is JArray roles)
        {
            foreach (JObject roleValue in roles.OfType<JObject>())
            {
                string? humanName = ReadString(roleValue, "HumanName") ?? ReadString(roleValue, "Role");
                if (humanName is null || !ImportedHumanoidAvatarProfile.TryParseRole(humanName, out EHumanoidAvatarRole role))
                    continue;

                int index = (int)role;
                if (seen[index])
                    throw new InvalidDataException($"Unity humanoid avatar profile '{profile.SourcePath}' maps role '{role}' more than once.");

                seen[index] = true;
                profile.Roles.Add(new ImportedHumanoidAvatarRoleProfile
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
            if (!ImportedHumanoidAvatarProfile.TryParseRole(boneName, out EHumanoidAvatarRole role)
                || seen[(int)role])
                continue;

            seen[(int)role] = true;
            profile.Roles.Add(new ImportedHumanoidAvatarRoleProfile
            {
                Role = role,
                HumanName = boneName,
                TransformName = boneName,
                Required = IsRequiredRole(role),
            });
        }
    }

    private static void ReadTwistChains(JObject root, ImportedHumanoidAvatarProfile profile)
    {
        if (root["TwistChains"] is JArray chains)
        {
            foreach (JObject chain in chains.OfType<JObject>())
            {
                if (!TryReadRole(chain, "ProximalRole", out EHumanoidAvatarRole proximal)
                    || !TryReadRole(chain, "DistalRole", out EHumanoidAvatarRole distal)
                    || !TryReadRole(chain, "EndRole", out EHumanoidAvatarRole end))
                    continue;

                profile.TwistChains.Add(new ImportedHumanoidTwistChainProfile
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

        profile.TwistChains.Add(new ImportedHumanoidTwistChainProfile
        {
            Name = "LeftArm",
            ProximalRole = EHumanoidAvatarRole.LeftUpperArm,
            DistalRole = EHumanoidAvatarRole.LeftLowerArm,
            EndRole = EHumanoidAvatarRole.LeftHand,
            ProximalDistribution = profile.AvatarSettings.UpperArmTwist,
            DistalDistribution = profile.AvatarSettings.LowerArmTwist,
        });
        profile.TwistChains.Add(new ImportedHumanoidTwistChainProfile
        {
            Name = "RightArm",
            ProximalRole = EHumanoidAvatarRole.RightUpperArm,
            DistalRole = EHumanoidAvatarRole.RightLowerArm,
            EndRole = EHumanoidAvatarRole.RightHand,
            ProximalDistribution = profile.AvatarSettings.UpperArmTwist,
            DistalDistribution = profile.AvatarSettings.LowerArmTwist,
        });
        profile.TwistChains.Add(new ImportedHumanoidTwistChainProfile
        {
            Name = "LeftLeg",
            ProximalRole = EHumanoidAvatarRole.LeftUpperLeg,
            DistalRole = EHumanoidAvatarRole.LeftLowerLeg,
            EndRole = EHumanoidAvatarRole.LeftFoot,
            ProximalDistribution = profile.AvatarSettings.UpperLegTwist,
            DistalDistribution = profile.AvatarSettings.LowerLegTwist,
        });
        profile.TwistChains.Add(new ImportedHumanoidTwistChainProfile
        {
            Name = "RightLeg",
            ProximalRole = EHumanoidAvatarRole.RightUpperLeg,
            DistalRole = EHumanoidAvatarRole.RightLowerLeg,
            EndRole = EHumanoidAvatarRole.RightFoot,
            ProximalDistribution = profile.AvatarSettings.UpperLegTwist,
            DistalDistribution = profile.AvatarSettings.LowerLegTwist,
        });
    }

    private static void ValidateRequiredRoles(ImportedHumanoidAvatarProfile profile)
    {
        var present = new bool[(int)EHumanoidAvatarRole.Count];
        for (int i = 0; i < profile.Roles.Count; i++)
            present[(int)profile.Roles[i].Role] = true;

        for (int i = 0; i < present.Length; i++)
        {
            var role = (EHumanoidAvatarRole)i;
            if (IsRequiredRole(role) && !present[i])
                throw new InvalidDataException($"Unity humanoid avatar profile '{profile.SourcePath}' is missing required role '{role}'.");
        }
    }

    private static bool IsRequiredRole(EHumanoidAvatarRole role)
        => role is EHumanoidAvatarRole.Hips
        or EHumanoidAvatarRole.Spine
        or EHumanoidAvatarRole.Head
        or EHumanoidAvatarRole.LeftUpperArm
        or EHumanoidAvatarRole.LeftLowerArm
        or EHumanoidAvatarRole.LeftHand
        or EHumanoidAvatarRole.RightUpperArm
        or EHumanoidAvatarRole.RightLowerArm
        or EHumanoidAvatarRole.RightHand
        or EHumanoidAvatarRole.LeftUpperLeg
        or EHumanoidAvatarRole.LeftLowerLeg
        or EHumanoidAvatarRole.LeftFoot
        or EHumanoidAvatarRole.RightUpperLeg
        or EHumanoidAvatarRole.RightLowerLeg
        or EHumanoidAvatarRole.RightFoot;

    private static bool TryReadRole(JObject value, string propertyName, out EHumanoidAvatarRole role)
        => ImportedHumanoidAvatarProfile.TryParseRole(ReadString(value, propertyName) ?? string.Empty, out role);

    private static void ReadCoupledMuscleCalibrations(JObject root, ImportedHumanoidAvatarProfile profile)
    {
        if (root["CoupledMuscleCalibrations"] is not JArray calibrations)
            return;

        foreach (JObject calibration in calibrations.OfType<JObject>())
        {
            string? boneName = ReadString(calibration, "BoneName");
            if (string.IsNullOrWhiteSpace(boneName))
                continue;

            var model = new ImportedHumanoidCoupledBoneModel
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
            if (name is not null && ImportedHumanoidMuscleMap.TryGetValue(name, out EHumanoidValue muscle))
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
                ? ConvertSourceLocalRotation(ReadQuaternion(rotation))
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
                ? ConvertSourceLocalPositionDelta(ReadVector3(position))
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
            Vector3 sourceValue = new(
                xValues[i]!.Value<float>(),
                yValues[i]!.Value<float>(),
                zValues[i]!.Value<float>());
            result[i] = isPosition
                ? ConvertSourceLocalPositionDelta(sourceValue)
                : ConvertSourceLocalRotationVector(sourceValue);
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

    private static ImportedHumanoidAvatarDescription ReadAvatarSettings(JObject? settings)
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

    private static ImportedHumanoidClipRootMotionSettings? ReadRootMotionSettings(JObject? settings)
    {
        if (settings is null)
            return null;

        return new ImportedHumanoidClipRootMotionSettings
        {
            StartTime = ReadFloat(settings, "StartTime", 0.0f),
            StopTime = ReadFloat(settings, "StopTime", 0.0f),
            OrientationOffsetY = ReadFloat(settings, "OrientationOffsetY", 0.0f),
            Level = ReadFloat(settings, "Level", 0.0f),
            CycleOffset = ReadFloat(settings, "CycleOffset", 0.0f),
            LoopTime = ReadBool(settings, "LoopTime"),
            LoopPose = ReadBool(settings, "LoopPose") || ReadBool(settings, "LoopBlend"),
            BakeOrientationIntoPose = ReadBool(settings, "BakeOrientationIntoPose"),
            BakePositionYIntoPose = ReadBool(settings, "BakePositionYIntoPose"),
            BakePositionXZIntoPose = ReadBool(settings, "BakePositionXZIntoPose"),
            KeepOriginalOrientation = ReadBool(settings, "KeepOriginalOrientation"),
            KeepOriginalPositionY = ReadBool(settings, "KeepOriginalPositionY"),
            KeepOriginalPositionXZ = ReadBool(settings, "KeepOriginalPositionXZ"),
            HeightFromFeet = ReadBool(settings, "HeightFromFeet"),
            Mirror = ReadBool(settings, "Mirror"),
        };
    }

    private static ImportedHumanoidRootAllocationFrame? ReadRootAllocationFrame(JObject? frame)
    {
        if (frame is null)
            return null;

        return new ImportedHumanoidRootAllocationFrame
        {
            HipsParentPositionInAnimatorRoot = ConvertSourceLocalPosition(
                ReadVector3(frame["HipsParentPositionInAnimatorRoot"] as JObject)),
            HipsParentRotationInAnimatorRoot = ConvertSourceLocalRotation(
                ReadQuaternion(frame["HipsParentRotationInAnimatorRoot"] as JObject ?? new JObject())),
            HipsParentScaleInAnimatorRoot = ReadVector3WithFallback(
                frame["HipsParentScaleInAnimatorRoot"] as JObject,
                Vector3.One),
        };
    }

    private static void ReadNeutralPose(JObject root, ImportedHumanoidAvatarProfile profile)
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
        ImportedHumanoidAvatarProfile profile,
        JObject bone,
        string rotationPropertyName)
    {
        string? boneName = ReadString(bone, "Name");
        if (string.IsNullOrWhiteSpace(boneName) || bone[rotationPropertyName] is not JObject rotation)
            return;

        profile.NeutralPoseBoneRotations[boneName] = ConvertSourceLocalRotation(ReadQuaternion(rotation));
        if (bone["LocalPosition"] is JObject localPosition)
            profile.ImportedNeutralBoneLocalPositions[boneName] = ConvertSourceLocalPosition(ReadVector3(localPosition));
    }

    private static void ReadMuscleResponses(JObject root, ImportedHumanoidAvatarProfile profile)
    {
        var responsesByBone = new Dictionary<string, List<ImportedHumanoidMuscleResponse>>(StringComparer.Ordinal);
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

        foreach ((string boneName, List<ImportedHumanoidMuscleResponse> responses) in responsesByBone)
        {
            responses.Sort((a, b) =>
                GetCompositionOrder(boneName, a.Muscle).CompareTo(GetCompositionOrder(boneName, b.Muscle)));
            profile.BoneResponses[boneName] = new ImportedHumanoidBoneResponseProfile
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
        Dictionary<string, List<ImportedHumanoidMuscleResponse>> responsesByBone,
        string? muscleName,
        string? boneName,
        JObject? negative,
        JObject? positive)
    {
        if (string.IsNullOrWhiteSpace(muscleName)
            || string.IsNullOrWhiteSpace(boneName)
            || negative is null
            || positive is null
            || !ImportedHumanoidMuscleMap.TryGetValue(muscleName, out EHumanoidValue muscle))
            return;

        if (!responsesByBone.TryGetValue(boneName, out List<ImportedHumanoidMuscleResponse>? responses))
        {
            responses = [];
            responsesByBone.Add(boneName, responses);
        }

        responses.Add(new ImportedHumanoidMuscleResponse
        {
            Muscle = muscle,
            NegativeRotation = ConvertSourceLocalRotation(ReadQuaternion(negative)),
            PositiveRotation = ConvertSourceLocalRotation(ReadQuaternion(positive)),
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

    private static Vector3 ReadVector3WithFallback(JObject? value, Vector3 fallback)
        => value is null
            ? fallback
            : new Vector3(
                ReadFloat(value, "X", ReadFloat(value, "x", fallback.X)),
                ReadFloat(value, "Y", ReadFloat(value, "y", fallback.Y)),
                ReadFloat(value, "Z", ReadFloat(value, "z", fallback.Z)));

    private static Vector3 ConvertSourceSemanticVector(Vector3 sourceVector)
        => new(-sourceVector.X, sourceVector.Y, sourceVector.Z);

    private static Vector3 NormalizeOrFallback(Vector3 value, Vector3 fallback)
        => float.IsFinite(value.X)
            && float.IsFinite(value.Y)
            && float.IsFinite(value.Z)
            && value.LengthSquared() > 1e-12f
                ? Vector3.Normalize(value)
                : fallback;

    private static Quaternion ConvertSourceLocalRotation(Quaternion sourceRotation)
        => Quaternion.Normalize(new Quaternion(
            sourceRotation.X,
            -sourceRotation.Y,
            -sourceRotation.Z,
            sourceRotation.W));

    private static Vector3 ConvertSourceLocalRotationVector(Vector3 sourceRotationVector)
        => new(sourceRotationVector.X, -sourceRotationVector.Y, -sourceRotationVector.Z);

    private static Vector3 ConvertSourceLocalPosition(Vector3 sourcePosition)
        => new(-sourcePosition.X, sourcePosition.Y, sourcePosition.Z);

    private static Vector3 ConvertSourceLocalPositionDelta(Vector3 sourcePositionDelta)
        => ConvertSourceLocalPosition(sourcePositionDelta);

    private static string? ReadString(JObject? value, string name)
        => value?[name]?.Value<string>();

    private static int ReadInt(JObject? value, string name, int fallback)
        => value?[name]?.Value<int?>() ?? fallback;

    private static float ReadFloat(JObject? value, string name, float fallback)
        => value?[name]?.Value<float?>() ?? fallback;

    private static bool ReadBool(JObject? value, string name)
        => value?[name]?.Value<bool?>() ?? false;
}
