using MemoryPack;
using XREngine.Components;
using XREngine.Scene.Prefabs;
using XREngine.Scene.Transforms;
using YamlDotNet.Serialization;

namespace XREngine.Scene;

public sealed partial class SceneNode
{
    [YamlIgnore, MemoryPackIgnore]
    internal TransformBase? OwnedRetirementTransform => _transform;
    [YamlIgnore, MemoryPackIgnore]
    internal EventList<XRComponent>? OwnedRetirementComponents => _components;

    internal void StopOwnedLifecycle(OwnedHierarchyLifecyclePermit permit)
    {
        permit.Enter(this);
        if (permit.Kind == EOwnedHierarchyLifecycle.Deactivate) DeactivateCore(permit.Retirement);
        else EndPlayCore(permit.Retirement);
        permit.Complete();
    }

    private void DestroyOwnedHierarchy(OwnedHierarchyRetirement retirement)
    {
        retirement.Validate();
        retirement.StopLifecycle(this, EOwnedHierarchyLifecycle.Deactivate);
        retirement.Validate();
        retirement.StopLifecycle(this, EOwnedHierarchyLifecycle.EndPlay);
        retirement.Validate();
        if (_transform is null)
            throw new InvalidOperationException("The owned node lost its transform before cleanup.");

        retirement.RetireChildren(_transform);
        while (_components.Count != 0)
        {
            XRComponent component = _components[^1];
            retirement.Validate();
            component.Destroying -= ComponentDestroying;
            component.Destroyed -= ComponentDestroyed;
            retirement.RemoveComponent(this, component);
            retirement.DestroyTarget(component);
        }
        _components.PostAnythingAdded -= OnComponentAdded;
        _components.PostAnythingRemoved -= OnComponentRemoved;

        // Parent notifications do not need to restart this terminal node's lifecycle.
        _transform.PropertyChanging -= TransformPropertyChangingHandler;
        _transform.PropertyChanged -= TransformPropertyChangedHandler;
        retirement.DestroyTarget(_transform);
        retirement.Validate();
        base.OnDestroying();
        retirement.RetireEmptyList(this, _components);
        retirement.CompleteCanonicalCleanup(this);
    }
}
