namespace XREngine.Animation;

public partial class BlendTree2D
{
    internal void CollectImportedHumanoidChildContributions(
        HumanoidMotionContributionBuffer destination,
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

        if (!TryCalculateWeights(x, y))
            return;

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
        HumanoidMotionContributionBuffer destination,
        IDictionary<string, AnimVar> variables,
        double normalizedTime,
        float weight,
        ulong occurrenceId,
        ulong lifecycleGeneration,
        bool mirror)
    {
        child.Motion?.CollectImportedHumanoidContributions(
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
