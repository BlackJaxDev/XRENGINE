using System.Reflection;
using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Scene;

namespace XREngine.Editor.UI;

/// <summary>Proves that a visible linked-list command still owns its source.</summary>
internal sealed class EditorLinkedSourceLease
{
    public required object Owner { get; init; }
    public required PropertyInfo Property { get; init; }
    public required object List { get; init; }
    public required EditorLinkedListAdapter Adapter { get; init; }
    public required EditorListSourceAccessor Accessor { get; init; }
    public required EditorLinkedListSnapshot Displayed { get; set; }
    public required object State { get; set; }
    public required object? World { get; init; }
    public required object? Document { get; init; }
    public required SceneNode? SceneNode { get; init; }
    public required Func<bool> IsViewCurrent { get; init; }

    public bool IsOwnerLocationCurrent()
        => Owner is not XRObjectBase { IsDestroyed: true } &&
           Owner is not XRObjectBase { IsDestroyQueued: true } &&
           Owner is not XRWorldObjectBase { World: RuntimeWorld { IsDisposing: true } } &&
           ReferenceEquals(EditorListMutation.WorldOf(Owner), World) &&
           ReferenceEquals(EditorListMutation.DocumentOf(Owner), Document) &&
           ReferenceEquals(EditorListMutation.NodeOf(Owner), SceneNode) &&
           (Owner is not XRComponent component || SceneNode is not null &&
               !SceneNode.IsDestroyed && !SceneNode.IsDestroyQueued &&
               SceneNode.Components.Contains(component)) &&
           Accessor.IsCurrent(Owner, List);

    public bool IsCurrent()
        => IsViewCurrent() && IsOwnerLocationCurrent() && ReferenceEquals(Property.GetValue(Owner), List) &&
           IsOwnerLocationCurrent() &&
           ReferenceEquals(EditorLinkedMutation.StateOf(List), State) &&
           Adapter.Matches(List, Displayed);

    public void AcceptCommitted(EditorLinkedListSnapshot snapshot, object state)
    {
        Displayed = snapshot;
        State = state;
    }
}
