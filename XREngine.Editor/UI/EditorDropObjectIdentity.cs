using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Editor.UI;

/// <summary>Checks an editor drop object's lifetime and current scene owner between callbacks.</summary>
internal readonly struct EditorDropObjectIdentity
{
    private readonly object _target;
    private readonly ObjectConstructionIdentity? _identity;
    private readonly ObjectConstructionIdentity? _ownerIdentity;
    private readonly SceneNode? _owner;
    private readonly object? _world;

    public EditorDropObjectIdentity(object target)
    {
        _target = target;
        _identity = target is XRObjectBase value ? ObjectConstructionIdentity.Capture(value) : null;
        _owner = target switch
        {
            XRComponent component => component.SceneNode,
            TransformBase transform => transform.SceneNode,
            SceneNode node => node,
            _ => null
        };
        _ownerIdentity = _owner is null ? null : ObjectConstructionIdentity.Capture(_owner);
        _world = (target as XRWorldObjectBase)?.World;
    }

    public bool IsCurrent
    {
        get
        {
            if (_identity is { } identity && (!identity.IsCurrent ||
                    identity.Target.IsDestroyed || identity.Target.IsDestroyQueued) ||
                _ownerIdentity is { } ownerIdentity && (!ownerIdentity.IsCurrent ||
                    ownerIdentity.Target.IsDestroyed || ownerIdentity.Target.IsDestroyQueued) ||
                _target is XRWorldObjectBase worldObject &&
                    !ReferenceEquals(worldObject.World, _world) ||
                _world is RuntimeWorld { IsDisposing: true })
                return false;
            if (_target is XRComponent component)
            {
                if (_owner is null || !ReferenceEquals(component.SceneNode, _owner))
                    return false;
                for (int index = 0; index < _owner.Components.Count; index++)
                    if (ReferenceEquals(_owner.Components[index], component))
                        return true;
                return false;
            }
            return _target is not TransformBase transform || _owner is not null &&
                ReferenceEquals(transform.SceneNode, _owner) && ReferenceEquals(_owner.Transform, transform);
        }
    }
}
