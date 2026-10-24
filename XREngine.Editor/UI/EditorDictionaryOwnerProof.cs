using System.Reflection;
using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Scene;

namespace XREngine.Editor.UI;

/// <summary>Retains a dictionary owner without retaining its inspector view.</summary>
internal sealed class EditorDictionaryOwnerProof(EditorDictionarySourceLease source)
{
    public object Owner { get; } = source.Owner;
    public PropertyInfo Property { get; } = source.Property;
    public object Dictionary { get; } = source.Dictionary;
    public EditorListSourceAccessor Accessor { get; } = source.Accessor;
    public object? World { get; } = source.World;
    public object? Document { get; } = source.Document;
    public SceneNode? SceneNode { get; } = source.SceneNode;

    public bool IsLocationCurrent()
        => Owner is not XRObjectBase { IsDestroyed: true } &&
           Owner is not XRObjectBase { IsDestroyQueued: true } &&
           Owner is not XRWorldObjectBase { World: RuntimeWorld { IsDisposing: true } } &&
           ReferenceEquals(EditorListMutation.WorldOf(Owner), World) &&
           ReferenceEquals(EditorListMutation.DocumentOf(Owner), Document) &&
           ReferenceEquals(EditorListMutation.NodeOf(Owner), SceneNode) &&
           (Owner is not XRComponent component || SceneNode is not null &&
               !SceneNode.IsDestroyed && !SceneNode.IsDestroyQueued &&
               SceneNode.Components.Contains(component)) &&
           Accessor.IsCurrent(Owner, Dictionary);

    public bool IsCurrent()
        => IsLocationCurrent() && ReferenceEquals(Property.GetValue(Owner), Dictionary) &&
           IsLocationCurrent();
}
