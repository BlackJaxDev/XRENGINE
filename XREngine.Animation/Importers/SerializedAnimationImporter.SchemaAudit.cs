using YamlDotNet.RepresentationModel;

namespace XREngine.Animation.Importers;

public static partial class AnimYamlImporter
{
    private static readonly HashSet<string> AnimationClipSchemaKeys = new(StringComparer.Ordinal)
    {
        "serializedVersion", "m_ObjectHideFlags", "m_CorrespondingSourceObject", "m_PrefabAsset",
        "m_PrefabInstance", "m_PrefabParentObject", "m_PrefabInternal", "m_Name",
        "m_Legacy", "m_Compressed", "m_UseHighQualityCurve", "m_RotationCurves", "m_CompressedRotationCurves",
        "m_EulerCurves", "m_PositionCurves", "m_ScaleCurves", "m_FloatCurves", "m_PPtrCurves", "m_SampleRate",
        "m_WrapMode", "m_Bounds", "m_ClipBindingConstant", "m_AnimationClipSettings", "m_EditorCurves",
        "m_EulerEditorCurves", "m_HasGenericRootTransform", "m_HasMotionFloatCurves", "m_GenerateMotionCurves",
        "m_Events", "m_MuscleClip"
    };

    private static readonly HashSet<string> AssetReferenceSchemaKeys = new(StringComparer.Ordinal)
    {
        "fileID", "guid", "type"
    };

    private static readonly HashSet<string> EditableCurveSchemaKeys = new(StringComparer.Ordinal)
    {
        "serializedVersion", "curve", "attribute", "path", "classID", "script", "flags"
    };

    private static readonly HashSet<string> CurveDataSchemaKeys = new(StringComparer.Ordinal)
    {
        "serializedVersion", "m_Curve", "m_PreInfinity", "m_PostInfinity", "m_RotationOrder"
    };

    private static readonly HashSet<string> CurveKeySchemaKeys = new(StringComparer.Ordinal)
    {
        "serializedVersion", "time", "value", "inSlope", "outSlope", "tangentMode", "weightedMode",
        "inWeight", "outWeight"
    };

    private static readonly HashSet<string> VectorSchemaKeys = new(StringComparer.Ordinal)
    {
        "x", "y", "z", "w"
    };

    private static readonly HashSet<string> ObjectCurveSchemaKeys = new(StringComparer.Ordinal)
    {
        "serializedVersion", "curve", "attribute", "path", "classID", "script", "flags"
    };

    private static readonly HashSet<string> ObjectCurveKeySchemaKeys = new(StringComparer.Ordinal)
    {
        "serializedVersion", "time", "value"
    };

    private static readonly HashSet<string> CompressedRotationCurveSchemaKeys = new(StringComparer.Ordinal)
    {
        "m_Path", "path", "m_Times", "m_Values", "m_Slopes", "m_PreInfinity", "m_PostInfinity"
    };

    private static readonly HashSet<string> PackedVectorSchemaKeys = new(StringComparer.Ordinal)
    {
        "m_NumItems", "m_Range", "m_Start", "m_Data", "m_BitSize"
    };

    private static readonly HashSet<string> BoundsSchemaKeys = new(StringComparer.Ordinal)
    {
        "m_Center", "m_Extent"
    };

    private static readonly HashSet<string> ClipBindingConstantSchemaKeys = new(StringComparer.Ordinal)
    {
        "genericBindings", "pptrCurveMapping"
    };

    private static readonly HashSet<string> GenericBindingSchemaKeys = new(StringComparer.Ordinal)
    {
        "serializedVersion", "path", "attribute", "script", "typeID", "classID", "customType",
        "isPPtrCurve", "isIntCurve", "isSerializeReferenceCurve"
    };

    private static readonly HashSet<string> ClipSettingsSchemaKeys = new(StringComparer.Ordinal)
    {
        "serializedVersion", "m_AdditiveReferencePoseClip", "m_AdditiveReferencePoseTime", "m_StartTime",
        "m_StopTime", "m_OrientationOffsetY", "m_Level", "m_CycleOffset", "m_HasAdditiveReferencePose",
        "m_LoopTime", "m_LoopBlend", "m_LoopBlendOrientation", "m_LoopBlendPositionY", "m_LoopBlendPositionXZ",
        "m_KeepOriginalOrientation", "m_KeepOriginalPositionY", "m_KeepOriginalPositionXZ", "m_HeightFromFeet",
        "m_Mirror"
    };

    private static readonly HashSet<string> MuscleClipSchemaKeys = new(StringComparer.Ordinal)
    {
        "m_StopTime", "m_Clip"
    };

    private static readonly HashSet<string> SerializedClipSchemaKeys = new(StringComparer.Ordinal)
    {
        "m_StreamedClip", "m_DenseClip", "m_ConstantClip"
    };

    private static readonly HashSet<string> StreamedClipSchemaKeys = new(StringComparer.Ordinal)
    {
        "data", "m_Data", "curveCount"
    };

    private static readonly HashSet<string> DenseClipSchemaKeys = new(StringComparer.Ordinal)
    {
        "m_FrameCount", "frameCount", "m_CurveCount", "curveCount", "m_SampleRate", "sampleRate",
        "m_BeginTime", "beginTime", "m_SampleArray", "sampleArray"
    };

    private static readonly HashSet<string> ConstantClipSchemaKeys = new(StringComparer.Ordinal)
    {
        "data", "m_Data"
    };

    private static readonly HashSet<string> AnimationEventSchemaKeys = new(StringComparer.Ordinal)
    {
        "time", "functionName", "data", "stringParameter", "objectReferenceParameter", "floatParameter",
        "intParameter", "messageOptions"
    };

    private static void AuditSourceSchema(YamlMappingNode clipMap, int serializedVersion,
        ImportedAnimationImportManifestBuilder manifestBuilder)
    {
        if (!ImportedAnimationImportCapabilityContract.SupportsSerializedVersion(serializedVersion))
            return;

        AuditMappingFields(clipMap, "AnimationClip", AnimationClipSchemaKeys, serializedVersion, manifestBuilder);
        foreach (var entry in clipMap.Children)
        {
            string field = entry.Key is YamlScalarNode scalar ? scalar.Value ?? "<unknown>" : "<unknown>";
            AuditNestedVersions(entry.Value, $"AnimationClip.{field}", serializedVersion, manifestBuilder);
        }

        AuditAssetReference(GetNodeOrNull(clipMap, "m_CorrespondingSourceObject"),
            "AnimationClip.m_CorrespondingSourceObject", serializedVersion, manifestBuilder);
        AuditAssetReference(GetNodeOrNull(clipMap, "m_PrefabAsset"),
            "AnimationClip.m_PrefabAsset", serializedVersion, manifestBuilder);
        AuditAssetReference(GetNodeOrNull(clipMap, "m_PrefabInstance"),
            "AnimationClip.m_PrefabInstance", serializedVersion, manifestBuilder);
        AuditAssetReference(GetNodeOrNull(clipMap, "m_PrefabParentObject"),
            "AnimationClip.m_PrefabParentObject", serializedVersion, manifestBuilder);
        AuditAssetReference(GetNodeOrNull(clipMap, "m_PrefabInternal"),
            "AnimationClip.m_PrefabInternal", serializedVersion, manifestBuilder);

        AuditEditableCurves(clipMap, "m_RotationCurves", serializedVersion, manifestBuilder);
        AuditEditableCurves(clipMap, "m_EulerCurves", serializedVersion, manifestBuilder);
        AuditEditableCurves(clipMap, "m_PositionCurves", serializedVersion, manifestBuilder);
        AuditEditableCurves(clipMap, "m_ScaleCurves", serializedVersion, manifestBuilder);
        AuditEditableCurves(clipMap, "m_FloatCurves", serializedVersion, manifestBuilder);
        AuditEditableCurves(clipMap, "m_EditorCurves", serializedVersion, manifestBuilder);
        AuditEditableCurves(clipMap, "m_EulerEditorCurves", serializedVersion, manifestBuilder);
        AuditCompressedRotationCurves(clipMap, serializedVersion, manifestBuilder);
        AuditObjectReferenceCurves(clipMap, serializedVersion, manifestBuilder);
        AuditBounds(clipMap, serializedVersion, manifestBuilder);
        AuditBindingConstant(clipMap, serializedVersion, manifestBuilder);
        AuditClipSettings(clipMap, serializedVersion, manifestBuilder);
        AuditMuscleClip(clipMap, serializedVersion, manifestBuilder);
        AuditAnimationEvents(clipMap, serializedVersion, manifestBuilder);
    }

    private static void AuditEditableCurves(YamlMappingNode clipMap, string field, int clipVersion,
        ImportedAnimationImportManifestBuilder manifestBuilder)
    {
        if (GetSequenceOrNull(clipMap, field) is not { } curves)
            return;

        for (int curveIndex = 0; curveIndex < curves.Children.Count; curveIndex++)
        {
            string curvePath = $"AnimationClip.{field}[{curveIndex}]";
            if (curves.Children[curveIndex] is not YamlMappingNode curve)
                continue;

            AuditMappingFields(curve, curvePath, EditableCurveSchemaKeys, clipVersion, manifestBuilder);
            AuditAssetReference(GetNodeOrNull(curve, "script"), $"{curvePath}.script", clipVersion, manifestBuilder);
            if (GetMappingOrNull(curve, "curve") is not { } curveData)
                continue;

            string curveDataPath = $"{curvePath}.curve";
            AuditMappingFields(curveData, curveDataPath, CurveDataSchemaKeys, clipVersion, manifestBuilder);
            if (GetSequenceOrNull(curveData, "m_Curve") is not { } keys)
                continue;

            for (int keyIndex = 0; keyIndex < keys.Children.Count; keyIndex++)
            {
                string keyPath = $"{curveDataPath}.m_Curve[{keyIndex}]";
                if (keys.Children[keyIndex] is not YamlMappingNode key)
                    continue;

                AuditMappingFields(key, keyPath, CurveKeySchemaKeys, clipVersion, manifestBuilder);
                AuditVectorIfMapping(GetNodeOrNull(key, "value"), $"{keyPath}.value", clipVersion, manifestBuilder);
                AuditVectorIfMapping(GetNodeOrNull(key, "inSlope"), $"{keyPath}.inSlope", clipVersion, manifestBuilder);
                AuditVectorIfMapping(GetNodeOrNull(key, "outSlope"), $"{keyPath}.outSlope", clipVersion, manifestBuilder);
                AuditVectorIfMapping(GetNodeOrNull(key, "inWeight"), $"{keyPath}.inWeight", clipVersion, manifestBuilder);
                AuditVectorIfMapping(GetNodeOrNull(key, "outWeight"), $"{keyPath}.outWeight", clipVersion, manifestBuilder);
            }
        }
    }

    private static void AuditCompressedRotationCurves(YamlMappingNode clipMap, int clipVersion,
        ImportedAnimationImportManifestBuilder manifestBuilder)
    {
        if (GetSequenceOrNull(clipMap, "m_CompressedRotationCurves") is not { } curves)
            return;

        for (int curveIndex = 0; curveIndex < curves.Children.Count; curveIndex++)
        {
            string curvePath = $"AnimationClip.m_CompressedRotationCurves[{curveIndex}]";
            if (curves.Children[curveIndex] is not YamlMappingNode curve)
                continue;

            AuditMappingFields(curve, curvePath, CompressedRotationCurveSchemaKeys, clipVersion, manifestBuilder);
            AuditPackedVector(GetNodeOrNull(curve, "m_Times"), $"{curvePath}.m_Times", clipVersion, manifestBuilder);
            AuditPackedVector(GetNodeOrNull(curve, "m_Values"), $"{curvePath}.m_Values", clipVersion, manifestBuilder);
            AuditPackedVector(GetNodeOrNull(curve, "m_Slopes"), $"{curvePath}.m_Slopes", clipVersion, manifestBuilder);
        }
    }

    private static void AuditObjectReferenceCurves(YamlMappingNode clipMap, int clipVersion,
        ImportedAnimationImportManifestBuilder manifestBuilder)
    {
        if (GetSequenceOrNull(clipMap, "m_PPtrCurves") is not { } curves)
            return;

        for (int curveIndex = 0; curveIndex < curves.Children.Count; curveIndex++)
        {
            string curvePath = $"AnimationClip.m_PPtrCurves[{curveIndex}]";
            if (curves.Children[curveIndex] is not YamlMappingNode curve)
                continue;

            AuditMappingFields(curve, curvePath, ObjectCurveSchemaKeys, clipVersion, manifestBuilder);
            AuditAssetReference(GetNodeOrNull(curve, "script"), $"{curvePath}.script", clipVersion, manifestBuilder);
            YamlSequenceNode? keys = GetSequenceOrNull(curve, "curve");
            if (keys is null && GetMappingOrNull(curve, "curve") is { } curveData)
            {
                AuditMappingFields(curveData, $"{curvePath}.curve", CurveDataSchemaKeys, clipVersion, manifestBuilder);
                keys = GetSequenceOrNull(curveData, "m_Curve");
            }
            if (keys is null)
                continue;

            for (int keyIndex = 0; keyIndex < keys.Children.Count; keyIndex++)
            {
                string keyPath = $"{curvePath}.curve[{keyIndex}]";
                if (keys.Children[keyIndex] is not YamlMappingNode key)
                    continue;

                AuditMappingFields(key, keyPath, ObjectCurveKeySchemaKeys, clipVersion, manifestBuilder);
                AuditAssetReference(GetNodeOrNull(key, "value"), $"{keyPath}.value", clipVersion, manifestBuilder);
            }
        }
    }

    private static void AuditBounds(YamlMappingNode clipMap, int clipVersion,
        ImportedAnimationImportManifestBuilder manifestBuilder)
    {
        if (GetMappingOrNull(clipMap, "m_Bounds") is not { } bounds)
            return;

        const string path = "AnimationClip.m_Bounds";
        AuditMappingFields(bounds, path, BoundsSchemaKeys, clipVersion, manifestBuilder);
        AuditVectorIfMapping(GetNodeOrNull(bounds, "m_Center"), $"{path}.m_Center", clipVersion, manifestBuilder);
        AuditVectorIfMapping(GetNodeOrNull(bounds, "m_Extent"), $"{path}.m_Extent", clipVersion, manifestBuilder);
    }

    private static void AuditBindingConstant(YamlMappingNode clipMap, int clipVersion,
        ImportedAnimationImportManifestBuilder manifestBuilder)
    {
        if (GetMappingOrNull(clipMap, "m_ClipBindingConstant") is not { } bindings)
            return;

        const string path = "AnimationClip.m_ClipBindingConstant";
        AuditMappingFields(bindings, path, ClipBindingConstantSchemaKeys, clipVersion, manifestBuilder);
        if (GetSequenceOrNull(bindings, "genericBindings") is { } genericBindings)
        {
            for (int bindingIndex = 0; bindingIndex < genericBindings.Children.Count; bindingIndex++)
            {
                string bindingPath = $"{path}.genericBindings[{bindingIndex}]";
                if (genericBindings.Children[bindingIndex] is not YamlMappingNode binding)
                    continue;

                AuditMappingFields(binding, bindingPath, GenericBindingSchemaKeys, clipVersion, manifestBuilder);
                AuditAssetReference(GetNodeOrNull(binding, "script"), $"{bindingPath}.script", clipVersion, manifestBuilder);
            }
        }
        if (GetSequenceOrNull(bindings, "pptrCurveMapping") is { } references)
        {
            for (int referenceIndex = 0; referenceIndex < references.Children.Count; referenceIndex++)
                AuditAssetReference(references.Children[referenceIndex],
                    $"{path}.pptrCurveMapping[{referenceIndex}]", clipVersion, manifestBuilder);
        }
    }

    private static void AuditClipSettings(YamlMappingNode clipMap, int clipVersion,
        ImportedAnimationImportManifestBuilder manifestBuilder)
    {
        if (GetMappingOrNull(clipMap, "m_AnimationClipSettings") is not { } settings)
            return;

        const string path = "AnimationClip.m_AnimationClipSettings";
        AuditMappingFields(settings, path, ClipSettingsSchemaKeys, clipVersion, manifestBuilder);
        AuditAssetReference(GetNodeOrNull(settings, "m_AdditiveReferencePoseClip"),
            $"{path}.m_AdditiveReferencePoseClip", clipVersion, manifestBuilder);
    }

    private static void AuditMuscleClip(YamlMappingNode clipMap, int clipVersion,
        ImportedAnimationImportManifestBuilder manifestBuilder)
    {
        if (GetMappingOrNull(clipMap, "m_MuscleClip") is not { } muscleClip)
            return;

        const string path = "AnimationClip.m_MuscleClip";
        AuditMappingFields(muscleClip, path, MuscleClipSchemaKeys, clipVersion, manifestBuilder);
        if (GetMappingOrNull(muscleClip, "m_Clip") is not { } serializedClip)
            return;

        string clipPath = $"{path}.m_Clip";
        AuditMappingFields(serializedClip, clipPath, SerializedClipSchemaKeys, clipVersion, manifestBuilder);
        AuditNamedMapping(serializedClip, "m_StreamedClip", StreamedClipSchemaKeys, clipPath, clipVersion, manifestBuilder);
        AuditNamedMapping(serializedClip, "m_DenseClip", DenseClipSchemaKeys, clipPath, clipVersion, manifestBuilder);
        AuditNamedMapping(serializedClip, "m_ConstantClip", ConstantClipSchemaKeys, clipPath, clipVersion, manifestBuilder);
    }

    private static void AuditAnimationEvents(YamlMappingNode clipMap, int clipVersion,
        ImportedAnimationImportManifestBuilder manifestBuilder)
    {
        if (GetSequenceOrNull(clipMap, "m_Events") is not { } events)
            return;

        for (int eventIndex = 0; eventIndex < events.Children.Count; eventIndex++)
        {
            string eventPath = $"AnimationClip.m_Events[{eventIndex}]";
            if (events.Children[eventIndex] is not YamlMappingNode animationEvent)
                continue;

            AuditMappingFields(animationEvent, eventPath, AnimationEventSchemaKeys, clipVersion, manifestBuilder);
            AuditAssetReference(GetNodeOrNull(animationEvent, "objectReferenceParameter"),
                $"{eventPath}.objectReferenceParameter", clipVersion, manifestBuilder);
        }
    }

    private static void AuditNamedMapping(YamlMappingNode parent, string field, HashSet<string> allowedKeys,
        string parentPath, int clipVersion, ImportedAnimationImportManifestBuilder manifestBuilder)
    {
        if (GetMappingOrNull(parent, field) is { } mapping)
            AuditMappingFields(mapping, $"{parentPath}.{field}", allowedKeys, clipVersion, manifestBuilder);
    }

    private static void AuditPackedVector(YamlNode? node, string path, int clipVersion,
        ImportedAnimationImportManifestBuilder manifestBuilder)
    {
        if (node is YamlMappingNode mapping)
            AuditMappingFields(mapping, path, PackedVectorSchemaKeys, clipVersion, manifestBuilder);
    }

    private static void AuditVectorIfMapping(YamlNode? node, string path, int clipVersion,
        ImportedAnimationImportManifestBuilder manifestBuilder)
    {
        if (node is YamlMappingNode mapping)
            AuditMappingFields(mapping, path, VectorSchemaKeys, clipVersion, manifestBuilder);
    }

    private static void AuditAssetReference(YamlNode? node, string path, int clipVersion,
        ImportedAnimationImportManifestBuilder manifestBuilder)
    {
        if (node is YamlMappingNode mapping)
            AuditMappingFields(mapping, path, AssetReferenceSchemaKeys, clipVersion, manifestBuilder);
    }

    private static void AuditMappingFields(YamlMappingNode map, string path, HashSet<string> allowedKeys,
        int clipVersion, ImportedAnimationImportManifestBuilder manifestBuilder)
    {
        foreach (var entry in map.Children)
        {
            if (entry.Key is YamlScalarNode key && !string.IsNullOrEmpty(key.Value)
                && allowedKeys.Contains(key.Value))
                continue;

            string field = entry.Key is YamlScalarNode scalar && !string.IsNullOrEmpty(scalar.Value)
                ? scalar.Value
                : "<non-scalar>";
            string fullPath = $"{path}.{field}";
            manifestBuilder.RecordSection(
                EImportedAnimationDataDomain.SourceEncoding,
                EImportedAnimationCapabilityState.Unsupported,
                fullPath,
                $"AnimationClip serializedVersion {clipVersion} contains unknown behaviorally relevant field '{fullPath}'.",
                entry.Value.ToString());
        }
    }

    private static void AuditNestedVersions(YamlNode node, string path, int clipVersion,
        ImportedAnimationImportManifestBuilder manifestBuilder)
    {
        if (node is YamlMappingNode map)
        {
            if (map.Children.TryGetValue(new YamlScalarNode("serializedVersion"), out YamlNode? versionNode)
                && versionNode is YamlScalarNode versionScalar
                && int.TryParse(versionScalar.Value, out int nestedVersion)
                && !ImportedAnimationImportCapabilityContract.SupportsNestedSerializedVersion(nestedVersion))
                manifestBuilder.RecordSection(
                    EImportedAnimationDataDomain.SourceEncoding,
                    EImportedAnimationCapabilityState.Unsupported,
                    $"{path}.serializedVersion",
                    $"AnimationClip serializedVersion {clipVersion} contains unsupported nested serializedVersion {nestedVersion} in '{path}'.",
                    map.ToString());

            foreach (var child in map.Children)
            {
                string field = child.Key is YamlScalarNode scalar ? scalar.Value ?? "<unknown>" : "<unknown>";
                AuditNestedVersions(child.Value, $"{path}.{field}", clipVersion, manifestBuilder);
            }
        }
        else if (node is YamlSequenceNode sequence)
        {
            for (int index = 0; index < sequence.Children.Count; index++)
                AuditNestedVersions(sequence.Children[index], $"{path}[{index}]", clipVersion, manifestBuilder);
        }
    }

    private static YamlNode? GetNodeOrNull(YamlMappingNode map, string key)
        => map.Children.TryGetValue(new YamlScalarNode(key), out YamlNode? node) ? node : null;
}
