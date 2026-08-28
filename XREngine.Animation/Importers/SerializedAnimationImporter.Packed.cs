using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using XREngine.Components.Animation;
using YamlDotNet.RepresentationModel;

namespace XREngine.Animation.Importers;

public static partial class AnimYamlImporter
{
    private const string PackedClipSourcePrefix = "m_MuscleClip.m_Clip";

    private static readonly string[] PackedHumanoidTranslationDofBones =
    [
        "Spine",
        "Chest",
        "UpperChest",
        "Neck",
        "Head",
        "LeftUpperLeg",
        "LeftLowerLeg",
        "LeftFoot",
        "LeftToes",
        "RightUpperLeg",
        "RightLowerLeg",
        "RightFoot",
        "RightToes",
        "LeftShoulder",
        "LeftUpperArm",
        "LeftLowerArm",
        "LeftHand",
        "RightShoulder",
        "RightUpperArm",
        "RightLowerArm",
        "RightHand",
    ];

    private sealed record PackedBindingChannel(
        ImportedAnimationBindingDescriptor Descriptor,
        string Attribute,
        char ComponentName);

    private sealed record StreamedCurveKey(
        int Index,
        float Coefficient0,
        float Coefficient1,
        float OutSlope,
        float Value)
    {
        public float CalculateNextInSlope(float deltaTime, float nextValue)
        {
            if (Coefficient0 == 0.0f && Coefficient1 == 0.0f && OutSlope == 0.0f)
                return float.PositiveInfinity;

            float duration = Math.Max(deltaTime, 0.0001f);
            float deltaValue = nextValue - Value;
            float inverseSquaredDuration = 1.0f / (duration * duration);
            float scaledOutSlope = OutSlope * duration;
            float scaledInSlope = 3.0f * deltaValue
                - 2.0f * scaledOutSlope
                - Coefficient1 / inverseSquaredDuration;
            return scaledInSlope / duration;
        }
    }

    private sealed record StreamedFrame(float Time, IReadOnlyList<StreamedCurveKey> Keys);

    private static void DecodeCompressedRotationCurves(
        YamlMappingNode clipMap,
        List<VectorCurve> destination,
        ImportedAnimationImportManifestBuilder manifestBuilder)
    {
        YamlSequenceNode? sequence = GetSequenceOrNull(clipMap, "m_CompressedRotationCurves");
        if (sequence is null || sequence.Children.Count == 0)
            return;

        List<VectorCurve> decoded = new(sequence.Children.Count);
        for (int curveIndex = 0; curveIndex < sequence.Children.Count; curveIndex++)
        {
            YamlNode sourceNode = sequence.Children[curveIndex];
            if (sourceNode is not YamlMappingNode curve)
            {
                manifestBuilder.RecordSection(
                    EImportedAnimationDataDomain.SourceEncoding,
                    EImportedAnimationCapabilityState.Unsupported,
                    $"m_CompressedRotationCurves[{curveIndex}]",
                    "Compressed rotation curve entry is not a mapping.",
                    sourceNode.ToString());
                continue;
            }
            if (!TryDecodeCompressedRotationCurve(curve, out VectorCurve? result, out string diagnostic))
            {
                manifestBuilder.RecordSection(
                    EImportedAnimationDataDomain.SourceEncoding,
                    EImportedAnimationCapabilityState.Unsupported,
                    $"m_CompressedRotationCurves[{curveIndex}]",
                    diagnostic,
                    sourceNode.ToString());
                continue;
            }

            decoded.Add(result);
        }

        if (decoded.Count != sequence.Children.Count)
            return;

        destination.AddRange(decoded);
        manifestBuilder.RecordSection(
            EImportedAnimationDataDomain.SourceEncoding,
            EImportedAnimationCapabilityState.SupportedAndApplied,
            "m_CompressedRotationCurves",
            $"Decoded {decoded.Count} PackedIntVector/PackedQuatVector rotation curves into native quaternion tracks.",
            serializedYaml: string.Empty);
    }

    private static bool TryDecodeCompressedRotationCurve(
        YamlMappingNode curve,
        out VectorCurve result,
        out string diagnostic)
    {
        result = null!;
        string path = GetScalarString(curve, "m_Path")
            ?? GetScalarString(curve, "path")
            ?? string.Empty;
        YamlMappingNode? timesNode = GetMappingOrNull(curve, "m_Times");
        YamlMappingNode? valuesNode = GetMappingOrNull(curve, "m_Values");
        YamlMappingNode? slopesNode = GetMappingOrNull(curve, "m_Slopes");
        if (timesNode is null || valuesNode is null || slopesNode is null)
        {
            diagnostic = "Compressed rotation curve is missing m_Times, m_Values, or m_Slopes.";
            return false;
        }

        if (!TryUnpackInts(timesNode, out int[] timeDeltas, out diagnostic))
            return false;
        if (!TryUnpackQuaternions(valuesNode, out Quaternion[] values, out diagnostic))
            return false;
        if (!TryUnpackFloats(slopesNode, out float[] slopes, out diagnostic))
            return false;
        if (values.Length != timeDeltas.Length)
        {
            diagnostic = $"Compressed rotation time/value counts differ ({timeDeltas.Length} versus {values.Length}).";
            return false;
        }
        if (slopes.Length != 0 && slopes.Length != values.Length * 4)
        {
            diagnostic = $"Compressed rotation slope count {slopes.Length} is neither zero nor four values per quaternion key ({values.Length * 4}).";
            return false;
        }

        Dictionary<char, List<CurveKey>> components = new(4)
        {
            ['x'] = new List<CurveKey>(values.Length),
            ['y'] = new List<CurveKey>(values.Length),
            ['z'] = new List<CurveKey>(values.Length),
            ['w'] = new List<CurveKey>(values.Length),
        };
        int cumulativeCentiseconds = 0;
        Quaternion previous = Quaternion.Identity;
        for (int keyIndex = 0; keyIndex < values.Length; keyIndex++)
        {
            if (timeDeltas[keyIndex] < 0
                || cumulativeCentiseconds > int.MaxValue - timeDeltas[keyIndex])
            {
                diagnostic = $"Compressed rotation time delta {keyIndex} is negative or overflows the cumulative time.";
                return false;
            }

            cumulativeCentiseconds += timeDeltas[keyIndex];
            float time = cumulativeCentiseconds * 0.01f;
            Quaternion value = values[keyIndex];
            if (!IsFinite(value) || value.LengthSquared() <= 1.0e-12f)
            {
                diagnostic = $"Compressed rotation key {keyIndex} decoded to a non-finite or zero quaternion.";
                return false;
            }
            value = Quaternion.Normalize(value);
            float sign = keyIndex > 0 && Quaternion.Dot(previous, value) < 0.0f ? -1.0f : 1.0f;
            if (sign < 0.0f)
                value = Negate(value);
            previous = value;

            AddCompressedQuaternionComponent(components['x'], time, value.X, GetSlope(slopes, keyIndex, 0) * sign);
            AddCompressedQuaternionComponent(components['y'], time, value.Y, GetSlope(slopes, keyIndex, 1) * sign);
            AddCompressedQuaternionComponent(components['z'], time, value.Z, GetSlope(slopes, keyIndex, 2) * sign);
            AddCompressedQuaternionComponent(components['w'], time, value.W, GetSlope(slopes, keyIndex, 3) * sign);
        }

        result = new VectorCurve(
            "m_CompressedRotationCurves",
            curve.ToString(),
            path,
            "m_LocalRotation",
            4,
            default,
            components.ToDictionary(
                static pair => pair.Key,
                static pair => (IReadOnlyList<CurveKey>)pair.Value),
            GetScalarInt(curve, "m_PreInfinity") ?? 0,
            GetScalarInt(curve, "m_PostInfinity") ?? 0);
        diagnostic = string.Empty;
        return true;
    }

    private static void AddCompressedQuaternionComponent(
        ICollection<CurveKey> destination,
        float time,
        float value,
        float slope)
        => destination.Add(new CurveKey(
            time,
            value,
            slope,
            slope,
            CombinedTangentMode: 0,
            WeightedMode: 0,
            InWeight: 1.0f / 3.0f,
            OutWeight: 1.0f / 3.0f));

    private static float GetSlope(float[] slopes, int keyIndex, int component)
        => slopes.Length == 0 ? 0.0f : slopes[keyIndex * 4 + component];

    private static void DecodePackedClipRepresentations(
        YamlMappingNode clipMap,
        string clipFilePath,
        List<ScalarCurve> scalarDestination,
        List<ObjectCurve> objectDestination,
        ImportedAnimationImportManifestBuilder manifestBuilder,
        bool hasAuthoritativeEditableScalarCurves,
        bool hasAuthoritativeEditableObjectCurves,
        float clipStartTime,
        float clipStopTime,
        int clipSampleRate)
    {
        YamlMappingNode? muscleClip = GetMappingOrNull(clipMap, "m_MuscleClip");
        YamlMappingNode? serializedClip = GetMappingOrNull(muscleClip, "m_Clip");
        if (serializedClip is null)
            return;

        YamlMappingNode? streamedNode = GetMappingOrNull(serializedClip, "m_StreamedClip");
        YamlMappingNode? denseNode = GetMappingOrNull(serializedClip, "m_DenseClip");
        YamlMappingNode? constantNode = GetMappingOrNull(serializedClip, "m_ConstantClip");
        bool hasStreamed = streamedNode is not null && ContainsSerializedSamples(streamedNode);
        bool hasDense = denseNode is not null && ContainsSerializedSamples(denseNode);
        bool hasConstant = constantNode is not null && ContainsSerializedSamples(constantNode);
        if (!hasStreamed && !hasDense && !hasConstant)
            return;

        if (!TryReadPackedBindingChannels(
            clipMap,
            out PackedBindingChannel[] bindingChannels,
            out SourceAssetReference[] pptrMapping,
            out string bindingDiagnostic))
        {
            manifestBuilder.RecordSection(
                EImportedAnimationDataDomain.SourceEncoding,
                EImportedAnimationCapabilityState.Unsupported,
                "m_ClipBindingConstant",
                bindingDiagnostic,
                GetMappingOrNull(clipMap, "m_ClipBindingConstant")?.ToString() ?? string.Empty);
            return;
        }

        List<ScalarCurve> decodedScalars = [];
        List<ObjectCurve> decodedObjects = [];
        int streamedCurveCount = 0;
        int denseCurveCount = 0;
        if (hasStreamed && !TryDecodeStreamedClip(
            streamedNode!,
            bindingChannels,
            pptrMapping,
            decodedScalars,
            decodedObjects,
            out streamedCurveCount,
            out string streamedDiagnostic))
        {
            RecordPackedDecodeFailure(manifestBuilder, "m_StreamedClip", streamedDiagnostic, streamedNode!);
            return;
        }
        if (hasDense && !TryDecodeDenseClip(
            denseNode!,
            bindingChannels,
            pptrMapping,
            streamedCurveCount,
            decodedScalars,
            decodedObjects,
            out denseCurveCount,
            out string denseDiagnostic))
        {
            RecordPackedDecodeFailure(manifestBuilder, "m_DenseClip", denseDiagnostic, denseNode!);
            return;
        }
        if (hasConstant && !TryDecodeConstantClip(
            constantNode!,
            bindingChannels,
            pptrMapping,
            streamedCurveCount + denseCurveCount,
            clipStartTime,
            ResolvePackedStopTime(muscleClip, clipStartTime, clipStopTime, clipSampleRate),
            decodedScalars,
            decodedObjects,
            out string constantDiagnostic))
        {
            RecordPackedDecodeFailure(manifestBuilder, "m_ConstantClip", constantDiagnostic, constantNode!);
            return;
        }

        int decodedChannelCount = streamedCurveCount + denseCurveCount;
        if (hasConstant)
            decodedChannelCount += GetSerializedFloatArrayLength(constantNode!, "data", "m_Data");
        if (decodedChannelCount > bindingChannels.Length)
        {
            RecordPackedDecodeFailure(
                manifestBuilder,
                "m_Clip",
                $"Packed clip exposes {decodedChannelCount} scalar channels but only {bindingChannels.Length} binding channels exist.",
                serializedClip);
            return;
        }

        NormalizePackedQuaternionCurves(decodedScalars, manifestBuilder);
        ResolvePackedObjectReferences(clipFilePath, decodedObjects, pptrMapping);
        if (!hasAuthoritativeEditableScalarCurves)
            scalarDestination.AddRange(decodedScalars);
        if (!hasAuthoritativeEditableObjectCurves)
            objectDestination.AddRange(decodedObjects);

        string disposition = hasAuthoritativeEditableScalarCurves || hasAuthoritativeEditableObjectCurves
            ? "Decoded and validated; each editable curve domain remains authoritative while packed-only domains use the same native typed tracks."
            : "Decoded into the same native typed tracks used by editable curves.";
        if (hasStreamed)
            RecordPackedDecodeSuccess(manifestBuilder, "m_StreamedClip", disposition);
        if (hasDense)
            RecordPackedDecodeSuccess(manifestBuilder, "m_DenseClip", disposition);
        if (hasConstant)
            RecordPackedDecodeSuccess(manifestBuilder, "m_ConstantClip", disposition);
    }

    private static bool TryReadPackedBindingChannels(
        YamlMappingNode clipMap,
        out PackedBindingChannel[] channels,
        out SourceAssetReference[] pptrMapping,
        out string diagnostic)
    {
        channels = [];
        pptrMapping = [];
        YamlMappingNode? bindingConstant = GetMappingOrNull(clipMap, "m_ClipBindingConstant");
        YamlSequenceNode? genericBindings = bindingConstant is null
            ? null
            : GetSequenceOrNull(bindingConstant, "genericBindings");
        if (genericBindings is null)
        {
            diagnostic = "Packed clip data requires m_ClipBindingConstant.genericBindings.";
            return false;
        }

        if (GetSequenceOrNull(bindingConstant!, "pptrCurveMapping") is YamlSequenceNode mappingSequence)
        {
            pptrMapping = new SourceAssetReference[mappingSequence.Children.Count];
            for (int i = 0; i < mappingSequence.Children.Count; i++)
            {
                if (mappingSequence.Children[i] is not YamlMappingNode reference)
                {
                    diagnostic = $"pptrCurveMapping[{i}] is not an object reference mapping.";
                    return false;
                }
                pptrMapping[i] = ReadAssetReference(reference);
            }
        }

        List<PackedBindingChannel> expanded = [];
        for (int bindingIndex = 0; bindingIndex < genericBindings.Children.Count; bindingIndex++)
        {
            if (genericBindings.Children[bindingIndex] is not YamlMappingNode binding)
            {
                diagnostic = $"genericBindings[{bindingIndex}] is not a mapping.";
                return false;
            }
            if (!TryReadUInt32(binding, "path", out uint pathHash)
                || !TryReadUInt32(binding, "attribute", out uint attributeHash))
            {
                diagnostic = $"genericBindings[{bindingIndex}] has an invalid path or attribute identifier.";
                return false;
            }

            int classId = GetScalarInt(binding, "typeID")
                ?? GetScalarInt(binding, "classID")
                ?? 0;
            int customTypeValue = GetScalarInt(binding, "customType") ?? 0;
            if ((uint)customTypeValue > byte.MaxValue)
            {
                diagnostic = $"genericBindings[{bindingIndex}] customType {customTypeValue} is outside the byte contract.";
                return false;
            }
            byte customType = (byte)customTypeValue;
            bool isPPtr = (GetScalarInt(binding, "isPPtrCurve") ?? 0) != 0;
            bool isInt = (GetScalarInt(binding, "isIntCurve") ?? 0) != 0;
            bool isSerializeReference = (GetScalarInt(binding, "isSerializeReferenceCurve") ?? 0) != 0;
            SourceAssetReference script = ReadAssetReference(GetMappingOrNull(binding, "script"));
            int componentCount = GetPackedBindingComponentCount(classId, attributeHash, isPPtr);
            for (int component = 0; component < componentCount; component++)
            {
                EImportedAnimationBindingValueKind valueKind = GetPackedBindingValueKind(
                    classId,
                    attributeHash,
                    componentCount,
                    isPPtr,
                    isInt);
                string attribute = GetPackedBindingAttribute(
                    classId,
                    attributeHash,
                    component,
                    customType,
                    isPPtr);
                bool requiresAdapter = RequiresPackedBindingAdapter(
                    classId,
                    pathHash,
                    attributeHash,
                    customType,
                    isPPtr,
                    isSerializeReference,
                    script,
                    attribute);
                ImportedAnimationBindingDescriptor descriptor = new()
                {
                    SourceField = $"{PackedClipSourcePrefix}.Binding[{bindingIndex}]",
                    NodePath = string.Empty,
                    Attribute = attribute,
                    PathHash = pathHash,
                    AttributeHash = attributeHash,
                    ClassId = classId,
                    Script = script,
                    ValueKind = valueKind,
                    Component = componentCount == 1 ? -1 : component,
                    CustomType = customType,
                    IsPPtrCurve = isPPtr,
                    IsIntCurve = isInt,
                    IsSerializeReferenceCurve = isSerializeReference,
                    RequiresAdapter = requiresAdapter,
                };
                expanded.Add(new PackedBindingChannel(
                    descriptor,
                    attribute,
                    componentCount == 1 ? '\0' : "xyzw"[component]));
            }
        }

        channels = [.. expanded];
        diagnostic = string.Empty;
        return true;
    }

    private static int GetPackedBindingComponentCount(int classId, uint attributeHash, bool isPPtr)
    {
        if (isPPtr || classId is not (4 or 224))
            return 1;
        return attributeHash switch
        {
            1 or 3 or 4 => 3,
            2 => 4,
            _ => 1,
        };
    }

    private static EImportedAnimationBindingValueKind GetPackedBindingValueKind(
        int classId,
        uint attributeHash,
        int componentCount,
        bool isPPtr,
        bool isInt)
    {
        if (isPPtr)
            return EImportedAnimationBindingValueKind.ObjectReference;
        if (isInt)
            return EImportedAnimationBindingValueKind.Integer;
        if (classId is 4 or 224)
        {
            return attributeHash switch
            {
                1 or 3 => EImportedAnimationBindingValueKind.Vector3,
                2 => EImportedAnimationBindingValueKind.Quaternion,
                4 => EImportedAnimationBindingValueKind.Euler,
                _ => EImportedAnimationBindingValueKind.Float,
            };
        }
        return componentCount switch
        {
            2 => EImportedAnimationBindingValueKind.Vector2,
            3 => EImportedAnimationBindingValueKind.Vector3,
            4 => EImportedAnimationBindingValueKind.Vector4,
            _ => EImportedAnimationBindingValueKind.Float,
        };
    }

    private static string GetPackedBindingAttribute(
        int classId,
        uint attributeHash,
        int component,
        byte customType,
        bool isPPtr)
    {
        if (classId == 137 && isPPtr && customType == 21)
            return $"m_Materials.Array.data[{attributeHash}]";
        if (classId == 95 && customType == 8 && !isPPtr
            && TryGetPackedHumanoidAttribute(attributeHash, out string humanoidAttribute))
            return humanoidAttribute;
        if (classId is not (4 or 224))
            return string.Empty;
        string baseName = attributeHash switch
        {
            1 => "m_LocalPosition",
            2 => "m_LocalRotation",
            3 => "m_LocalScale",
            4 => "localEulerAnglesRaw",
            _ => string.Empty,
        };
        if (baseName.Length == 0 || component < 0)
            return baseName;
        char suffix = "xyzw"[component];
        return $"{baseName}.{suffix}";
    }

    private static bool RequiresPackedBindingAdapter(
        int classId,
        uint pathHash,
        uint attributeHash,
        byte customType,
        bool isPPtr,
        bool isSerializeReference,
        SourceAssetReference script,
        string attribute)
    {
        if (isSerializeReference || !script.IsNull || classId is 114)
            return true;
        if (classId is 4 or 224 && attributeHash is >= 1 and <= 4)
            return false;
        if (classId == 95 && customType == 8 && pathHash == 0)
            return !IsNativePackedHumanoidAttribute(attribute);
        if (classId == 137)
            return !((!isPPtr && customType == 20)
                || (isPPtr && customType == 21 && !string.IsNullOrWhiteSpace(attribute)));
        return string.IsNullOrWhiteSpace(attribute) || RequiresExplicitBindingAdapter(classId, script);
    }

    private static bool TryGetPackedHumanoidAttribute(uint attributeId, out string attribute)
    {
        attribute = attributeId switch
        {
            <= 2 => $"MotionT.{"xyz"[(int)attributeId]}",
            <= 6 => $"MotionQ.{"xyzw"[(int)attributeId - 3]}",
            <= 9 => $"RootT.{"xyz"[(int)attributeId - 7]}",
            <= 13 => $"RootQ.{"xyzw"[(int)attributeId - 10]}",
            <= 16 => $"LeftFootT.{"xyz"[(int)attributeId - 14]}",
            <= 20 => $"LeftFootQ.{"xyzw"[(int)attributeId - 17]}",
            <= 23 => $"RightFootT.{"xyz"[(int)attributeId - 21]}",
            <= 27 => $"RightFootQ.{"xyzw"[(int)attributeId - 24]}",
            <= 30 => $"LeftHandT.{"xyz"[(int)attributeId - 28]}",
            <= 34 => $"LeftHandQ.{"xyzw"[(int)attributeId - 31]}",
            <= 37 => $"RightHandT.{"xyz"[(int)attributeId - 35]}",
            <= 41 => $"RightHandQ.{"xyzw"[(int)attributeId - 38]}",
            _ => string.Empty,
        };
        if (attribute.Length > 0)
            return true;

        if (attributeId > int.MaxValue)
            return false;

        int numericAttributeId = (int)attributeId;
        int muscleIndex = numericAttributeId - 42;
        if ((uint)muscleIndex < (uint)ImportedHumanoidMuscleMap.OrderedMuscleEntries.Count)
        {
            attribute = ImportedHumanoidMuscleMap.OrderedMuscleEntries[muscleIndex].CurveAttributeName;
            return true;
        }

        int translationDofIndex = numericAttributeId - 137;
        int boneIndex = translationDofIndex / 3;
        int componentIndex = translationDofIndex % 3;
        if (translationDofIndex >= 0
            && (uint)boneIndex < (uint)PackedHumanoidTranslationDofBones.Length)
        {
            attribute = $"{PackedHumanoidTranslationDofBones[boneIndex]}TDOF.{"xyz"[componentIndex]}";
            return true;
        }

        return false;
    }

    private static bool IsNativePackedHumanoidAttribute(string attribute)
        => TryMapRootMotionComponent(attribute, out _, out _)
            || TryMapIKGoalComponent(attribute, out _, out _, out _)
            || ImportedHumanoidMuscleMap.TryGetValue(attribute, out _);

    /// <summary>
    /// Packed humanoid bindings use numeric attributes, but the native RootT/RootQ,
    /// IK, and muscle channels must converge on the same evaluator used by editable
    /// curves. MotionT/MotionQ and translation-DoF channels stay on the explicit
    /// adapter path until the Phase 9 avatar solver owns those semantics.
    /// </summary>
    private static bool IsNativePackedHumanoidSemanticBinding(ScalarCurve curve)
        => curve.BindingDescriptor is
        {
            ClassId: 95,
            CustomType: 8,
            PathHash: 0,
            RequiresAdapter: false,
            IsPPtrCurve: false,
        };

    private static bool TryDecodeStreamedClip(
        YamlMappingNode streamed,
        PackedBindingChannel[] bindings,
        SourceAssetReference[] pptrMapping,
        List<ScalarCurve> scalarDestination,
        List<ObjectCurve> objectDestination,
        out int curveCount,
        out string diagnostic)
    {
        curveCount = 0;
        if (!TryReadUInt32(streamed, "curveCount", out uint curveCountValue))
        {
            diagnostic = "StreamedClip.curveCount is missing or invalid.";
            return false;
        }
        if (curveCountValue > int.MaxValue || curveCountValue > bindings.Length)
        {
            diagnostic = $"StreamedClip.curveCount {curveCountValue} exceeds available binding channels {bindings.Length}.";
            return false;
        }
        curveCount = (int)curveCountValue;
        if (!TryReadUInt32Array(streamed, out uint[] words, "data", "m_Data"))
        {
            diagnostic = "StreamedClip.data is missing or cannot be decoded as a UInt32 array.";
            return false;
        }
        if (!TryReadStreamedFrames(words, curveCount, out List<StreamedFrame> frames, out diagnostic))
            return false;
        if (frames.Count < 2)
        {
            diagnostic = "StreamedClip must contain Unity's leading and trailing sentinel frames.";
            return false;
        }

        Dictionary<int, List<CurveKey>> channelKeys = new(curveCount);
        int firstAuthoredFrame = 1;
        int lastAuthoredFrameExclusive = frames.Count - 1;
        for (int frameIndex = firstAuthoredFrame; frameIndex < lastAuthoredFrameExclusive; frameIndex++)
        {
            StreamedFrame frame = frames[frameIndex];
            for (int keyIndex = 0; keyIndex < frame.Keys.Count; keyIndex++)
            {
                StreamedCurveKey key = frame.Keys[keyIndex];
                float inSlope = FindStreamedInSlope(frames, frameIndex, key);
                if (!channelKeys.TryGetValue(key.Index, out List<CurveKey>? keys))
                {
                    keys = [];
                    channelKeys.Add(key.Index, keys);
                }
                keys.Add(new CurveKey(
                    frame.Time,
                    key.Value,
                    inSlope,
                    key.OutSlope,
                    CombinedTangentMode: 0,
                    WeightedMode: 0,
                    InWeight: 1.0f / 3.0f,
                    OutWeight: 1.0f / 3.0f));
            }
        }

        for (int channel = 0; channel < curveCount; channel++)
        {
            if (!channelKeys.TryGetValue(channel, out List<CurveKey>? keys) || keys.Count == 0)
            {
                diagnostic = $"StreamedClip channel {channel} has no authored keys between its sentinel frames.";
                return false;
            }
            keys.Sort(static (left, right) => left.Time.CompareTo(right.Time));
            if (!TryAppendPackedCurve(bindings[channel], keys, pptrMapping, scalarDestination, objectDestination, out diagnostic))
                return false;
        }

        diagnostic = string.Empty;
        return true;
    }

    private static bool TryReadStreamedFrames(
        uint[] words,
        int curveCount,
        out List<StreamedFrame> frames,
        out string diagnostic)
    {
        frames = [];
        byte[] bytes = new byte[words.Length * sizeof(uint)];
        for (int i = 0; i < words.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * sizeof(uint), sizeof(uint)), words[i]);

        int offset = 0;
        while (offset < bytes.Length)
        {
            if (bytes.Length - offset < 8)
            {
                diagnostic = $"StreamedClip frame header is truncated at byte {offset}.";
                return false;
            }
            float time = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4)));
            int keyCount = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 4, 4));
            offset += 8;
            if (float.IsNaN(time) || keyCount < 0 || keyCount > curveCount)
            {
                diagnostic = $"StreamedClip frame {frames.Count} has invalid time {time:R} or key count {keyCount}.";
                return false;
            }
            int keyBytes = checked(keyCount * 20);
            if (keyBytes > bytes.Length - offset)
            {
                diagnostic = $"StreamedClip frame {frames.Count} key payload is truncated.";
                return false;
            }

            List<StreamedCurveKey> keys = new(keyCount);
            HashSet<int> seenIndices = [];
            for (int keyIndex = 0; keyIndex < keyCount; keyIndex++)
            {
                int index = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4));
                float coefficient0 = ReadSingleLittleEndian(bytes, offset + 4);
                float coefficient1 = ReadSingleLittleEndian(bytes, offset + 8);
                float outSlope = ReadSingleLittleEndian(bytes, offset + 12);
                float value = ReadSingleLittleEndian(bytes, offset + 16);
                offset += 20;
                if ((uint)index >= (uint)curveCount || !seenIndices.Add(index)
                    || !float.IsFinite(coefficient0) || !float.IsFinite(coefficient1)
                    || (!float.IsFinite(outSlope) && !float.IsInfinity(outSlope))
                    || !float.IsFinite(value))
                {
                    diagnostic = $"StreamedClip frame {frames.Count} contains an invalid or duplicate curve index/value.";
                    return false;
                }
                keys.Add(new StreamedCurveKey(index, coefficient0, coefficient1, outSlope, value));
            }
            frames.Add(new StreamedFrame(time, keys));
        }

        for (int frameIndex = 1; frameIndex < frames.Count - 1; frameIndex++)
        {
            if (!float.IsFinite(frames[frameIndex].Time)
                || frames[frameIndex].Time < frames[frameIndex - 1].Time)
            {
                diagnostic = $"StreamedClip authored frame {frameIndex} has a non-finite or decreasing time.";
                return false;
            }
        }

        diagnostic = string.Empty;
        return true;
    }

    private static float FindStreamedInSlope(
        IReadOnlyList<StreamedFrame> frames,
        int frameIndex,
        StreamedCurveKey key)
    {
        // Unity leaves the first authored frame's incoming slope at its zero
        // initialization; the leading sentinel exists for stream framing, not
        // as a finite Hermite predecessor.
        if (frameIndex <= 1)
            return 0.0f;

        for (int previousFrameIndex = frameIndex - 1; previousFrameIndex >= 0; previousFrameIndex--)
        {
            StreamedFrame previous = frames[previousFrameIndex];
            for (int previousKeyIndex = 0; previousKeyIndex < previous.Keys.Count; previousKeyIndex++)
            {
                StreamedCurveKey previousKey = previous.Keys[previousKeyIndex];
                if (previousKey.Index == key.Index)
                    return previousKey.CalculateNextInSlope(frames[frameIndex].Time - previous.Time, key.Value);
            }
        }
        return 0.0f;
    }

    private static bool TryDecodeDenseClip(
        YamlMappingNode dense,
        PackedBindingChannel[] bindings,
        SourceAssetReference[] pptrMapping,
        int channelOffset,
        List<ScalarCurve> scalarDestination,
        List<ObjectCurve> objectDestination,
        out int curveCount,
        out string diagnostic)
    {
        curveCount = GetScalarInt(dense, "m_CurveCount")
            ?? GetScalarInt(dense, "curveCount")
            ?? 0;
        int frameCount = GetScalarInt(dense, "m_FrameCount")
            ?? GetScalarInt(dense, "frameCount")
            ?? 0;
        float sampleRate = GetScalarFloat(dense, "m_SampleRate")
            ?? GetScalarFloat(dense, "sampleRate")
            ?? 0.0f;
        float beginTime = GetScalarFloat(dense, "m_BeginTime")
            ?? GetScalarFloat(dense, "beginTime")
            ?? 0.0f;
        if (curveCount < 0 || frameCount < 0 || sampleRate <= 0.0f
            || !float.IsFinite(sampleRate) || !float.IsFinite(beginTime))
        {
            diagnostic = "DenseClip has an invalid frame count, curve count, sample rate, or begin time.";
            return false;
        }
        if (channelOffset + curveCount > bindings.Length)
        {
            diagnostic = $"DenseClip channels [{channelOffset}, {channelOffset + curveCount}) exceed {bindings.Length} binding channels.";
            return false;
        }
        if (!TryReadFloatArray(dense, out float[] samples, "m_SampleArray", "sampleArray"))
        {
            diagnostic = "DenseClip.m_SampleArray is missing or invalid.";
            return false;
        }
        int expectedSampleCount;
        try
        {
            expectedSampleCount = checked(frameCount * curveCount);
        }
        catch (OverflowException)
        {
            diagnostic = "DenseClip sample dimensions overflow the supported array size.";
            return false;
        }
        if (samples.Length != expectedSampleCount)
        {
            diagnostic = $"DenseClip sample count {samples.Length} does not equal frameCount*curveCount ({expectedSampleCount}).";
            return false;
        }

        for (int curveIndex = 0; curveIndex < curveCount; curveIndex++)
        {
            List<CurveKey> keys = new(frameCount);
            for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
            {
                float value = samples[frameIndex * curveCount + curveIndex];
                if (!float.IsFinite(value))
                {
                    diagnostic = $"DenseClip sample ({frameIndex}, {curveIndex}) is not finite.";
                    return false;
                }
                float inSlope = frameIndex > 0
                    ? (value - samples[(frameIndex - 1) * curveCount + curveIndex]) * sampleRate
                    : frameCount > 1
                        ? (samples[curveCount + curveIndex] - value) * sampleRate
                        : 0.0f;
                float outSlope = frameIndex + 1 < frameCount
                    ? (samples[(frameIndex + 1) * curveCount + curveIndex] - value) * sampleRate
                    : inSlope;
                keys.Add(new CurveKey(
                    beginTime + frameIndex / sampleRate,
                    value,
                    inSlope,
                    outSlope,
                    CombinedTangentMode: 0,
                    WeightedMode: 0,
                    InWeight: 1.0f / 3.0f,
                    OutWeight: 1.0f / 3.0f));
            }
            if (!TryAppendPackedCurve(
                bindings[channelOffset + curveIndex],
                keys,
                pptrMapping,
                scalarDestination,
                objectDestination,
                out diagnostic))
                return false;
        }

        diagnostic = string.Empty;
        return true;
    }

    private static bool TryDecodeConstantClip(
        YamlMappingNode constant,
        PackedBindingChannel[] bindings,
        SourceAssetReference[] pptrMapping,
        int channelOffset,
        float startTime,
        float stopTime,
        List<ScalarCurve> scalarDestination,
        List<ObjectCurve> objectDestination,
        out string diagnostic)
    {
        if (!TryReadFloatArray(constant, out float[] values, "data", "m_Data"))
        {
            diagnostic = "ConstantClip.data is missing or invalid.";
            return false;
        }
        if (channelOffset + values.Length > bindings.Length)
        {
            diagnostic = $"ConstantClip channels [{channelOffset}, {channelOffset + values.Length}) exceed {bindings.Length} binding channels.";
            return false;
        }
        if (!float.IsFinite(startTime) || !float.IsFinite(stopTime) || stopTime < startTime)
        {
            diagnostic = $"ConstantClip bounds [{startTime:R}, {stopTime:R}] are invalid.";
            return false;
        }

        for (int curveIndex = 0; curveIndex < values.Length; curveIndex++)
        {
            if (!float.IsFinite(values[curveIndex]))
            {
                diagnostic = $"ConstantClip value {curveIndex} is not finite.";
                return false;
            }
            CurveKey[] keys =
            [
                new(startTime, values[curveIndex], 0.0f, 0.0f, 0, 0, 1.0f / 3.0f, 1.0f / 3.0f),
                new(stopTime, values[curveIndex], 0.0f, 0.0f, 0, 0, 1.0f / 3.0f, 1.0f / 3.0f),
            ];
            if (!TryAppendPackedCurve(
                bindings[channelOffset + curveIndex],
                keys,
                pptrMapping,
                scalarDestination,
                objectDestination,
                out diagnostic))
                return false;
        }

        diagnostic = string.Empty;
        return true;
    }

    private static bool TryAppendPackedCurve(
        PackedBindingChannel binding,
        IReadOnlyList<CurveKey> keys,
        SourceAssetReference[] pptrMapping,
        ICollection<ScalarCurve> scalarDestination,
        ICollection<ObjectCurve> objectDestination,
        out string diagnostic)
    {
        ImportedAnimationBindingDescriptor descriptor = binding.Descriptor;
        if (descriptor.IsPPtrCurve)
        {
            ObjectCurveKey[] objectKeys = new ObjectCurveKey[keys.Count];
            for (int keyIndex = 0; keyIndex < keys.Count; keyIndex++)
            {
                float rawIndex = keys[keyIndex].Value;
                int mappingIndex = (int)MathF.Round(rawIndex);
                if (!float.IsFinite(rawIndex) || MathF.Abs(rawIndex - mappingIndex) > 0.0001f)
                {
                    diagnostic = $"PPtr packed channel value {rawIndex:R} is not an integral mapping index.";
                    return false;
                }
                SourceAssetReference reference;
                if (mappingIndex == -1)
                    reference = default;
                else if ((uint)mappingIndex < (uint)pptrMapping.Length)
                    reference = pptrMapping[mappingIndex];
                else
                {
                    diagnostic = $"PPtr packed mapping index {mappingIndex} is outside [0, {pptrMapping.Length}).";
                    return false;
                }
                objectKeys[keyIndex] = new ObjectCurveKey(keys[keyIndex].Time, reference, keyIndex);
            }
            objectDestination.Add(new ObjectCurve(
                descriptor.SourceField,
                string.Empty,
                null,
                binding.Attribute,
                descriptor.ClassId,
                descriptor.Script,
                objectKeys,
                descriptor));
            diagnostic = string.Empty;
            return true;
        }

        if (descriptor.IsIntCurve)
            keys = MakeDiscretePackedKeys(keys);

        scalarDestination.Add(new ScalarCurve(
            descriptor.SourceField,
            string.Empty,
            null,
            binding.Attribute,
            descriptor.ClassId,
            descriptor.Script,
            keys,
            PreInfinity: 0,
            PostInfinity: 0,
            descriptor));
        diagnostic = string.Empty;
        return true;
    }

    private static IReadOnlyList<CurveKey> MakeDiscretePackedKeys(IReadOnlyList<CurveKey> source)
    {
        CurveKey[] discrete = new CurveKey[source.Count];
        for (int i = 0; i < source.Count; i++)
        {
            discrete[i] = source[i] with
            {
                InSlope = float.PositiveInfinity,
                OutSlope = float.PositiveInfinity,
                WeightedMode = 0,
                InWeight = 1.0f / 3.0f,
                OutWeight = 1.0f / 3.0f,
            };
        }
        return discrete;
    }

    private static bool ContainsSerializedSamples(YamlNode node)
    {
        if (node is YamlSequenceNode sequence)
            return sequence.Children.Count > 0;

        if (node is not YamlMappingNode mapping)
            return node is YamlScalarNode scalar
                && !string.IsNullOrWhiteSpace(scalar.Value)
                && scalar.Value is not "0";

        foreach ((YamlNode keyNode, YamlNode valueNode) in mapping.Children)
        {
            string key = (keyNode as YamlScalarNode)?.Value ?? string.Empty;
            if ((key.Contains("data", StringComparison.OrdinalIgnoreCase)
                    || key.Contains("sample", StringComparison.OrdinalIgnoreCase)
                    || key.Contains("curve", StringComparison.OrdinalIgnoreCase))
                && ContainsSerializedSamples(valueNode))
                return true;
        }

        return false;
    }

    private static void NormalizePackedQuaternionCurves(
        List<ScalarCurve> curves,
        ImportedAnimationImportManifestBuilder manifestBuilder)
    {
        Dictionary<(uint path, uint attribute, int? classId), ScalarCurve[]> groups = [];
        for (int i = 0; i < curves.Count; i++)
        {
            ScalarCurve curve = curves[i];
            ImportedAnimationBindingDescriptor? descriptor = curve.BindingDescriptor;
            if (descriptor?.ValueKind != EImportedAnimationBindingValueKind.Quaternion
                || descriptor.Component is < 0 or > 3)
                continue;
            var key = (descriptor.PathHash, descriptor.AttributeHash, descriptor.ClassId);
            if (!groups.TryGetValue(key, out ScalarCurve[]? channels))
            {
                channels = new ScalarCurve[4];
                groups.Add(key, channels);
            }
            channels[descriptor.Component] = curve;
        }

        foreach (ScalarCurve[] channels in groups.Values)
        {
            if (channels.Any(static channel => channel is null))
            {
                manifestBuilder.RecordSection(
                    EImportedAnimationDataDomain.SourceEncoding,
                    EImportedAnimationCapabilityState.Unsupported,
                    PackedClipSourcePrefix,
                    "Packed quaternion binding does not contain all four scalar channels.",
                    serializedYaml: string.Empty);
                continue;
            }
            Dictionary<char, IReadOnlyList<CurveKey>> source = new(4)
            {
                ['x'] = channels[0].Keys,
                ['y'] = channels[1].Keys,
                ['z'] = channels[2].Keys,
                ['w'] = channels[3].Keys,
            };
            if (!TryNormalizeQuaternionChannels(source, out var normalized, out string diagnostic))
            {
                if (TryValidateUnevenQuaternionChannels(
                    channels[0],
                    channels[1],
                    channels[2],
                    channels[3],
                    out string unevenDiagnostic))
                {
                    manifestBuilder.RecordNotice(
                        EImportedAnimationDataDomain.SourceEncoding,
                        "Packed quaternion components use independently reduced key times; native playback combines, normalizes, and shortest-arc blends the quartet after scalar evaluation.");
                    continue;
                }

                manifestBuilder.RecordSection(
                    EImportedAnimationDataDomain.SourceEncoding,
                    EImportedAnimationCapabilityState.Unsupported,
                    PackedClipSourcePrefix,
                    string.IsNullOrEmpty(unevenDiagnostic) ? diagnostic : unevenDiagnostic,
                    serializedYaml: string.Empty);
                continue;
            }
            for (int component = 0; component < 4; component++)
            {
                ScalarCurve original = channels[component];
                int curveIndex = curves.IndexOf(original);
                curves[curveIndex] = original with { Keys = normalized["xyzw"[component]] };
            }
        }
    }

    private static void ResolvePackedObjectReferences(
        string clipFilePath,
        List<ObjectCurve> curves,
        SourceAssetReference[] mapping)
    {
        HashSet<string> requestedGuids = mapping
            .Where(static reference => !string.IsNullOrWhiteSpace(reference.Guid))
            .Select(static reference => reference.Guid)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> resolvedPaths = ResolveSourceGuidPaths(clipFilePath, requestedGuids);
        for (int curveIndex = 0; curveIndex < curves.Count; curveIndex++)
        {
            ObjectCurve curve = curves[curveIndex];
            ObjectCurveKey[] resolvedKeys = new ObjectCurveKey[curve.Keys.Count];
            for (int keyIndex = 0; keyIndex < curve.Keys.Count; keyIndex++)
            {
                ObjectCurveKey key = curve.Keys[keyIndex];
                SourceAssetReference reference = key.Value;
                if (resolvedPaths.TryGetValue(reference.Guid, out string? resolvedPath))
                    reference = reference with { ResolvedAssetPath = resolvedPath };
                resolvedKeys[keyIndex] = key with { Value = reference };
            }
            curves[curveIndex] = curve with { Keys = resolvedKeys };
        }
    }

    private static float ResolvePackedStopTime(
        YamlMappingNode? muscleClip,
        float clipStartTime,
        float clipStopTime,
        int sampleRate)
    {
        float muscleStop = GetScalarFloatOrNull(muscleClip, "m_StopTime") ?? clipStopTime;
        if (float.IsFinite(muscleStop) && muscleStop >= clipStartTime)
            return muscleStop;
        return clipStartTime + (sampleRate > 0 ? 1.0f / sampleRate : 0.0f);
    }

    private static void RecordPackedDecodeFailure(
        ImportedAnimationImportManifestBuilder manifestBuilder,
        string sourceField,
        string diagnostic,
        YamlNode source)
        => manifestBuilder.RecordSection(
            EImportedAnimationDataDomain.SourceEncoding,
            EImportedAnimationCapabilityState.Unsupported,
            $"{PackedClipSourcePrefix}.{sourceField}",
            diagnostic,
            source.ToString());

    private static void RecordPackedDecodeSuccess(
        ImportedAnimationImportManifestBuilder manifestBuilder,
        string sourceField,
        string diagnostic)
        => manifestBuilder.RecordSection(
            EImportedAnimationDataDomain.SourceEncoding,
            EImportedAnimationCapabilityState.SupportedAndApplied,
            $"{PackedClipSourcePrefix}.{sourceField}",
            diagnostic,
            serializedYaml: string.Empty);

    private static bool TryUnpackInts(
        YamlMappingNode packed,
        out int[] values,
        out string diagnostic)
    {
        values = [];
        if (!TryReadUInt32(packed, "m_NumItems", out uint itemCount)
            || !TryReadByteArray(packed, out byte[] data, "m_Data", "data")
            || !TryReadByte(packed, "m_BitSize", out byte bitSize))
        {
            diagnostic = "PackedIntVector header or byte payload is invalid.";
            return false;
        }
        if (itemCount > int.MaxValue || (itemCount > 0 && bitSize is 0 or > 31))
        {
            diagnostic = $"PackedIntVector item count {itemCount} or bit size {bitSize} is unsupported.";
            return false;
        }
        if (!HasEnoughBits(data, itemCount, bitSize))
        {
            diagnostic = "PackedIntVector byte payload is truncated.";
            return false;
        }

        values = new int[itemCount];
        BitReader reader = new(data);
        for (int i = 0; i < values.Length; i++)
            values[i] = (int)reader.ReadBits(bitSize);
        diagnostic = string.Empty;
        return true;
    }

    private static bool TryUnpackFloats(
        YamlMappingNode packed,
        out float[] values,
        out string diagnostic)
    {
        values = [];
        if (!TryReadUInt32(packed, "m_NumItems", out uint itemCount)
            || !TryReadByteArray(packed, out byte[] data, "m_Data", "data")
            || !TryReadByte(packed, "m_BitSize", out byte bitSize))
        {
            diagnostic = "PackedFloatVector header or byte payload is invalid.";
            return false;
        }
        float range = GetScalarFloat(packed, "m_Range") ?? 0.0f;
        float start = GetScalarFloat(packed, "m_Start") ?? 0.0f;
        if (itemCount > int.MaxValue || !float.IsFinite(range) || !float.IsFinite(start)
            || (itemCount > 0 && bitSize is 0 or > 31))
        {
            diagnostic = $"PackedFloatVector item count {itemCount}, range, start, or bit size {bitSize} is invalid.";
            return false;
        }
        if (!HasEnoughBits(data, itemCount, bitSize))
        {
            diagnostic = "PackedFloatVector byte payload is truncated.";
            return false;
        }

        values = new float[itemCount];
        BitReader reader = new(data);
        uint maximum = (1u << bitSize) - 1u;
        for (int i = 0; i < values.Length; i++)
            values[i] = reader.ReadBits(bitSize) * range / maximum + start;
        diagnostic = string.Empty;
        return true;
    }

    private static bool TryUnpackQuaternions(
        YamlMappingNode packed,
        out Quaternion[] values,
        out string diagnostic)
    {
        values = [];
        if (!TryReadUInt32(packed, "m_NumItems", out uint itemCount)
            || !TryReadByteArray(packed, out byte[] data, "m_Data", "data"))
        {
            diagnostic = "PackedQuatVector header or byte payload is invalid.";
            return false;
        }
        if (itemCount > int.MaxValue || !HasEnoughBits(data, itemCount, 32))
        {
            diagnostic = $"PackedQuatVector item count {itemCount} is too large or its byte payload is truncated.";
            return false;
        }

        values = new Quaternion[itemCount];
        BitReader reader = new(data);
        Span<float> components = stackalloc float[4];
        for (int item = 0; item < values.Length; item++)
        {
            uint flags = reader.ReadBits(3);
            int omittedComponent = (int)(flags & 3u);
            components.Clear();
            float sumSquared = 0.0f;
            for (int component = 0; component < 4; component++)
            {
                if (component == omittedComponent)
                    continue;
                int bitSize = (omittedComponent + 1) % 4 == component ? 9 : 10;
                uint maximum = (1u << bitSize) - 1u;
                float value = reader.ReadBits(bitSize) / (0.5f * maximum) - 1.0f;
                components[component] = value;
                sumSquared += value * value;
            }
            if (sumSquared > 1.0001f)
            {
                diagnostic = $"PackedQuatVector item {item} has an invalid reconstructed squared length {sumSquared:R}.";
                return false;
            }
            components[omittedComponent] = MathF.Sqrt(Math.Max(0.0f, 1.0f - sumSquared));
            if ((flags & 4u) != 0)
                components[omittedComponent] = -components[omittedComponent];
            values[item] = new Quaternion(components[0], components[1], components[2], components[3]);
        }

        diagnostic = string.Empty;
        return true;
    }

    private static bool TryReadUInt32Array(
        YamlMappingNode parent,
        out uint[] values,
        params string[] keys)
    {
        values = [];
        if (!TryGetFirstNode(parent, keys, out YamlNode? node))
            return false;
        if (TryGetArraySequence(node, out YamlSequenceNode? sequence))
        {
            values = new uint[sequence.Children.Count];
            for (int i = 0; i < values.Length; i++)
            {
                if (sequence.Children[i] is not YamlScalarNode scalar
                    || !TryParseUInt32(scalar.Value, out values[i]))
                    return false;
            }
            return true;
        }
        if (node is not YamlScalarNode scalarNode)
            return false;
        string scalarValue = scalarNode.Value?.Trim() ?? string.Empty;
        if (scalarValue.Length == 0)
            return true;
        string[] tokens = SplitNumericTokens(scalarValue);
        if (tokens.Length > 1)
        {
            values = new uint[tokens.Length];
            for (int i = 0; i < tokens.Length; i++)
                if (!TryParseUInt32(tokens[i], out values[i]))
                    return false;
            return true;
        }
        if (IsHexBlob(scalarValue) && scalarValue.Length % 8 == 0)
        {
            values = new uint[scalarValue.Length / 8];
            for (int i = 0; i < values.Length; i++)
                if (!uint.TryParse(scalarValue.AsSpan(i * 8, 8), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out values[i]))
                    return false;
            return true;
        }
        if (!TryParseUInt32(scalarValue, out uint single))
            return false;
        values = [single];
        return true;
    }

    private static bool TryReadFloatArray(
        YamlMappingNode parent,
        out float[] values,
        params string[] keys)
    {
        values = [];
        if (!TryGetFirstNode(parent, keys, out YamlNode? node))
            return false;
        if (TryGetArraySequence(node, out YamlSequenceNode? sequence))
        {
            values = new float[sequence.Children.Count];
            for (int i = 0; i < values.Length; i++)
            {
                if (sequence.Children[i] is not YamlScalarNode scalar
                    || !TryParseSingle(scalar.Value, out values[i]))
                    return false;
            }
            return true;
        }
        if (node is not YamlScalarNode scalarNode)
            return false;
        string[] tokens = SplitNumericTokens(scalarNode.Value ?? string.Empty);
        values = new float[tokens.Length];
        for (int i = 0; i < tokens.Length; i++)
            if (!TryParseSingle(tokens[i], out values[i]))
                return false;
        return true;
    }

    private static bool TryReadByteArray(
        YamlMappingNode parent,
        out byte[] values,
        params string[] keys)
    {
        values = [];
        if (!TryGetFirstNode(parent, keys, out YamlNode? node))
            return false;
        if (TryGetArraySequence(node, out YamlSequenceNode? sequence))
        {
            values = new byte[sequence.Children.Count];
            for (int i = 0; i < values.Length; i++)
            {
                if (sequence.Children[i] is not YamlScalarNode scalar
                    || !byte.TryParse(scalar.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out values[i]))
                    return false;
            }
            return true;
        }
        if (node is not YamlScalarNode scalarNode)
            return false;
        string scalarValue = scalarNode.Value?.Trim() ?? string.Empty;
        if (scalarValue.Length == 0)
            return true;
        if (IsHexBlob(scalarValue) && scalarValue.Length % 2 == 0)
        {
            values = Convert.FromHexString(scalarValue);
            return true;
        }
        string[] tokens = SplitNumericTokens(scalarValue);
        values = new byte[tokens.Length];
        for (int i = 0; i < tokens.Length; i++)
            if (!byte.TryParse(tokens[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out values[i]))
                return false;
        return true;
    }

    private static int GetSerializedFloatArrayLength(YamlMappingNode parent, params string[] keys)
        => TryReadFloatArray(parent, out float[] values, keys) ? values.Length : 0;

    private static bool TryGetFirstNode(
        YamlMappingNode parent,
        IReadOnlyList<string> keys,
        out YamlNode node)
    {
        for (int i = 0; i < keys.Count; i++)
        {
            if (parent.Children.TryGetValue(new YamlScalarNode(keys[i]), out node!))
                return true;
        }
        node = null!;
        return false;
    }

    private static bool TryGetArraySequence(YamlNode node, out YamlSequenceNode sequence)
    {
        if (node is YamlSequenceNode direct)
        {
            sequence = direct;
            return true;
        }
        if (node is YamlMappingNode mapping)
        {
            string[] candidates = ["Array", "data", "m_Data"];
            for (int i = 0; i < candidates.Length; i++)
            {
                if (mapping.Children.TryGetValue(new YamlScalarNode(candidates[i]), out YamlNode? child)
                    && child is YamlSequenceNode nested)
                {
                    sequence = nested;
                    return true;
                }
            }
        }
        sequence = null!;
        return false;
    }

    private static string[] SplitNumericTokens(string value)
        => value.Trim().TrimStart('[').TrimEnd(']')
            .Split([',', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool TryReadUInt32(YamlMappingNode parent, string key, out uint value)
        => TryParseUInt32(GetScalarString(parent, key), out value);

    private static bool TryParseUInt32(string? text, out uint value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        ReadOnlySpan<char> span = text.AsSpan().Trim();
        if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return uint.TryParse(span[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        return uint.TryParse(span, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryReadByte(YamlMappingNode parent, string key, out byte value)
    {
        value = 0;
        string? text = GetScalarString(parent, key);
        return byte.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryParseSingle(string? text, out float value)
    {
        value = 0.0f;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && uint.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint bits))
        {
            value = BitConverter.UInt32BitsToSingle(bits);
            return true;
        }
        return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static bool IsHexBlob(string value)
    {
        if (value.Length == 0)
            return false;
        for (int i = 0; i < value.Length; i++)
        {
            if (!Uri.IsHexDigit(value[i]))
                return false;
        }
        return true;
    }

    private static bool HasEnoughBits(byte[] data, uint itemCount, int bitsPerItem)
        => (ulong)data.Length * 8ul >= (ulong)itemCount * (uint)bitsPerItem;

    private static float ReadSingleLittleEndian(byte[] data, int offset)
        => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4)));

    private static bool IsFinite(Quaternion value)
        => float.IsFinite(value.X)
            && float.IsFinite(value.Y)
            && float.IsFinite(value.Z)
            && float.IsFinite(value.W);

    private static Quaternion Negate(Quaternion value)
        => new(-value.X, -value.Y, -value.Z, -value.W);

    private ref struct BitReader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _bitOffset;

        public uint ReadBits(int bitCount)
        {
            uint value = 0;
            for (int bit = 0; bit < bitCount; bit++)
            {
                int sourceOffset = _bitOffset + bit;
                int sourceByte = sourceOffset >> 3;
                int sourceBit = sourceOffset & 7;
                value |= (uint)((_data[sourceByte] >> sourceBit) & 1) << bit;
            }
            _bitOffset += bitCount;
            return value;
        }
    }
}
