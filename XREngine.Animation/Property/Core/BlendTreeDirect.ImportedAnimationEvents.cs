using XREngine.Animation.Importers;

namespace XREngine.Animation;

public partial class BlendTreeDirect
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
            child.Motion?.CollectImportedAnimationEvents(
                destination,
                variables,
                ResolveChildNormalizedPhase(previousNormalizedTime, child.Speed, child.CycleOffset),
                ResolveChildNormalizedPhase(currentNormalizedTime, child.Speed, child.CycleOffset),
                includePrevious,
                weight * ReadChildWeight(child, variables) * normalizer,
                CombineOccurrenceId(occurrenceId, child.MotionOccurrenceId));
        }
    }
}
