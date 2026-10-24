using XREngine.Data.Core;
using XREngine.Scene;

namespace XREngine.Editor;

/// <summary>Restores only the ordered selection still owned by this duplicate action.</summary>
internal sealed class HierarchyCloneSelection
{
    private readonly ObjectConstructionIdentity[] _previous = Selection.SceneNodes
        .Select(ObjectConstructionIdentity.Capture).ToArray();
    private SceneNode[] _expected = [.. Selection.SceneNodes];
    private bool _settled = true;

    internal void Select(SceneNode node) => Assign([node]);

    internal void Restore()
        => Assign(_previous.Where(identity => identity.IsCurrent &&
            !identity.Target.IsDestroyed && !identity.Target.IsDestroyQueued)
            .Select(identity => (SceneNode)identity.Target).ToArray());

    private void Assign(SceneNode[] requested)
    {
        if (!_settled) return;
        if (!Matches(_expected))
        {
            _settled = false;
            return;
        }
        if (Matches(requested)) return;
        // Observers receive a separate array. They cannot change our expected state.
        SceneNode[] expected = [.. requested];
        try { Selection.SceneNodes = [.. requested]; }
        catch { _settled = false; throw; }
        if (Matches(expected))
            _expected = expected;
        else
            _settled = false;
    }

    private static bool Matches(SceneNode[] expected)
    {
        SceneNode[] current = Selection.SceneNodes;
        if (current.Length != expected.Length) return false;
        for (int index = 0; index < current.Length; index++)
            if (!ReferenceEquals(current[index], expected[index])) return false;
        return true;
    }
}
