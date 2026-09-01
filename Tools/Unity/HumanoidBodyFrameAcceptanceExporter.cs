#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Batch-only Unity reference recorder for humanoid Body-frame behavior.
/// Invoke with <c>-executeMethod HumanoidBodyFrameAcceptanceExporter.ExportBatch</c>,
/// <c>-bodyFrameModel Assets/path/to/model.fbx</c>, and <c>-bodyFrameOutput absolute/output.json</c>.
/// </summary>
public static class HumanoidBodyFrameAcceptanceExporter
{
    private const int SchemaVersion = 1;

    /// <summary>
    /// Exports a public-API Mecanim reference record for the requested imported humanoid and a procedural avatar.
    /// </summary>
    public static void ExportBatch()
    {
        string modelArgument = RequireArgument("-bodyFrameModel");
        string outputPath = RequireArgument("-bodyFrameOutput");
        bool geometryMode = string.Equals(OptionalArgument("-bodyFrameCaptureMode"), "geometry", StringComparison.OrdinalIgnoreCase);
        string assetPath = ToAssetPath(modelArgument, out string sourceFullPath);
        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
        if (prefab == null)
            throw new InvalidOperationException($"No model GameObject could be loaded from '{assetPath}'.");

        var report = new BatchReport
        {
            UnityVersion = Application.unityVersion,
            SourceModelPath = sourceFullPath,
            SourceModelSha256 = ComputeSha256(sourceFullPath),
            CaptureMode = geometryMode ? "geometry" : "full",
            Imported = geometryMode ? new AvatarCapture { Kind = "not-captured-in-geometry-mode" } : CaptureImported(prefab, assetPath),
            Procedural = geometryMode ? new AvatarCapture { Kind = "not-captured-in-geometry-mode" } : CaptureProcedural(false),
            ProceduralTranslationDof = geometryMode ? new AvatarCapture { Kind = "not-captured-in-geometry-mode" } : CaptureProcedural(true),
        };
        if (geometryMode)
            CaptureGeometryVariants(report.GeometryVariants);

        string directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        File.WriteAllText(outputPath, JsonUtility.ToJson(report, true));
        Debug.Log($"Humanoid Body-frame acceptance export complete: {outputPath}");
    }

    private static AvatarCapture CaptureImported(GameObject prefab, string assetPath)
    {
        ModelImporter importer = AssetImporter.GetAtPath(assetPath) as ModelImporter;
        if (importer == null || importer.animationType != ModelImporterAnimationType.Human)
            throw new InvalidOperationException($"'{assetPath}' must be imported as a Humanoid model.");

        GameObject instance = UnityEngine.Object.Instantiate(prefab);
        instance.name = "ImportedHumanoidAcceptanceInstance";
        try
        {
            Animator animator = instance.GetComponentInChildren<Animator>();
            if (animator == null || animator.avatar == null || !animator.avatar.isHuman)
                throw new InvalidOperationException("Imported model has no valid humanoid Animator/Avatar.");
            return CaptureAvatar("imported", animator, animator.avatar, true);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(instance);
        }
    }

    private static AvatarCapture CaptureProcedural(bool hasTranslationDof)
    {
        return CaptureProcedural(new ProceduralGeometry { VariantTag = hasTranslationDof ? "procedural_hasTranslationDoF_true" : "procedural_hasTranslationDoF_false", HasTranslationDof = hasTranslationDof }, false);
    }

    private static AvatarCapture CaptureProcedural(ProceduralGeometry geometry, bool compact)
    {
        GameObject root = BuildProceduralRig(geometry, out Avatar avatar);
        try
        {
            var animator = root.AddComponent<Animator>();
            animator.avatar = avatar;
            AvatarCapture capture = CaptureAvatar("procedural", animator, avatar, !compact, compact);
            capture.VariantTag = geometry.VariantTag;
            return capture;
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(avatar);
            UnityEngine.Object.DestroyImmediate(root);
        }
    }

    private static void CaptureGeometryVariants(List<AvatarCapture> variants)
    {
        variants.Add(CaptureProcedural(new ProceduralGeometry { VariantTag = "geometry_baseline" }, true));
        variants.Add(CaptureProcedural(new ProceduralGeometry { VariantTag = "geometry_both_upper_arms_plus_0_05", UpperArmLengthDelta = 0.05f }, true));
        variants.Add(CaptureProcedural(new ProceduralGeometry { VariantTag = "geometry_both_forearms_plus_0_05", ForearmLengthDelta = 0.05f }, true));
        variants.Add(CaptureProcedural(new ProceduralGeometry { VariantTag = "geometry_upper_chest_strong_subdivision_mapped", UpperChestPositionY = 0.08f, NeckPositionY = 0.25f }, true));
        variants.Add(CaptureProcedural(new ProceduralGeometry { VariantTag = "geometry_upper_chest_strong_subdivision_unmapped", UpperChestPositionY = 0.08f, NeckPositionY = 0.25f, MapUpperChest = false }, true));
        variants.Add(CaptureProcedural(new ProceduralGeometry { VariantTag = "geometry_toes_unmapped", MapToes = false }, true));
        variants.Add(CaptureProcedural(new ProceduralGeometry
        {
            VariantTag = "geometry_heldout_asymmetric_proportions_full_mapping",
            LocalPositionDeltas = new Dictionary<HumanBodyBones, Vector3>
            {
                [HumanBodyBones.Spine] = new(0.0f, 0.04f, 0.0f),
                [HumanBodyBones.LeftUpperLeg] = new(0.025f, 0.05f, 0.0f), [HumanBodyBones.RightUpperLeg] = new(0.035f, -0.07f, 0.0f),
                [HumanBodyBones.LeftLowerLeg] = new(0.0f, -0.05f, 0.0f), [HumanBodyBones.RightLowerLeg] = new(0.0f, 0.07f, 0.0f),
                [HumanBodyBones.UpperChest] = new(0.0f, -0.08f, 0.0f), [HumanBodyBones.Neck] = new(0.0f, 0.09f, 0.0f),
                [HumanBodyBones.LeftShoulder] = new(-0.04f, -0.04f, 0.03f), [HumanBodyBones.RightShoulder] = new(-0.03f, 0.05f, -0.02f),
                [HumanBodyBones.LeftUpperArm] = new(-0.04f, 0.0f, 0.0f), [HumanBodyBones.RightUpperArm] = new(-0.04f, 0.0f, 0.0f),
                [HumanBodyBones.LeftLowerArm] = new(0.07f, 0.0f, 0.0f), [HumanBodyBones.RightLowerArm] = new(0.07f, 0.0f, 0.0f),
                [HumanBodyBones.LeftHand] = new(-0.07f, 0.0f, 0.0f), [HumanBodyBones.RightHand] = new(-0.06f, 0.0f, 0.0f),
            },
        }, true));
    }

    private static AvatarCapture CaptureAvatar(string kind, Animator animator, Avatar avatar, bool includeRawAvatarProperties, bool compact = false)
    {
        if (!avatar.isValid || !avatar.isHuman)
            throw new InvalidOperationException($"{kind} AvatarBuilder/import result is not a valid humanoid Avatar.");

        animator.applyRootMotion = false;
        animator.runtimeAnimatorController = null;
        animator.enabled = true;
        animator.Rebind();
        animator.Update(0.0f);

        var capture = new AvatarCapture
        {
            Kind = kind,
            AvatarName = avatar.name,
            AvatarIsValid = avatar.isValid,
            AvatarIsHuman = avatar.isHuman,
            HumanScale = animator.humanScale,
            HumanDescription = HumanDescriptionRecord.From(avatar.humanDescription),
            Hierarchy = CaptureHierarchy(animator.transform),
            Roles = CaptureRoles(animator),
            MuscleRanges = CaptureMuscleRanges(),
            BindMetrics = CaptureBindMetrics(animator),
        };
        using (var handler = new HumanPoseHandler(avatar, animator.transform))
        {
            HumanPose current = default;
            handler.GetHumanPose(ref current);
            capture.InitialRestGet = HumanPoseRecord.From(current);
            HumanPose neutral = ClonePose(current);
            Array.Clear(neutral.muscles, 0, neutral.muscles.Length);
            handler.SetHumanPose(ref neutral);
            capture.Neutral = CapturePose("neutral", handler, neutral, animator);
            CaptureRestHierarchyGetProbes(capture.HierarchyGetProbes, handler, neutral, animator);

            CaptureNamedSequence(capture.Poses, handler, neutral, animator, compact ? "geometry" : "before_sweep", false);

            if (!compact)
            {
                for (int index = 0; index < HumanTrait.MuscleCount; index++)
                {
                    capture.Poses.Add(CaptureMuscleProbe(handler, neutral, animator, index, -0.5f));
                    capture.Poses.Add(CaptureMuscleProbe(handler, neutral, animator, index, 0.5f));
                }
                CaptureNamedSequence(capture.Poses, handler, neutral, animator, "after_sweep_reverse", true);
            }
            handler.SetHumanPose(ref current);
        }

        if (!compact)
        using (var freshHandler = new HumanPoseHandler(avatar, animator.transform))
        {
            HumanPose freshRest = default;
            freshHandler.GetHumanPose(ref freshRest);
            capture.FreshHandlerRestGet = HumanPoseRecord.From(freshRest);
            HumanPose freshNeutral = ClonePose(freshRest);
            Array.Clear(freshNeutral.muscles, 0, freshNeutral.muscles.Length);
            freshHandler.SetHumanPose(ref freshNeutral);
            capture.FreshHandlerNeutral = CapturePose("fresh_handler_neutral", freshHandler, freshNeutral, animator);
            CaptureNamedSequence(capture.Poses, freshHandler, freshNeutral, animator, "fresh_handler", false);
        }

        if (includeRawAvatarProperties)
            capture.SerializedAvatarProperties = CaptureSerializedAvatarProperties(avatar);
        return capture;
    }

    private static void CaptureNamedSequence(List<PoseCapture> poses, HumanPoseHandler handler, HumanPose neutral, Animator animator, string suffix, bool reverse)
    {
        var requests = new List<NamedPoseRequest>
        {
            new NamedPoseRequest("asymmetric", ResolveMuscles(("Left Arm Down-Up", 0.5f), ("Right Arm Down-Up", -0.35f), ("Left Upper Leg Front-Back", 0.4f), ("Right Upper Leg Front-Back", -0.45f)), Quaternion.identity),
            new NamedPoseRequest("combined", ResolveMuscles(("Spine Front-Back", 0.35f), ("Chest Twist Left-Right", -0.3f), ("Left Arm Down-Up", 0.5f), ("Right Arm Down-Up", -0.5f), ("Left Upper Leg Front-Back", 0.25f), ("Right Upper Leg Front-Back", -0.25f)), Quaternion.identity),
            new NamedPoseRequest("body_yaw_pitch", Array.Empty<MuscleValue>(), Quaternion.Euler(17.0f, 31.0f, 0.0f)),
            new NamedPoseRequest("combined_body_yaw_pitch", ResolveMuscles(("Spine Front-Back", 0.35f), ("Left Arm Down-Up", 0.5f), ("Right Upper Leg Front-Back", -0.25f)), Quaternion.Euler(17.0f, 31.0f, 0.0f)),
        };
        if (reverse)
            requests.Reverse();
        for (int index = 0; index < requests.Count; index++)
        {
            NamedPoseRequest request = requests[index];
            poses.Add(CaptureNamedPose(handler, neutral, animator, $"{request.Name}_{suffix}", request.Muscles, Vector3.zero, request.BodyRotation));
        }
    }

    private static BindMetricsRecord CaptureBindMetrics(Animator animator)
    {
        Transform hips = animator.GetBoneTransform(HumanBodyBones.Hips);
        Transform leftFoot = animator.GetBoneTransform(HumanBodyBones.LeftFoot);
        Transform rightFoot = animator.GetBoneTransform(HumanBodyBones.RightFoot);
        return new BindMetricsRecord
        {
            RootLocalPosition = Vector3Record.From(animator.transform.localPosition),
            RootLocalRotation = QuaternionRecord.From(animator.transform.localRotation),
            RootLocalScale = Vector3Record.From(animator.transform.localScale),
            HipsToLeftFootWorldDistance = hips != null && leftFoot != null ? Vector3.Distance(hips.position, leftFoot.position) : float.NaN,
            HipsToRightFootWorldDistance = hips != null && rightFoot != null ? Vector3.Distance(hips.position, rightFoot.position) : float.NaN,
        };
    }

    private static void CaptureRestHierarchyGetProbes(List<HierarchyGetProbe> probes, HumanPoseHandler handler, HumanPose neutral, Animator animator)
    {
        HumanBodyBones[] selectedBones =
        {
            HumanBodyBones.Hips,
            HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot, HumanBodyBones.LeftToes,
            HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot, HumanBodyBones.RightToes,
            HumanBodyBones.Spine, HumanBodyBones.Chest, HumanBodyBones.UpperChest, HumanBodyBones.Neck, HumanBodyBones.Head,
            HumanBodyBones.LeftShoulder, HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand,
            HumanBodyBones.RightShoulder, HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand,
        };
        foreach (HumanBodyBones bone in selectedBones)
        {
            Transform target = animator.GetBoneTransform(bone);
            if (target == null)
                continue;
            handler.SetHumanPose(ref neutral);
            List<TransformSnapshot> restSnapshot = CaptureTransformSnapshots(animator.transform);
            List<WorldTransformSnapshot> descendantWorldSnapshot = CaptureDescendantWorldSnapshots(animator.transform, target);
            try
            {
                Vector3 delta = new Vector3(0.01f, 0.0f, 0.0f);
                target.position += delta;
                RestoreWorldSnapshots(descendantWorldSnapshot);
                HumanPose readback = default;
                handler.GetHumanPose(ref readback);
                probes.Add(new HierarchyGetProbe
                {
                    Role = bone.ToString(),
                    WorldTranslation = Vector3Record.From(delta),
                    Readback = HumanPoseRecord.From(readback),
                    HierarchyAfterGet = CaptureHierarchy(animator.transform),
                });
            }
            finally
            {
                RestoreTransformSnapshots(restSnapshot);
                handler.SetHumanPose(ref neutral);
            }
        }
    }

    private static List<TransformSnapshot> CaptureTransformSnapshots(Transform root)
    {
        var result = new List<TransformSnapshot>();
        AddTransformSnapshots(root, result);
        return result;
    }

    private static void AddTransformSnapshots(Transform transform, List<TransformSnapshot> result)
    {
        result.Add(new TransformSnapshot(transform));
        for (int index = 0; index < transform.childCount; index++)
            AddTransformSnapshots(transform.GetChild(index), result);
    }

    private static List<WorldTransformSnapshot> CaptureDescendantWorldSnapshots(Transform root, Transform ancestor)
    {
        var result = new List<WorldTransformSnapshot>();
        AddDescendantWorldSnapshots(root, ancestor, result);
        return result;
    }

    private static void AddDescendantWorldSnapshots(Transform transform, Transform ancestor, List<WorldTransformSnapshot> result)
    {
        if (transform != ancestor && transform.IsChildOf(ancestor))
            result.Add(new WorldTransformSnapshot(transform));
        for (int index = 0; index < transform.childCount; index++)
            AddDescendantWorldSnapshots(transform.GetChild(index), ancestor, result);
    }

    private static void RestoreTransformSnapshots(List<TransformSnapshot> snapshots)
    {
        for (int index = snapshots.Count - 1; index >= 0; index--)
            snapshots[index].Restore();
    }

    private static void RestoreWorldSnapshots(List<WorldTransformSnapshot> snapshots)
    {
        for (int index = 0; index < snapshots.Count; index++)
            snapshots[index].Restore();
    }

    private static PoseCapture CaptureMuscleProbe(HumanPoseHandler handler, HumanPose neutral, Animator animator, int index, float amount)
    {
        return CaptureNamedPose(handler, neutral, animator, $"{HumanTrait.MuscleName[index]}_{amount.ToString(CultureInfo.InvariantCulture)}", new[] { new MuscleValue(index, amount) }, Vector3.zero, Quaternion.identity);
    }

    private static MuscleValue[] ResolveMuscles(params (string Name, float Value)[] controls)
    {
        var values = new List<MuscleValue>(controls.Length);
        for (int controlIndex = 0; controlIndex < controls.Length; controlIndex++)
        {
            bool found = false;
            for (int muscleIndex = 0; muscleIndex < HumanTrait.MuscleCount; muscleIndex++)
                if (string.Equals(HumanTrait.MuscleName[muscleIndex], controls[controlIndex].Name, StringComparison.Ordinal))
                {
                    values.Add(new MuscleValue(muscleIndex, controls[controlIndex].Value));
                    found = true;
                    break;
                }
            if (!found)
                throw new InvalidOperationException($"Unity HumanTrait does not expose the required muscle '{controls[controlIndex].Name}'.");
        }
        return values.ToArray();
    }

    private static PoseCapture CaptureNamedPose(HumanPoseHandler handler, HumanPose neutral, Animator animator, string name, MuscleValue[] values, Vector3 bodyPositionOffset, Quaternion bodyRotation)
    {
        HumanPose requested = ClonePose(neutral);
        requested.bodyPosition += bodyPositionOffset;
        requested.bodyRotation = bodyRotation * requested.bodyRotation;
        for (int i = 0; i < values.Length; i++)
            if (values[i].Index >= 0 && values[i].Index < requested.muscles.Length)
                requested.muscles[values[i].Index] = values[i].Value;
        handler.SetHumanPose(ref requested);
        return CapturePose(name, handler, requested, animator);
    }

    private static PoseCapture CapturePose(string name, HumanPoseHandler handler, HumanPose requested, Animator animator)
    {
        HumanPose readback = default;
        handler.GetHumanPose(ref readback);
        return new PoseCapture
        {
            Name = name,
            Requested = HumanPoseRecord.From(requested),
            Readback = HumanPoseRecord.From(readback),
            Bones = CaptureBoneTransforms(animator),
            Hierarchy = CaptureHierarchy(animator.transform),
        };
    }

    private static List<BoneTransformRecord> CaptureBoneTransforms(Animator animator)
    {
        var result = new List<BoneTransformRecord>();
        for (int i = 0; i < (int)HumanBodyBones.LastBone; i++)
        {
            HumanBodyBones bone = (HumanBodyBones)i;
            Transform transform = animator.GetBoneTransform(bone);
            if (transform == null)
                continue;
            result.Add(new BoneTransformRecord
            {
                Role = bone.ToString(),
                Path = RelativePath(animator.transform, transform),
                LocalPosition = Vector3Record.From(transform.localPosition),
                LocalRotation = QuaternionRecord.From(transform.localRotation),
                LocalScale = Vector3Record.From(transform.localScale),
                RootPosition = Vector3Record.From(animator.transform.InverseTransformPoint(transform.position)),
                RootRotation = QuaternionRecord.From(Quaternion.Inverse(animator.transform.rotation) * transform.rotation),
            });
        }
        return result;
    }

    private static List<HierarchyNodeRecord> CaptureHierarchy(Transform root)
    {
        var result = new List<HierarchyNodeRecord>();
        AddHierarchy(root, root, result);
        return result;
    }

    private static void AddHierarchy(Transform root, Transform node, List<HierarchyNodeRecord> result)
    {
        result.Add(new HierarchyNodeRecord { Path = RelativePath(root, node), Name = node.name, LocalPosition = Vector3Record.From(node.localPosition), LocalRotation = QuaternionRecord.From(node.localRotation), LocalScale = Vector3Record.From(node.localScale) });
        for (int i = 0; i < node.childCount; i++)
            AddHierarchy(root, node.GetChild(i), result);
    }

    private static List<RoleRecord> CaptureRoles(Animator animator)
    {
        var result = new List<RoleRecord>();
        for (int i = 0; i < (int)HumanBodyBones.LastBone; i++)
        {
            HumanBodyBones bone = (HumanBodyBones)i;
            Transform transform = animator.GetBoneTransform(bone);
            int parentIndex = HumanTrait.GetParentBone(i);
            result.Add(new RoleRecord
            {
                HumanBoneIndex = i,
                Role = bone.ToString(),
                IsMapped = transform != null,
                Path = transform == null ? string.Empty : RelativePath(animator.transform, transform),
                ParentHumanBoneIndex = parentIndex,
                ParentRole = parentIndex >= 0 && parentIndex < (int)HumanBodyBones.LastBone ? ((HumanBodyBones)parentIndex).ToString() : string.Empty,
                DefaultHierarchyMass = HumanTrait.GetBoneDefaultHierarchyMass(i),
            });
        }
        return result;
    }

    private static List<MuscleRangeRecord> CaptureMuscleRanges()
    {
        var result = new List<MuscleRangeRecord>(HumanTrait.MuscleCount);
        for (int i = 0; i < HumanTrait.MuscleCount; i++)
            result.Add(new MuscleRangeRecord { Index = i, Name = HumanTrait.MuscleName[i], Minimum = HumanTrait.GetMuscleDefaultMin(i), Maximum = HumanTrait.GetMuscleDefaultMax(i) });
        return result;
    }

    private static List<SerializedPropertyRecord> CaptureSerializedAvatarProperties(Avatar avatar)
    {
        var result = new List<SerializedPropertyRecord>();
        var serialized = new SerializedObject(avatar);
        SerializedProperty property = serialized.GetIterator();
        while (property.Next(true))
        {
            string path = property.propertyPath;
            if (path.IndexOf("human", StringComparison.OrdinalIgnoreCase) < 0 && path.IndexOf("mass", StringComparison.OrdinalIgnoreCase) < 0 && path.IndexOf("center", StringComparison.OrdinalIgnoreCase) < 0 && path.IndexOf("axes", StringComparison.OrdinalIgnoreCase) < 0 && path.IndexOf("length", StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            result.Add(new SerializedPropertyRecord { Path = path, Type = property.propertyType.ToString(), Value = SerializedValue(property) });
        }
        return result;
    }

    private static string SerializedValue(SerializedProperty property)
    {
        switch (property.propertyType)
        {
            case SerializedPropertyType.Integer: return property.longValue.ToString(CultureInfo.InvariantCulture);
            case SerializedPropertyType.Boolean: return property.boolValue.ToString();
            case SerializedPropertyType.Float: return property.floatValue.ToString("R", CultureInfo.InvariantCulture);
            case SerializedPropertyType.String: return property.stringValue;
            case SerializedPropertyType.Vector2: return property.vector2Value.ToString("R");
            case SerializedPropertyType.Vector3: return property.vector3Value.ToString("R");
            case SerializedPropertyType.Vector4: return property.vector4Value.ToString("R");
            case SerializedPropertyType.Quaternion: return property.quaternionValue.ToString("R");
            case SerializedPropertyType.ObjectReference: return property.objectReferenceValue == null ? "null" : AssetDatabase.GetAssetPath(property.objectReferenceValue);
            default: return string.Empty;
        }
    }

    private static GameObject BuildProceduralRig(ProceduralGeometry geometry, out Avatar avatar)
    {
        var root = new GameObject("ProceduralAcceptanceRoot");
        var transforms = new Dictionary<HumanBodyBones, Transform>();
        for (int i = 0; i < (int)HumanBodyBones.LastBone; i++)
            EnsureBone((HumanBodyBones)i);

        var human = new List<HumanBone>();
        foreach (KeyValuePair<HumanBodyBones, Transform> pair in transforms)
        {
            if (!geometry.MapUpperChest && pair.Key == HumanBodyBones.UpperChest)
                continue;
            if (!geometry.MapToes && (pair.Key == HumanBodyBones.LeftToes || pair.Key == HumanBodyBones.RightToes))
                continue;
            human.Add(new HumanBone { humanName = HumanTrait.BoneName[(int)pair.Key], boneName = pair.Value.name, limit = new HumanLimit { useDefaultValues = true } });
        }
        var skeleton = new List<SkeletonBone>();
        AddSkeleton(root.transform, skeleton);
        var description = new HumanDescription
        {
            human = human.ToArray(), skeleton = skeleton.ToArray(),
            upperArmTwist = 0.5f, lowerArmTwist = 0.5f, upperLegTwist = 0.5f, lowerLegTwist = 0.5f,
            armStretch = 0.05f, legStretch = 0.05f, feetSpacing = 0.0f, hasTranslationDoF = geometry.HasTranslationDof,
        };
        avatar = AvatarBuilder.BuildHumanAvatar(root, description);
        avatar.name = "ProceduralAcceptanceAvatar";
        return root;

        Transform EnsureBone(HumanBodyBones bone)
        {
            if (transforms.TryGetValue(bone, out Transform existing))
                return existing;
            HumanBodyBones? parentBone = ProceduralParent(bone);
            Transform parent = parentBone.HasValue ? EnsureBone(parentBone.Value) : root.transform;
            var child = new GameObject(bone.ToString()).transform;
            child.SetParent(parent, false);
            child.localPosition = ProceduralPosition(bone, geometry);
            transforms.Add(bone, child);
            return child;
        }
    }

    private static void AddSkeleton(Transform transform, List<SkeletonBone> result)
    {
        result.Add(new SkeletonBone { name = transform.name, position = transform.localPosition, rotation = transform.localRotation, scale = transform.localScale });
        for (int i = 0; i < transform.childCount; i++) AddSkeleton(transform.GetChild(i), result);
    }

    private static HumanBodyBones? ProceduralParent(HumanBodyBones bone)
    {
        string name = bone.ToString();
        if (bone == HumanBodyBones.Hips) return null;
        if (name.StartsWith("LeftThumb") || name.StartsWith("LeftIndex") || name.StartsWith("LeftMiddle") || name.StartsWith("LeftRing") || name.StartsWith("LeftLittle")) return name.EndsWith("Proximal") ? HumanBodyBones.LeftHand : (HumanBodyBones)Enum.Parse(typeof(HumanBodyBones), name.Replace("Intermediate", "Proximal").Replace("Distal", "Intermediate"));
        if (name.StartsWith("RightThumb") || name.StartsWith("RightIndex") || name.StartsWith("RightMiddle") || name.StartsWith("RightRing") || name.StartsWith("RightLittle")) return name.EndsWith("Proximal") ? HumanBodyBones.RightHand : (HumanBodyBones)Enum.Parse(typeof(HumanBodyBones), name.Replace("Intermediate", "Proximal").Replace("Distal", "Intermediate"));
        switch (bone)
        {
            case HumanBodyBones.Spine: return HumanBodyBones.Hips; case HumanBodyBones.Chest: return HumanBodyBones.Spine; case HumanBodyBones.UpperChest: return HumanBodyBones.Chest; case HumanBodyBones.Neck: return HumanBodyBones.UpperChest; case HumanBodyBones.Head: return HumanBodyBones.Neck;
            case HumanBodyBones.LeftEye: case HumanBodyBones.RightEye: case HumanBodyBones.Jaw: return HumanBodyBones.Head;
            case HumanBodyBones.LeftShoulder: return HumanBodyBones.UpperChest; case HumanBodyBones.LeftUpperArm: return HumanBodyBones.LeftShoulder; case HumanBodyBones.LeftLowerArm: return HumanBodyBones.LeftUpperArm; case HumanBodyBones.LeftHand: return HumanBodyBones.LeftLowerArm;
            case HumanBodyBones.RightShoulder: return HumanBodyBones.UpperChest; case HumanBodyBones.RightUpperArm: return HumanBodyBones.RightShoulder; case HumanBodyBones.RightLowerArm: return HumanBodyBones.RightUpperArm; case HumanBodyBones.RightHand: return HumanBodyBones.RightLowerArm;
            case HumanBodyBones.LeftUpperLeg: return HumanBodyBones.Hips; case HumanBodyBones.LeftLowerLeg: return HumanBodyBones.LeftUpperLeg; case HumanBodyBones.LeftFoot: return HumanBodyBones.LeftLowerLeg; case HumanBodyBones.LeftToes: return HumanBodyBones.LeftFoot;
            case HumanBodyBones.RightUpperLeg: return HumanBodyBones.Hips; case HumanBodyBones.RightLowerLeg: return HumanBodyBones.RightUpperLeg; case HumanBodyBones.RightFoot: return HumanBodyBones.RightLowerLeg; case HumanBodyBones.RightToes: return HumanBodyBones.RightFoot;
            default: return null;
        }
    }

    private static Vector3 ProceduralPosition(HumanBodyBones bone, ProceduralGeometry geometry)
    {
        string name = bone.ToString();
        if (name.Contains("Finger") || name.Contains("Thumb") || name.Contains("Index") || name.Contains("Middle") || name.Contains("Ring") || name.Contains("Little")) return new Vector3(name.StartsWith("Left") ? -0.035f : 0.035f, 0.0f, 0.02f) + Delta(bone);
        switch (bone)
        {
            case HumanBodyBones.Hips: return new Vector3(0.0f, 1.0f, 0.0f) + Delta(bone);
            case HumanBodyBones.Spine: return new Vector3(0.0f, 0.25f, 0.0f) + Delta(bone);
            case HumanBodyBones.Chest: return new Vector3(0.0f, 0.26f, 0.0f);
            case HumanBodyBones.UpperChest: return new Vector3(0.0f, geometry.UpperChestPositionY, 0.0f) + Delta(bone);
            case HumanBodyBones.Neck: return new Vector3(0.0f, geometry.NeckPositionY, 0.0f) + Delta(bone);
            case HumanBodyBones.Head: return new Vector3(0.0f, 0.20f, 0.0f);
            case HumanBodyBones.LeftUpperLeg: return new Vector3(-0.14f, -0.42f, 0.0f) + Delta(bone);
            case HumanBodyBones.LeftLowerLeg: return new Vector3(0.0f, -0.44f, 0.0f) + Delta(bone);
            case HumanBodyBones.LeftFoot: return new Vector3(0.0f, -0.42f, 0.10f);
            case HumanBodyBones.LeftToes: return new Vector3(0.0f, 0.0f, 0.17f);
            case HumanBodyBones.RightUpperLeg: return new Vector3(0.14f, -0.42f, 0.0f) + Delta(bone);
            case HumanBodyBones.RightLowerLeg: return new Vector3(0.0f, -0.44f, 0.0f) + Delta(bone);
            case HumanBodyBones.RightFoot: return new Vector3(0.0f, -0.42f, 0.10f);
            case HumanBodyBones.RightToes: return new Vector3(0.0f, 0.0f, 0.17f);
            case HumanBodyBones.LeftShoulder: return new Vector3(-0.16f, 0.09f, 0.0f) + Delta(bone);
            case HumanBodyBones.LeftUpperArm: return new Vector3(-0.27f - geometry.UpperArmLengthDelta, 0.0f, 0.0f) + Delta(bone);
            case HumanBodyBones.LeftLowerArm: return new Vector3(-0.26f - geometry.ForearmLengthDelta, 0.0f, 0.0f) + Delta(bone);
            case HumanBodyBones.LeftHand: return new Vector3(-0.21f, 0.0f, 0.0f) + Delta(bone);
            case HumanBodyBones.RightShoulder: return new Vector3(0.16f, 0.09f, 0.0f) + Delta(bone);
            case HumanBodyBones.RightUpperArm: return new Vector3(0.27f + geometry.UpperArmLengthDelta, 0.0f, 0.0f) + Delta(bone);
            case HumanBodyBones.RightLowerArm: return new Vector3(0.26f + geometry.ForearmLengthDelta, 0.0f, 0.0f) + Delta(bone);
            case HumanBodyBones.RightHand: return new Vector3(0.21f, 0.0f, 0.0f) + Delta(bone);
            case HumanBodyBones.LeftEye: return new Vector3(-0.045f, 0.035f, 0.15f);
            case HumanBodyBones.RightEye: return new Vector3(0.045f, 0.035f, 0.15f);
            case HumanBodyBones.Jaw: return new Vector3(0.0f, -0.06f, 0.13f);
            default: return new Vector3(0.0f, 0.03f, 0.08f) + Delta(bone);
        }

        Vector3 Delta(HumanBodyBones target) => geometry.LocalPositionDeltas.TryGetValue(target, out Vector3 value) ? value : Vector3.zero;
    }

    private static HumanPose ClonePose(HumanPose source) => new HumanPose { bodyPosition = source.bodyPosition, bodyRotation = source.bodyRotation, muscles = (float[])source.muscles.Clone() };
    private static string RequireArgument(string name) { string[] args = Environment.GetCommandLineArgs(); for (int i = 0; i + 1 < args.Length; i++) if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1]; throw new ArgumentException($"Missing required command-line argument {name}."); }
    private static string OptionalArgument(string name) { string[] args = Environment.GetCommandLineArgs(); for (int i = 0; i + 1 < args.Length; i++) if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1]; return string.Empty; }
    private static string ToAssetPath(string path, out string sourceFullPath)
    {
        string projectRoot = NormalizeFullPath(Directory.GetParent(Application.dataPath).FullName);
        string dataPath = NormalizeFullPath(Application.dataPath);
        string requestedPath = Path.IsPathRooted(path) ? path : Path.Combine(projectRoot, path);
        sourceFullPath = NormalizeFullPath(requestedPath);
        string assetsPrefix = dataPath + Path.DirectorySeparatorChar;
        if (!sourceFullPath.StartsWith(assetsPrefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Model must be an imported file below this Unity project Assets directory: {dataPath}");

        return sourceFullPath.Substring(projectRoot.Length).TrimStart(Path.DirectorySeparatorChar).Replace('\\', '/');
    }

    private static string NormalizeFullPath(string path)
    {
        string fullPath = Path.GetFullPath(path).Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        return fullPath.TrimEnd(Path.DirectorySeparatorChar);
    }
    private static string ComputeSha256(string path) { using (FileStream stream = File.OpenRead(path)) using (SHA256 hash = SHA256.Create()) { byte[] bytes = hash.ComputeHash(stream); return BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant(); } }
    private static string RelativePath(Transform root, Transform transform) { if (transform == root) return "."; var names = new List<string>(); for (Transform current = transform; current != null && current != root; current = current.parent) names.Add(current.name); names.Reverse(); return string.Join("/", names); }

    [Serializable] private sealed class BatchReport { public int SchemaVersion = HumanoidBodyFrameAcceptanceExporter.SchemaVersion; public string Source = "UnityMecanimPublicApi"; public string CaptureMode = string.Empty; public string UnityVersion = string.Empty; public string SourceModelPath = string.Empty; public string SourceModelSha256 = string.Empty; public AvatarCapture Imported = new(); public AvatarCapture Procedural = new(); public AvatarCapture ProceduralTranslationDof = new(); public List<AvatarCapture> GeometryVariants = new(); }
    [Serializable] private sealed class AvatarCapture { public string Kind = string.Empty; public string VariantTag = string.Empty; public string AvatarName = string.Empty; public bool AvatarIsValid; public bool AvatarIsHuman; public float HumanScale; public HumanDescriptionRecord HumanDescription = new(); public List<HierarchyNodeRecord> Hierarchy = new(); public List<RoleRecord> Roles = new(); public List<MuscleRangeRecord> MuscleRanges = new(); public BindMetricsRecord BindMetrics = new(); public HumanPoseRecord InitialRestGet = new(); public PoseCapture Neutral = new(); public List<HierarchyGetProbe> HierarchyGetProbes = new(); public List<PoseCapture> Poses = new(); public HumanPoseRecord FreshHandlerRestGet = new(); public PoseCapture FreshHandlerNeutral = new(); public List<SerializedPropertyRecord> SerializedAvatarProperties = new(); }
    [Serializable] private sealed class HumanDescriptionRecord { public float UpperArmTwist; public float LowerArmTwist; public float UpperLegTwist; public float LowerLegTwist; public float ArmStretch; public float LegStretch; public float FeetSpacing; public bool HasTranslationDoF; public List<HumanBoneRecord> Human = new(); public List<SkeletonBoneRecord> Skeleton = new(); public static HumanDescriptionRecord From(HumanDescription value) { var r = new HumanDescriptionRecord { UpperArmTwist = value.upperArmTwist, LowerArmTwist = value.lowerArmTwist, UpperLegTwist = value.upperLegTwist, LowerLegTwist = value.lowerLegTwist, ArmStretch = value.armStretch, LegStretch = value.legStretch, FeetSpacing = value.feetSpacing, HasTranslationDoF = value.hasTranslationDoF }; foreach (HumanBone b in value.human) r.Human.Add(new HumanBoneRecord { HumanName = b.humanName, BoneName = b.boneName, Center = Vector3Record.From(b.limit.center), AxisLength = b.limit.axisLength, Minimum = Vector3Record.From(b.limit.min), Maximum = Vector3Record.From(b.limit.max), UseDefaultValues = b.limit.useDefaultValues }); foreach (SkeletonBone b in value.skeleton) r.Skeleton.Add(new SkeletonBoneRecord { Name = b.name, Position = Vector3Record.From(b.position), Rotation = QuaternionRecord.From(b.rotation), Scale = Vector3Record.From(b.scale) }); return r; } }
    [Serializable] private sealed class HumanBoneRecord { public string HumanName = string.Empty; public string BoneName = string.Empty; public Vector3Record Center = new(); public float AxisLength; public Vector3Record Minimum = new(); public Vector3Record Maximum = new(); public bool UseDefaultValues; }
    [Serializable] private sealed class SkeletonBoneRecord { public string Name = string.Empty; public Vector3Record Position = new(); public QuaternionRecord Rotation = new(); public Vector3Record Scale = new(); }
    [Serializable] private sealed class HierarchyNodeRecord { public string Path = string.Empty; public string Name = string.Empty; public Vector3Record LocalPosition = new(); public QuaternionRecord LocalRotation = new(); public Vector3Record LocalScale = new(); }
    [Serializable] private sealed class RoleRecord { public int HumanBoneIndex; public string Role = string.Empty; public bool IsMapped; public string Path = string.Empty; public int ParentHumanBoneIndex; public string ParentRole = string.Empty; public float DefaultHierarchyMass; }
    [Serializable] private sealed class MuscleRangeRecord { public int Index; public string Name = string.Empty; public float Minimum; public float Maximum; }
    [Serializable] private sealed class PoseCapture { public string Name = string.Empty; public HumanPoseRecord Requested = new(); public HumanPoseRecord Readback = new(); public List<BoneTransformRecord> Bones = new(); public List<HierarchyNodeRecord> Hierarchy = new(); }
    [Serializable] private sealed class HumanPoseRecord { public string BodyPositionSpace = "HumanPose bodyPosition (world center of mass normalized by Animator.humanScale)"; public Vector3Record BodyPosition = new(); public QuaternionRecord BodyRotation = new(); public List<float> Muscles = new(); public static HumanPoseRecord From(HumanPose value) { return new HumanPoseRecord { BodyPosition = Vector3Record.From(value.bodyPosition), BodyRotation = QuaternionRecord.From(value.bodyRotation), Muscles = new List<float>(value.muscles) }; } }
    [Serializable] private sealed class BoneTransformRecord { public string Role = string.Empty; public string Path = string.Empty; public string RootSpace = "Animator root local"; public Vector3Record LocalPosition = new(); public QuaternionRecord LocalRotation = new(); public Vector3Record LocalScale = new(); public Vector3Record RootPosition = new(); public QuaternionRecord RootRotation = new(); }
    [Serializable] private sealed class SerializedPropertyRecord { public string Path = string.Empty; public string Type = string.Empty; public string Value = string.Empty; }
    [Serializable] private sealed class BindMetricsRecord { public Vector3Record RootLocalPosition = new(); public QuaternionRecord RootLocalRotation = new(); public Vector3Record RootLocalScale = new(); public float HipsToLeftFootWorldDistance; public float HipsToRightFootWorldDistance; }
    [Serializable] private sealed class HierarchyGetProbe { public string Role = string.Empty; public string Operation = "Public HumanPoseHandler.GetHumanPose after target world translation with descendant world transforms restored"; public Vector3Record WorldTranslation = new(); public HumanPoseRecord Readback = new(); public List<HierarchyNodeRecord> HierarchyAfterGet = new(); }
    [Serializable] private sealed class Vector3Record { public float X; public float Y; public float Z; public static Vector3Record From(Vector3 value) => new Vector3Record { X = value.x, Y = value.y, Z = value.z }; }
    [Serializable] private sealed class QuaternionRecord { public float X; public float Y; public float Z; public float W; public static QuaternionRecord From(Quaternion value) => new QuaternionRecord { X = value.x, Y = value.y, Z = value.z, W = value.w }; }
    private readonly struct MuscleValue { public readonly int Index; public readonly float Value; public MuscleValue(int index, float value) { Index = index; Value = value; } }
    private readonly struct NamedPoseRequest { public readonly string Name; public readonly MuscleValue[] Muscles; public readonly Quaternion BodyRotation; public NamedPoseRequest(string name, MuscleValue[] muscles, Quaternion bodyRotation) { Name = name; Muscles = muscles; BodyRotation = bodyRotation; } }
    private sealed class ProceduralGeometry { public string VariantTag = string.Empty; public bool HasTranslationDof; public bool MapUpperChest = true; public bool MapToes = true; public float UpperArmLengthDelta; public float ForearmLengthDelta; public float UpperChestPositionY = 0.17f; public float NeckPositionY = 0.16f; public Dictionary<HumanBodyBones, Vector3> LocalPositionDeltas = new(); }
    private sealed class TransformSnapshot { private readonly Transform transform; private readonly Vector3 localPosition; private readonly Quaternion localRotation; private readonly Vector3 localScale; public TransformSnapshot(Transform transform) { this.transform = transform; localPosition = transform.localPosition; localRotation = transform.localRotation; localScale = transform.localScale; } public void Restore() { transform.localPosition = localPosition; transform.localRotation = localRotation; transform.localScale = localScale; } }
    private sealed class WorldTransformSnapshot { private readonly Transform transform; private readonly Vector3 position; private readonly Quaternion rotation; public WorldTransformSnapshot(Transform transform) { this.transform = transform; position = transform.position; rotation = transform.rotation; } public void Restore() { transform.SetPositionAndRotation(position, rotation); } }
}
#endif
