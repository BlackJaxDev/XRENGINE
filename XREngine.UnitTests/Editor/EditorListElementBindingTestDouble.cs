using System.Collections;
using System.Reflection;
using XREngine.Editor.UI;
using XREngine.Scene;

namespace XREngine.UnitTests.Editor;

internal sealed class EditorListElementBindingTestDouble : IEditorListElementBinding
{
    private ulong _revision;
    private object _state;
    private object? _displayedValue;
    private bool _leased = true;

    public EditorListElementBindingTestDouble(EditorListMutationTestTarget owner,
        EditorListMutationContext context, int index)
    {
        Owner = owner;
        OwnerProperty = typeof(EditorListMutationTestTarget).GetProperty(nameof(owner.Values))!;
        Collection = owner.Values;
        Context = context;
        Index = index;
        RowId = context.Rows.GetItemId(index);
        _revision = context.Revision;
        _state = EditorListMutation.GetStateToken(Collection);
        _displayedValue = Collection[index];
    }

    public object Owner { get; }
    public object? OwnerDocument => null;
    public object? OwnerWorld => null;
    public SceneNode? OwnerNode => null;
    public PropertyInfo OwnerProperty { get; }
    public IList Collection { get; }
    public object CollectionState => _state;
    public object? DisplayedValue => _displayedValue;
    public int Index { get; }
    public ulong RowId { get; }
    public ulong ContextRevision => _revision;
    public EditorListMutationContext Context { get; }
    public bool IsLeased => _leased;
    public void AcceptLease() => _leased = true;
    public void ReleaseLease() => _leased = false;
    public int Value
    {
        get => _revision == Context.Revision ? (int)Collection[Index]! : 0;
        set => EditorListMutation.Set([this], _ => value);
    }

    public void AcceptCommitted(object? value, object state, ulong revision)
    {
        _displayedValue = value;
        _state = state;
        _revision = revision;
    }
}
