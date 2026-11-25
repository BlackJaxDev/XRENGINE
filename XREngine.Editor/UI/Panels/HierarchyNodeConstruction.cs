using XREngine.Data.Core;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Editor;

/// <summary>Owns a new child until a hierarchy edit or its recovery history takes ownership.</summary>
internal sealed class HierarchyNodeConstruction
{
    private readonly ObjectCacheOwnership _allocations;
    private readonly TransformBase _transform;
    private readonly RuntimeWorld _world;

    public HierarchyNodeConstruction(RuntimeWorld world)
    {
        _world = world;
        using ObjectCachePublicationScope publication = XRObjectBase.BeginIndependentObjectCachePublication();
        Node = new SceneNode { IsActiveSelf = false };
        _transform = Node.Transform;
        _allocations = publication.CompleteWithOwnership();
    }

    public SceneNode Node { get; }

    public void RetireRestoredCreation()
    {
        using StructuralChangeHandle cleanup = Undo.BeginStructuralMutation("Discard failed child",
            Retire,
            () => throw new InvalidOperationException("A failed child creation must be issued again."));
        try
        {
            Retire();
            cleanup.RemoveRestoredStep();
        }
        catch (Exception exception)
        {
            Debug.LogException(exception, "A failed child creation retains its cleanup inverse.");
        }
    }

    private void Retire()
    {
        if (Node.IsDestroyed && _allocations.Objects.Count == 0)
            return;
        if (!CanRetire())
            throw new InvalidOperationException("The failed child acquired another owner before cleanup.");
        foreach (XRObjectBase allocation in _allocations.Objects)
            Undo.Untrack(allocation);
        XRObjectBase[] allocations = [.. _allocations.Objects];
        bool Guard(XRObjectBase _) => CanRetire();
        foreach (XRObjectBase allocation in allocations)
            allocation.Destroying += Guard;
        try { _allocations.Dispose(); }
        finally
        {
            foreach (XRObjectBase allocation in allocations)
                allocation.Destroying -= Guard;
        }
    }

    private bool CanRetire()
        => Node.IsDestroyed
            ? _transform.SceneNode is null || ReferenceEquals(_transform.SceneNode, Node)
            : ReferenceEquals(Node.Transform, _transform) && Node.World is null && Node.Parent is null &&
              !_world.RootNodes.Contains(Node) && _transform.Children.Count == 0 && Node.Components.Count == 0 &&
              HierarchySceneOwnership.CaptureRootScenes(Node, _world).Length == 0 &&
              !Selection.SceneNodes.Contains(Node);
}
