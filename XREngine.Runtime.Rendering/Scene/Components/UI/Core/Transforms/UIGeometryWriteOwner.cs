using XREngine.Data.Core;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Rendering.UI;

/// <summary>Checks the original attachment and lifetime of one geometry write.</summary>
internal readonly struct UIGeometryWriteOwner(UITransform target, SceneNode node)
{
    private readonly UITransform _target = target;
    private readonly SceneNode _node = node;
    private readonly ObjectConstructionIdentity _identity = ObjectConstructionIdentity.Capture(target);
    private readonly ObjectConstructionIdentity _nodeIdentity = ObjectConstructionIdentity.Capture(node);
    private readonly TransformBase? _parent = target.Parent;
    private readonly ObjectConstructionIdentity? _parentIdentity = target.Parent is { } parent
        ? ObjectConstructionIdentity.Capture(parent) : null;
    private readonly UICanvasTransform? _canvas = target.ParentCanvas;
    private readonly ObjectConstructionIdentity? _canvasIdentity = target.ParentCanvas is { } canvas
        ? ObjectConstructionIdentity.Capture(canvas) : null;
    private readonly IRuntimeWorldContext? _world = target.World;
    private readonly IRuntimeWorldContext? _nodeWorld = node.World;

    public void RequireCurrent()
    {
        if (!_identity.IsCurrent || !_nodeIdentity.IsCurrent || _target.IsDestroyed || _target.IsDestroyQueued ||
            _node.IsDestroyed || _node.IsDestroyQueued || _node.IsTransformNull ||
            !ReferenceEquals(_target.SceneNode, _node) || !ReferenceEquals(_node.Transform, _target) ||
            !ReferenceEquals(_target.Parent, _parent) || !ReferenceEquals(_target.ParentCanvas, _canvas) ||
            !ReferenceEquals(_target.World, _world) || !ReferenceEquals(_node.World, _nodeWorld) ||
            _parentIdentity is { } p && (!p.IsCurrent || p.Target.IsDestroyed || p.Target.IsDestroyQueued) ||
            _canvasIdentity is { } c && (!c.IsCurrent || c.Target.IsDestroyed || c.Target.IsDestroyQueued))
            throw new InvalidOperationException("The geometry owner changed during assignment.");
    }
}
