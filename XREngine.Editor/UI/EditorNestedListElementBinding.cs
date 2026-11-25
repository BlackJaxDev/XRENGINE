using System.Collections;
using System.Reflection;
using XREngine.Scene;

namespace XREngine.Editor.UI;

/// <summary>Edits one inner-list slot while retaining its distinct outer source path.</summary>
internal sealed class EditorNestedListElementBinding<T> : IEditorNestedListElementBinding
{
    private readonly EditorNestedListSourceLease[] _paths;
    private readonly EditorNestedListMutationContext _nestedContext;
    private readonly EditorListMutationContext _context;
    private readonly Func<bool> _rowCurrent;
    private readonly int _index;
    private readonly ulong _rowId;
    private ulong _revision;
    private readonly int _sourceIndex;
    private bool _leased = true;
    private object? _displayed;

    public EditorNestedListElementBinding(EditorNestedListSourceLease[] paths,
        EditorNestedListMutationContext nestedContext, EditorListMutationContext context,
        int index, ulong rowId, int sourceIndex, Func<bool> rowCurrent)
    {
        _paths = paths;
        _nestedContext = nestedContext;
        _context = context;
        _index = index;
        _rowId = rowId;
        _revision = context.Revision;
        _sourceIndex = sourceIndex;
        _rowCurrent = rowCurrent;
        _displayed = paths[sourceIndex].InnerDisplayed[index];
    }

    public EditorNestedListSourceLease[] Paths => _paths;
    public EditorNestedListSourceLease SourcePath => _paths[_sourceIndex];
    public EditorNestedListMutationContext MutationContext => _nestedContext;
    public bool IsRowCurrent => _leased && _nestedContext.IsActive && _rowCurrent() &&
        _context.IsActive && _index >= 0 && _index < _context.Rows.Count &&
        _context.Rows.GetItemId(_index) == _rowId && _context.Revision == _revision &&
        _paths.All(path => path.IsCommandCurrent());
    public object Owner => _paths[_sourceIndex].Owner;
    public object? OwnerDocument => EditorListMutation.DocumentOf(Owner);
    public object? OwnerWorld => EditorListMutation.WorldOf(Owner);
    public SceneNode? OwnerNode => EditorListMutation.NodeOf(Owner);
    public PropertyInfo OwnerProperty => _paths[_sourceIndex].Property;
    public IList Collection => _paths[_sourceIndex].Inner;
    public object CollectionState => _paths[_sourceIndex].InnerState;
    public object? DisplayedValue => _displayed;
    public int Index => _index;
    public ulong RowId => _rowId;
    public ulong ContextRevision => _revision;
    public EditorListMutationContext Context => _context;
    public bool IsLeased => _leased;
    public void AcceptLease() => _leased = true;
    public void ReleaseLease() => _leased = false;

    public void AcceptCommitted(object? value, object state, ulong revision)
    {
        _displayed = value;
        _revision = revision;
    }

    public T? Value
    {
        get => IsRowCurrent ? _displayed is T value ? value : default : default;
        set
        {
            if (!IsRowCurrent) throw new InvalidOperationException("The nested list row is no longer current.");
            EditorNestedListMutation.Apply(_paths, EEditorListMutation.Set, _index,
                _ => value, _nestedContext, () => IsRowCurrent, [this]);
        }
    }
}
