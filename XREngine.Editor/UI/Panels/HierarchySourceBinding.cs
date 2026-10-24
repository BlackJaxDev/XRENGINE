using XREngine.Data.Core;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Editor;

/// <summary>Retains the exact property and child-list subscriptions for a hierarchy node.</summary>
internal sealed class HierarchySourceBinding(SceneNode node, TransformBase transform)
{
    public readonly SceneNode Node = node;
    public readonly TransformBase Transform = transform;
    public readonly EventList<TransformBase> Children = transform.Children;
    public EventList<TransformBase>.SingleHandler? Added;
    public EventList<TransformBase>.SingleHandler? Removed;
    public XRPropertyChangedEventHandler? Changed;
    public bool Released;
}
