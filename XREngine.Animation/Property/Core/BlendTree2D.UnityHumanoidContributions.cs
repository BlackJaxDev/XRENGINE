namespace XREngine.Animation;

public partial class BlendTree2D
{
    internal void CollectUnityHumanoidChildContributions(
        UnityHumanoidMotionContributionBuffer destination,
        IDictionary<string, AnimVar> variables,
        double normalizedTime,
        float weight,
        ulong occurrenceId,
        ulong lifecycleGeneration,
        bool mirror)
    {
        float x = variables.TryGetValue(XParameterName, out AnimVar? xVariable)
            ? xVariable.FloatValue
            : 0.0f;
        float y = variables.TryGetValue(YParameterName, out AnimVar? yVariable)
            ? yVariable.FloatValue
            : 0.0f;
        x = float.IsFinite(x) ? x : 0.0f;
        y = float.IsFinite(y) ? y : 0.0f;

        if (_needsSort || _sortedByX.Length != _children.Count || _sortedByY.Length != _children.Count)
            UpdateSortedArrays();
        if (_children.Count == 0)
            return;
        if (_children.Count == 1)
        {
            CollectChild(_children[0], destination, variables, normalizedTime, weight, occurrenceId, lifecycleGeneration, mirror);
            return;
        }

        if (_children.Count == 2)
        {
            if (BlendType == EBlendType.Cartesian)
                CalculateLinearWeightsNoBounding(x, y);
            else
                CalculateInverseDistanceWeightsNoBounding(x, y);
        }
        else
        {
            FindBoundingChildren(x, y);
            if (_boundingChildCount == 0)
                return;
            switch (BlendType)
            {
                case EBlendType.Barycentric: CalculateBaryCentricWeights(x, y); break;
                case EBlendType.Cartesian: CalculateCartesianWeights(x, y); break;
                case EBlendType.Directional: CalculateDirectionalWeights(x, y); break;
            }
        }

        for (int i = 0; i < _weightCount; i++)
        {
            ChildWeight childWeight = _childWeights[i];
            CollectChild(
                childWeight.Child,
                destination,
                variables,
                normalizedTime,
                weight * Math.Max(0.0f, childWeight.Weight),
                occurrenceId,
                lifecycleGeneration,
                mirror);
        }
    }

    private static void CollectChild(
        Child child,
        UnityHumanoidMotionContributionBuffer destination,
        IDictionary<string, AnimVar> variables,
        double normalizedTime,
        float weight,
        ulong occurrenceId,
        ulong lifecycleGeneration,
        bool mirror)
    {
        child.Motion?.CollectUnityHumanoidContributions(
            destination,
            variables,
            ResolveChildNormalizedPhase(
                normalizedTime,
                child.Speed,
                child.CycleOffset),
            weight,
            CombineOccurrenceId(occurrenceId, child.MotionOccurrenceId),
            lifecycleGeneration,
            mirror ^ child.HumanoidMirror);
    }
}
