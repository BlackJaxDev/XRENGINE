namespace XREngine.Fbx;

public static class FbxAnimationParser
{
    public static FbxAnimationDocument Parse(FbxStructuralDocument structural, FbxSemanticDocument semantic, FbxDeformerDocument deformers)
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
                Dictionary<long, FbxScalarCurve> curvesByObjectId = ParseCurves(reader, semantic);

                List<FbxAnimationStackBinding> stacks = [];
                foreach (FbxSceneObject stackObject in semantic.Objects.Where(static sceneObject => sceneObject.Category == FbxObjectCategory.AnimationStack))
                {
                    HashSet<long> curveNodeIds = CollectCurveNodeIdsForStack(semantic, stackObject.Id);
                    if (curveNodeIds.Count == 0)
                        continue;

                    Dictionary<long, NodeAnimationAccumulator> nodeAnimations = [];
                    Dictionary<long, BlendShapeAnimationAccumulator> blendShapeAnimations = [];

                    foreach (long curveNodeId in curveNodeIds)
                        AccumulateCurveNodeBindings(semantic, deformers, curvesByObjectId, curveNodeId, nodeAnimations, blendShapeAnimations);

                    FbxNodeAnimationBinding[] frozenNodeAnimations = [.. nodeAnimations.Values
                        .OrderBy(static animation => animation.ModelObjectId)
                        .Select(static animation => animation.ToBinding())];
                    FbxBlendShapeAnimationBinding[] frozenBlendShapeAnimations = [.. blendShapeAnimations.Values
                        .OrderBy(static animation => animation.ChannelObjectId)
                        .Select(static animation => animation.ToBinding())];

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

    private static Dictionary<long, FbxScalarCurve> ParseCurves(FbxStructuralValueReader reader, FbxSemanticDocument semantic)
    {
        using IDisposable? profilerScope = FbxTrace.StartProfilerScope("AnimationParser");
        Dictionary<long, FbxScalarCurve> curvesByObjectId = new();
        foreach (FbxSceneObject curveObject in semantic.Objects.Where(static sceneObject => sceneObject.Category == FbxObjectCategory.AnimationCurve))
        {
            long[] keyTimes = reader.TryReadInt64ArrayChild(curveObject.NodeIndex, "KeyTime") ?? [];
            double[] keyValues = reader.TryReadDoubleArrayChild(curveObject.NodeIndex, "KeyValueFloat")
                ?? reader.TryReadDoubleArrayChild(curveObject.NodeIndex, "KeyValueDouble")
                ?? [];
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
        IDictionary<long, BlendShapeAnimationAccumulator> blendShapeAnimations)
    {
        using IDisposable? profilerScope = FbxTrace.StartProfilerScope("AnimationParser");
        if (!semantic.TryGetObject(curveNodeId, out FbxSceneObject curveNodeObject))
            return;
        if (!semantic.ObjectIndexById.TryGetValue(curveNodeId, out int curveNodeObjectIndex))
            return;

        CurveNodeTarget? target = ResolveCurveNodeTarget(semantic, curveNodeObjectIndex);
        if (target is null)
            return;

        Dictionary<string, FbxScalarCurve> componentCurves = [];
        foreach (int inboundConnectionIndex in semantic.GetInboundConnectionIndices(curveNodeObjectIndex))
        {
            FbxConnection connection = semantic.Connections[inboundConnectionIndex];
            if (connection.Source.Id is not long curveObjectId)
                continue;
            if (!curvesByObjectId.TryGetValue(curveObjectId, out FbxScalarCurve? curve))
                continue;

            string? componentName = NormalizeCurveComponentName(connection.PropertyName);
            if (componentName is null)
            {
                if (target.TargetKind == CurveNodeTargetKind.BlendShapeWeight)
                    componentName = "Weight";
                else
                    continue;
            }

            componentCurves[componentName] = curve;
        }

        if (componentCurves.Count == 0)
            return;

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
                    return;

                BlendShapeAnimationAccumulator blendShapeAnimation = GetOrCreateBlendShapeAnimation(blendShapeAnimations, channelBinding);
                if (componentCurves.TryGetValue("Weight", out FbxScalarCurve? weightCurve))
                    blendShapeAnimation.WeightCurve = weightCurve;
                break;
        }
    }

    private static CurveNodeTarget? ResolveCurveNodeTarget(FbxSemanticDocument semantic, int curveNodeObjectIndex)
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

    private sealed record CurveNodeTarget(CurveNodeTargetKind TargetKind, long TargetObjectId);

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