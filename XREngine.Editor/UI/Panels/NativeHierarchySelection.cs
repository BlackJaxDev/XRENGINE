using XREngine.Rendering.UI;
using XREngine.Scene;

namespace XREngine.Editor;

/// <summary>Mirrors selected scene identities into a virtual tree without realizing rows.</summary>
internal sealed class NativeHierarchySelection
{
    private HashSet<ulong> _selected = [];
    private HashSet<ulong> _next = [];
    private UIVirtualTreeProvider? _owner;

    public bool Synchronize(UIVirtualTreeProvider provider,
        IReadOnlyDictionary<Guid, ulong> ids, IReadOnlyDictionary<ulong, SceneNode> nodes,
        IReadOnlyList<SceneNode> selection, bool reset = false)
    {
        bool replace = reset || !ReferenceEquals(_owner, provider);
        _next.Clear();
        ulong active = 0;
        for (int index = 0; index < selection.Count; index++)
        {
            SceneNode node = selection[index];
            if (node is null || node.IsDestroyed || node.IsDestroyQueued ||
                !ids.TryGetValue(node.ID, out ulong id) ||
                !nodes.TryGetValue(id, out SceneNode? current) || !ReferenceEquals(current, node) ||
                !provider.TryGetNode(id, out _))
                continue;
            _next.Add(id);
            if (active == 0)
                active = id;
        }
        bool changed = false;
        foreach (ulong id in _selected)
            if (!_next.Contains(id))
                changed |= provider.SetSelected(id, false);
        foreach (ulong id in _next)
            if (replace || !_selected.Contains(id))
                changed |= provider.SetSelected(id, true);
        changed |= provider.Activate(active);
        (_selected, _next) = (_next, _selected);
        _owner = provider;
        return changed;
    }

    public void Clear()
    {
        _selected.Clear();
        _next.Clear();
        _owner = null;
    }
}
