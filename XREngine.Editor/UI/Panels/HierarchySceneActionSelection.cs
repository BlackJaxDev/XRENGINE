using XREngine.Data.Core;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Editor;

/// <summary>Changes only the selection publication retained before one scene unload.</summary>
internal sealed class HierarchySceneActionSelection
{
    private readonly ObjectConstructionIdentity[] _before;
    private readonly SceneNode[] _filtered;
    private SceneNode[] _expected;
    private ulong _revision;
    private bool _owned = true;

    internal HierarchySceneActionSelection(XRScene scene)
    {
        _revision = Selection.PublicationRevision;
        _expected = [.. Selection.SceneNodes];
        _before = _expected.Select(ObjectConstructionIdentity.Capture).ToArray();
        _filtered = _expected.Where(node => !BelongsToScene(node, scene)).ToArray();
    }

    internal void Apply() => Assign(_filtered);

    internal void Restore()
        => Assign(_before.Where(identity => identity.IsCurrent && !identity.Target.IsDestroyed && !identity.Target.IsDestroyQueued)
            .Select(identity => (SceneNode)identity.Target).ToArray());

    private void Assign(SceneNode[] requested)
    {
        if (!_owned) return;
        if (Selection.PublicationRevision != _revision || !Matches(_expected))
        {
            _owned = false;
            return;
        }
        if (Matches(requested)) return;
        ulong next = unchecked(_revision + 1);
        try { Selection.SceneNodes = [.. requested]; }
        finally
        {
            // A callback can replace the array or republish equal values. Neither
            // publication belongs to this unload, even when values later match.
            _owned = Selection.PublicationRevision == next && Matches(requested);
            if (_owned)
            {
                _revision = next;
                _expected = [.. requested];
            }
        }
    }

    private static bool Matches(SceneNode[] expected)
    {
        SceneNode[] current = Selection.SceneNodes;
        if (current.Length != expected.Length) return false;
        for (int index = 0; index < current.Length; index++)
            if (!ReferenceEquals(current[index], expected[index])) return false;
        return true;
    }

    private static bool BelongsToScene(SceneNode node, XRScene scene)
    {
        TransformBase? transform = node.OwnedRetirementTransform;
        HashSet<TransformBase> visited = new(ReferenceEqualityComparer.Instance);
        while (transform is not null && visited.Add(transform))
        {
            if (transform.OwnedRetirementParent is { } parent)
            {
                transform = parent;
                continue;
            }
            return transform.OwnedRetirementNode is { } root &&
                root.CaptureSceneRootMemberships().Any(membership =>
                    ReferenceEquals(membership.Scene, scene) && membership.Collection.IsCurrent);
        }
        return false;
    }
}
