namespace XREngine.Animation;

public partial class BlendTree1D
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
        if (_children.Count == 0)
            return;

        if (_needsSort)
        {
            _needsSort = false;
            _children.Sort(_childComparer);
        }

        float parameterValue = variables.TryGetValue(ParameterName, out AnimVar? variable)
            ? variable.FloatValue
            : 0.0f;
        if (!float.IsFinite(parameterValue) || parameterValue <= _children[0].Threshold)
        {
            CollectChild(_children[0], destination, variables, normalizedTime, weight, occurrenceId, lifecycleGeneration, mirror);
            return;
        }

        int lastIndex = _children.Count - 1;
        if (parameterValue >= _children[lastIndex].Threshold)
        {
            CollectChild(_children[lastIndex], destination, variables, normalizedTime, weight, occurrenceId, lifecycleGeneration, mirror);
            return;
        }

        int upperIndex = 1;
        while (upperIndex < _children.Count && _children[upperIndex].Threshold < parameterValue)
            upperIndex++;

        Child lower = _children[upperIndex - 1];
        Child upper = _children[upperIndex];
        float interval = upper.Threshold - lower.Threshold;
        if (!float.IsFinite(interval) || MathF.Abs(interval) <= float.Epsilon)
        {
            CollectChild(lower, destination, variables, normalizedTime, weight, occurrenceId, lifecycleGeneration, mirror);
            return;
        }

        float upperWeight = Math.Clamp((parameterValue - lower.Threshold) / interval, 0.0f, 1.0f);
        CollectChild(lower, destination, variables, normalizedTime, weight * (1.0f - upperWeight), occurrenceId, lifecycleGeneration, mirror);
        CollectChild(upper, destination, variables, normalizedTime, weight * upperWeight, occurrenceId, lifecycleGeneration, mirror);
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
