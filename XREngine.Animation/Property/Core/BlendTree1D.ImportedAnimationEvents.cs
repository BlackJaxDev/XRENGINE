using XREngine.Animation.Importers;

namespace XREngine.Animation;

public partial class BlendTree1D
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
        if (_children.Count == 0)
            return;
        if (_needsSort)
        {
            _needsSort = false;
            _children.Sort(_childComparer);
        }

        float parameter = variables.TryGetValue(ParameterName, out AnimVar? variable)
            ? variable.FloatValue
            : 0.0f;
        if (!float.IsFinite(parameter) || parameter <= _children[0].Threshold)
        {
            CollectChildEvents(_children[0], destination, variables, previousNormalizedTime, currentNormalizedTime, includePrevious, weight, occurrenceId);
            return;
        }

        int lastIndex = _children.Count - 1;
        if (parameter >= _children[lastIndex].Threshold)
        {
            CollectChildEvents(_children[lastIndex], destination, variables, previousNormalizedTime, currentNormalizedTime, includePrevious, weight, occurrenceId);
            return;
        }

        int upperIndex = 1;
        while (upperIndex < _children.Count && _children[upperIndex].Threshold < parameter)
            upperIndex++;
        Child lower = _children[upperIndex - 1];
        Child upper = _children[upperIndex];
        float interval = upper.Threshold - lower.Threshold;
        if (MathF.Abs(interval) <= float.Epsilon || !float.IsFinite(interval))
        {
            CollectChildEvents(lower, destination, variables, previousNormalizedTime, currentNormalizedTime, includePrevious, weight, occurrenceId);
            return;
        }

        float upperWeight = Math.Clamp((parameter - lower.Threshold) / interval, 0.0f, 1.0f);
        CollectChildEvents(lower, destination, variables, previousNormalizedTime, currentNormalizedTime, includePrevious, weight * (1.0f - upperWeight), occurrenceId);
        CollectChildEvents(upper, destination, variables, previousNormalizedTime, currentNormalizedTime, includePrevious, weight * upperWeight, occurrenceId);
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
