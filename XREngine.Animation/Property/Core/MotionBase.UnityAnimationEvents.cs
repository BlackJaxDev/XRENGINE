using XREngine.Animation.Importers;

namespace XREngine.Animation;

public abstract partial class MotionBase
{
    internal int GetUnityAnimationEventCapacity()
        => this switch
        {
            AnimationClip clip => clip.UnityEvents.Length,
            BlendTree1D tree => SumUnityAnimationEventCapacity(tree.Children),
            BlendTree2D tree => SumUnityAnimationEventCapacity(tree.Children),
            BlendTreeDirect tree => SumUnityAnimationEventCapacity(tree.Children),
            _ => 0,
        };

    internal void CollectUnityAnimationEvents(
        UnityAnimationEventBuffer destination,
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
                clip.CollectUnityAnimationEvents(
                    destination,
                    previousNormalizedTime * clip.LengthInSeconds,
                    currentNormalizedTime * clip.LengthInSeconds,
                    includePrevious,
                    occurrenceId,
                    weight);
                break;
            case BlendTree1D tree:
                tree.CollectUnityAnimationEventsFromChildren(
                    destination,
                    variables,
                    previousNormalizedTime,
                    currentNormalizedTime,
                    includePrevious,
                    weight,
                    occurrenceId);
                break;
            case BlendTree2D tree:
                tree.CollectUnityAnimationEventsFromChildren(
                    destination,
                    variables,
                    previousNormalizedTime,
                    currentNormalizedTime,
                    includePrevious,
                    weight,
                    occurrenceId);
                break;
            case BlendTreeDirect tree:
                tree.CollectUnityAnimationEventsFromChildren(
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

    private static int SumUnityAnimationEventCapacity(IEnumerable<BlendTree1D.Child> children)
    {
        int count = 0;
        foreach (BlendTree1D.Child child in children)
            count += child.Motion?.GetUnityAnimationEventCapacity() ?? 0;
        return count;
    }

    private static int SumUnityAnimationEventCapacity(IEnumerable<BlendTree2D.Child> children)
    {
        int count = 0;
        foreach (BlendTree2D.Child child in children)
            count += child.Motion?.GetUnityAnimationEventCapacity() ?? 0;
        return count;
    }

    private static int SumUnityAnimationEventCapacity(IEnumerable<BlendTreeDirect.Child> children)
    {
        int count = 0;
        foreach (BlendTreeDirect.Child child in children)
            count += child.Motion?.GetUnityAnimationEventCapacity() ?? 0;
        return count;
    }
}
