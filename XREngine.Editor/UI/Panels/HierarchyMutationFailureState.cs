using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Editor;

/// <summary>Allows retry only while an incomplete edit still has its exact captured state.</summary>
internal sealed class HierarchyMutationFailureState
{
    private readonly SceneNode _node;
    private readonly RuntimeWorld _world;
    private readonly TransformBase _transform;
    private readonly TransformBase? _parent;
    private readonly TransformBase? _firstParent;
    private readonly TransformBase? _secondParent;
    private readonly bool _firstContains;
    private readonly bool _secondContains;
    private readonly IRuntimeWorldContext? _nodeWorld;
    private readonly bool _inRoots;
    private readonly bool _published;
    private readonly bool _active;
    private readonly HierarchyTransformState _values;
    private readonly HierarchySceneRootSlot[] _scenes;

    public HierarchyMutationFailureState(HierarchyPlacement before, HierarchyPlacement after)
    {
        _node = before.Node;
        _world = before.World;
        _transform = _node.Transform;
        _parent = _transform.Parent;
        _firstParent = before.Parent;
        _secondParent = after.Parent;
        _firstContains = _firstParent?.Children.Contains(_transform) == true;
        _secondContains = _secondParent?.Children.Contains(_transform) == true;
        _nodeWorld = _node.World;
        _inRoots = _world.RootNodes.Contains(_node);
        _published = before.IsPublishedRoot();
        _active = _node.IsActiveSelf;
        _values = new(_transform);
        _scenes = HierarchySceneOwnership.CaptureRootScenes(_node, _world)
            .Select(scene => HierarchySceneRootSlot.Capture(scene, _node, _world)).ToArray();
    }

    public bool Matches()
    {
        if (!ReferenceEquals(_node.Transform, _transform) || !ReferenceEquals(_transform.Parent, _parent) ||
            !ReferenceEquals(_node.World, _nodeWorld) || _world.RootNodes.Contains(_node) != _inRoots ||
            _node.IsActiveSelf != _active || !_values.Matches() ||
            (_firstParent?.Children.Contains(_transform) == true) != _firstContains ||
            (_secondParent?.Children.Contains(_transform) == true) != _secondContains)
            return false;
        bool published = false;
        foreach (SceneNode root in _world.RootNodes.Snapshot)
            published |= ReferenceEquals(root, _node);
        if (published != _published)
            return false;
        foreach (HierarchySceneRootSlot slot in _scenes)
            if (!ReferenceEquals(slot.Scene.SceneRoots, slot.Roots) || slot.Roots.IndexOf(_node) != slot.Index)
                return false;
        foreach (XRScene scene in HierarchySceneOwnership.CaptureRootScenes(_node, _world))
            if (!_scenes.Any(slot => ReferenceEquals(slot.Scene, scene)))
                return false;
        return true;
    }
}
