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
        x = float.IsFinite(x) ? x : 0.0f;
        y = float.IsFinite(y) ? y : 0.0f;
        if (_needsSort || _sortedByX.Length != _children.Count || _sortedByY.Length != _children.Count)
            UpdateSortedArrays();
        if (_children.Count == 0)
            return;
        if (_children.Count == 1)
        {
            CollectChildEvents(_children[0], destination, variables, previousNormalizedTime, currentNormalizedTime, includePrevious, weight, occurrenceId);
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
