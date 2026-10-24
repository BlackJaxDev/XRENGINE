using MemoryPack;
using XREngine.Scene;
using XREngine.Scene.Prefabs;
using YamlDotNet.Serialization;

namespace XREngine.Components;

public abstract partial class XRComponent
{
    [YamlIgnore, MemoryPackIgnore]
    internal SceneNode? OwnedRetirementNode => _sceneNode;

    internal void StopOwnedLifecycle(OwnedHierarchyLifecyclePermit permit)
    {
        permit.Enter(this);
        if (permit.Kind == EOwnedHierarchyLifecycle.Deactivate) DeactivateCore();
        else OnEndPlay();
        permit.Complete();
    }
}
