using XREngine.Animation.Importers;

namespace XREngine.Animation;

public abstract partial class MotionBase
{
    internal int GetImportedAnimationEventCapacity()
        => this switch
        {
            AnimationClip clip => clip.ImportedEvents.Length,
            BlendTree1D tree => SumImportedAnimationEventCapacity(tree.Children),
            BlendTree2D tree => SumImportedAnimationEventCapacity(tree.Children),
            BlendTreeDirect tree => SumImportedAnimationEventCapacity(tree.Children),
            _ => 0,
        };

    internal void CollectImportedAnimationEvents(
        ImportedAnimationEventBuffer destination,
        IDictionary<string, AnimVar> variables,
        double previousNormalizedTime,
        double currentNormalizedTime,
        bool includePrevious,
        float weight,
        ulong occurrenceId)
    {
        if (!(weight > float.Epsilon))
            return;

        switch (this)
        {
            case AnimationClip clip:
                clip.CollectImportedAnimationEvents(
                    destination,
                    previousNormalizedTime * clip.LengthInSeconds,
                    currentNormalizedTime * clip.LengthInSeconds,
                    includePrevious,
                    occurrenceId,
                    weight);
                break;
            case BlendTree1D tree:
                tree.CollectImportedAnimationEventsFromChildren(
                    destination,
                    variables,
                    previousNormalizedTime,
                    currentNormalizedTime,
                    includePrevious,
                    weight,
                    occurrenceId);
                break;
            case BlendTree2D tree:
                tree.CollectImportedAnimationEventsFromChildren(
                    destination,
                    variables,
                    previousNormalizedTime,
                    currentNormalizedTime,
                    includePrevious,
                    weight,
                    occurrenceId);
                break;
            case BlendTreeDirect tree:
                tree.CollectImportedAnimationEventsFromChildren(
                    destination,
                    variables,
                    previousNormalizedTime,
                    currentNormalizedTime,
                    includePrevious,
                    weight,
                    occurrenceId);
                break;
        }
    }

    private static int SumImportedAnimationEventCapacity(IEnumerable<BlendTree1D.Child> children)
    {
        int count = 0;
        foreach (BlendTree1D.Child child in children)
            count += child.Motion?.GetImportedAnimationEventCapacity() ?? 0;
        return count;
    }

    private static int SumImportedAnimationEventCapacity(IEnumerable<BlendTree2D.Child> children)
    {
        int count = 0;
        foreach (BlendTree2D.Child child in children)
            count += child.Motion?.GetImportedAnimationEventCapacity() ?? 0;
        return count;
    }

    private static int SumImportedAnimationEventCapacity(IEnumerable<BlendTreeDirect.Child> children)
    {
        int count = 0;
        foreach (BlendTreeDirect.Child child in children)
            count += child.Motion?.GetImportedAnimationEventCapacity() ?? 0;
        return count;
    }
}
