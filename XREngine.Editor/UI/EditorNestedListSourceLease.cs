using System.Collections;
using System.Reflection;
using XREngine.Components;
using XREngine.Core.Files;
using XREngine.Data.Core;
using XREngine.Scene;

namespace XREngine.Editor.UI;

/// <summary>Captures one direct collection-valued outer row and the durable path to its inner list.</summary>
internal sealed class EditorNestedListSourceLease
{
    private readonly IEditorListElementBinding? _listBinding;
    private readonly IEditorLinkedElementBinding? _linkedBinding;
    private readonly IEditorDictionaryElementBinding? _dictionaryBinding;
    private readonly EditorListSourceAccessor? _listAccessor;
    private readonly IList? _outerList;
    private readonly object? _outerState;
    private readonly int _outerIndex;
    private readonly ulong _outerRowId;
    private readonly ulong _outerRevision;
    private readonly EditorLinkedSourceLease? _linkedSource;
    private readonly EditorLinkedListSnapshot? _linkedDisplayed;
    private readonly object? _linkedState;
    private readonly object? _linkedNode;
    private readonly EditorDictionarySourceLease? _dictionarySource;
    private readonly EditorDictionarySnapshot? _dictionaryDisplayed;
    private readonly object? _dictionaryState;
    private readonly object? _dictionaryKey;
    private readonly object? _world;
    private readonly object? _document;
    private readonly SceneNode? _ownerNode;
    private readonly ObjectConstructionIdentity? _ownerIdentity;
    private readonly ObjectConstructionIdentity? _nodeIdentity;
    private readonly ObjectConstructionIdentity? _documentIdentity;
    private object?[] _innerDisplayed;
    private object _innerState;
    private bool _requiresOuterRowLease = true;

    private EditorNestedListSourceLease(IEditorListElementBinding binding, IList inner,
        object?[] displayed, object state)
    {
        _listBinding = binding;
        _listAccessor = EditorListSourceAccessor.For(binding.OwnerProperty)
            ?? throw new InvalidOperationException("This nested list needs a callback-free outer owner property.");
        _outerList = binding.Collection;
        _outerState = binding.CollectionState;
        _outerIndex = binding.Index;
        _outerRowId = binding.RowId;
        _outerRevision = binding.ContextRevision;
        Inner = inner;
        _innerDisplayed = displayed;
        _innerState = state;
        Owner = binding.Owner;
        Property = binding.OwnerProperty;
        _world = binding.OwnerWorld;
        _document = binding.OwnerDocument;
        _ownerNode = binding.OwnerNode;
        _ownerIdentity = Owner is XRObjectBase owner ? ObjectConstructionIdentity.Capture(owner) : null;
        _nodeIdentity = _ownerNode is null ? null : ObjectConstructionIdentity.Capture(_ownerNode);
        _documentIdentity = _document is XRObjectBase document ? ObjectConstructionIdentity.Capture(document) : null;
    }

    private EditorNestedListSourceLease(IEditorLinkedElementBinding binding, IList inner,
        object?[] displayed, object state)
    {
        _linkedBinding = binding;
        _linkedSource = binding.Source;
        _linkedDisplayed = binding.Source.Displayed;
        _linkedState = binding.Source.State;
        _linkedNode = binding.Node;
        Inner = inner;
        _innerDisplayed = displayed;
        _innerState = state;
        Owner = binding.Source.Owner;
        Property = binding.Source.Property;
        _world = binding.Source.World;
        _document = binding.Source.Document;
        _ownerNode = binding.Source.SceneNode;
        _ownerIdentity = Owner is XRObjectBase owner ? ObjectConstructionIdentity.Capture(owner) : null;
        _nodeIdentity = _ownerNode is null ? null : ObjectConstructionIdentity.Capture(_ownerNode);
        _documentIdentity = _document is XRObjectBase document ? ObjectConstructionIdentity.Capture(document) : null;
    }

    private EditorNestedListSourceLease(IEditorDictionaryElementBinding binding, IList inner,
        object?[] displayed, object state)
    {
        _dictionaryBinding = binding;
        _dictionarySource = binding.Source;
        _dictionaryDisplayed = binding.Source.Displayed;
        _dictionaryState = binding.Source.State;
        _dictionaryKey = binding.Key;
        Inner = inner;
        _innerDisplayed = displayed;
        _innerState = state;
        Owner = binding.Source.Owner;
        Property = binding.Source.Property;
        _world = binding.Source.World;
        _document = binding.Source.Document;
        _ownerNode = binding.Source.SceneNode;
        _ownerIdentity = Owner is XRObjectBase owner ? ObjectConstructionIdentity.Capture(owner) : null;
        _nodeIdentity = _ownerNode is null ? null : ObjectConstructionIdentity.Capture(_ownerNode);
        _documentIdentity = _document is XRObjectBase document ? ObjectConstructionIdentity.Capture(document) : null;
    }

    public object Owner { get; }
    public PropertyInfo Property { get; }
    public object? OwnerDocument => _document;
    public object? OwnerWorld => _world;
    public SceneNode? OwnerNode => _ownerNode;
    public IList Inner { get; }
    public object?[] InnerDisplayed => _innerDisplayed;
    public object InnerState => _innerState;
    public void PromoteToDetail() => _requiresOuterRowLease = false;

    public static EditorNestedListSourceLease Capture(object binding)
    {
        IList inner = binding switch
        {
            IEditorListElementBinding list when list.DisplayedValue is IList value => value,
            IEditorLinkedElementBinding linked when linked.DisplayedValue is IList value => value,
            IEditorDictionaryElementBinding dictionary when dictionary.DisplayedValue is IList value => value,
            _ => throw new InvalidOperationException("The outer row does not contain a nested list.")
        };
        object?[] snapshot = EditorListSequenceKernel.Snapshot(inner);
        object state = EditorListMutation.GetStateToken(inner);
        EditorNestedListSourceLease lease = binding switch
        {
            IEditorListElementBinding list => new(list, inner, snapshot, state),
            IEditorLinkedElementBinding linked => new(linked, inner, snapshot, state),
            IEditorDictionaryElementBinding dictionary => new(dictionary, inner, snapshot, state),
            _ => throw new InvalidOperationException("The outer row is not a supported collection binding.")
        };
        if (lease.Owner is XRWorldObjectBase && lease._document is null)
            throw new InvalidOperationException("The scene owner has no unique writable source document.");
        if (!lease.IsCommandCurrent())
            throw new InvalidOperationException("The nested list path changed while it was captured.");
        if (ReferenceEquals(lease._outerList, lease.Inner))
            throw new InvalidOperationException("A list cannot be edited through a cycle to itself.");
        return lease;
    }

    public bool IsCommandCurrent()
    {
        if (!IsOwnerPathCurrent() || !IsInnerCurrent()) return false;
        if (_listBinding is { } list)
            return (!_requiresOuterRowLease || list.IsLeased && list.Context.IsActive &&
                list.Context.Revision == _outerRevision) &&
                list.Index == _outerIndex && list.RowId == _outerRowId &&
                _outerIndex >= 0 && _outerIndex < list.Context.Rows.Count &&
                list.Context.Rows.GetItemId(_outerIndex) == _outerRowId &&
                ReferenceEquals(list.Collection, _outerList) &&
                ReferenceEquals(EditorListMutation.GetStateToken(_outerList!), _outerState) &&
                _outerIndex < _outerList!.Count && ReferenceEquals(_outerList[_outerIndex], Inner);
        if (_linkedBinding is { } linked)
            return (!_requiresOuterRowLease || linked.IsCurrent) && _linkedSource is { } source &&
                Array.IndexOf(source.Displayed.Nodes, _linkedNode) >= 0 &&
                ReferenceEquals(source.Displayed.Values[Array.IndexOf(source.Displayed.Nodes, _linkedNode)], Inner);
        if (_dictionaryBinding is { } dictionary)
        {
            if ((_requiresOuterRowLease && !dictionary.IsCurrent) || _dictionarySource is not { } source) return false;
            int index = source.Adapter.IndexOf(source.Displayed, _dictionaryKey!);
            return index >= 0 && ReferenceEquals(source.Displayed.Values[index], Inner);
        }
        return false;
    }

    /// <summary>Checks source ancestry for replay without relying on a live virtual-row lease.</summary>
    public bool IsOwnerPathCurrent()
    {
        if (!LifetimesCurrent() ||
            Owner is XRObjectBase { IsDestroyed: true } or XRObjectBase { IsDestroyQueued: true } ||
            Owner is XRWorldObjectBase { World: RuntimeWorld { IsDisposing: true } } ||
            Owner is XRWorldObjectBase && _document is null ||
            !ReferenceEquals(EditorListMutation.WorldOf(Owner), _world) ||
            !ReferenceEquals(EditorListMutation.DocumentOf(Owner), _document) ||
            !ReferenceEquals(EditorListMutation.NodeOf(Owner), _ownerNode) ||
            Owner is XRComponent component && (_ownerNode is null || _ownerNode.IsDestroyed ||
                _ownerNode.IsDestroyQueued || !_ownerNode.Components.Contains(component)))
            return false;
        if (_listBinding is { } list)
            return ReferenceEquals(list.Owner, Owner) && Equals(list.OwnerProperty, Property) &&
                _listAccessor is { } accessor && accessor.IsCurrent(Owner, _outerList!) &&
                ReferenceEquals(EditorListMutation.GetStateToken(_outerList!), _outerState) &&
                _outerIndex >= 0 && _outerIndex < _outerList!.Count &&
                ReferenceEquals(_outerList[_outerIndex], Inner) && LifetimesCurrent();
        if (_linkedSource is { } linked && _linkedDisplayed is { } linkedDisplayed)
        {
            int index = Array.IndexOf(linkedDisplayed.Nodes, _linkedNode);
            return linked.IsOwnerLocationCurrent() &&
                ReferenceEquals(EditorLinkedMutation.StateOf(linked.List), _linkedState) &&
                linked.Adapter.Matches(linked.List, linkedDisplayed) && index >= 0 &&
                ReferenceEquals(linkedDisplayed.Values[index], Inner) && LifetimesCurrent();
        }
        if (_dictionarySource is { } dictionary && _dictionaryDisplayed is { } dictionaryDisplayed)
        {
            int index = dictionary.Adapter.IndexOf(dictionaryDisplayed, _dictionaryKey!);
            return dictionary.IsOwnerLocationCurrent() &&
                ReferenceEquals(EditorDictionaryMutation.StateOf(dictionary.Dictionary), _dictionaryState) &&
                dictionary.Adapter.Matches(dictionary.Dictionary, dictionaryDisplayed) && index >= 0 &&
                ReferenceEquals(dictionaryDisplayed.Values[index], Inner) && LifetimesCurrent();
        }
        return false;
    }

    /// <summary>Resolves only the original document while its exact source path remains owned.</summary>
    public bool TryGetCurrentDocument(out XRAsset? document)
    {
        document = _document as XRAsset;
        return document is not null && IsOwnerPathCurrent() && LifetimesCurrent();
    }

    private bool LifetimesCurrent()
        => IsLive(_ownerIdentity) && IsLive(_nodeIdentity) && IsLive(_documentIdentity);

    private static bool IsLive(ObjectConstructionIdentity? identity)
        => identity is not { } captured || captured.IsCurrent &&
            !captured.Target.IsDestroyed && !captured.Target.IsDestroyQueued;

    public bool IsInnerCurrent()
        => ReferenceEquals(EditorListMutation.GetStateToken(Inner), _innerState) &&
           EditorListSequenceKernel.Matches(Inner, _innerDisplayed);

    public void AcceptInner(object?[] values, object state)
    {
        _innerDisplayed = values;
        _innerState = state;
    }

    public bool RefreshInnerSnapshot()
    {
        if (!IsOwnerPathCurrent()) return false;
        object?[] current = EditorListSequenceKernel.Snapshot(Inner);
        object state = EditorListMutation.GetStateToken(Inner);
        _innerDisplayed = current;
        _innerState = state;
        return true;
    }
}
