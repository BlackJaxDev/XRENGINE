namespace XREngine.Fbx;

public static class FbxAnimationParser
{
    /// <summary>
    /// Parse animation bindings. Require one default layer and linear curves when lossless linear animation is required.
    /// </summary>
    public static FbxAnimationDocument Parse(FbxStructuralDocument structural, FbxSemanticDocument semantic, FbxDeformerDocument deformers, bool requireLosslessLinearAnimation = false)
    {
        ArgumentNullException.ThrowIfNull(structural);
        ArgumentNullException.ThrowIfNull(semantic);
        ArgumentNullException.ThrowIfNull(deformers);
        using IDisposable? profilerScope = FbxTrace.StartProfilerScope("AnimationParser");

        return FbxTrace.TraceOperation(
            "AnimationParser",
            $"Parsing animation data from {semantic.Objects.Count:N0} semantic objects.",
            document => $"Parsed animations: curves={document.CurvesByObjectId.Count:N0}, stacks={document.Stacks.Count:N0}, nodeAnimations={document.Stacks.Sum(static stack => stack.NodeAnimations.Count):N0}, blendShapeAnimations={document.Stacks.Sum(static stack => stack.BlendShapeAnimations.Count):N0}",
            () =>
            {
                FbxStructuralValueReader reader = new(structural);
                Dictionary<long, FbxScalarCurve> curvesByObjectId = ParseCurves(reader, semantic, requireLosslessLinearAnimation);
                if (requireLosslessLinearAnimation)
                    ValidateBlendShapeChannels(reader, semantic);

                List<FbxAnimationStackBinding> stacks = [];
                foreach (FbxSceneObject stackObject in semantic.Objects.Where(static sceneObject => sceneObject.Category == FbxObjectCategory.AnimationStack))
                {
                    if (requireLosslessLinearAnimation)
                        ValidateStackLayer(semantic, stackObject);

                    HashSet<long> curveNodeIds = CollectCurveNodeIdsForStack(semantic, stackObject.Id);
                    if (curveNodeIds.Count == 0)
                        continue;

                    Dictionary<long, NodeAnimationAccumulator> nodeAnimations = [];
                    Dictionary<long, BlendShapeAnimationAccumulator> blendShapeAnimations = [];
                    Dictionary<(long targetId, CurveNodeTargetKind targetKind, string component), FbxScalarCurve>? assignedChannels = requireLosslessLinearAnimation ? [] : null;

                    foreach (long curveNodeId in curveNodeIds)
                        AccumulateCurveNodeBindings(semantic, deformers, curvesByObjectId, curveNodeId, nodeAnimations, blendShapeAnimations, assignedChannels);

                    FbxNodeAnimationBinding[] frozenNodeAnimations = [.. nodeAnimations.Values
                        .OrderBy(static animation => animation.ModelObjectId)
                        .Select(static animation => animation.ToBinding())];
                    FbxBlendShapeAnimationBinding[] frozenBlendShapeAnimations = [.. blendShapeAnimations.Values
                        .OrderBy(static animation => animation.ChannelObjectId)
                        .Select(static animation => animation.ToBinding())];

                    if (requireLosslessLinearAnimation)
                        ValidateStackTimeSpan(stackObject, frozenNodeAnimations, frozenBlendShapeAnimations);

                    if (frozenNodeAnimations.Length == 0 && frozenBlendShapeAnimations.Length == 0)
                        continue;

                    if (FbxTrace.IsEnabled(FbxLogVerbosity.Verbose))
                    {
                        FbxTrace.Verbose(
                            "AnimationParser",
                            $"Animation stack '{stackObject.DisplayName}' (objectId={stackObject.Id}) resolved {curveNodeIds.Count:N0} curve node(s), {frozenNodeAnimations.Length:N0} node animation binding(s), and {frozenBlendShapeAnimations.Length:N0} blend-shape animation binding(s).");
                    }

                    stacks.Add(new FbxAnimationStackBinding(
                        stackObject.Id,
                        stackObject.DisplayName,
                        stackObject.NodeIndex,
                        frozenNodeAnimations,
                        frozenBlendShapeAnimations));
                }

                return new FbxAnimationDocument([.. stacks.OrderBy(static stack => stack.ObjectIndex)], new Dictionary<long, FbxScalarCurve>(curvesByObjectId));
            });
    }

    private static Dictionary<long, FbxScalarCurve> ParseCurves(FbxStructuralValueReader reader, FbxSemanticDocument semantic, bool requireLosslessLinearAnimation)
    {
        using IDisposable? profilerScope = FbxTrace.StartProfilerScope("AnimationParser");
        Dictionary<long, FbxScalarCurve> curvesByObjectId = new();
        foreach (FbxSceneObject curveObject in semantic.Objects.Where(static sceneObject => sceneObject.Category == FbxObjectCategory.AnimationCurve))
        {
            long[] keyTimes = reader.TryReadInt64ArrayChild(curveObject.NodeIndex, "KeyTime") ?? [];
            double[] keyValues = reader.TryReadDoubleArrayChild(curveObject.NodeIndex, "KeyValueFloat")
                ?? reader.TryReadDoubleArrayChild(curveObject.NodeIndex, "KeyValueDouble")
                ?? [];
            if (requireLosslessLinearAnimation)
                ValidateCurve(reader, curveObject, keyTimes, keyValues);

            int keyCount = Math.Min(keyTimes.Length, keyValues.Length);
            if (keyCount == 0)
                continue;

            long[] trimmedTimes = new long[keyCount];
            float[] trimmedValues = new float[keyCount];
            for (int index = 0; index < keyCount; index++)
            {
                trimmedTimes[index] = keyTimes[index];
                trimmedValues[index] = (float)keyValues[index];
            }

            curvesByObjectId[curveObject.Id] = new FbxScalarCurve(trimmedTimes, trimmedValues);
        }

        return curvesByObjectId;
    }

    private static void ValidateCurve(FbxStructuralValueReader reader, FbxSceneObject curveObject, long[] keyTimes, double[] keyValues)
    {
        string curveName = $"animation curve '{curveObject.DisplayName}' (objectId={curveObject.Id})";
        if (reader.TryFindChildNode(curveObject.NodeIndex, "PreExtrapolation", out _)
            || reader.TryFindChildNode(curveObject.NodeIndex, "PostExtrapolation", out _)
            || curveObject.InlineAttributes.Keys.Any(static name => name.Contains("Extrapolation", StringComparison.OrdinalIgnoreCase))
            || curveObject.Properties.Keys.Any(static name => name.Contains("Extrapolation", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException($"FBX {curveName} has explicit extrapolation settings; strict animation supports only implicit constant endpoints.");
        }
        if (keyTimes.Length == 0 || keyTimes.Length != keyValues.Length)
            throw new InvalidDataException($"FBX {curveName} has {keyTimes.Length} key times and {keyValues.Length} key values; strict animation requires matching nonempty arrays.");

        for (int index = 0; index < keyTimes.Length; index++)
        {
            if (index > 0 && keyTimes[index] <= keyTimes[index - 1])
                throw new InvalidDataException($"FBX {curveName} has non-increasing key times at key {index}.");
            if (!double.IsFinite(keyValues[index]) || !float.IsFinite((float)keyValues[index]))
                throw new InvalidDataException($"FBX {curveName} has a non-finite or out-of-range value at key {index}.");
            if ((double)(float)keyValues[index] != keyValues[index])
                throw new InvalidDataException($"FBX {curveName} loses key value precision when stored as a float at key {index}.");
        }

        int[]? flags = reader.TryReadInt32ArrayChild(curveObject.NodeIndex, "KeyAttrFlags");
        int[]? referenceCounts = reader.TryReadInt32ArrayChild(curveObject.NodeIndex, "KeyAttrRefCount");
        if (flags is null || referenceCounts is null || flags.Length == 0 || flags.Length != referenceCounts.Length)
            throw new InvalidDataException($"FBX {curveName} has missing or mismatched KeyAttrFlags and KeyAttrRefCount arrays.");

        long coveredKeys = 0;
        for (int index = 0; index < flags.Length; index++)
        {
            if ((flags[index] & 0x0F) != 0x04)
                throw new InvalidDataException($"FBX {curveName} uses unsupported KeyAttrFlags 0x{flags[index]:X8} at attribute run {index}; strict animation supports linear interpolation only.");
            if (referenceCounts[index] <= 0)
                throw new InvalidDataException($"FBX {curveName} has an invalid KeyAttrRefCount at attribute run {index}.");
            coveredKeys += referenceCounts[index];
            if (coveredKeys > keyTimes.Length)
                throw new InvalidDataException($"FBX {curveName} has KeyAttrRefCount coverage beyond its {keyTimes.Length} keys.");
        }

        if (coveredKeys != keyTimes.Length)
            throw new InvalidDataException($"FBX {curveName} has KeyAttrRefCount coverage for {coveredKeys} of {keyTimes.Length} keys.");
    }

    private static void ValidateBlendShapeChannels(FbxStructuralValueReader reader, FbxSemanticDocument semantic)
    {
        foreach (FbxSceneObject channel in semantic.Objects)
        {
            if (channel.Category != FbxObjectCategory.Deformer
                || !channel.Subclass.Equals("BlendShapeChannel", StringComparison.OrdinalIgnoreCase))
                continue;

            double[]? fullWeights = reader.TryReadDoubleArrayChild(channel.NodeIndex, "FullWeights");
            if (fullWeights is { Length: > 0 })
            {
                double firstWeight = fullWeights[0];
                for (int index = 0; index < fullWeights.Length; index++)
                {
                    // One Shape with one positive weight value has one morph endpoint, even if the exporter repeats that value.
                    if (!double.IsFinite(fullWeights[index]) || fullWeights[index] <= 0.0d || fullWeights[index] != firstWeight)
                        throw new InvalidDataException($"FBX blend-shape channel '{channel.DisplayName}' (objectId={channel.Id}) has nonuniform or invalid FullWeights at entry {index}; progressive shapes are not supported.");
                }
            }

            int shapeCount = 0;
            HashSet<long> shapeIds = [];
            int channelIndex = semantic.ObjectIndexById[channel.Id];
            foreach (int connectionIndex in semantic.GetInboundConnectionIndices(channelIndex))
                AddShape(semantic.Connections[connectionIndex].Source.Id);
            foreach (int connectionIndex in semantic.GetOutboundConnectionIndices(channelIndex))
                AddShape(semantic.Connections[connectionIndex].Destination.Id);

            if (shapeCount > 1)
                throw new InvalidDataException($"FBX blend-shape channel '{channel.DisplayName}' (objectId={channel.Id}) has {shapeCount} connected Shape geometries; progressive shapes are not supported.");

            void AddShape(long? objectId)
            {
                if (objectId is not long id || !shapeIds.Add(id))
                    return;
                if (semantic.TryGetObject(id, out FbxSceneObject connected)
                    && connected.Category == FbxObjectCategory.Geometry
                    && connected.Subclass.Equals("Shape", StringComparison.OrdinalIgnoreCase))
                    shapeCount++;
            }
        }
    }

    private static void ValidateStackTimeSpan(
        FbxSceneObject stack,
        IReadOnlyList<FbxNodeAnimationBinding> nodeAnimations,
        IReadOnlyList<FbxBlendShapeAnimationBinding> blendShapeAnimations)
    {
        long firstKey = long.MaxValue;
        long lastKey = long.MinValue;
        foreach (FbxNodeAnimationBinding animation in nodeAnimations)
            Include(animation.EnumerateKeyTimes());
        foreach (FbxBlendShapeAnimationBinding animation in blendShapeAnimations)
            Include(animation.EnumerateKeyTimes());

        if (firstKey == long.MaxValue)
            return;

        bool hasStart = stack.Properties.ContainsKey("LocalStart") || stack.InlineAttributes.ContainsKey("LocalStart");
        bool hasStop = stack.Properties.ContainsKey("LocalStop") || stack.InlineAttributes.ContainsKey("LocalStop");
        if (hasStart && (!TryGetStackTime(stack, "LocalStart", out long localStart) || localStart != firstKey))
        {
            throw new InvalidDataException($"FBX animation stack '{stack.DisplayName}' (objectId={stack.Id}) has LocalStart that does not match its first animated key {firstKey}.");
        }
        if (hasStop && (!TryGetStackTime(stack, "LocalStop", out long localStop) || localStop != lastKey))
        {
            throw new InvalidDataException($"FBX animation stack '{stack.DisplayName}' (objectId={stack.Id}) has LocalStop that does not match its last animated key {lastKey}.");
        }

        void Include(IEnumerable<long> keyTimes)
        {
            foreach (long keyTime in keyTimes)
            {
                firstKey = Math.Min(firstKey, keyTime);
                lastKey = Math.Max(lastKey, keyTime);
            }
        }
    }

    private static bool TryGetStackTime(FbxSceneObject stack, string name, out long time)
    {
        if (stack.Properties.TryGetValue(name, out FbxPropertyEntry? property)
            && property.TryGetValue(0, out FbxSemanticValue propertyValue))
            return propertyValue.TryGetInt64(out time);
        if (stack.InlineAttributes.TryGetValue(name, out FbxSemanticValue inlineValue))
            return inlineValue.TryGetInt64(out time);
        time = default;
        return false;
    }

    private static void ValidateStackLayer(FbxSemanticDocument semantic, FbxSceneObject stackObject)
    {
        int layerCount = 0;
        foreach (int connectionIndex in semantic.GetInboundConnectionIndices(semantic.ObjectIndexById[stackObject.Id]))
        {
            FbxConnection connection = semantic.Connections[connectionIndex];
            if (connection.Source.Id is not long sourceId || !semantic.TryGetObject(sourceId, out FbxSceneObject sourceObject))
                continue;
            if (sourceObject.Category == FbxObjectCategory.AnimationCurveNode)
                throw new InvalidDataException($"FBX animation stack '{stackObject.DisplayName}' (objectId={stackObject.Id}) has a curve node outside an animation layer.");
            if (sourceObject.Category != FbxObjectCategory.AnimationLayer)
                continue;

            layerCount++;
            ValidateLayerProperty(sourceObject, "Weight", 100.0d);
            ValidateLayerProperty(sourceObject, "BlendMode", 0.0d);
            ValidateLayerProperty(sourceObject, "Mute", 0.0d);
            ValidateLayerProperty(sourceObject, "Solo", 0.0d);
            ValidateLayerProperty(sourceObject, "RotationAccumulationMode", 0.0d);
            ValidateLayerProperty(sourceObject, "ScaleAccumulationMode", 0.0d);
        }

        if (layerCount != 1)
            throw new InvalidDataException($"FBX animation stack '{stackObject.DisplayName}' (objectId={stackObject.Id}) has {layerCount} layers; strict animation requires exactly one.");
    }

    private static void ValidateLayerProperty(FbxSceneObject layer, string propertyName, double expectedValue)
    {
        if (layer.InlineAttributes.TryGetValue(propertyName, out FbxSemanticValue inlineValue)
            && (!inlineValue.TryGetDouble(out double inlineActual) || inlineActual != expectedValue))
        {
            throw new InvalidDataException($"FBX animation layer '{layer.DisplayName}' (objectId={layer.Id}) has unsupported {propertyName}; strict animation requires {expectedValue}.");
        }

        if (!layer.Properties.TryGetValue(propertyName, out FbxPropertyEntry? property))
            return;
        if (!property.TryGetValue(0, out FbxSemanticValue value)
            || !value.TryGetDouble(out double actualValue)
            || actualValue != expectedValue)
        {
            throw new InvalidDataException($"FBX animation layer '{layer.DisplayName}' (objectId={layer.Id}) has unsupported {propertyName}; strict animation requires {expectedValue}.");
        }
    }

    private static HashSet<long> CollectCurveNodeIdsForStack(FbxSemanticDocument semantic, long stackObjectId)
    {
        using IDisposable? profilerScope = FbxTrace.StartProfilerScope("AnimationParser");
        HashSet<long> curveNodeIds = [];
        Stack<long> pending = new();
        HashSet<long> visited = [];
        pending.Push(stackObjectId);

        while (pending.Count > 0)
        {
            long currentObjectId = pending.Pop();
            if (!visited.Add(currentObjectId))
                continue;
            if (!semantic.ObjectIndexById.TryGetValue(currentObjectId, out int objectIndex))
                continue;
            if (semantic.TryGetObject(currentObjectId, out FbxSceneObject currentObject) && currentObject.Category == FbxObjectCategory.AnimationCurveNode)
                curveNodeIds.Add(currentObjectId);

            foreach (int inboundConnectionIndex in semantic.GetInboundConnectionIndices(objectIndex))
            {
                FbxConnection connection = semantic.Connections[inboundConnectionIndex];
                if (connection.Source.Id is not long sourceObjectId)
                    continue;
                if (!semantic.TryGetObject(sourceObjectId, out FbxSceneObject sourceObject))
                    continue;

                if (sourceObject.Category is FbxObjectCategory.AnimationLayer or FbxObjectCategory.AnimationCurveNode)
                    pending.Push(sourceObjectId);
            }
        }

        return curveNodeIds;
    }

    private static void AccumulateCurveNodeBindings(
        FbxSemanticDocument semantic,
        FbxDeformerDocument deformers,
        IReadOnlyDictionary<long, FbxScalarCurve> curvesByObjectId,
        long curveNodeId,
        IDictionary<long, NodeAnimationAccumulator> nodeAnimations,
        IDictionary<long, BlendShapeAnimationAccumulator> blendShapeAnimations,
        IDictionary<(long targetId, CurveNodeTargetKind targetKind, string component), FbxScalarCurve>? assignedChannels)
    {
        using IDisposable? profilerScope = FbxTrace.StartProfilerScope("AnimationParser");
        if (!semantic.TryGetObject(curveNodeId, out FbxSceneObject curveNodeObject))
            return;
        if (!semantic.ObjectIndexById.TryGetValue(curveNodeId, out int curveNodeObjectIndex))
            return;

        CurveNodeTarget? target = ResolveCurveNodeTarget(semantic, deformers, curveNodeObjectIndex, assignedChannels is not null);
        if (target is null)
        {
            if (assignedChannels is not null)
                throw new InvalidDataException($"FBX animation curve node '{curveNodeObject.DisplayName}' (objectId={curveNodeId}) has no supported model transform or blend-shape weight target.");
            return;
        }

        if (assignedChannels is not null)
        {
            int targetCount = 0;
            foreach (int outboundConnectionIndex in semantic.GetOutboundConnectionIndices(curveNodeObjectIndex))
            {
                FbxConnection connection = semantic.Connections[outboundConnectionIndex];
                if (connection.Destination.Id is long destinationId
                    && (!semantic.TryGetObject(destinationId, out FbxSceneObject destinationObject)
                        || destinationObject.Category != FbxObjectCategory.AnimationLayer))
                    targetCount++;
            }
            if (targetCount != 1)
                throw new InvalidDataException($"FBX animation curve node '{curveNodeObject.DisplayName}' (objectId={curveNodeId}) has {targetCount} targets; strict animation requires one supported target.");
        }

        Dictionary<string, FbxScalarCurve> componentCurves = [];
        foreach (int inboundConnectionIndex in semantic.GetInboundConnectionIndices(curveNodeObjectIndex))
        {
            FbxConnection connection = semantic.Connections[inboundConnectionIndex];
            if (connection.Source.Id is not long curveObjectId)
                continue;
            if (assignedChannels is not null
                && semantic.TryGetObject(curveObjectId, out FbxSceneObject sourceObject)
                && sourceObject.Category != FbxObjectCategory.AnimationCurve)
                throw new InvalidDataException($"FBX animation curve node '{curveNodeObject.DisplayName}' (objectId={curveNodeId}) has an unsupported source object {curveObjectId}.");
            if (!curvesByObjectId.TryGetValue(curveObjectId, out FbxScalarCurve? curve))
            {
                if (assignedChannels is not null)
                    throw new InvalidDataException($"FBX animation curve node '{curveNodeObject.DisplayName}' (objectId={curveNodeId}) references missing or empty curve {curveObjectId}.");
                continue;
            }

            string? componentName = NormalizeCurveComponentName(connection.PropertyName);
            if (componentName is null)
            {
                if (target.TargetKind == CurveNodeTargetKind.BlendShapeWeight)
                {
                    string? sourceProperty = NormalizePropertyName(connection.PropertyName);
                    if (sourceProperty?.StartsWith("d|", StringComparison.OrdinalIgnoreCase) == true)
                        sourceProperty = sourceProperty[2..];
                    if (assignedChannels is not null
                        && sourceProperty is not null
                        && !sourceProperty.Equals(target.GeometryShapeProperty ?? "DeformPercent", StringComparison.Ordinal))
                        throw new InvalidDataException($"FBX animation curve node '{curveNodeObject.DisplayName}' (objectId={curveNodeId}) has unsupported blend-shape component '{connection.PropertyName}'.");
                    componentName = "Weight";
                }
                else
                {
                    if (assignedChannels is not null)
                        throw new InvalidDataException($"FBX animation curve node '{curveNodeObject.DisplayName}' (objectId={curveNodeId}) has unsupported curve component '{connection.PropertyName}'.");
                    continue;
                }
            }

            if (assignedChannels is not null)
            {
                bool supportedComponent = target.TargetKind == CurveNodeTargetKind.BlendShapeWeight
                    ? componentName == "Weight"
                    : componentName is "X" or "Y" or "Z";
                if (!supportedComponent)
                    throw new InvalidDataException($"FBX animation curve node '{curveNodeObject.DisplayName}' (objectId={curveNodeId}) has unsupported {componentName} component for its target.");
                var channel = (target.TargetObjectId, target.TargetKind, componentName);
                if (assignedChannels.TryGetValue(channel, out FbxScalarCurve? existingCurve))
                {
                    if (!CurvesMatch(existingCurve, curve))
                        throw new InvalidDataException($"FBX animation target {target.TargetObjectId} has conflicting {target.TargetKind}.{componentName} curve bindings.");
                }
                else
                    assignedChannels.Add(channel, curve);
            }

            componentCurves[componentName] = curve;
        }

        if (componentCurves.Count == 0)
        {
            if (assignedChannels is not null)
                throw new InvalidDataException($"FBX animation curve node '{curveNodeObject.DisplayName}' (objectId={curveNodeId}) has no supported curve bindings.");
            return;
        }

        if (assignedChannels is not null
            && target.TargetKind != CurveNodeTargetKind.BlendShapeWeight
            && (componentCurves.Count != 3
                || !componentCurves.ContainsKey("X")
                || !componentCurves.ContainsKey("Y")
                || !componentCurves.ContainsKey("Z")))
        {
            throw new InvalidDataException($"FBX animation curve node '{curveNodeObject.DisplayName}' (objectId={curveNodeId}) has sparse {target.TargetKind} curves; strict animation requires X, Y, and Z because curve-node defaults are not preserved.");
        }

        switch (target.TargetKind)
        {
            case CurveNodeTargetKind.ModelTranslation:
            case CurveNodeTargetKind.ModelRotation:
            case CurveNodeTargetKind.ModelScale:
                NodeAnimationAccumulator nodeAnimation = GetOrCreateNodeAnimation(nodeAnimations, target.TargetObjectId);
                foreach ((string componentName, FbxScalarCurve curve) in componentCurves)
                    nodeAnimation.Assign(target.TargetKind, componentName, curve);
                break;

            case CurveNodeTargetKind.BlendShapeWeight:
                if (!deformers.TryGetBlendShapeChannel(target.TargetObjectId, out FbxBlendShapeChannelBinding channelBinding))
                {
                    if (assignedChannels is not null)
                        throw new InvalidDataException($"FBX animation curve node '{curveNodeObject.DisplayName}' (objectId={curveNodeId}) targets unresolved blend-shape channel {target.TargetObjectId}.");
                    return;
                }

                BlendShapeAnimationAccumulator blendShapeAnimation = GetOrCreateBlendShapeAnimation(blendShapeAnimations, channelBinding);
                if (componentCurves.TryGetValue("Weight", out FbxScalarCurve? weightCurve))
                    blendShapeAnimation.WeightCurve = weightCurve;
                break;
        }
    }

    private static bool CurvesMatch(FbxScalarCurve first, FbxScalarCurve second)
    {
        if (first.KeyTimes.Count != second.KeyTimes.Count || first.Values.Count != second.Values.Count)
            return false;
        for (int index = 0; index < first.KeyTimes.Count; index++)
        {
            if (first.KeyTimes[index] != second.KeyTimes[index] || first.Values[index] != second.Values[index])
                return false;
        }
        return true;
    }

    private static CurveNodeTarget? ResolveCurveNodeTarget(FbxSemanticDocument semantic, FbxDeformerDocument deformers, int curveNodeObjectIndex, bool strict)
    {
        foreach (int outboundConnectionIndex in semantic.GetOutboundConnectionIndices(curveNodeObjectIndex))
        {
            FbxConnection connection = semantic.Connections[outboundConnectionIndex];
            if (connection.Destination.Id is not long destinationObjectId)
                continue;
            if (!semantic.TryGetObject(destinationObjectId, out FbxSceneObject destinationObject))
                continue;

            string? propertyName = NormalizePropertyName(connection.PropertyName);
            if (destinationObject.Category == FbxObjectCategory.Model)
            {
                if (propertyName is "Lcl Translation" or "Translation")
                    return new CurveNodeTarget(CurveNodeTargetKind.ModelTranslation, destinationObjectId);
                if (propertyName is "Lcl Rotation" or "Rotation")
                    return new CurveNodeTarget(CurveNodeTargetKind.ModelRotation, destinationObjectId);
                if (propertyName is "Lcl Scaling" or "Scaling")
                    return new CurveNodeTarget(CurveNodeTargetKind.ModelScale, destinationObjectId);
            }

            if (destinationObject.Category == FbxObjectCategory.Deformer
                && destinationObject.Subclass.Equals("BlendShapeChannel", StringComparison.OrdinalIgnoreCase)
                && (propertyName is null || propertyName.Equals("DeformPercent", StringComparison.OrdinalIgnoreCase)))
            {
                return new CurveNodeTarget(CurveNodeTargetKind.BlendShapeWeight, destinationObjectId);
            }

            if (strict && destinationObject.Category == FbxObjectCategory.Geometry && propertyName is not null)
            {
                FbxBlendShapeChannelBinding? matchedChannel = null;
                foreach (FbxBlendShapeChannelBinding channel in deformers.GetBlendShapeChannels(destinationObjectId))
                {
                    if (!channel.Name.Equals(propertyName, StringComparison.Ordinal))
                        continue;
                    if (matchedChannel is not null)
                        throw new InvalidDataException($"FBX geometry '{destinationObject.DisplayName}' (objectId={destinationObjectId}) has multiple blend-shape channels named '{propertyName}'.");
                    matchedChannel = channel;
                }

                if (matchedChannel is not null)
                {
                    if (!destinationObject.Properties.TryGetValue(propertyName, out FbxPropertyEntry? property)
                        || !property.TryGetValue(0, out FbxSemanticValue defaultValue)
                        || !defaultValue.TryGetDouble(out double defaultPercent)
                        || !double.IsFinite(defaultPercent)
                        || (float)defaultPercent != matchedChannel.DefaultDeformPercent)
                    {
                        throw new InvalidDataException($"FBX geometry '{destinationObject.DisplayName}' (objectId={destinationObjectId}) has a '{propertyName}' property that does not match its blend-shape channel default.");
                    }

                    // Some exporters mirror a blend-shape weight on the mesh as a same-name property.
                    return new CurveNodeTarget(CurveNodeTargetKind.BlendShapeWeight, matchedChannel.ChannelObjectId, propertyName);
                }
            }
        }

        return null;
    }

    private static NodeAnimationAccumulator GetOrCreateNodeAnimation(IDictionary<long, NodeAnimationAccumulator> nodeAnimations, long modelObjectId)
    {
        if (!nodeAnimations.TryGetValue(modelObjectId, out NodeAnimationAccumulator? animation))
        {
            animation = new NodeAnimationAccumulator(modelObjectId);
            nodeAnimations[modelObjectId] = animation;
        }

        return animation;
    }

    private static BlendShapeAnimationAccumulator GetOrCreateBlendShapeAnimation(IDictionary<long, BlendShapeAnimationAccumulator> blendShapeAnimations, FbxBlendShapeChannelBinding channelBinding)
    {
        if (!blendShapeAnimations.TryGetValue(channelBinding.ChannelObjectId, out BlendShapeAnimationAccumulator? animation))
        {
            animation = new BlendShapeAnimationAccumulator(channelBinding);
            blendShapeAnimations[channelBinding.ChannelObjectId] = animation;
        }

        return animation;
    }

    private static string? NormalizePropertyName(string? propertyName)
    {
        if (string.IsNullOrWhiteSpace(propertyName))
            return null;

        return propertyName.Trim().Trim('"');
    }

    private static string? NormalizeCurveComponentName(string? propertyName)
    {
        string? normalized = NormalizePropertyName(propertyName);
        if (normalized is null)
            return null;

        if (normalized.StartsWith("d|", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[2..];

        return normalized switch
        {
            "X" => "X",
            "Y" => "Y",
            "Z" => "Z",
            "W" => "W",
            "DeformPercent" => "Weight",
            _ => null,
        };
    }

    private enum CurveNodeTargetKind
    {
        ModelTranslation,
        ModelRotation,
        ModelScale,
        BlendShapeWeight,
    }

    private sealed record CurveNodeTarget(CurveNodeTargetKind TargetKind, long TargetObjectId, string? GeometryShapeProperty = null);

    private sealed class NodeAnimationAccumulator(long modelObjectId)
    {
        public long ModelObjectId { get; } = modelObjectId;

        public FbxScalarCurve? TranslationX { get; set; }
        public FbxScalarCurve? TranslationY { get; set; }
        public FbxScalarCurve? TranslationZ { get; set; }
        public FbxScalarCurve? RotationX { get; set; }
        public FbxScalarCurve? RotationY { get; set; }
        public FbxScalarCurve? RotationZ { get; set; }
        public FbxScalarCurve? ScaleX { get; set; }
        public FbxScalarCurve? ScaleY { get; set; }
        public FbxScalarCurve? ScaleZ { get; set; }

        public void Assign(CurveNodeTargetKind targetKind, string componentName, FbxScalarCurve curve)
        {
            switch (targetKind)
            {
                case CurveNodeTargetKind.ModelTranslation:
                    AssignTranslation(componentName, curve);
                    break;
                case CurveNodeTargetKind.ModelRotation:
                    AssignRotation(componentName, curve);
                    break;
                case CurveNodeTargetKind.ModelScale:
                    AssignScale(componentName, curve);
                    break;
            }
        }

        public FbxNodeAnimationBinding ToBinding()
            => new(ModelObjectId, TranslationX, TranslationY, TranslationZ, RotationX, RotationY, RotationZ, ScaleX, ScaleY, ScaleZ);

        private void AssignTranslation(string componentName, FbxScalarCurve curve)
        {
            switch (componentName)
            {
                case "X": TranslationX = curve; break;
                case "Y": TranslationY = curve; break;
                case "Z": TranslationZ = curve; break;
            }
        }

        private void AssignRotation(string componentName, FbxScalarCurve curve)
        {
            switch (componentName)
            {
                case "X": RotationX = curve; break;
                case "Y": RotationY = curve; break;
                case "Z": RotationZ = curve; break;
            }
        }

        private void AssignScale(string componentName, FbxScalarCurve curve)
        {
            switch (componentName)
            {
                case "X": ScaleX = curve; break;
                case "Y": ScaleY = curve; break;
                case "Z": ScaleZ = curve; break;
            }
        }
    }

    private sealed class BlendShapeAnimationAccumulator(FbxBlendShapeChannelBinding channelBinding)
    {
        public long ChannelObjectId { get; } = channelBinding.ChannelObjectId;
        public long GeometryObjectId { get; } = channelBinding.GeometryObjectId;
        public string BlendShapeName { get; } = channelBinding.Name;
        public float FullWeight { get; } = channelBinding.FullWeight;
        public float DefaultDeformPercent { get; } = channelBinding.DefaultDeformPercent;
        public FbxScalarCurve? WeightCurve { get; set; }

        public FbxBlendShapeAnimationBinding ToBinding()
            => new(GeometryObjectId, ChannelObjectId, BlendShapeName, FullWeight, DefaultDeformPercent, WeightCurve ?? new FbxScalarCurve([0L], [DefaultDeformPercent]));
    }
}
