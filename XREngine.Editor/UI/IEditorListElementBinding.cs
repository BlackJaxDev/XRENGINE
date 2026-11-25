using System.Collections;
using System.Reflection;
using XREngine.Scene;

namespace XREngine.Editor.UI;

/// <summary>Identifies the real owner of a list element editor.</summary>
internal interface IEditorListElementBinding
{
    object Owner { get; }
    object? OwnerDocument { get; }
    object? OwnerWorld { get; }
    SceneNode? OwnerNode { get; }
    PropertyInfo OwnerProperty { get; }
    IList Collection { get; }
    object CollectionState { get; }
    object? DisplayedValue { get; }
    int Index { get; }
    ulong RowId { get; }
    ulong ContextRevision { get; }
    EditorListMutationContext Context { get; }
    bool IsLeased { get; }
    void AcceptLease();
    void ReleaseLease();
    void AcceptCommitted(object? value, object state, ulong revision);
}
