using System.Numerics;
using XREngine.Components;
using XREngine.Scene.Prefabs;
using XREngine.Scene.Transforms;
using XREngine.UnityScriptHost.Contracts;
using YamlDotNet.RepresentationModel;

namespace XREngine.Scene.Importers;

/// <summary>
/// Converts avatar SDK constraint MonoBehaviours and native (built-in) constraint components into
/// <see cref="WeightedTransformConstraintComponent"/> instances, and assigns their evaluation order
/// after the full hierarchy exists.
/// </summary>
/// <remarks>
/// The avatar SDK converts native constraints to its own constraints by copying each field to the field
/// of the same meaning (for example m_TranslationOffsets to ParentPositionOffset), so both families map
/// to the same component settings. Source coordinates convert to engine coordinates (Z negated).
/// </remarks>
internal static partial class SerializedSceneImporter
{
    private const string AvatarConstraintScriptGuid = "58e2f01a24261a14cb82e6d3399e8b16";

    // The source editor identifies a MonoBehaviour class in a managed DLL by the first four bytes of
    // MD4("s\0\0\0" + namespace + class name). These are the avatar SDK constraint component classes.
    private const long AvatarPositionConstraintScriptFileId = 1116338486;
    private const long AvatarRotationConstraintScriptFileId = 1788371120;
    private const long AvatarScaleConstraintScriptFileId = 41250163;
    private const long AvatarParentConstraintScriptFileId = 575728033;
    private const long AvatarLookAtConstraintScriptFileId = -372946275;
    private const long AvatarAimConstraintScriptFileId = -926596935;

    /// <summary>The source forward axis (+Z) in engine coordinates. Look-at constraints aim it.</summary>
    private static readonly Vector3 SourceForwardInEngine = new(0.0f, 0.0f, -1.0f);

    private enum SourceConstraintKind
    {
        Position,
        Rotation,
        Scale,
        Parent,
        Aim,
        LookAt,
    }

    private static bool IsAvatarConstraint(ParsedMonoBehaviour behaviour)
        => TryGetAvatarConstraintKind(behaviour, out _);

    private static bool TryGetAvatarConstraintKind(ParsedMonoBehaviour behaviour, out SourceConstraintKind kind)
    {
        kind = default;
        if (!string.Equals(behaviour.Script.Guid, AvatarConstraintScriptGuid, StringComparison.OrdinalIgnoreCase))
            return false;

        switch (behaviour.Script.FileId)
        {
            case AvatarPositionConstraintScriptFileId: kind = SourceConstraintKind.Position; return true;
            case AvatarRotationConstraintScriptFileId: kind = SourceConstraintKind.Rotation; return true;
            case AvatarScaleConstraintScriptFileId: kind = SourceConstraintKind.Scale; return true;
            case AvatarParentConstraintScriptFileId: kind = SourceConstraintKind.Parent; return true;
            case AvatarLookAtConstraintScriptFileId: kind = SourceConstraintKind.LookAt; return true;
            case AvatarAimConstraintScriptFileId: kind = SourceConstraintKind.Aim; return true;
            default: return false;
        }
    }

    private static bool TryGetNativeConstraintKind(string typeName, out SourceConstraintKind kind)
    {
        kind = typeName switch
        {
            "PositionConstraint" => SourceConstraintKind.Position,
            "RotationConstraint" => SourceConstraintKind.Rotation,
            "ScaleConstraint" => SourceConstraintKind.Scale,
            "ParentConstraint" => SourceConstraintKind.Parent,
            "AimConstraint" => SourceConstraintKind.Aim,
            "LookAtConstraint" => SourceConstraintKind.LookAt,
            _ => (SourceConstraintKind)(-1),
        };
        return Enum.IsDefined(kind);
    }

    /// <summary>Reads the owner GameObject and the enabled flag of a native constraint document.</summary>
    private static ParsedNativeConstraint ParseNativeConstraint(
        long fileId,
        string typeName,
        YamlMappingNode mapping,
        int documentOrder,
        bool isStripped)
        => new(
            fileId,
            GetReferenceFileId(mapping, "m_GameObject"),
            (GetScalarInt(mapping, "m_Enabled") ?? 1) != 0,
            typeName,
            mapping,
            isStripped,
            documentOrder);

    /// <summary>
    /// Creates the constraint component for an avatar SDK constraint MonoBehaviour. The component is added to
    /// the owner node; TargetTransform names the transform that it moves (the owner when unset).
    /// </summary>
    private static void AttachAvatarConstraint(
        ParsedMonoBehaviour behaviour,
        ImportedHierarchy hierarchy,
        ImportState state)
    {
        if (!TryGetAvatarConstraintKind(behaviour, out SourceConstraintKind kind) ||
            !TryResolveBehaviourNode(behaviour, hierarchy, state, out SceneNode? node) || node is null)
        {
            return;
        }

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
        component.Sources = ParseAvatarConstraintSources(fields, hierarchy);
        component.Weight = Clamp01(GetScalarFloat(fields, "GlobalWeight") ?? 1.0f);
        component.SolveInLocalSpace = (GetScalarInt(fields, "SolveInLocalSpace") ?? 0) != 0;
        component.Locked = (GetScalarInt(fields, "Locked") ?? 0) != 0;
        component.FreezeToWorld = (GetScalarInt(fields, "FreezeToWorld") ?? 0) != 0;
        component.RebakeOffsetsWhenUnfrozen = (GetScalarInt(fields, "RebakeOffsetsWhenUnfrozen") ?? 0) != 0;
        component.Channels = ParseConstraintChannels(
            fields,
            "AffectsPositionX", "AffectsPositionY", "AffectsPositionZ",
            "AffectsRotationX", "AffectsRotationY", "AffectsRotationZ",
            "AffectsScaleX", "AffectsScaleY", "AffectsScaleZ");
        ApplyConstraintPoseSettings(
            component,
            kind,
            fields,
            positionAtRestKey: "PositionAtRest",
            positionOffsetKey: "PositionOffset",
            rotationAtRestKey: "RotationAtRest",
            rotationOffsetKey: "RotationOffset",
            scaleAtRestKey: "ScaleAtRest",
            scaleOffsetKey: "ScaleOffset");

        switch (kind)
        {
            case SourceConstraintKind.Aim:
                ApplyAimSettings(
                    component,
                    GetVector3(fields, "AimAxis", Vector3.UnitZ),
                    GetVector3(fields, "UpAxis", Vector3.UnitY),
                    MapAvatarConstraintWorldUpType(GetScalarInt(fields, "WorldUp") ?? 0),
                    GetVector3(fields, "WorldUpVector", Vector3.UnitY),
                    ResolveTransformReference(GetNode(fields, "WorldUpTransform"), hierarchy));
                break;
            case SourceConstraintKind.LookAt:
                ApplyLookAtSettings(
                    component,
                    (GetScalarInt(fields, "UseUpTransform") ?? 0) != 0,
                    GetScalarFloat(fields, "Roll") ?? 0.0f,
                    ResolveTransformReference(GetNode(fields, "WorldUpTransform"), hierarchy));
                break;
        }

        component.IsActive = behaviour.Enabled && (GetScalarInt(fields, "IsActive") ?? 1) != 0;
        hierarchy.ComponentsByFileId[behaviour.FileId] = component;
        MarkAdaptedScriptDependency(behaviour, state);
        RecordAvatarFeature(state, behaviour, hierarchy, "avatar.constraint",
            UnityConversionFeatureStatus.Converted, "Weighted transform constraint",
            "The importer attached a native weighted transform constraint and mapped its supported fields.",
            nativeTarget: typeof(WeightedTransformConstraintComponent).FullName);
    }

    /// <summary>
    /// Creates the constraint component for a native constraint. A native constraint always moves
    /// its own GameObject and always solves in world space.
    /// </summary>
    private static void AttachNativeConstraint(
        ParsedNativeConstraint constraint,
        ImportedHierarchy hierarchy,
        ImportState state)
    {
        if (!TryGetNativeConstraintKind(constraint.TypeName, out SourceConstraintKind kind))
            return;

        if (!hierarchy.NodesByGameObjectId.TryGetValue(constraint.GameObjectFileId, out SceneNode? node))
        {
            AddAvatarDiagnostic(
                state,
                constraint.FileId,
                hierarchy,
                "SRCCONSTRAINT0001",
                SourceImportDiagnosticSeverity.Error,
                $"{constraint.TypeName} owner GameObject fileID '{constraint.GameObjectFileId}' could not be resolved after prefab correspondence binding.");
            return;
        }

        YamlMappingNode fields = constraint.Fields;
        WeightedTransformConstraintComponent? component = node.AddComponent<WeightedTransformConstraintComponent>();
        if (component is null)
            return;

        component.TargetTransform = node.Transform;
        component.Sources = ParseNativeConstraintSources(fields, hierarchy, kind == SourceConstraintKind.Parent);
        component.Weight = Clamp01(GetScalarFloat(fields, "m_Weight") ?? 1.0f);
        component.SolveInLocalSpace = false;
        component.Locked = (GetScalarInt(fields, "m_IsLocked") ?? 0) != 0;
        component.Channels = kind == SourceConstraintKind.LookAt
            ? TransformConstraintChannels.Rotation
            : ParseConstraintChannels(
                fields,
                "m_AffectTranslationX", "m_AffectTranslationY", "m_AffectTranslationZ",
                "m_AffectRotationX", "m_AffectRotationY", "m_AffectRotationZ",
                "m_AffectScalingX", "m_AffectScalingY", "m_AffectScalingZ");
        ApplyConstraintPoseSettings(
            component,
            kind,
            fields,
            positionAtRestKey: "m_TranslationAtRest",
            positionOffsetKey: "m_TranslationOffset",
            rotationAtRestKey: "m_RotationAtRest",
            rotationOffsetKey: "m_RotationOffset",
            scaleAtRestKey: "m_ScaleAtRest",
            scaleOffsetKey: "m_ScaleOffset");

        switch (kind)
        {
            case SourceConstraintKind.Aim:
                ApplyAimSettings(
                    component,
                    GetVector3(fields, "m_AimVector", Vector3.UnitZ),
                    GetVector3(fields, "m_UpVector", Vector3.UnitY),
                    MapNativeConstraintWorldUpType(GetScalarInt(fields, "m_UpType") ?? 0),
                    GetVector3(fields, "m_WorldUpVector", Vector3.UnitY),
                    ResolveTransformReference(GetNode(fields, "m_WorldUpObject"), hierarchy));
                break;
            case SourceConstraintKind.LookAt:
                ApplyLookAtSettings(
                    component,
                    (GetScalarInt(fields, "m_UseUpObject") ?? 0) != 0,
                    GetScalarFloat(fields, "m_Roll") ?? 0.0f,
                    ResolveTransformReference(GetNode(fields, "m_WorldUpObject"), hierarchy));
                break;
        }

        // LookAtConstraint stores its active flag under a misspelled name.
        int active = GetScalarInt(fields, "m_Active") ?? GetScalarInt(fields, "m_IsContraintActive") ?? 1;
        component.IsActive = constraint.Enabled && active != 0;
        hierarchy.ComponentsByFileId[constraint.FileId] = component;
    }

    /// <summary>
    /// Sets the mode, offsets, and rest pose for one constraint kind. Only the keys that the kind owns
    /// are read: position constraints read position values, rotation, aim, and look-at constraints read
    /// rotation values, scale constraints read scale values, and parent constraints read the position
    /// and rotation rest pose (their offsets are per source). The rest pose is the target's local pose;
    /// the position offset is in the target's parent space; rotation values are Euler degrees; the scale
    /// offset multiplies the source scale.
    /// </summary>
    private static void ApplyConstraintPoseSettings(
        WeightedTransformConstraintComponent component,
        SourceConstraintKind kind,
        YamlMappingNode fields,
        string positionAtRestKey,
        string positionOffsetKey,
        string rotationAtRestKey,
        string rotationOffsetKey,
        string scaleAtRestKey,
        string scaleOffsetKey)
    {
        component.Mode = kind switch
        {
            SourceConstraintKind.Parent => TransformConstraintMode.Parent,
            SourceConstraintKind.Aim or SourceConstraintKind.LookAt => TransformConstraintMode.Aim,
            _ => TransformConstraintMode.Match,
        };

        bool readsPosition = kind is SourceConstraintKind.Position or SourceConstraintKind.Parent;
        bool readsRotation = kind is not SourceConstraintKind.Position and not SourceConstraintKind.Scale;
        bool readsScale = kind == SourceConstraintKind.Scale;
        bool hasRestPose = false;

        if (readsPosition)
        {
            hasRestPose |= GetNode(fields, positionAtRestKey) is not null;
            component.PositionAtRest = ConvertPosition(GetVector3(fields, positionAtRestKey, Vector3.Zero));
            if (kind == SourceConstraintKind.Position)
                component.PositionOffset = ConvertPosition(GetVector3(fields, positionOffsetKey, Vector3.Zero));
        }

        if (readsRotation)
        {
            hasRestPose |= GetNode(fields, rotationAtRestKey) is not null;
            component.RotationAtRest = ConvertEulerDegrees(GetVector3(fields, rotationAtRestKey, Vector3.Zero));
            if (kind != SourceConstraintKind.Parent)
                component.RotationOffset = ConvertEulerDegrees(GetVector3(fields, rotationOffsetKey, Vector3.Zero));
        }

        if (readsScale)
        {
            hasRestPose |= GetNode(fields, scaleAtRestKey) is not null;
            component.ScaleAtRest = GetVector3(fields, scaleAtRestKey, Vector3.One);
            component.ScaleOffset = GetVector3(fields, scaleOffsetKey, Vector3.One);
        }

        component.HasRestPose = hasRestPose;
    }

    /// <summary>Sets the aim settings of an aim constraint, converting the axes to engine coordinates.</summary>
    private static void ApplyAimSettings(
        WeightedTransformConstraintComponent component,
        Vector3 aimAxis,
        Vector3 upAxis,
        TransformConstraintWorldUpType worldUpType,
        Vector3 worldUpVector,
        TransformBase? worldUpTransform)
    {
        component.AimAxis = ConvertPosition(aimAxis);
        component.UpAxis = ConvertPosition(upAxis);
        component.WorldUpType = worldUpType;
        component.WorldUpVector = ConvertPosition(worldUpVector);
        component.WorldUpTransform = worldUpTransform;
        component.Roll = 0.0f;
    }

    /// <summary>
    /// Sets the aim settings of a look-at constraint: the forward axis aims at the sources and +Y turns
    /// toward the scene up direction, or toward the up transform when one is used. Roll applies only
    /// without an up transform. A source rotation of r degrees around +Z is a rotation of -r degrees around
    /// the engine forward axis (-Z), because the conversion negates Z.
    /// </summary>
    private static void ApplyLookAtSettings(
        WeightedTransformConstraintComponent component,
        bool useUpTransform,
        float rollDegrees,
        TransformBase? worldUpTransform)
    {
        component.AimAxis = SourceForwardInEngine;
        component.UpAxis = Vector3.UnitY;
        component.WorldUpType = useUpTransform
            ? TransformConstraintWorldUpType.ObjectUp
            : TransformConstraintWorldUpType.SceneUp;
        component.WorldUpVector = Vector3.UnitY;
        component.WorldUpTransform = useUpTransform ? worldUpTransform : null;
        component.Roll = useUpTransform ? 0.0f : -rollDegrees;
    }

    /// <summary>Avatar SDK WorldUpType values: SceneUp, ObjectUp, ObjectRotationUp, Vector, None.</summary>
    private static TransformConstraintWorldUpType MapAvatarConstraintWorldUpType(int value)
        => value switch
        {
            1 => TransformConstraintWorldUpType.ObjectUp,
            2 => TransformConstraintWorldUpType.ObjectRotationUp,
            3 => TransformConstraintWorldUpType.Vector,
            4 => TransformConstraintWorldUpType.None,
            _ => TransformConstraintWorldUpType.SceneUp,
        };

    /// <summary>Native AimConstraint.WorldUpType values: SceneUp, ObjectRotationUp, ObjectUp, Vector, None.</summary>
    private static TransformConstraintWorldUpType MapNativeConstraintWorldUpType(int value)
        => value switch
        {
            1 => TransformConstraintWorldUpType.ObjectRotationUp,
            2 => TransformConstraintWorldUpType.ObjectUp,
            3 => TransformConstraintWorldUpType.Vector,
            4 => TransformConstraintWorldUpType.None,
            _ => TransformConstraintWorldUpType.SceneUp,
        };

    /// <summary>
    /// Reads an avatar SDK keyable source list. The SDK stores up to 16 sources in fields source0 to source15,
    /// the used count in totalLength, and further sources in overflowList. Without totalLength every
    /// mapping entry counts. Sources with a weight of zero are kept, because animation can raise the
    /// weight; entries without a transform are dropped.
    /// </summary>
    private static List<TransformConstraintSource> ParseAvatarConstraintSources(
        YamlMappingNode fields,
        ImportedHierarchy hierarchy)
    {
        if (GetNode(fields, "Sources") is not YamlMappingNode sourceCollection)
            return [];

        var result = new List<TransformConstraintSource>();
        int? totalLength = GetScalarInt(sourceCollection, "totalLength");
        int flatCount = 0;
        foreach ((YamlNode _, YamlNode value) in sourceCollection.Children)
        {
            if (value is not YamlMappingNode source)
                continue;
            if (totalLength.HasValue && flatCount >= totalLength.Value)
                break;

            flatCount++;
            AddAvatarConstraintSource(source, hierarchy, result);
        }

        if (GetNode(sourceCollection, "overflowList") is YamlSequenceNode overflow)
        {
            foreach (YamlNode value in overflow.Children)
            {
                if (value is YamlMappingNode source)
                    AddAvatarConstraintSource(source, hierarchy, result);
            }
        }

        return result;
    }

    private static void AddAvatarConstraintSource(
        YamlMappingNode source,
        ImportedHierarchy hierarchy,
        List<TransformConstraintSource> result)
    {
        TransformBase? sourceTransform = ResolveTransformReference(GetNode(source, "SourceTransform"), hierarchy);
        if (sourceTransform is null)
            return;

        result.Add(new TransformConstraintSource
        {
            SourceTransform = sourceTransform,
            Weight = MathF.Max(0.0f, GetScalarFloat(source, "Weight") ?? 0.0f),
            PositionOffset = ConvertPosition(GetVector3(source, "ParentPositionOffset", Vector3.Zero)),
            RotationOffset = ConvertEulerDegrees(GetVector3(source, "ParentRotationOffset", Vector3.Zero)),
        });
    }

    /// <summary>
    /// Reads the m_Sources list of a native constraint. A parent constraint keeps its per-source
    /// offsets in the parallel lists m_TranslationOffsets and m_RotationOffsets.
    /// </summary>
    private static List<TransformConstraintSource> ParseNativeConstraintSources(
        YamlMappingNode fields,
        ImportedHierarchy hierarchy,
        bool readParentOffsets)
    {
        if (GetNode(fields, "m_Sources") is not YamlSequenceNode sourceSequence)
            return [];

        YamlSequenceNode? translationOffsets = readParentOffsets ? GetNode(fields, "m_TranslationOffsets") as YamlSequenceNode : null;
        YamlSequenceNode? rotationOffsets = readParentOffsets ? GetNode(fields, "m_RotationOffsets") as YamlSequenceNode : null;
        var result = new List<TransformConstraintSource>(sourceSequence.Children.Count);
        for (int index = 0; index < sourceSequence.Children.Count; index++)
        {
            if (sourceSequence.Children[index] is not YamlMappingNode source)
                continue;

            TransformBase? sourceTransform = ResolveTransformReference(GetNode(source, "sourceTransform"), hierarchy);
            if (sourceTransform is null)
                continue;

            result.Add(new TransformConstraintSource
            {
                SourceTransform = sourceTransform,
                Weight = MathF.Max(0.0f, GetScalarFloat(source, "weight") ?? 0.0f),
                PositionOffset = ConvertPosition(GetSequenceVector3(translationOffsets, index)),
                RotationOffset = ConvertEulerDegrees(GetSequenceVector3(rotationOffsets, index)),
            });
        }

        return result;
    }

    private static Vector3 GetSequenceVector3(YamlSequenceNode? sequence, int index)
    {
        if (sequence is null || index >= sequence.Children.Count || sequence.Children[index] is not YamlMappingNode vector)
            return Vector3.Zero;

        return new Vector3(
            GetScalarFloat(vector, "x") ?? 0.0f,
            GetScalarFloat(vector, "y") ?? 0.0f,
            GetScalarFloat(vector, "z") ?? 0.0f);
    }

    /// <summary>Reads the nine affect flags (position, rotation, scale; X, Y, Z) into channel flags.</summary>
    private static TransformConstraintChannels ParseConstraintChannels(
        YamlMappingNode fields,
        string positionX, string positionY, string positionZ,
        string rotationX, string rotationY, string rotationZ,
        string scaleX, string scaleY, string scaleZ)
    {
        TransformConstraintChannels result = TransformConstraintChannels.None;
        AddConstraintChannel(fields, positionX, TransformConstraintChannels.PositionX, ref result);
        AddConstraintChannel(fields, positionY, TransformConstraintChannels.PositionY, ref result);
        AddConstraintChannel(fields, positionZ, TransformConstraintChannels.PositionZ, ref result);
        AddConstraintChannel(fields, rotationX, TransformConstraintChannels.RotationX, ref result);
        AddConstraintChannel(fields, rotationY, TransformConstraintChannels.RotationY, ref result);
        AddConstraintChannel(fields, rotationZ, TransformConstraintChannels.RotationZ, ref result);
        AddConstraintChannel(fields, scaleX, TransformConstraintChannels.ScaleX, ref result);
        AddConstraintChannel(fields, scaleY, TransformConstraintChannels.ScaleY, ref result);
        AddConstraintChannel(fields, scaleZ, TransformConstraintChannels.ScaleZ, ref result);
        return result;
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

    /// <summary>
    /// Collects every constraint under the composed roots and assigns its evaluation order from the
    /// constraint dependencies. This runs once on the final hierarchy, because a constraint in one
    /// prefab can read a transform that a constraint in a nested prefab moves.
    /// </summary>
    private static void AssignConstraintExecutionOrders(IEnumerable<SceneNode> roots, ImportState state)
    {
        var constraints = new List<WeightedTransformConstraintComponent>();
        foreach (SceneNode root in roots)
            CollectConstraints(root, constraints);

        if (constraints.Count == 0)
            return;

        int ignoredCycleEdges = TransformConstraintExecutionOrder.Assign(constraints);
        if (ignoredCycleEdges == 0)
            return;

        state.Context.AddDiagnostic(
            "SRCCONSTRAINT0002",
            SourceImportDiagnosticSeverity.Warning,
            SourceImportDiagnosticCategory.AvatarComponent,
            $"{ignoredCycleEdges} constraint dependencies form cycles; those dependencies were ignored when the evaluation order was assigned.",
            state.EntryFilePath);
    }

    private static void CollectConstraints(SceneNode node, List<WeightedTransformConstraintComponent> constraints)
    {
        foreach (WeightedTransformConstraintComponent constraint in node.GetComponents<WeightedTransformConstraintComponent>())
            constraints.Add(constraint);

        foreach (TransformBase childTransform in node.Transform.Children)
            if (childTransform.SceneNode is SceneNode childNode)
                CollectConstraints(childNode, constraints);
    }
}
