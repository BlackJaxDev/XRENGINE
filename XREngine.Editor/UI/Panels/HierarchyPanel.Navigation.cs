using XREngine.Rendering.UI;
using XREngine.Scene;

namespace XREngine.Editor;

public partial class HierarchyPanel
{
    private ulong _pendingHierarchyNavigationId;
    private IUICanvasInputSource? _pendingHierarchyNavigationInput;
    private UIListTransform? _pendingHierarchyNavigationList;
    private UIVirtualTreeProvider? _pendingHierarchyNavigationProvider;
    private ulong _pendingHierarchyNavigationProviderIncarnation;
    private SceneNode? _pendingHierarchyNavigationNode;
    private XRScene? _pendingHierarchyNavigationScene;

    private void BindHierarchyNavigation(HierarchyRow row, ulong itemId, ulong incarnation)
    {
        if (_virtualTree is not { } provider || _treeListTransform is not { } list ||
            !ReferenceEquals(list.VirtualProvider, provider) ||
            !list.TryGetVirtualRowLease(row.Transform, out UIVirtualRowLease lease) ||
            lease.ItemId != itemId || incarnation != 0 && lease.Incarnation != incarnation)
        {
            row.Button.UnbindNavigation();
            return;
        }
        row.Button.BindNavigation(provider, list, itemId, lease.Incarnation);
    }

    private void OnHierarchyNavigationRequested(HierarchyRow row, ulong fromId, ulong targetId)
    {
        ClearPendingHierarchyNavigation();
        if (!IsHierarchyRowCurrent(row) || row.ItemId != fromId ||
            _virtualTree is not { } provider || !provider.TryGetNode(targetId, out _) ||
            _treeListTransform is not { } list || !ReferenceEquals(list.VirtualProvider, provider) ||
            GetCanvasInput() is not { } input)
            return;
        _pendingHierarchyNavigationId = targetId;
        _pendingHierarchyNavigationInput = input;
        _pendingHierarchyNavigationList = list;
        _pendingHierarchyNavigationProvider = provider;
        _pendingHierarchyNavigationProviderIncarnation = list.VirtualProviderIncarnation;
        _pendingHierarchyNavigationNode = _hierarchyNodes.GetValueOrDefault(targetId);
        _pendingHierarchyNavigationScene = _hierarchyScenes.GetValueOrDefault(targetId);
    }

    private void OnHierarchyRowFocused(HierarchyRow row)
    {
        if (!IsHierarchyRowCurrent(row) || _pendingHierarchyNavigationId == 0)
            return;
        ulong requested = _pendingHierarchyNavigationId;
        IUICanvasInputSource? requestedInput = _pendingHierarchyNavigationInput;
        UIListTransform? requestedList = _pendingHierarchyNavigationList;
        UIVirtualTreeProvider? requestedProvider = _pendingHierarchyNavigationProvider;
        ulong requestedProviderIncarnation = _pendingHierarchyNavigationProviderIncarnation;
        SceneNode? requestedNode = _pendingHierarchyNavigationNode;
        XRScene? requestedScene = _pendingHierarchyNavigationScene;
        ClearPendingHierarchyNavigation();
        IUICanvasInputSource? input = GetCanvasInput();
        if (row.ItemId != requested || row.Button.BoundItemId != requested ||
            _treeListTransform is not { } list || !ReferenceEquals(list, requestedList) ||
            !ReferenceEquals(_virtualTree, requestedProvider) || !ReferenceEquals(list.VirtualProvider, requestedProvider) ||
            list.VirtualProviderIncarnation != requestedProviderIncarnation ||
            !ReferenceEquals(row.Node, requestedNode) || !ReferenceEquals(row.Scene, requestedScene) ||
            !list.TryGetVirtualRowLease(row.Transform, out UIVirtualRowLease lease) || lease.ItemId != requested ||
            input is null || !ReferenceEquals(input, requestedInput) || !ReferenceEquals(input.FocusedComponent, row.Button) ||
            !input.IsDispatchingVirtualItemFocus(list, requested, row.Button))
            return;
        if (row.Node is { IsDestroyed: false, IsDestroyQueued: false } node)
            Selection.SceneNodes = [node];
        else
            _virtualTree?.Activate(requested);
    }

    private void ClearPendingHierarchyNavigation()
    {
        _pendingHierarchyNavigationId = 0;
        _pendingHierarchyNavigationInput = null;
        _pendingHierarchyNavigationList = null;
        _pendingHierarchyNavigationProvider = null;
        _pendingHierarchyNavigationProviderIncarnation = 0;
        _pendingHierarchyNavigationNode = null;
        _pendingHierarchyNavigationScene = null;
    }

    private void ReconcileHierarchyNavigationSource()
    {
        if (_pendingHierarchyNavigationId != 0 &&
            (_virtualTree?.TryGetNode(_pendingHierarchyNavigationId, out _) != true ||
             !ReferenceEquals(_hierarchyNodes.GetValueOrDefault(_pendingHierarchyNavigationId), _pendingHierarchyNavigationNode) ||
             !ReferenceEquals(_hierarchyScenes.GetValueOrDefault(_pendingHierarchyNavigationId), _pendingHierarchyNavigationScene)))
            ClearPendingHierarchyNavigation();
    }

    private void OnHierarchyExpansionChanged(HierarchyRow row, ulong itemId, bool expanded)
    {
        if (!IsHierarchyRowCurrent(row) || row.ItemId != itemId)
            return;
        StoreHierarchyExpansion(itemId, expanded);
        NotifyVirtualTreeChanged();
        if (IsHierarchyRowCurrent(row))
            SaveHierarchyState();
    }

    private void StoreHierarchyExpansion(ulong itemId, bool expanded)
    {
        if (_hierarchyNodes.TryGetValue(itemId, out SceneNode? node))
        {
            if (expanded)
                _collapsedNodes.Remove(node.ID);
            else
                _collapsedNodes.Add(node.ID);
        }
        else if (_hierarchyScenes.TryGetValue(itemId, out XRScene? scene))
        {
            if (expanded)
                _collapsedSections.Remove(scene.ID);
            else
                _collapsedSections.Add(scene.ID);
        }
    }
}
