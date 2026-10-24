namespace XREngine.Scene;

public sealed partial class SceneNode
{
    private HashSet<RootNodeCollection>? _runtimeRootOwners;

    /// <summary>Copies exact runtime-root owners, including retained failed attachments.</summary>
    public RootNodeCollection[] CaptureRuntimeRootMemberships()
    {
        lock (SceneRootCollection.MutationGate)
            return _runtimeRootOwners is null ? [] : [.. _runtimeRootOwners];
    }

    internal void PrepareRuntimeRootOwner(RootNodeCollection owner)
    {
        _runtimeRootOwners ??= new(ReferenceEqualityComparer.Instance);
        _runtimeRootOwners.EnsureCapacity(checked(_runtimeRootOwners.Count + 1));
    }

    internal void AddRuntimeRootOwner(RootNodeCollection owner) => _runtimeRootOwners!.Add(owner);
    internal void RemoveRuntimeRootOwner(RootNodeCollection owner) => _runtimeRootOwners?.Remove(owner);
}
