using XREngine.Scene;

namespace XREngine.Editor;

/// <summary>Captures the exact authoring sources before a queued menu or drop command.</summary>
internal sealed class HierarchyCommandContext
{
    private readonly Func<bool> _ownerIsCurrent;
    private readonly HierarchyPlacement[] _sources;
    private readonly XRWorld? _worldAsset;
    private readonly List<XRScene>? _sceneList;
    private readonly IRuntimeWorldScenePolicy? _policy;
    private readonly XRScene? _scene;
    private readonly SceneRootCollection? _roots;
    private readonly bool _visible;
    private readonly bool _loaded;

    public HierarchyCommandContext(RuntimeWorld world, IReadOnlyList<SceneNode> nodes,
        Func<bool> ownerIsCurrent, XRScene? scene = null)
    {
        World = world;
        _ownerIsCurrent = ownerIsCurrent;
        _sources = new HierarchyPlacement[nodes.Count];
        for (int index = 0; index < nodes.Count; index++)
        {
            if (!HierarchySceneOwnership.CanAuthor(nodes[index], world))
                throw new InvalidOperationException("The hierarchy command contains a node from another owner.");
            _sources[index] = HierarchyPlacement.Capture(nodes[index], world);
        }
        _worldAsset = world.TargetWorld;
        _sceneList = world.TargetWorld?.Scenes;
        _policy = world.ScenePolicy;
        _scene = scene;
        _roots = scene?.SceneRoots;
        _visible = scene?.IsVisible == true;
        _loaded = scene is not null && world.IsSceneLoaded(scene);
    }

    public RuntimeWorld World { get; }

    public bool IsOwnerCurrent()
        => !World.IsDisposing && ReferenceEquals(World.TargetWorld, _worldAsset) &&
            ReferenceEquals(World.TargetWorld?.Scenes, _sceneList) && ReferenceEquals(World.ScenePolicy, _policy) &&
            _ownerIsCurrent();

    public bool CanStart()
    {
        if (!IsOwnerCurrent())
            return false;
        foreach (HierarchyPlacement source in _sources)
            if (!source.Matches())
                return false;
        return _scene is null || !_scene.IsDestroyed && !_scene.IsDestroyQueued &&
            _sceneList?.Contains(_scene) == true && ReferenceEquals(_scene.SceneRoots, _roots) &&
            _scene.IsVisible == _visible && World.IsSceneLoaded(_scene) == _loaded &&
            (!_loaded || World.IsSceneReady(_scene));
    }
}
