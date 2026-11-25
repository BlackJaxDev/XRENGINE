using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Scene.Transforms;

namespace XREngine.Scene.Prefabs;

/// <summary>Captures exact structural links before an owned clone runs initialization callbacks.</summary>
internal sealed record OwnedHierarchyGraphEntry(TransformBase Transform, TransformBase? Parent,
    SceneNode? Node, EventList<TransformBase> ChildList, TransformBase[] Children,
    EventList<XRComponent>? ComponentList, XRComponent[] Components);
