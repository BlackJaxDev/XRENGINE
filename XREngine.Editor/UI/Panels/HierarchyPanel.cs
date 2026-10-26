using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using XREngine;
using XREngine.Components;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Editor.Mcp;
using XREngine.Editor.UI;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.Scene.Transforms;
using XREngine.Timers;
using static XREngine.Editor.EditorImGuiUI;

namespace XREngine.Editor;

public partial class HierarchyPanel : EditorPanel, IUIScrollReceiver
{
    private List<SceneNode> _rootNodes = [];
    public List<SceneNode> RootNodes
    {
        get => _rootNodes;
        set => SetField(ref _rootNodes, value);
    }

    private const float Margin = 4.0f;
    private const int DepthIncrement = 10;
    private const float ArrowWidth = 16.0f;
    private static readonly Vector4 DropTargetHighlight = new(0.25f, 0.55f, 0.95f, 0.35f);
    private static readonly Vector4 RootDropHighlight = new(0.18f, 0.18f, 0.18f, 0.30f);
    private static readonly ColorF4 SelectedNodeColor = new(0.22f, 0.50f, 0.85f, 0.45f);
    private static FontGlyphSet NativeUiIconFont => FontGlyphSet.LoadDefaultUIIconFont();
    private static FontGlyphSet NativeUiEmojiFont => FontGlyphSet.LoadDefaultUIEmojiFont();

    private readonly Dictionary<UIInteractableComponent, Vector2> _pendingDragStarts = new();
    private SceneNode? _lastClickedNode;
    private long _lastClickTicks = long.MinValue;
    private UIListTransform? _treeListTransform;
    private UIBoundableTransform? _scrollbarTrackTransform;
    private UIBoundableTransform? _scrollbarThumbTransform;
    private int _renderedNodeCount;
    private float _scrollOffset;
    private readonly HashSet<Guid> _collapsedNodes = new();
    private UIContextMenuComponent? _contextMenu;
    private SceneNode? _nodePendingRename;
    private Guid? _renameFocusRestoreId;

    // Scene section state.
    private readonly HashSet<Guid> _collapsedSections = new();
    private bool _showEditorScene;

    private const float ScrollbarWidth = 8.0f;
    private const float ScrollbarPadding = 4.0f;
    private const float MinScrollbarThumbHeight = 24.0f;
    private const float ScrollStepPixels = 32.0f;

    private bool _focusCameraOnDoubleClick = true;
    public bool FocusCameraOnDoubleClick
    {
        get => _focusCameraOnDoubleClick;
        set => SetField(ref _focusCameraOnDoubleClick, value);
    }

    private float _doubleClickThreshold = 0.45f;
    public float DoubleClickThreshold
    {
        get => _doubleClickThreshold;
        set => SetField(ref _doubleClickThreshold, MathF.Max(0.05f, value));
    }

    private float _doubleClickFocusDuration = 0.35f;
    public float DoubleClickFocusDuration
    {
        get => _doubleClickFocusDuration;
        set => SetField(ref _doubleClickFocusDuration, MathF.Max(0.01f, value));
    }

    protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
    {
        base.OnPropertyChanged(propName, prev, field);
        switch (propName)
        {
            case nameof(RootNodes):
                RemakeChildren();
                break;
        }
    }

    private void OnSelectionChanged(SceneNode[] _) => UpdateNodeHighlights();

    public void RemakeChildren()
    {
        if (!CanUseHierarchy)
            return;
        if (_remakingHierarchy || _releasingHierarchy)
        {
            _remakePending = true;
            return;
        }
        _remakingHierarchy = true;
        _remakePending = false;
        uint revision = _hierarchyRevision;
        try
        {
            RemakeHierarchyChildren(revision);
        }
        finally
        {
            _remakingHierarchy = false;
            if (_remakePending && CanUseHierarchy)
                ScheduleHierarchyWork();
        }
    }

    private void RemakeHierarchyChildren(uint revision)
    {
        using var sample = Engine.Profiler.Start("HierarchyPanel.RemakeChildren");
        float savedScroll = _scrollOffset;

        if (_virtualTree is not null)
        {
            RebuildVirtualTree();
            return;
        }

        // Release the old menu before replacing the retained child subtree.
        ReleaseHierarchyMenu();
        if (!CanUseHierarchy || revision != _hierarchyRevision)
            return;

        foreach (var child in SceneNode.Transform.Children.ToArray())
        {
            child.SceneNode?.Destroy(true);
            if (!CanUseHierarchy || revision != _hierarchyRevision)
                return;
        }
        _treeListTransform = null;
        _scrollbarTrackTransform = null;
        _scrollbarThumbTransform = null;
        _renderedNodeCount = 0;
        _requireHierarchyViewRestore?.Invoke();
        _scrollOffset = savedScroll;
        //Create the root menu transform - this is a horizontal list of buttons.
        CreateTree(SceneNode);

        BoundableTransform?.InvalidateLayout();
    }

    public float ItemHeight { get; set; } = 30.0f;

    public bool IsCollapsed(SceneNode node) => _collapsedNodes.Contains(node.ID);

    public void ToggleCollapse(SceneNode node)
    {
        if (!CanUseHierarchy)
            return;
        BeginHierarchyViewEdit();
        if (!_collapsedNodes.Remove(node.ID))
            _collapsedNodes.Add(node.ID);
        if (_virtualTree is not null && _hierarchyIds.TryGetValue(node.ID, out ulong id))
        {
            _virtualTree.SetExpanded(id, !_collapsedNodes.Contains(node.ID));
            NotifyVirtualTreeChanged();
        }
        SaveHierarchyState();
    }

    private void RestoreHierarchyState()
    {
        ulong intent = _hierarchyViewIntent;
        RuntimeWorld? world = TryGetActiveWorld();
        Guid worldId = world?.TargetWorld?.ID ?? Guid.Empty;
        NativeHierarchyState? saved = NativeHierarchyStateStore.Load(worldId);
        if (saved is null || intent != _hierarchyViewIntent ||
            !ReferenceEquals(TryGetActiveWorld(), world))
            return;
        _collapsedNodes.Clear();
        _collapsedNodes.UnionWith(saved.CollapsedNodes ?? []);
        _collapsedSections.Clear();
        _collapsedSections.UnionWith(saved.CollapsedSections ?? []);
        _scrollOffset = float.IsFinite(saved.ScrollOffset) ? MathF.Max(0, saved.ScrollOffset) : 0;
        _showEditorScene = saved.ShowEditorScene;
        _renameFocusRestoreId = saved.FocusedRename;
    }

    private void RestoreRenameFocus()
    {
        if (_suppressSavedRenameFocus)
        {
            _suppressSavedRenameFocus = false;
            _renameFocusRestoreId = null;
            return;
        }
        if (_renameFocusRestoreId is not { } focusId)
            return;
        _renameFocusRestoreId = null;
        SceneNode? node = _hierarchyNodes.Values.FirstOrDefault(candidate => candidate.ID == focusId);
        if (node is not null)
            BeginRename(node);
    }

    private void SaveHierarchyState()
    {
        Guid worldId = TryGetActiveWorld()?.TargetWorld?.ID ?? Guid.Empty;
        NativeHierarchyStateStore.Save(worldId, new NativeHierarchyState(
            [.. _collapsedNodes], [.. _collapsedSections], _scrollOffset, _showEditorScene,
            _nodePendingRename?.ID ?? _renameFocusRestoreId));
    }

    public partial class NodeWrapper(SceneNode node) : UIComponent
    {
        private bool _highlighted;

        public SceneNode Node { get; set; } = node;
        public bool IsRoot => Node?.Parent is null;
        public bool IsLeaf => Node is null || !HasSceneNodeChildren(Node);
        public bool Highlighted
        {
            get => _highlighted;
            set => SetField(ref _highlighted, value);
        }
        public int Depth => Node?.Transform?.Depth ?? 0;
    }

    private void CreateTree(SceneNode parentNode)
        => CreateVirtualTree(parentNode);

    public bool HandleMouseScroll(float diff)
    {
        if (!CanUseHierarchy || _treeListTransform is null)
            return true; // still consume to prevent camera zoom

        BeginHierarchyViewEdit();
        float maxOffset = GetMaxScrollOffset();
        if (maxOffset > 0.0f)
        {
            _scrollOffset = Math.Clamp(_scrollOffset - (diff * ScrollStepPixels), 0.0f, maxOffset);
            UpdateScrollMetrics();
        }
        return true; // always consume scroll over hierarchy panel
    }

    private void UpdateScrollMetrics()
    {
        if (_requireHierarchyViewRestore is { } requireRestore)
        {
            requireRestore();
            return;
        }
        if (_treeListTransform is null)
            return;

        float viewportHeight = GetViewportHeight();
        float maxOffset = GetMaxScrollOffset(viewportHeight);
        _scrollOffset = Math.Clamp(_scrollOffset, 0.0f, maxOffset);

        // Shift content by scroll offset; virtual bounds cull to the viewport
        _treeListTransform.ContentScrollOffset = _scrollOffset;
        _treeListTransform.SetVirtualBounds(viewportHeight, 0.0f);

        UpdateScrollbarVisual(viewportHeight, maxOffset);
    }

    private float GetViewportHeight()
    {
        float panelHeight = BoundableTransform.ActualHeight;
        if (panelHeight <= 0.0f)
            panelHeight = BoundableTransform.Height ?? 300.0f;
        return MathF.Max(ItemHeight, panelHeight - (ScrollbarPadding * 2.0f));
    }

    private float GetContentHeight()
        => _virtualTree is not null
            ? (float)Math.Min((double)_virtualTree.Count * ItemHeight, float.MaxValue)
            : MathF.Max(0.0f, _renderedNodeCount * ItemHeight);

    private float GetMaxScrollOffset()
        => GetMaxScrollOffset(GetViewportHeight());

    private float GetMaxScrollOffset(float viewportHeight)
    {
        float contentHeight = GetContentHeight();
        return MathF.Max(0.0f, contentHeight - viewportHeight);
    }

    private void UpdateScrollbarVisual(float viewportHeight, float maxOffset)
    {
        if (_scrollbarTrackTransform is null || _scrollbarThumbTransform is null)
            return;

        float trackHeight = _scrollbarTrackTransform.ActualHeight;
        if (trackHeight <= 0.0f)
            trackHeight = viewportHeight;

        float contentHeight = GetContentHeight();
        bool canScroll = maxOffset > 0.0f && contentHeight > 0.0f;
        _scrollbarTrackTransform.Visibility = canScroll ? EVisibility.Visible : EVisibility.Hidden;
        _scrollbarThumbTransform.Visibility = canScroll ? EVisibility.Visible : EVisibility.Hidden;
        if (!canScroll)
            return;

        float thumbHeight = MathF.Max(MinScrollbarThumbHeight, (viewportHeight / contentHeight) * trackHeight);
        float travel = MathF.Max(0.0f, trackHeight - thumbHeight);
        float ratio = maxOffset <= 0.0f ? 0.0f : _scrollOffset / maxOffset;

        _scrollbarThumbTransform.Height = thumbHeight;
        _scrollbarThumbTransform.Translation = new Vector2(0.0f, -travel * ratio);
    }


}
