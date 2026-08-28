namespace XREngine.Animation;

public abstract partial class MotionBase
{
    internal void CollectImportedHumanoidContributions(
        HumanoidMotionContributionBuffer destination,
        IDictionary<string, AnimVar> variables,
        double normalizedTime,
        float weight,
        ulong occurrenceId,
        ulong lifecycleGeneration,
        bool mirror)
    {
        switch (this)
        {
            case AnimationClip clip:
                if (clip.TryCreateImportedHumanoidMotionContribution(
                    normalizedTime,
                    weight,
                    CombineOccurrenceId(occurrenceId, clip.ID),
                    lifecycleGeneration,
                    mirror,
                    out HumanoidMotionContribution contribution))
                {
                    destination.TryAdd(contribution);
                }
                break;
            case BlendTree1D tree1D:
                tree1D.CollectImportedHumanoidChildContributions(
                    destination,
                    variables,
                    normalizedTime,
                    weight,
                    occurrenceId,
                    lifecycleGeneration,
                    mirror);
                break;
            case BlendTree2D tree2D:
                tree2D.CollectImportedHumanoidChildContributions(
                    destination,
                    variables,
                    normalizedTime,
                    weight,
                    occurrenceId,
                    lifecycleGeneration,
                    mirror);
                break;
            case BlendTreeDirect directTree:
                directTree.CollectImportedHumanoidChildContributions(
                    destination,
                    variables,
                    normalizedTime,
                    weight,
                    occurrenceId,
                    lifecycleGeneration,
                    mirror);
                break;
        }
    }

    internal static ulong CombineOccurrenceId(ulong parent, Guid id)
    {
        Span<byte> bytes = stackalloc byte[16];
        id.TryWriteBytes(bytes);
        ulong hash = parent == 0UL ? 14695981039346656037UL : parent;
        for (int i = 0; i < bytes.Length; i++)
        {
            hash ^= bytes[i];
            hash *= 1099511628211UL;
        }
        return hash;
    }
}
