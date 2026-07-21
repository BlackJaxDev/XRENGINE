using System.Runtime.CompilerServices;

namespace XREngine.Components;

/// <summary>
/// Per-world content-addressed store for immutable physics-chain templates.
/// Templates are compared only when an authored structural version changes.
/// </summary>
internal sealed class PhysicsChainTemplateCache
{
    private static readonly ConditionalWeakTable<PhysicsChainWorld, PhysicsChainTemplateCache> WorldCaches = [];
    private readonly Dictionary<ulong, List<PhysicsChainTemplate>> _templatesByHash = [];
    private long _nextStableId;

    public int UniqueTemplateCount { get; private set; }

    public static PhysicsChainTemplateCache ForWorld(PhysicsChainWorld world)
        => WorldCaches.GetValue(world, static _ => new PhysicsChainTemplateCache());

    public PhysicsChainTemplate GetOrAdd(PhysicsChainTemplate candidate)
    {
        if (_templatesByHash.TryGetValue(candidate.ContentHash, out List<PhysicsChainTemplate>? bucket))
        {
            for (int i = 0; i < bucket.Count; ++i)
                if (bucket[i].ContentEquals(candidate))
                    return bucket[i];
        }
        else
        {
            bucket = [];
            _templatesByHash.Add(candidate.ContentHash, bucket);
        }

        candidate.AssignStableId(checked(++_nextStableId));
        bucket.Add(candidate);
        ++UniqueTemplateCount;
        return candidate;
    }
}
