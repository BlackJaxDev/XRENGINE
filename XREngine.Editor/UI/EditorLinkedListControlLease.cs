using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Editor.UI;

/// <summary>Captures one editor control and its exact path under a recycled row editor.</summary>
internal sealed class EditorLinkedListControlLease
{
    private readonly XRComponent _component;
    private readonly ObjectConstructionIdentity _componentIdentity;
    private readonly SceneNode[] _path;
    private readonly ObjectConstructionIdentity[] _pathIdentities;
    private readonly TransformBase[] _transforms;
    private readonly ObjectConstructionIdentity[] _transformIdentities;

    private EditorLinkedListControlLease(XRComponent component, SceneNode[] path)
    {
        _component = component;
        _componentIdentity = ObjectConstructionIdentity.Capture(component);
        _path = path;
        _pathIdentities = path.Select(node => ObjectConstructionIdentity.Capture(node)).ToArray();
        _transforms = path.Select(node => node.Transform).ToArray();
        _transformIdentities = _transforms.Select(transform => ObjectConstructionIdentity.Capture(transform)).ToArray();
    }

    public static EditorLinkedListControlLease Capture(XRComponent component, SceneNode editor)
    {
        SceneNode? node = component.SceneNode;
        List<SceneNode> reversePath = [];
        while (node is not null)
        {
            reversePath.Add(node);
            if (ReferenceEquals(node, editor)) break;
            node = node.Transform.Parent?.SceneNode;
        }
        if (reversePath.Count == 0 || !ReferenceEquals(reversePath[^1], editor))
            throw new InvalidOperationException("An editor control is outside its retained row editor.");
        reversePath.Reverse();
        return new(component, reversePath.ToArray());
    }

    public bool IsCurrent(SceneNode editor)
    {
        if (!_componentIdentity.IsCurrent || _component.IsDestroyed || _component.IsDestroyQueued ||
            _path.Length == 0 || !ReferenceEquals(_path[0], editor) ||
            !ReferenceEquals(_component.SceneNode, _path[^1]) ||
            !_path[^1].Components.Contains(_component))
            return false;
        for (int index = 0; index < _path.Length; index++)
        {
            SceneNode node = _path[index];
            if (!_pathIdentities[index].IsCurrent || node.IsDestroyed || node.IsDestroyQueued ||
                !ReferenceEquals(node.Transform, _transforms[index]) ||
                !_transformIdentities[index].IsCurrent ||
                _transforms[index].IsDestroyed || _transforms[index].IsDestroyQueued)
                return false;
            if (index > 0 && !ReferenceEquals(_transforms[index].Parent, _transforms[index - 1]))
                return false;
        }
        return true;
    }
}
