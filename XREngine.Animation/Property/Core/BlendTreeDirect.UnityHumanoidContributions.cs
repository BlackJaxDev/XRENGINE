namespace XREngine.Animation;

public partial class BlendTreeDirect
{
    private bool _normalizeBlendValues;

    /// <summary>
    /// Normalizes direct child weights before pose and Body/root composition.
    /// Leave disabled for independent additive controls such as blend shapes.
    /// </summary>
    public bool NormalizeBlendValues
    {
        get => _normalizeBlendValues;
        set => SetField(ref _normalizeBlendValues, value);
    }

    internal void CollectUnityHumanoidChildContributions(
        UnityHumanoidMotionContributionBuffer destination,
        IDictionary<string, AnimVar> variables,
        double normalizedTime,
        float weight,
        ulong occurrenceId,
        ulong lifecycleGeneration,
        bool mirror)
    {
        float totalWeight = 0.0f;
        if (NormalizeBlendValues)
        {
            for (int i = 0; i < Children.Count; i++)
                totalWeight += ReadChildWeight(Children[i], variables);
        }

        float normalizer = NormalizeBlendValues && totalWeight > float.Epsilon
            ? 1.0f / totalWeight
            : 1.0f;
        for (int i = 0; i < Children.Count; i++)
        {
            Child child = Children[i];
            float childWeight = ReadChildWeight(child, variables) * normalizer;
            child.Motion?.CollectUnityHumanoidContributions(
                destination,
                variables,
                ResolveChildNormalizedPhase(
                    normalizedTime,
                    child.Speed,
                    child.CycleOffset),
                weight * childWeight,
                CombineOccurrenceId(occurrenceId, child.MotionOccurrenceId),
                lifecycleGeneration,
                mirror ^ child.HumanoidMirror);
        }
    }

    private static float ReadChildWeight(Child child, IDictionary<string, AnimVar> variables)
    {
        float value = child.WeightParameterName is not null
            && variables.TryGetValue(child.WeightParameterName, out AnimVar? parameter)
                ? parameter.FloatValue
                : 1.0f;
        return float.IsFinite(value) ? Math.Max(0.0f, value) : 0.0f;
    }
}
