using System.Reflection;
using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Editor.UI;
using XREngine.Scene.Transforms;
using XREngine.Rendering.UI;
using XREngine.Scene;

namespace XREngine.Editor;

public partial class InspectorPanel
{
    private readonly List<InspectorPropertyColumnLayout> _propertyColumnRows = [];
    private UIScrollableTransform? _propertyColumnScroll;
    private UIListTransform? _propertyColumnList;
    private UIVirtualPropertyGridProvider<PropertyInfo>? _propertyColumnGrid;
    private ObjectConstructionIdentity? _propertyColumnScrollIdentity;
    private ObjectConstructionIdentity? _propertyColumnListIdentity;
    private ulong _propertyColumnIncarnation;
    private ulong _propertyColumnEpoch;
    private bool _propertyColumnTickRegistered;
    private Func<bool>? _propertyColumnCurrent;
    private EditorVirtualRowControlLease? _propertyColumnOwnerPath;
    private readonly List<(TransformBase Transform, long Membership)> _propertyColumnMemberships = [];

    private void StartPropertyColumnLayout()
    {
        if (_propertyScroll is not { } scroll || _virtualPropertyList is not { } list ||
            _virtualPropertyGrid is not { } grid || !ReferenceEquals(list.VirtualProvider, grid))
            return;
        _propertyColumnScroll = scroll;
        _propertyColumnList = list;
        _propertyColumnGrid = grid;
        _propertyColumnScrollIdentity = ObjectConstructionIdentity.Capture(scroll);
        _propertyColumnListIdentity = ObjectConstructionIdentity.Capture(list);
        _propertyColumnIncarnation = list.VirtualProviderIncarnation;
        if (scroll.SceneNode?.GetComponent<UIScrollRegionComponent>() is not { } region)
            throw new InvalidOperationException("The inspector property viewport has no scroll owner.");
        _propertyColumnOwnerPath = EditorVirtualRowControlLease.Capture(region, SceneNode);
        for (TransformBase? current = list; current is not null && !ReferenceEquals(current.SceneNode, SceneNode);
            current = current.Parent)
            _propertyColumnMemberships.Add((current, current.HierarchyMembershipRevision));
        _propertyColumnCurrent ??= IsPropertyColumnLayoutCurrent;
        scroll.PropertyChanged += PropertyColumnViewportChanged;
        QueuePropertyColumnLayout();
    }

    private bool IsPropertyColumnLayoutCurrent()
        => IsActiveInHierarchy && !_destroyingOwner && !IsDestroyed && !IsDestroyQueued &&
            _propertyColumnOwnerPath?.IsCurrent(SceneNode) == true && PropertyColumnPathIsCurrent() &&
            _propertyColumnScroll is { IsDestroyed: false, IsDestroyQueued: false } scroll &&
            _propertyColumnList is { IsDestroyed: false, IsDestroyQueued: false } list &&
            _propertyColumnScrollIdentity is { IsCurrent: true } &&
            _propertyColumnListIdentity is { IsCurrent: true } &&
            ReferenceEquals(_propertyScroll, scroll) && ReferenceEquals(_virtualPropertyList, list) &&
            ReferenceEquals(scroll.SceneNode?.Transform, scroll) && ReferenceEquals(list.SceneNode?.Transform, list) &&
            ReferenceEquals(list.Parent, scroll) && ReferenceEquals(_virtualPropertyGrid, _propertyColumnGrid) &&
            ReferenceEquals(list.VirtualProvider, _propertyColumnGrid) &&
            list.VirtualProviderIncarnation == _propertyColumnIncarnation;

    private bool PropertyColumnPathIsCurrent()
    {
        foreach (var entry in _propertyColumnMemberships)
            if (!entry.Transform.HasUsableHierarchyMembershipRevision ||
                entry.Transform.HierarchyMembershipRevision != entry.Membership ||
                !ReferenceEquals(entry.Transform.World, World))
                return false;
        return _propertyColumnMemberships.Count != 0;
    }
    private void PropertyColumnViewportChanged(object? sender, IXRPropertyChangedEventArgs change)
    {
        if (ReferenceEquals(sender, _propertyColumnScroll) &&
            change.PropertyName is nameof(UIBoundableTransform.ActualSize) or nameof(UIBoundableTransform.Padding))
            QueuePropertyColumnLayout();
    }

    private void QueuePropertyColumnLayout()
    {
        if (_propertyColumnTickRegistered || !IsActiveInHierarchy || !IsPropertyColumnLayoutCurrent())
            return;
        RegisterTick(ETickGroup.Late, ETickOrder.Scene, ApplyPendingPropertyColumnLayout);
        _propertyColumnTickRegistered = true;
    }

    private void ApplyPendingPropertyColumnLayout()
    {
        CancelPropertyColumnLayoutTick();
        if (!IsPropertyColumnLayoutCurrent()) return;
        ulong epoch = _propertyColumnEpoch;
        for (int index = 0; index < _propertyColumnRows.Count; index++)
        {
            ApplyPropertyColumnLayout(_propertyColumnRows[index]);
            if (epoch != _propertyColumnEpoch || !IsPropertyColumnLayoutCurrent()) return;
        }
    }

    private bool ApplyPropertyColumnLayout(InspectorPropertyColumnLayout columns)
    {
        if (!IsPropertyColumnLayoutCurrent()) return false;
        float width = _propertyColumnScroll!.ActualWidth;
        if (!float.IsFinite(width) || width <= 0) return true;
        return columns.TryApply(_propertyColumnList!, _propertyColumnGrid!,
            _propertyColumnIncarnation, _propertyColumnCurrent!, width);
    }

    private InspectorPropertyColumnLayout CapturePropertyColumns(SceneNode row,
        UIBoundableTransform value)
    {
        if (row.Transform.Children.Count != 2 ||
            row.Transform.Children[0] is not UIBoundableTransform name ||
            !ReferenceEquals(row.Transform.Children[1], value) || name.SceneNode?.Name != "name")
            throw new InvalidOperationException("The inspector property row has no exact two-cell layout.");
        InspectorPropertyColumnLayout columns = new(row, name, value);
        _propertyColumnRows.Add(columns);
        return columns;
    }

    private void CancelPropertyColumnLayoutTick()
    {
        if (!_propertyColumnTickRegistered) return;
        _propertyColumnTickRegistered = false;
        UnregisterTick(ETickGroup.Late, ETickOrder.Scene, ApplyPendingPropertyColumnLayout);
    }

    private void StopPropertyColumnLayout()
    {
        _propertyColumnEpoch++;
        CancelPropertyColumnLayoutTick();
        if (_propertyColumnScroll is { } scroll)
            scroll.PropertyChanged -= PropertyColumnViewportChanged;
        _propertyColumnScroll = null;
        _propertyColumnList = null;
        _propertyColumnGrid = null;
        _propertyColumnScrollIdentity = null;
        _propertyColumnListIdentity = null;
        _propertyColumnOwnerPath = null;
        _propertyColumnMemberships.Clear();
        foreach (InspectorPropertyColumnLayout row in _propertyColumnRows) row.Revoke();
        _propertyColumnRows.Clear();
    }
}
