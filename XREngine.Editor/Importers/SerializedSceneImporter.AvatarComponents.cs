using System.Globalization;
using System.Numerics;
using XREngine.Animation;
using XREngine.Components;
using XREngine.Components.Animation;
using XREngine.Components.Scene.Mesh;
using XREngine.Data;
using XREngine.Scene.Prefabs;
using XREngine.Scene.Transforms;
using YamlDotNet.RepresentationModel;

namespace XREngine.Scene.Importers;

internal static partial class SerializedSceneImporter
{
    private const string AvatarDynamicsScriptGuid = "2a2c05204084d904aa4945ccff20d8e5";
    private const long AvatarDynamicsColliderScriptFileId = -1631200402;
    private const long AvatarDynamicsChainScriptFileId = 1661641543;
    private const string AvatarConstraintScriptGuid = "58e2f01a24261a14cb82e6d3399e8b16";
    private const long AvatarConstraintScriptFileId = 1116338486;
    private const string AvatarPresentationScriptGuid = "67cc4cb7839cd3741b63733d5adf0442";
    private const long AvatarPresentationScriptFileId = 542108242;
    private const string UploadPipelineMetadataScriptGuid = "4ecd63eff847044b68db9453ce219299";
    private const long UploadPipelineMetadataScriptFileId = -1427037861;

    private static void AttachAvatarComponents(
        ParsedSourceFile parsed,
        ImportedHierarchy hierarchy,
        ImportState state)
    {
        ParsedMonoBehaviour[] behaviours =
        [
            .. parsed.MonoBehaviours
                .Where(static behaviour => !behaviour.IsStripped)
                .OrderBy(static behaviour => behaviour.DocumentOrder),
        ];

        foreach (ParsedMonoBehaviour behaviour in behaviours.Where(IsAvatarDynamicsCollider))
            AttachAvatarDynamicsCollider(behaviour, hierarchy, state);

        foreach (ParsedMonoBehaviour behaviour in behaviours.Where(IsAvatarDynamicsChain))
            AttachAvatarDynamicsChain(behaviour, hierarchy, state);

        foreach (ParsedMonoBehaviour behaviour in behaviours.Where(IsAvatarConstraint))
            AttachAvatarConstraint(behaviour, hierarchy, state);

        foreach (ParsedMonoBehaviour behaviour in behaviours.Where(IsAvatarPresentationDescriptor))
            AttachAvatarPresentation(behaviour, hierarchy, state);

        foreach (ParsedMonoBehaviour behaviour in behaviours.Where(IsUploadPipelineMetadata))
            IgnoreUploadPipelineMetadata(behaviour, hierarchy, state);

        foreach (ParsedMonoBehaviour behaviour in behaviours.Where(static behaviour =>
                     !IsAvatarDynamicsCollider(behaviour) &&
                     !IsAvatarDynamicsChain(behaviour) &&
                     !IsAvatarConstraint(behaviour) &&
                     !IsAvatarPresentationDescriptor(behaviour) &&
                     !IsUploadPipelineMetadata(behaviour)))
        {
            PreserveUnsupportedBehaviour(behaviour, hierarchy, state);
        }
    }

    private static void AttachAvatarDynamicsCollider(
        ParsedMonoBehaviour behaviour,
        ImportedHierarchy hierarchy,
        ImportState state)
    {
        if (!TryResolveBehaviourNode(behaviour, hierarchy, state, out SceneNode? node) || node is null)
            return;

        YamlMappingNode fields = behaviour.SerializedFields;
        int shapeType = GetScalarInt(fields, "shapeType") ?? 0;
        PhysicsChainColliderBase? collider = shapeType switch
        {
            0 or 1 => node.AddComponent<PhysicsChainCollider>(),
            2 => node.AddComponent<PhysicsChainPlaneCollider>(),
            _ => null,
        };

        if (collider is null)
        {
            AddAvatarDiagnostic(
                state,
                behaviour,
                "UNITYVRC0001",
                SourceImportDiagnosticSeverity.Warning,
                $"VRChat PhysBone collider shapeType '{shapeType}' is unsupported and was retained as metadata only.");
            PreserveUnsupportedBehaviour(behaviour, hierarchy, state);
            return;
        }

        collider.IsActive = behaviour.Enabled;
        collider.RootTransformOverride = ResolveTransformReference(
            GetNode(fields, "rootTransform"),
            hierarchy,
            fallback: null);
        collider._center = ConvertPosition(GetVector3(fields, "position", Vector3.Zero));
        collider.LocalRotationOffset = ConvertRotation(GetQuaternion(fields, "rotation", Quaternion.Identity));
        collider._bound = (GetScalarInt(fields, "insideBounds") ?? 0) != 0
            ? PhysicsChainColliderBase.EBound.Inside
            : PhysicsChainColliderBase.EBound.Outside;
        collider._direction = PhysicsChainColliderBase.Direction.Y;

        if (collider is PhysicsChainCollider volumeCollider)
        {
            volumeCollider._radius = MathF.Max(GetScalarFloat(fields, "radius") ?? 0.0f, 0.0f);
            volumeCollider._height = shapeType == 0
                ? 0.0f
                : MathF.Max(GetScalarFloat(fields, "height") ?? 0.0f, 0.0f);
            volumeCollider._radius2 = 0.0f;
        }

        hierarchy.ComponentsByFileId[behaviour.FileId] = collider;
        MarkAdaptedScriptDependency(behaviour, state);

        if ((GetScalarInt(fields, "bonesAsSpheres") ?? 0) != 0 ||
            (GetScalarInt(fields, "globalCollisionFlags") ?? 0) != 0)
        {
            AddAvatarDiagnostic(
                state,
                behaviour,
                "UNITYVRC0002",
                SourceImportDiagnosticSeverity.Warning,
                "VRChat collider bones-as-spheres/global collision filtering has no native equivalent and was not applied.");
        }
    }

    private static void AttachAvatarDynamicsChain(
        ParsedMonoBehaviour behaviour,
        ImportedHierarchy hierarchy,
        ImportState state)
    {
        if (!TryResolveBehaviourNode(behaviour, hierarchy, state, out SceneNode? node) || node is null)
            return;

        PhysicsChainComponent? component = node.AddComponent<PhysicsChainComponent>();
        if (component is null)
            return;

        YamlMappingNode fields = behaviour.SerializedFields;
        Transform? root = ResolveTransformReference(GetNode(fields, "rootTransform"), hierarchy, node.Transform) as Transform;
        component.Root = root;
        component.Roots = root is null ? null : [root];
        component.IsActive = behaviour.Enabled;
        component.EndOffset = ConvertPosition(GetVector3(fields, "endpointPosition", Vector3.Zero));
        component.Elasticity = Clamp01(GetScalarFloat(fields, "pull") ?? 0.0f);
        component.Damping = Clamp01(1.0f - (GetScalarFloat(fields, "spring") ?? 0.0f));
        component.Stiffness = Clamp01(GetScalarFloat(fields, "stiffness") ?? 0.0f);
        component.Inert = Clamp01(GetScalarFloat(fields, "immobile") ?? 0.0f);
        component.Radius = MathF.Max(GetScalarFloat(fields, "radius") ?? 0.0f, 0.0f);
        component.Gravity = ConvertDirection(new Vector3(0.0f, -(GetScalarFloat(fields, "gravity") ?? 0.0f), 0.0f));
        component.Exclusions = ResolveTransformSequence(GetNode(fields, "ignoreTransforms"), hierarchy);
        component.Colliders = (GetScalarInt(fields, "allowCollision") ?? 1) == 0
            ? []
            : ResolveColliderSequence(GetNode(fields, "colliders"), hierarchy);
        component.ElasticityDistrib = ParseSourceCurve(fields, "pullCurve");
        component.DampingDistrib = ParseSourceCurve(fields, "springCurve", static value => 1.0f - value, invertTangents: true);
        component.StiffnessDistrib = ParseSourceCurve(fields, "stiffnessCurve");
        component.InertDistrib = ParseSourceCurve(fields, "immobileCurve");
        component.RadiusDistrib = ParseSourceCurve(fields, "radiusCurve");

        int integrationType = GetScalarInt(fields, "integrationType") ?? 0;
        component.UpdateMode = integrationType switch
        {
            1 => PhysicsChainComponent.EUpdateMode.FixedUpdate,
            _ => PhysicsChainComponent.EUpdateMode.Normal,
        };

        hierarchy.ComponentsByFileId[behaviour.FileId] = component;
        MarkAdaptedScriptDependency(behaviour, state);

        var approximated = new List<string>(8);
        if ((GetScalarFloat(fields, "gravityFalloff") ?? 0.0f) != 0.0f ||
            HasCurveKeys(fields, "gravityFalloffCurve") ||
            HasCurveKeys(fields, "gravityCurve"))
        {
            approximated.Add("gravity curves/falloff");
        }

        if ((GetScalarInt(fields, "limitType") ?? 0) != 0)
            approximated.Add("angular limits");
        if ((GetScalarFloat(fields, "maxStretch") ?? 0.0f) != 0.0f ||
            (GetScalarFloat(fields, "maxSquish") ?? 0.0f) != 0.0f)
        {
            approximated.Add("stretch/squish");
        }

        if ((GetScalarInt(fields, "allowGrabbing") ?? 0) != 0 ||
            (GetScalarInt(fields, "allowPosing") ?? 0) != 0)
        {
            approximated.Add("VRChat grabbing/posing");
        }

        if ((GetScalarInt(fields, "isAnimated") ?? 0) != 0)
            approximated.Add("animated-parameter integration");
        if ((GetScalarInt(fields, "multiChildType") ?? 0) != 0)
            approximated.Add("multi-child averaging");

        string mappedSummary =
            "Mapped root, ignored transforms, endpoint, radius/curve, gravity, pull/curve, spring/curve, " +
            "stiffness/curve, immobility/curve, collision list, enabled state, and update mode.";
        string approximationSummary = approximated.Count == 0
            ? " No active parameters required an unsupported approximation."
            : $" Unsupported active semantics: {string.Join(", ", approximated)}.";
        AddAvatarDiagnostic(
            state,
            behaviour,
            "UNITYVRC0003",
            approximated.Count == 0 ? SourceImportDiagnosticSeverity.Info : SourceImportDiagnosticSeverity.Warning,
            mappedSummary + approximationSummary);
    }

    private static void AttachAvatarConstraint(
        ParsedMonoBehaviour behaviour,
        ImportedHierarchy hierarchy,
        ImportState state)
    {
        if (!TryResolveBehaviourNode(behaviour, hierarchy, state, out SceneNode? node) || node is null)
            return;

        YamlMappingNode fields = behaviour.SerializedFields;
        WeightedTransformConstraintComponent? component = node.AddComponent<WeightedTransformConstraintComponent>();
        if (component is null)
            return;

        component.TargetTransform = ResolveTransformReference(GetNode(fields, "TargetTransform"), hierarchy, node.Transform);
        component.Sources = ParseConstraintSources(fields, hierarchy);
        component.Weight = Clamp01(GetScalarFloat(fields, "GlobalWeight") ?? 1.0f);
        component.SolveInLocalSpace = (GetScalarInt(fields, "SolveInLocalSpace") ?? 0) != 0;
        component.Locked = (GetScalarInt(fields, "Locked") ?? 0) != 0;
        component.Channels = ParseConstraintChannels(fields);
        component.IsActive = behaviour.Enabled && (GetScalarInt(fields, "IsActive") ?? 1) != 0;

        hierarchy.ComponentsByFileId[behaviour.FileId] = component;
        MarkAdaptedScriptDependency(behaviour, state);

        if ((GetScalarInt(fields, "FreezeToWorld") ?? 0) != 0 ||
            (GetScalarInt(fields, "RebakeOffsetsWhenUnfrozen") ?? 0) != 0)
        {
            AddAvatarDiagnostic(
                state,
                behaviour,
                "UNITYVRC0004",
                SourceImportDiagnosticSeverity.Warning,
                "VRChat constraint freeze/rebake editor semantics were not reproduced; weighted runtime source offsets remain active.");
        }
    }

    private static void AttachAvatarPresentation(
        ParsedMonoBehaviour behaviour,
        ImportedHierarchy hierarchy,
        ImportState state)
    {
        if (!TryResolveBehaviourNode(behaviour, hierarchy, state, out SceneNode? node) || node is null)
            return;

        YamlMappingNode fields = behaviour.SerializedFields;
        AvatarPresentationComponent? component = node.AddComponent<AvatarPresentationComponent>();
        if (component is null)
            return;

        component.IsActive = behaviour.Enabled;
        component.AvatarRoot = ResolveTransformReference(
            GetNode(fields, "avatarRoot") ?? GetNode(fields, "AvatarRoot"),
            hierarchy,
            node.Transform);
        component.ViewPosition = ConvertPosition(GetVector3(fields, "ViewPosition", Vector3.Zero));
        component.LipSyncMode = (AvatarLipSyncMode)Math.Clamp(
            GetScalarInt(fields, "lipSync") ?? 0,
            (int)AvatarLipSyncMode.Default,
            (int)AvatarLipSyncMode.ParameterOnly);
        component.JawBone = ResolveTransformReference(GetNode(fields, "lipSyncJawBone"), hierarchy);
        component.JawClosedRotation = ConvertRotation(GetQuaternion(fields, "lipSyncJawClosed", Quaternion.Identity));
        component.JawOpenRotation = ConvertRotation(GetQuaternion(fields, "lipSyncJawOpen", Quaternion.Identity));
        component.VisemeRenderer = ResolveComponentReference<ModelComponent>(GetNode(fields, "VisemeSkinnedMesh"), hierarchy);
        component.MouthOpenBlendShapeName = GetScalarString(fields, "MouthOpenBlendShapeName") ?? string.Empty;
        component.VisemeBlendShapeNames = ParseStringSequence(GetNode(fields, "VisemeBlendShapes"));
        component.EyeLook = ParseEyeLookMetadata(fields, hierarchy);
        component.AnimationLayers =
        [
            .. ParseAnimationLayers(GetNode(fields, "baseAnimationLayers")),
            .. ParseAnimationLayers(GetNode(fields, "specialAnimationLayers")),
        ];
        component.AnimationPreset = ToIdentity(ParseReference(GetNode(fields, "AnimationPreset")), SourceAssetObjectKind.Asset);

        hierarchy.ComponentsByFileId[behaviour.FileId] = component;
        MarkAdaptedScriptDependency(behaviour, state);

        if ((GetScalarInt(fields, "customExpressions") ?? 0) != 0)
        {
            AddAvatarDiagnostic(
                state,
                behaviour,
                "UNITYVRC0005",
                SourceImportDiagnosticSeverity.Info,
                "Avatar custom expression menu/parameters are behavior-only metadata; missing references remain non-fatal and are reported by the dependency manifest.");
        }
    }

    private static void IgnoreUploadPipelineMetadata(
        ParsedMonoBehaviour behaviour,
        ImportedHierarchy hierarchy,
        ImportState state)
    {
        MarkScriptDependencyOutcome(behaviour, state, SourceImportConversionOutcome.IgnoredOptional);
        AddAvatarDiagnostic(
            state,
            behaviour,
            "UNITYVRC0006",
            SourceImportDiagnosticSeverity.Info,
            "VRChat PipelineManager upload identity and SDK pipeline status were intentionally ignored as editor-only metadata.");
    }

    private static void PreserveUnsupportedBehaviour(
        ParsedMonoBehaviour behaviour,
        ImportedHierarchy hierarchy,
        ImportState state)
    {
        hierarchy.NodesByGameObjectId.TryGetValue(behaviour.GameObjectFileId, out SceneNode? node);
        state.Context.UnsupportedBehaviours.Add(new UnsupportedSourceBehaviourMetadata
        {
            Identity = new SourceAssetIdentity
            {
                AssetGuid = hierarchy.SourceGuid ?? string.Empty,
                LocalFileId = behaviour.FileId,
                ObjectKind = SourceAssetObjectKind.Component,
            },
            SceneNodePath = node is null ? string.Empty : GetSceneNodePath(node),
            ScriptGuid = behaviour.Script.Guid ?? string.Empty,
            ScriptFileId = behaviour.Script.FileId,
            Enabled = behaviour.Enabled,
            SerializedYaml = behaviour.SerializedYaml,
        });
        MarkScriptDependencyOutcome(behaviour, state, SourceImportConversionOutcome.IgnoredOptional);
        AddAvatarDiagnostic(
            state,
            behaviour,
            "UNITYVRC0007",
            SourceImportDiagnosticSeverity.Info,
            $"Unsupported MonoBehaviour script '{behaviour.Script.Guid}:{behaviour.Script.FileId}' was preserved for inspection without attaching a fake runtime behavior.");
    }

    private static bool TryResolveBehaviourNode(
        ParsedMonoBehaviour behaviour,
        ImportedHierarchy hierarchy,
        ImportState state,
        out SceneNode? node)
    {
        if (hierarchy.NodesByGameObjectId.TryGetValue(behaviour.GameObjectFileId, out node))
            return true;

        AddAvatarDiagnostic(
            state,
            behaviour,
            "UNITYVRC0008",
            SourceImportDiagnosticSeverity.Error,
            $"MonoBehaviour owner GameObject fileID '{behaviour.GameObjectFileId}' could not be resolved after prefab correspondence binding.");
        return false;
    }

    private static AvatarGazeBinding ParseEyeLookMetadata(
        YamlMappingNode fields,
        ImportedHierarchy hierarchy)
    {
        var metadata = new AvatarGazeBinding
        {
            Enabled = (GetScalarInt(fields, "enableEyeLook") ?? 0) != 0,
        };
        if (GetNode(fields, "customEyeLookSettings") is not YamlMappingNode eyeSettings)
            return metadata;

        metadata.LeftEye = ResolveTransformReference(GetNode(eyeSettings, "leftEye"), hierarchy);
        metadata.RightEye = ResolveTransformReference(GetNode(eyeSettings, "rightEye"), hierarchy);
        (metadata.LeftStraight, metadata.RightStraight) = ParseLinkedEyeRotations(eyeSettings, "eyesLookingStraight");
        (metadata.LeftUp, metadata.RightUp) = ParseLinkedEyeRotations(eyeSettings, "eyesLookingUp");
        (metadata.LeftDown, metadata.RightDown) = ParseLinkedEyeRotations(eyeSettings, "eyesLookingDown");
        (metadata.LeftLookLeft, metadata.RightLookLeft) = ParseLinkedEyeRotations(eyeSettings, "eyesLookingLeft");
        (metadata.LeftLookRight, metadata.RightLookRight) = ParseLinkedEyeRotations(eyeSettings, "eyesLookingRight");
        metadata.EyelidType = GetScalarInt(eyeSettings, "eyelidType") ?? 0;
        metadata.EyelidRenderer = ResolveComponentReference<ModelComponent>(
            GetNode(eyeSettings, "eyelidsSkinnedMesh"),
            hierarchy);
        metadata.EyelidBlendShapeIndices = ParseLittleEndianInt32Hex(
            GetScalarString(eyeSettings, "eyelidsBlendshapes"));
        return metadata;
    }

    private static (Quaternion Left, Quaternion Right) ParseLinkedEyeRotations(
        YamlMappingNode eyeSettings,
        string key)
    {
        if (GetNode(eyeSettings, key) is not YamlMappingNode rotations)
            return (Quaternion.Identity, Quaternion.Identity);

        return (
            ConvertRotation(GetQuaternion(rotations, "left", Quaternion.Identity)),
            ConvertRotation(GetQuaternion(rotations, "right", Quaternion.Identity)));
    }

    private static IEnumerable<ImportedAvatarAnimationLayer> ParseAnimationLayers(YamlNode? node)
    {
        if (node is not YamlSequenceNode sequence)
            yield break;

        foreach (YamlNode child in sequence.Children)
        {
            if (child is not YamlMappingNode layer)
                continue;

            yield return new ImportedAvatarAnimationLayer
            {
                LayerType = GetScalarInt(layer, "type") ?? 0,
                Enabled = (GetScalarInt(layer, "isEnabled") ?? 0) != 0,
                IsDefault = (GetScalarInt(layer, "isDefault") ?? 0) != 0,
                Controller = ToIdentity(ParseReference(GetNode(layer, "animatorController")), SourceAssetObjectKind.Asset),
                Mask = ToIdentity(ParseReference(GetNode(layer, "mask")), SourceAssetObjectKind.Asset),
            };
        }
    }

    private static List<TransformConstraintSource> ParseConstraintSources(
        YamlMappingNode fields,
        ImportedHierarchy hierarchy)
    {
        if (GetNode(fields, "Sources") is not YamlMappingNode sourceCollection)
            return [];

        var result = new List<TransformConstraintSource>();
        foreach ((YamlNode _, YamlNode value) in sourceCollection.Children)
        {
            if (value is not YamlMappingNode source)
                continue;

            TransformBase? sourceTransform = ResolveTransformReference(GetNode(source, "SourceTransform"), hierarchy);
            float weight = GetScalarFloat(source, "Weight") ?? 0.0f;
            if (sourceTransform is null || weight <= 0.0f)
                continue;

            Vector3 rotationDegrees = GetVector3(source, "ParentRotationOffset", Vector3.Zero);
            result.Add(new TransformConstraintSource
            {
                SourceTransform = sourceTransform,
                Weight = weight,
                PositionOffset = ConvertPosition(GetVector3(source, "ParentPositionOffset", Vector3.Zero)),
                RotationOffset = ConvertEulerDegrees(rotationDegrees),
                ScaleOffset = GetVector3(source, "ScaleOffset", Vector3.Zero),
            });
        }

        return result;
    }

    private static TransformConstraintChannels ParseConstraintChannels(YamlMappingNode fields)
    {
        TransformConstraintChannels result = TransformConstraintChannels.None;
        AddConstraintChannel(fields, "AffectsPositionX", TransformConstraintChannels.PositionX, ref result);
        AddConstraintChannel(fields, "AffectsPositionY", TransformConstraintChannels.PositionY, ref result);
        AddConstraintChannel(fields, "AffectsPositionZ", TransformConstraintChannels.PositionZ, ref result);
        AddConstraintChannel(fields, "AffectsRotationX", TransformConstraintChannels.RotationX, ref result);
        AddConstraintChannel(fields, "AffectsRotationY", TransformConstraintChannels.RotationY, ref result);
        AddConstraintChannel(fields, "AffectsRotationZ", TransformConstraintChannels.RotationZ, ref result);
        AddConstraintChannel(fields, "AffectsScaleX", TransformConstraintChannels.ScaleX, ref result);
        AddConstraintChannel(fields, "AffectsScaleY", TransformConstraintChannels.ScaleY, ref result);
        AddConstraintChannel(fields, "AffectsScaleZ", TransformConstraintChannels.ScaleZ, ref result);
        return result == TransformConstraintChannels.None
            ? TransformConstraintChannels.Parent
            : result;
    }

    private static void AddConstraintChannel(
        YamlMappingNode fields,
        string key,
        TransformConstraintChannels channel,
        ref TransformConstraintChannels result)
    {
        if ((GetScalarInt(fields, key) ?? 0) != 0)
            result |= channel;
    }

    private static PhysicsChainCoefficientCurve? ParseSourceCurve(
        YamlMappingNode fields,
        string key,
        Func<float, float>? valueTransform = null,
        bool invertTangents = false)
    {
        if (GetNode(fields, key) is not YamlMappingNode curveDocument ||
            GetNode(curveDocument, "m_Curve") is not YamlSequenceNode keyframes ||
            keyframes.Children.Count == 0)
        {
            return null;
        }

        List<PhysicsChainCoefficientKeyframe> parsedKeyframes = new(keyframes.Children.Count);
        foreach (YamlNode child in keyframes.Children)
        {
            if (child is not YamlMappingNode keyframe)
                continue;

            float time = GetScalarFloat(keyframe, "time") ?? 0.0f;
            float value = GetScalarFloat(keyframe, "value") ?? 0.0f;
            float inSlope = GetScalarFloat(keyframe, "inSlope") ?? 0.0f;
            float outSlope = GetScalarFloat(keyframe, "outSlope") ?? 0.0f;
            if (valueTransform is not null)
                value = valueTransform(value);
            if (invertTangents)
            {
                inSlope = -inSlope;
                outSlope = -outSlope;
            }

            parsedKeyframes.Add(new PhysicsChainCoefficientKeyframe(
                time,
                value,
                inSlope,
                outSlope));
        }

        if (parsedKeyframes.Count == 0)
            return null;

        var curve = new PhysicsChainCoefficientCurve();
        for (int i = 0; i < parsedKeyframes.Count; i++)
            curve.Keyframes.Add(parsedKeyframes[i]);
        return curve;
    }

    private static bool HasCurveKeys(YamlMappingNode fields, string key)
        => GetNode(fields, key) is YamlMappingNode curveDocument &&
           GetNode(curveDocument, "m_Curve") is YamlSequenceNode keyframes &&
           keyframes.Children.Count > 0;

    private static List<TransformBase> ResolveTransformSequence(
        YamlNode? node,
        ImportedHierarchy hierarchy)
    {
        if (node is not YamlSequenceNode sequence)
            return [];

        var result = new List<TransformBase>(sequence.Children.Count);
        foreach (YamlNode child in sequence.Children)
        {
            TransformBase? transform = ResolveTransformReference(child, hierarchy);
            if (transform is not null)
                result.Add(transform);
        }

        return result;
    }

    private static List<PhysicsChainColliderBase> ResolveColliderSequence(
        YamlNode? node,
        ImportedHierarchy hierarchy)
    {
        if (node is not YamlSequenceNode sequence)
            return [];

        var result = new List<PhysicsChainColliderBase>(sequence.Children.Count);
        foreach (YamlNode child in sequence.Children)
        {
            SourceReference reference = ParseReference(child);
            if (hierarchy.ComponentsByFileId.TryGetValue(reference.FileId, out XRComponent? component) &&
                component is PhysicsChainColliderBase collider)
            {
                result.Add(collider);
            }
        }

        return result;
    }

    private static TransformBase? ResolveTransformReference(
        YamlNode? node,
        ImportedHierarchy hierarchy,
        TransformBase? fallback = null)
    {
        SourceReference reference = ParseReference(node);
        if (reference.FileId == 0)
            return fallback;

        return hierarchy.NodesByTransformId.TryGetValue(reference.FileId, out SceneNode? transformNode)
            ? transformNode.Transform
            : hierarchy.NodesByGameObjectId.TryGetValue(reference.FileId, out SceneNode? gameObjectNode)
                ? gameObjectNode.Transform
                : fallback;
    }

    private static T? ResolveComponentReference<T>(YamlNode? node, ImportedHierarchy hierarchy)
        where T : XRComponent
    {
        SourceReference reference = ParseReference(node);
        return hierarchy.ComponentsByFileId.TryGetValue(reference.FileId, out XRComponent? component)
            ? component as T
            : null;
    }

    private static SourceAssetIdentity? ToIdentity(SourceReference reference, SourceAssetObjectKind kind)
        => reference.FileId == 0 && string.IsNullOrWhiteSpace(reference.Guid)
            ? null
            : new SourceAssetIdentity
            {
                AssetGuid = reference.Guid ?? string.Empty,
                LocalFileId = reference.FileId,
                ObjectKind = kind,
            };

    private static List<string> ParseStringSequence(YamlNode? node)
        => node is YamlSequenceNode sequence
            ? [.. sequence.Children.OfType<YamlScalarNode>().Select(static value => value.Value ?? string.Empty)]
            : [];

    private static List<int> ParseLittleEndianInt32Hex(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length % 8 != 0)
            return [];

        try
        {
            byte[] bytes = Convert.FromHexString(value);
            var result = new List<int>(bytes.Length / sizeof(int));
            for (int offset = 0; offset + sizeof(int) <= bytes.Length; offset += sizeof(int))
                result.Add(BitConverter.ToInt32(bytes, offset));
            return result;
        }
        catch (FormatException)
        {
            return [];
        }
    }

    private static Quaternion ConvertEulerDegrees(Vector3 sourceEulerDegrees)
    {
        const float degreesToRadians = MathF.PI / 180.0f;
        Quaternion sourceRotation = Quaternion.CreateFromYawPitchRoll(
            sourceEulerDegrees.Y * degreesToRadians,
            sourceEulerDegrees.X * degreesToRadians,
            sourceEulerDegrees.Z * degreesToRadians);
        return ConvertRotation(sourceRotation);
    }

    private static float Clamp01(float value)
        => Math.Clamp(value, 0.0f, 1.0f);

    private static string GetSceneNodePath(SceneNode node)
    {
        var segments = new Stack<string>();
        for (SceneNode? current = node; current is not null; current = current.Parent)
            segments.Push(current.Name ?? SceneNode.DefaultName);
        return string.Join("/", segments);
    }

    private static void MarkAdaptedScriptDependency(ParsedMonoBehaviour behaviour, ImportState state)
        => MarkScriptDependencyOutcome(behaviour, state, SourceImportConversionOutcome.Converted);

    private static void MarkScriptDependencyOutcome(
        ParsedMonoBehaviour behaviour,
        ImportState state,
        SourceImportConversionOutcome outcome)
    {
        if (string.IsNullOrWhiteSpace(behaviour.Script.Guid))
            return;

        string? scriptPath = ResolveAssetPath(state, behaviour.Script.Guid);
        if (!string.IsNullOrWhiteSpace(scriptPath) && File.Exists(scriptPath))
            state.Context.MarkOutcome(scriptPath, outcome);
    }

    private static void AddAvatarDiagnostic(
        ImportState state,
        ParsedMonoBehaviour behaviour,
        string code,
        SourceImportDiagnosticSeverity severity,
        string message)
    {
        state.Context.AddDiagnostic(
            code,
            severity,
            SourceImportDiagnosticCategory.AvatarComponent,
            message,
            state.EntryFilePath,
            identity: new SourceAssetIdentity
            {
                AssetGuid = behaviour.Script.Guid ?? string.Empty,
                LocalFileId = behaviour.FileId,
                ObjectKind = SourceAssetObjectKind.Component,
            });
    }

    private static bool IsAvatarDynamicsCollider(ParsedMonoBehaviour behaviour)
        => MatchesScript(behaviour, AvatarDynamicsScriptGuid, AvatarDynamicsColliderScriptFileId);

    private static bool IsAvatarDynamicsChain(ParsedMonoBehaviour behaviour)
        => MatchesScript(behaviour, AvatarDynamicsScriptGuid, AvatarDynamicsChainScriptFileId);

    private static bool IsAvatarConstraint(ParsedMonoBehaviour behaviour)
        => MatchesScript(behaviour, AvatarConstraintScriptGuid, AvatarConstraintScriptFileId);

    private static bool IsAvatarPresentationDescriptor(ParsedMonoBehaviour behaviour)
        => MatchesScript(behaviour, AvatarPresentationScriptGuid, AvatarPresentationScriptFileId);

    private static bool IsUploadPipelineMetadata(ParsedMonoBehaviour behaviour)
        => MatchesScript(behaviour, UploadPipelineMetadataScriptGuid, UploadPipelineMetadataScriptFileId);

    private static bool MatchesScript(ParsedMonoBehaviour behaviour, string scriptGuid, long scriptFileId)
        => behaviour.Script.FileId == scriptFileId &&
           string.Equals(behaviour.Script.Guid, scriptGuid, StringComparison.OrdinalIgnoreCase);
}
