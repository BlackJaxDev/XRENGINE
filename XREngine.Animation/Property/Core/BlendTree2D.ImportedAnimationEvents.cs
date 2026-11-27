using XREngine.Animation.Importers;

namespace XREngine.Animation;

public partial class BlendTree2D
{
    internal void CollectImportedAnimationEventsFromChildren(
        ImportedAnimationEventBuffer destination,
        IDictionary<string, AnimVar> variables,
        double previousNormalizedTime,
        double currentNormalizedTime,
        bool includePrevious,
        float weight,
        ulong occurrenceId)
    {
        float x = variables.TryGetValue(XParameterName, out AnimVar? xVariable) ? xVariable.FloatValue : 0.0f;
        float y = variables.TryGetValue(YParameterName, out AnimVar? yVariable) ? yVariable.FloatValue : 0.0f;
        if (!TryCalculateWeights(x, y))
            return;

        for (int i = 0; i < _weightCount; i++)
        {
            ChildWeight childWeight = _childWeights[i];
            CollectChildEvents(
                childWeight.Child,
                destination,
                variables,
                previousNormalizedTime,
                currentNormalizedTime,
                includePrevious,
                weight * Math.Max(0.0f, childWeight.Weight),
                occurrenceId);
        }
    }

    private static void CollectChildEvents(
        Child child,
        ImportedAnimationEventBuffer destination,
        IDictionary<string, AnimVar> variables,
        double previousNormalizedTime,
        double currentNormalizedTime,
        bool includePrevious,
        float weight,
        ulong occurrenceId)
        => child.Motion?.CollectImportedAnimationEvents(
            destination,
            variables,
            ResolveChildNormalizedPhase(previousNormalizedTime, child.Speed, child.CycleOffset),
            ResolveChildNormalizedPhase(currentNormalizedTime, child.Speed, child.CycleOffset),
            includePrevious,
            weight,
            CombineOccurrenceId(occurrenceId, child.MotionOccurrenceId));
}
