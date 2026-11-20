using System.Globalization;
using System.Numerics;
using XREngine.Animation;
using XREngine.Components;
using XREngine.Components.Animation;
using XREngine.Components.Scene.Mesh;
using XREngine.Data;
using XREngine.Scene.Prefabs;
using XREngine.Scene.Transforms;
using XREngine.UnityScriptHost.Contracts;
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
            RecordAvatarFeature(state, behaviour, hierarchy, "avatar.dynamics.collider.shape",
                UnityConversionFeatureStatus.Unknown, "Collider shape conversion",
                $"The collider shape value '{shapeType}' has no selected native mapping.",
                lostFunctionality: "The collider was retained as metadata and was not attached.");
            AddAvatarDiagnostic(
                state,
                behaviour,
                hierarchy,
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
        collider.Center = ConvertPosition(GetVector3(fields, "position", Vector3.Zero));
        collider.LocalRotationOffset = ConvertRotation(GetQuaternion(fields, "rotation", Quaternion.Identity));
        collider.Bound = (GetScalarInt(fields, "insideBounds") ?? 0) != 0
            ? PhysicsChainColliderBase.EBound.Inside
            : PhysicsChainColliderBase.EBound.Outside;
        collider.Axis = PhysicsChainColliderBase.Direction.Y;
        collider.ParticleContactOnly = (GetScalarInt(fields, "bonesAsSpheres") ?? 0) != 0;

        if (collider is PhysicsChainCollider volumeCollider)
        {
            volumeCollider.Radius = MathF.Max(GetScalarFloat(fields, "radius") ?? 0.0f, 0.0f);
            volumeCollider.Height = shapeType == 0
                ? 0.0f
                : MathF.Max(GetScalarFloat(fields, "height") ?? 0.0f, 0.0f);
            volumeCollider.Radius2 = 0.0f;
        }

        hierarchy.ComponentsByFileId[behaviour.FileId] = collider;
        MarkAdaptedScriptDependency(behaviour, state);
        RecordAvatarFeature(state, behaviour, hierarchy, "avatar.dynamics.collider",
            UnityConversionFeatureStatus.Converted, "Physics chain collision",
            "The importer attached a native physics chain collider and mapped its supported fields.",
            nativeTarget: collider.GetType().FullName);

        if ((GetScalarInt(fields, "globalCollisionFlags") ?? 0) != 0)
        {
            RecordAvatarFeature(state, behaviour, hierarchy, "avatar.dynamics.collider.global-collision-filter",
                UnityConversionFeatureStatus.MissingNativeCapability, "Global collision filtering",
                "The source enables global collision filtering, but the native collider has no matching control.",
                member: "globalCollisionFlags",
                lostFunctionality: "Global collision filtering was not applied.",
                requiredNativeCapability: "Global collision filtering for physics chain colliders");
            AddAvatarDiagnostic(
                state,
                behaviour,
                hierarchy,
                "UNITYVRC0002",
                SourceImportDiagnosticSeverity.Warning,
                "VRChat collider global collision filtering has no native equivalent and was not applied.");
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
        {
            RecordAvatarFeature(state, behaviour, hierarchy, "avatar.dynamics.chain",
                UnityConversionFeatureStatus.Unknown, "Physics chain simulation",
                "The native component could not be attached; conversion did not complete.");
            return;
        }

        YamlMappingNode fields = behaviour.SerializedFields;
        Transform? root = ResolveTransformReference(GetNode(fields, "rootTransform"), hierarchy, node.Transform) as Transform;
        component.Root = root;
        component.Roots = root is null ? null : [root];
        component.IsActive = behaviour.Enabled;
        component.EndOffset = ConvertPosition(GetVector3(fields, "endpointPosition", Vector3.Zero));
        component.EndOffsetInBoneSpace = true;
        component.Elasticity = Clamp01(GetScalarFloat(fields, "pull") ?? 0.0f);
        float spring = GetScalarFloat(fields, "spring") ?? 0.0f;
        // Damping is one minus the curved spring value, so a spring curve sets the damping directly.
        component.Damping = HasCurveKeys(fields, "springCurve") ? 1.0f : Clamp01(1.0f - spring);
        int integrationType = GetScalarInt(fields, "integrationType") ?? 0;
        // Stiffness exists only in Advanced integration. Spring and Momentum share the serialized "spring" field.
        component.Stiffness = integrationType == 1 ? Clamp01(GetScalarFloat(fields, "stiffness") ?? 0.0f) : 0.0f;
        component.Inert = Clamp01(GetScalarFloat(fields, "immobile") ?? 0.0f);
        component.InertFollowsRootParent = true;
        // Multi-Child Type Ignore keeps the children of a branching bone at their rest offsets.
        component.PinBranchChildren = (GetScalarInt(fields, "multiChildType") ?? 0) == 0;
        // Measured PhysBone behavior: bones aim relative to their simulated parents, stiffness holds the
        // offset from the parent without resisting gravity, and the settled gravity angle does not depend on bone size.
        component.RestFollowsParent = true;
        component.StiffnessMode = EPhysicsChainStiffnessMode.HoldParentOffset;
        component.GravityMode = EPhysicsChainGravityMode.SegmentRatio;
        component.CarryParticlesBetweenSteps = false;
        component.CollisionMode = EPhysicsChainCollisionMode.Segment;
        component.Radius = MathF.Max(GetScalarFloat(fields, "radius") ?? 0.0f, 0.0f);
        component.Gravity = ConvertDirection(new Vector3(0.0f, -(GetScalarFloat(fields, "gravity") ?? 0.0f), 0.0f));
        component.GravityFalloff = Clamp01(GetScalarFloat(fields, "gravityFalloff") ?? 0.0f);
        component.LimitType = (GetScalarInt(fields, "limitType") ?? 0) switch
        {
            1 => EPhysicsChainLimitType.Angle,
            2 => EPhysicsChainLimitType.Hinge,
            3 => EPhysicsChainLimitType.Polar,
            _ => EPhysicsChainLimitType.None,
        };
        component.MaxAngle = GetScalarFloat(fields, "maxAngleX") ?? 45.0f;
        component.MaxYaw = GetScalarFloat(fields, "maxAngleZ") ?? 45.0f;
        component.MaxAngleDistrib = ParseSourceCurve(fields, "maxAngleXCurve");
        component.MaxYawDistrib = ParseSourceCurve(fields, "maxAngleZCurve");
        Vector3 limitEuler = GetVector3(fields, "limitRotation", Vector3.Zero);
        component.LimitRotation = ConvertRotation(Quaternion.CreateFromYawPitchRoll(
            float.DegreesToRadians(limitEuler.Y),
            float.DegreesToRadians(limitEuler.X),
            float.DegreesToRadians(limitEuler.Z)));
        component.MaxStretch = GetScalarFloat(fields, "maxStretch") ?? 0.0f;
        component.MaxSquish = GetScalarFloat(fields, "maxSquish") ?? 0.0f;
        component.StretchMotion = GetScalarFloat(fields, "stretchMotion") ?? 0.0f;
        component.Exclusions = ResolveTransformSequence(GetNode(fields, "ignoreTransforms"), hierarchy);
        // Source allowCollision controls unlisted colliders, not this explicit list.
        component.Colliders = ResolveColliderSequence(GetNode(fields, "colliders"), hierarchy);
        component.ElasticityDistrib = ParseSourceCurve(fields, "pullCurve");
        component.DampingDistrib = ParseSourceCurve(fields, "springCurve", value => 1.0f - spring * value, tangentScale: -spring);
        component.StiffnessDistrib = ParseSourceCurve(fields, "stiffnessCurve");
        component.InertDistrib = ParseSourceCurve(fields, "immobileCurve");
        component.RadiusDistrib = ParseSourceCurve(fields, "radiusCurve");

        // Integration Type selects a force model. It does not select update timing.
        component.UpdateMode = PhysicsChainComponent.EUpdateMode.Normal;

        hierarchy.ComponentsByFileId[behaviour.FileId] = component;
        MarkAdaptedScriptDependency(behaviour, state);
        RecordAvatarFeature(state, behaviour, hierarchy, "avatar.dynamics.chain",
            UnityConversionFeatureStatus.Converted, "Physics chain simulation",
            "The importer attached a native physics chain and mapped its supported fields.",
            nativeTarget: typeof(PhysicsChainComponent).FullName);

        var approximated = new List<string>(8);
        if (HasCurveKeys(fields, "gravityFalloffCurve") || HasCurveKeys(fields, "gravityCurve"))
            approximated.Add("gravity curves");
        if (HasCurveKeys(fields, "limitRotationXCurve") ||
            HasCurveKeys(fields, "limitRotationYCurve") ||
            HasCurveKeys(fields, "limitRotationZCurve"))
        {
            approximated.Add("limit rotation curves");
        }

        if (HasCurveKeys(fields, "maxStretchCurve") ||
            HasCurveKeys(fields, "maxSquishCurve") ||
            HasCurveKeys(fields, "stretchMotionCurve"))
        {
            approximated.Add("stretch and squish curves");
        }

        if ((GetScalarInt(fields, "immobileType") ?? 0) != 0)
            approximated.Add("world immobile type");
        if (integrationType == 1)
            approximated.Add("Momentum read as Spring");

        bool allowsGrabbing = (GetScalarInt(fields, "allowGrabbing") ?? 0) != 0;
        bool allowsPosing = (GetScalarInt(fields, "allowPosing") ?? 0) != 0;
        if (allowsGrabbing || allowsPosing)
        {
            approximated.Add("VRChat grabbing/posing");
            AddAvatarDiagnostic(
                state,
                behaviour,
                hierarchy,
                "UNITYVRC0011",
                SourceImportDiagnosticSeverity.Warning,
                "Source grabbing/posing flags were not mapped onto the generic physics chain. " +
                "A future converter must target native physics-chain interaction and authority inputs so local and remote posing share one runtime contract.");
        }

        if ((GetScalarInt(fields, "isAnimated") ?? 0) != 0)
            approximated.Add("animated-parameter integration");
        if ((GetScalarInt(fields, "multiChildType") ?? 0) != 0)
            approximated.Add("multi-child averaging");

        string mappedSummary =
            "Mapped root, ignored transforms, endpoint, radius/curve, gravity/falloff, pull/curve, spring/curve, " +
            "stiffness/curve, immobility/curve, angle limits, stretch, squish, collision list, and enabled state.";
        string approximationSummary = approximated.Count == 0
            ? " No active parameters required an unsupported approximation."
            : $" Unsupported active semantics: {string.Join(", ", approximated)}.";
        if (approximated.Count != 0)
            RecordAvatarFeature(state, behaviour, hierarchy, "avatar.dynamics.chain.source-semantics",
                UnityConversionFeatureStatus.Unknown, "Physics chain source semantics",
                $"The importer detected active source semantics that it did not map: {string.Join(", ", approximated)}.",
                lostFunctionality: string.Join(", ", approximated));
        AddAvatarDiagnostic(
            state,
            behaviour,
            hierarchy,
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
        {
            RecordAvatarFeature(state, behaviour, hierarchy, "avatar.constraint",
                UnityConversionFeatureStatus.Unknown, "Weighted transform constraint",
                "The native component could not be attached; conversion did not complete.");
            return;
        }

        component.TargetTransform = ResolveTransformReference(GetNode(fields, "TargetTransform"), hierarchy, node.Transform);
        component.Sources = ParseConstraintSources(fields, hierarchy);
        component.Weight = Clamp01(GetScalarFloat(fields, "GlobalWeight") ?? 1.0f);
        component.SolveInLocalSpace = (GetScalarInt(fields, "SolveInLocalSpace") ?? 0) != 0;
        component.Locked = (GetScalarInt(fields, "Locked") ?? 0) != 0;
        component.Channels = ParseConstraintChannels(fields);
        ApplyConstraintOffsetsAndRestPose(component, fields);
        component.FreezeToWorld = (GetScalarInt(fields, "FreezeToWorld") ?? 0) != 0;
        component.IsActive = behaviour.Enabled && (GetScalarInt(fields, "IsActive") ?? 1) != 0;

        hierarchy.ComponentsByFileId[behaviour.FileId] = component;
        MarkAdaptedScriptDependency(behaviour, state);
        RecordAvatarFeature(state, behaviour, hierarchy, "avatar.constraint",
            UnityConversionFeatureStatus.Converted, "Weighted transform constraint",
            "The importer attached a native weighted transform constraint and mapped its supported fields.",
            nativeTarget: typeof(WeightedTransformConstraintComponent).FullName);

        if ((GetScalarInt(fields, "RebakeOffsetsWhenUnfrozen") ?? 0) != 0)
        {
            RecordAvatarFeature(state, behaviour, hierarchy, "avatar.constraint.freeze-rebake",
                UnityConversionFeatureStatus.Unknown, "Constraint freeze and offset rebake",
                "The source enables freeze or rebake editor semantics that the importer did not reproduce.",
                lostFunctionality: "Freeze and rebake editor semantics were not reproduced.");
            AddAvatarDiagnostic(
                state,
                behaviour,
                hierarchy,
                "UNITYVRC0004",
                SourceImportDiagnosticSeverity.Warning,
                "VRChat constraint offset rebake on unfreeze was not reproduced; the authored offsets stay fixed.");
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
        {
            RecordAvatarFeature(state, behaviour, hierarchy, "avatar.presentation",
                UnityConversionFeatureStatus.Unknown, "Avatar presentation",
                "The native component could not be attached; conversion did not complete.");
            return;
        }

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

        hierarchy.ComponentsByFileId[behaviour.FileId] = component;
        MarkAdaptedScriptDependency(behaviour, state);
        RecordAvatarFeature(state, behaviour, hierarchy, "avatar.presentation",
            UnityConversionFeatureStatus.Converted, "Avatar presentation",
            "The importer attached a native avatar presentation component and mapped its supported fields.",
            nativeTarget: typeof(AvatarPresentationComponent).FullName);

        var animationGraph = new SerializedAvatarAnimationGraphRecord
        {
            DescriptorIdentity = new SourceAssetIdentity
            {
                AssetGuid = hierarchy.SourceGuid ?? string.Empty,
                LocalFileId = behaviour.FileId,
                ObjectKind = SourceAssetObjectKind.Component,
            },
            AnimationPreset = ToIdentity(
                ParseReference(GetNode(fields, "AnimationPreset")),
                SourceAssetObjectKind.Asset),
            Layers =
            [
                .. ParseAnimationLayers(GetNode(fields, "baseAnimationLayers"), "baseAnimationLayers"),
                .. ParseAnimationLayers(GetNode(fields, "specialAnimationLayers"), "specialAnimationLayers"),
            ],
            UsesCustomExpressions = (GetScalarInt(fields, "customExpressions") ?? 0) != 0,
            ExpressionMenu = ToIdentity(
                ParseReference(GetNode(fields, "expressionsMenu") ?? GetNode(fields, "ExpressionsMenu")),
                SourceAssetObjectKind.Asset),
            ExpressionParameters = ToIdentity(
                ParseReference(GetNode(fields, "expressionParameters") ?? GetNode(fields, "ExpressionParameters")),
                SourceAssetObjectKind.Asset),
        };
        hierarchy.AvatarAnimationGraphsByFileId[behaviour.FileId] = animationGraph;
        hierarchy.AvatarAnimationGraphOwners[animationGraph] = node;
        state.Context.RegisterAvatarAnimationGraph(node, animationGraph);
        MarkSourceEvidenceOutcome(animationGraph.AnimationPreset, state);
        MarkSourceEvidenceOutcome(animationGraph.ExpressionMenu, state);
        MarkSourceEvidenceOutcome(animationGraph.ExpressionParameters, state);
        foreach (SerializedAvatarAnimationLayer layer in animationGraph.Layers)
        {
            MarkSourceEvidenceOutcome(layer.Controller, state);
            MarkSourceEvidenceOutcome(layer.Mask, state);
        }

        bool hasExecutableLayerEvidence = animationGraph.AnimationPreset is not null ||
            animationGraph.Layers.Any(static layer => layer.Enabled && layer.Controller is not null);
        if (hasExecutableLayerEvidence)
            RecordAvatarFeature(state, behaviour, hierarchy, "avatar.animation-layers",
                UnityConversionFeatureStatus.MissingConverter, "Avatar animation layers",
                "The importer retained layer references but did not merge controllers into the native animation state machine.",
                nativeTarget: "XREngine.Components.AnimStateMachineComponent",
                lostFunctionality: "Avatar animation layers were not attached for playback.");
        AddAvatarDiagnostic(
            state,
            behaviour,
            hierarchy,
            "UNITYVRC0009",
            hasExecutableLayerEvidence
                ? SourceImportDiagnosticSeverity.Warning
                : SourceImportDiagnosticSeverity.Info,
            $"Retained {animationGraph.Layers.Count} avatar animation layer record(s) in the import manifest. " +
            "No layer metadata was published on the runtime avatar and no controller was attached because a compiler has not yet merged the supported layers into one native AnimStateMachineComponent.");

        if (animationGraph.UsesCustomExpressions ||
            animationGraph.ExpressionMenu is not null ||
            animationGraph.ExpressionParameters is not null)
        {
            RecordAvatarFeature(state, behaviour, hierarchy, "avatar.expressions",
                UnityConversionFeatureStatus.Unknown, "Avatar expressions",
                "The importer retained expression references, but no native expression mapping was selected.",
                lostFunctionality: "Expression menus and parameters were not executable after import.");
            AddAvatarDiagnostic(
                state,
                behaviour,
                hierarchy,
                "UNITYVRC0010",
                SourceImportDiagnosticSeverity.Warning,
                "Avatar expression menu and parameter assets were retained in the import manifest as intentional conversion loss. " +
                "They will remain non-executable until a native expression graph can feed the avatar's single animation state machine.");
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
            hierarchy,
            "UNITYVRC0006",
            SourceImportDiagnosticSeverity.Info,
            "VRChat PipelineManager upload identity and SDK pipeline status were intentionally ignored as editor-only metadata.");
    }

    private static void PreserveUnsupportedBehaviour(
        ParsedMonoBehaviour behaviour,
        ImportedHierarchy hierarchy,
        ImportState state)
    {
        RecordAvatarFeature(state, behaviour, hierarchy, "unity.behaviour.unknown",
            UnityConversionFeatureStatus.Unknown, "Source MonoBehaviour",
            "No selected native converter recognized the source script identity.");
        hierarchy.NodesByGameObjectId.TryGetValue(behaviour.GameObjectFileId, out SceneNode? node);
        List<string> serializedFieldNames = CreateBoundedSerializedFieldNames(
            behaviour.SerializedFields,
            out bool serializedFieldNamesTruncated);
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
            SerializedPayloadByteCount = System.Text.Encoding.UTF8.GetByteCount(behaviour.SerializedYaml),
            SerializedPayloadSha256 = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(behaviour.SerializedYaml))),
            SerializedFieldCount = behaviour.SerializedFields.Children.Count,
            SerializedFieldNames = serializedFieldNames,
            SerializedFieldNamesTruncated = serializedFieldNamesTruncated,
        });
        MarkScriptDependencyOutcome(behaviour, state, SourceImportConversionOutcome.IgnoredOptional);
        AddAvatarDiagnostic(
            state,
            behaviour,
            hierarchy,
            "UNITYVRC0007",
            SourceImportDiagnosticSeverity.Info,
            $"Unsupported MonoBehaviour script '{behaviour.Script.Guid}:{behaviour.Script.FileId}' was summarized in the import manifest without retaining opaque YAML or attaching a fake runtime behavior.");
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
            hierarchy,
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

    private static IEnumerable<SerializedAvatarAnimationLayer> ParseAnimationLayers(
        YamlNode? node,
        string sourceCollection)
    {
        if (node is not YamlSequenceNode sequence)
            yield break;

        for (int sourceIndex = 0; sourceIndex < sequence.Children.Count; sourceIndex++)
        {
            YamlNode child = sequence.Children[sourceIndex];
            if (child is not YamlMappingNode layer)
                continue;

            yield return new SerializedAvatarAnimationLayer
            {
                SourceCollection = sourceCollection,
                SourceIndex = sourceIndex,
                LayerType = GetScalarInt(layer, "type") ?? 0,
                Enabled = (GetScalarInt(layer, "isEnabled") ?? 0) != 0,
                IsDefault = (GetScalarInt(layer, "isDefault") ?? 0) != 0,
                Controller = ToIdentity(ParseReference(GetNode(layer, "animatorController")), SourceAssetObjectKind.Asset),
                Mask = ToIdentity(ParseReference(GetNode(layer, "mask")), SourceAssetObjectKind.Asset),
            };
        }
    }

    private static void MarkSourceEvidenceOutcome(SourceAssetIdentity? identity, ImportState state)
    {
        if (identity is null || string.IsNullOrWhiteSpace(identity.AssetGuid))
            return;

        string? sourcePath = ResolveAssetPath(state, identity.AssetGuid);
        if (!string.IsNullOrWhiteSpace(sourcePath) && File.Exists(sourcePath))
            state.Context.MarkOutcome(sourcePath, SourceImportConversionOutcome.IgnoredOptional);
    }

    private static List<string> CreateBoundedSerializedFieldNames(
        YamlMappingNode fields,
        out bool truncated)
    {
        const int maximumFieldCount = 64;
        const int maximumFieldNameLength = 96;
        const int maximumAggregateCharacters = 2_048;

        string[] sourceNames =
        [
            .. fields.Children.Keys
                .OfType<YamlScalarNode>()
                .Select(static key => key.Value ?? string.Empty)
                .Where(static key => !string.IsNullOrWhiteSpace(key))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal),
        ];
        var result = new List<string>(Math.Min(sourceNames.Length, maximumFieldCount));
        int aggregateCharacters = 0;
        truncated = sourceNames.Length > maximumFieldCount;
        for (int index = 0; index < sourceNames.Length && result.Count < maximumFieldCount; index++)
        {
            string sourceName = sourceNames[index];
            string boundedName = sourceName.Length <= maximumFieldNameLength
                ? sourceName
                : sourceName[..maximumFieldNameLength];
            truncated |= boundedName.Length != sourceName.Length;
            if (aggregateCharacters + boundedName.Length > maximumAggregateCharacters)
            {
                truncated = true;
                break;
            }

            result.Add(boundedName);
            aggregateCharacters += boundedName.Length;
        }

        return result;
    }

    /// <summary>
    /// Reads the constraint-level offsets and the rest pose of a VRChat constraint. The offsets are
    /// added to the weighted source pose (PositionOffset in the target's parent space, RotationOffset as Euler
    /// degrees after the source rotation). The rest pose (PositionAtRest and RotationAtRest, the
    /// target's local pose) is where the constraint blends from by its weight and what it holds when
    /// the total source weight is zero. Unity coordinates convert to engine coordinates.
    /// </summary>
    private static void ApplyConstraintOffsetsAndRestPose(WeightedTransformConstraintComponent component, YamlMappingNode fields)
    {
        component.PositionOffset = ConvertPosition(GetVector3(fields, "PositionOffset", Vector3.Zero));
        component.RotationOffset = ConvertEulerDegrees(GetVector3(fields, "RotationOffset", Vector3.Zero));
        bool hasPositionAtRest = GetNode(fields, "PositionAtRest") is not null;
        bool hasRotationAtRest = GetNode(fields, "RotationAtRest") is not null;
        component.HasRestPose = hasPositionAtRest || hasRotationAtRest;
        component.PositionAtRest = ConvertPosition(GetVector3(fields, "PositionAtRest", Vector3.Zero));
        component.RotationAtRest = ConvertEulerDegrees(GetVector3(fields, "RotationAtRest", Vector3.Zero));
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
        float tangentScale = 1.0f)
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
            inSlope *= tangentScale;
            outSlope *= tangentScale;

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
        ImportedHierarchy hierarchy,
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
                AssetGuid = hierarchy.SourceGuid ?? string.Empty,
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
