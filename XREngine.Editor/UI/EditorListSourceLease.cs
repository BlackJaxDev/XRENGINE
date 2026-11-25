using System.Collections;
using System.Reflection;
using XREngine.Scene;

namespace XREngine.Editor.UI;

/// <summary>Captures the exact source of a list detail command.</summary>
internal sealed class EditorListSourceLease
{
    public required object Owner { get; init; }
    public required PropertyInfo Property { get; init; }
    public required IList List { get; init; }
    public required object State { get; init; }
    public required object? World { get; init; }
    public required object? Document { get; init; }
    public required SceneNode? Node { get; init; }
    public required int Count { get; init; }

    public static EditorListSourceLease Capture(object owner, PropertyInfo property)
    {
        EditorListSourceAccessor accessor = EditorListSourceAccessor.For(property) ??
            throw new InvalidOperationException("This list source needs a callback-free owner property.");
        object? world = EditorListMutation.WorldOf(owner);
        object? document = EditorListMutation.DocumentOf(owner);
        SceneNode? node = EditorListMutation.NodeOf(owner);
        if (property.GetValue(owner) is not IList list)
            throw new InvalidOperationException("The list command has no source collection.");
        if (!accessor.IsCurrent(owner, list))
            throw new InvalidOperationException("The list command source changed while it was bound.");
        if (!ReferenceEquals(world, EditorListMutation.WorldOf(owner)) ||
            !ReferenceEquals(document, EditorListMutation.DocumentOf(owner)) ||
            !ReferenceEquals(node, EditorListMutation.NodeOf(owner)))
            throw new InvalidOperationException("The list owner moved while the command was bound.");
        return new()
        {
            Owner = owner, Property = property, List = list,
            State = EditorListMutation.GetStateToken(list),
            World = world, Document = document, Node = node, Count = list.Count
        };
    }
}
