using System;
using System.Collections.Generic;
using System.Numerics;
using XREngine.Components;
using XREngine.Data.Colors;
using XREngine.Editor.UI;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.Scene.Transforms;
using XREngine.Timers;
using static XREngine.Editor.EditorImGuiUI;

namespace XREngine.Editor;

public partial class HierarchyPanel
{
    private sealed class HierarchyRow
    {
        public required UIBoundableTransform Transform;
        public required UIButtonComponent Button;
        public required UIMaterialComponent Background;
        public required UITextComponent Label;
        public required UIBoundableTransform LabelTransform;
        public required UIButtonComponent Arrow;
        public required UIBoundableTransform ArrowTransform;
        public required UITextComponent ArrowGlyph;
        public required UIButtonComponent Toggle;
        public required UIBoundableTransform ToggleTransform;
        public required UITextComponent ToggleGlyph;
        public required UIButtonComponent Unload;
        public required UIBoundableTransform UnloadTransform;
        public required UITextInputComponent RenameInput;
        public required UIBoundableTransform RenameTransform;
        public IDisposable? DropTarget;
        public SceneNode? Node;
        public XRScene? Scene;
        public ulong ItemId;
    }

    private UIVirtualTreeProvider? _virtualTree;
    private readonly Dictionary<Guid, ulong> _hierarchyIds = [];
    private readonly Dictionary<ulong, SceneNode> _hierarchyNodes = [];
    private readonly Dictionary<Guid, ulong> _sceneIds = [];
    private readonly Dictionary<ulong, XRScene> _hierarchyScenes = [];
    private readonly Dictionary<UIBoundableTransform, HierarchyRow> _hierarchyRows = [];
    private readonly Dictionary<SceneNode, (EventList<TransformBase>.SingleHandler Added, EventList<TransformBase>.SingleHandler Removed)> _sourceListeners = [];
    private ulong _nextHierarchyId = 4;
    private ulong _sourceFingerprint;
    private long _nextSourcePollTicks;
    private static readonly long SourcePollIntervalTicks = EngineTimer.SecondsToStopwatchTicks(0.25f);
    private const ulong WorldNameRowId = 1;
    private const ulong WorldPathRowId = 2;
    private const ulong GameModeRowId = 3;
    private const ulong EditorSceneToggleRowId = 4;

    private void CreateVirtualTree(SceneNode parentNode)
    {
        var listNode = parentNode.NewChild<UIMaterialComponent>(out var menuMat);
        menuMat.SetBatchSolidColor(ColorF4.Transparent);
        UIListTransform list = listNode.SetTransform<UIListTransform>();
        list.MinAnchor = Vector2.Zero;
        list.MaxAnchor = Vector2.One;
        list.NormalizedPivot = Vector2.Zero;
        list.DisplayHorizontal = false;
        list.ItemSpacing = 0;
        list.Padding = Vector4.Zero;
        list.ItemAlignment = EListAlignment.TopOrLeft;
        list.ItemSize = ItemHeight;
        _treeListTransform = list;

        _virtualTree = new UIVirtualTreeProvider(CreateHierarchyRow, BindHierarchyRow, UnbindHierarchyRow);
        list.VirtualProvider = _virtualTree;
        list.VirtualRowRecycling += OnHierarchyRowRecycling;

        EditorDragDropUtility.RegisterDropTarget(list,
            HandleVirtualRootDrop,
            payload => EditorDragDropUtility.IsSceneNodePayload(payload) || EditorDragDropUtility.TryGetAssetOrFilePath(payload, out _),
            hovering => menuMat.SetBatchSolidColor(hovering ? RootDropHighlight : Vector4.Zero));

        CreateVirtualScrollbar(parentNode);
        RebuildVirtualTree();
    }

    private void CreateVirtualScrollbar(SceneNode parentNode)
    {
        var trackNode = parentNode.NewChild<UIMaterialComponent>(out var trackMaterial);
        UIBoundableTransform track = trackNode.SetTransform<UIBoundableTransform>();
        track.MinAnchor = new Vector2(1, 0);
        track.MaxAnchor = Vector2.One;
        track.Width = ScrollbarWidth;
        track.Margins = new Vector4(0, ScrollbarPadding, ScrollbarPadding, ScrollbarPadding);
        trackMaterial.SetBatchSolidColor(new Vector4(0, 0, 0, 0.15f));
        _scrollbarTrackTransform = track;

        var thumbNode = trackNode.NewChild<UIMaterialComponent>(out var thumbMaterial);
        UIBoundableTransform thumb = thumbNode.SetTransform<UIBoundableTransform>();
        thumb.MinAnchor = new Vector2(0, 1);
        thumb.MaxAnchor = Vector2.One;
        thumb.Height = MinScrollbarThumbHeight;
        thumb.NormalizedPivot = new Vector2(0, 1);
        thumb.BlocksInputBehind = true;
        thumbMaterial.SetBatchSolidColor(new Vector4(1, 1, 1, 0.35f));
        _scrollbarThumbTransform = thumb;
    }

    private void RebuildVirtualTree()
    {
        if (_virtualTree is null)
            return;
        UnsubscribeAllSourceNodes();
        _hierarchyNodes.Clear();
        _hierarchyScenes.Clear();
        _virtualTree.BeginBulkLoad();
        RuntimeWorld? world = TryGetActiveWorld();
        string worldName = world?.TargetWorld?.Name ?? "World";
        string path = world?.TargetWorld?.FilePath ?? "(unsaved)";
        string gameMode = world?.GameMode?.GetType().Name ?? "<none>";
        _virtualTree.Insert(WorldNameRowId, 0, int.MaxValue, $"World: {worldName}", true);
        _virtualTree.Insert(WorldPathRowId, 0, int.MaxValue, path, true);
        _virtualTree.Insert(GameModeRowId, 0, int.MaxValue, $"GameMode: {gameMode}", true);
        _virtualTree.Insert(EditorSceneToggleRowId, 0, int.MaxValue, "Show Editor Scene", true);
        if (world?.TargetWorld is { Scenes.Count: > 0 } targetWorld)
        {
            foreach (XRScene scene in targetWorld.Scenes)
            {
                if (scene.IsEditorOnly && !_showEditorScene)
                    continue;
                ulong sceneId = GetOrCreateSceneId(scene);
                _hierarchyScenes[sceneId] = scene;
                string name = string.IsNullOrWhiteSpace(scene.Name) ? "Untitled Scene" : scene.Name!;
                _virtualTree.Insert(sceneId, 0, int.MaxValue, name + (scene.IsDirty ? " *" : string.Empty));
                _virtualTree.SetExpanded(sceneId, !_collapsedSections.Contains(scene.ID));
                foreach (SceneNode root in scene.RootNodes)
                    if (root is not null)
                        AppendSourceNode(root, sceneId);
            }
            foreach (SceneNode root in CollectUnassignedRoots(world!, targetWorld.Scenes))
                AppendSourceNode(root, 0);
        }
        else
            foreach (SceneNode root in RootNodes)
                if (root is not null)
                    AppendSourceNode(root, 0);
        _virtualTree.EndBulkLoad();
        _sourceFingerprint = CaptureSourceFingerprint();
        NotifyVirtualTreeChanged();
    }

    private void PollVirtualTreeSources()
    {
        if (_virtualTree is null)
            return;
        long now = Engine.ElapsedTicks;
        if (now < _nextSourcePollTicks)
            return;
        _nextSourcePollTicks = now + SourcePollIntervalTicks;
        ulong fingerprint = CaptureSourceFingerprint();
        if (fingerprint != _sourceFingerprint)
            RebuildVirtualTree();
    }

    private ulong CaptureSourceFingerprint()
    {
        const ulong multiplier = 1099511628211UL;
        ulong hash = 14695981039346656037UL;
        RuntimeWorld? world = TryGetActiveWorld();
        if (world?.TargetWorld is { } target)
        {
            hash = unchecked((hash ^ (uint)target.ID.GetHashCode()) * multiplier);
            hash = unchecked((hash ^ (uint)target.Scenes.Count) * multiplier);
            for (int sceneIndex = 0; sceneIndex < target.Scenes.Count; sceneIndex++)
            {
                XRScene scene = target.Scenes[sceneIndex];
                hash = unchecked((hash ^ (uint)scene.ID.GetHashCode()) * multiplier);
                hash = unchecked((hash ^ (uint)scene.RootNodes.Count) * multiplier);
                hash = unchecked((hash ^ (scene.IsVisible ? 1UL : 0UL)) * multiplier);
                hash = unchecked((hash ^ (scene.IsDirty ? 2UL : 0UL)) * multiplier);
                hash = unchecked((hash ^ (scene.IsEditorOnly ? 4UL : 0UL)) * multiplier);
                hash = unchecked((hash ^ (uint)(scene.Name?.GetHashCode() ?? 0)) * multiplier);
                for (int rootIndex = 0; rootIndex < scene.RootNodes.Count; rootIndex++)
                    if (scene.RootNodes[rootIndex] is { } root)
                        hash = unchecked((hash ^ (uint)root.ID.GetHashCode()) * multiplier);
            }
            hash = unchecked((hash ^ (uint)world.RootNodes.Count) * multiplier);
            for (int rootIndex = 0; rootIndex < world.RootNodes.Count; rootIndex++)
                if (world.RootNodes[rootIndex] is { } root)
                    hash = unchecked((hash ^ (uint)root.ID.GetHashCode()) * multiplier);
        }
        else
        {
            hash = unchecked((hash ^ (uint)RootNodes.Count) * multiplier);
            for (int rootIndex = 0; rootIndex < RootNodes.Count; rootIndex++)
                if (RootNodes[rootIndex] is { } root)
                    hash = unchecked((hash ^ (uint)root.ID.GetHashCode()) * multiplier);
        }
        return hash;
    }

    private void AppendSourceNode(SceneNode node, ulong parentId)
    {
        if (_virtualTree is null)
            return;
        ulong id = GetOrCreateHierarchyId(node);
        _hierarchyNodes[id] = node;
        _virtualTree.Insert(id, parentId, int.MaxValue, node.Name ?? SceneNode.DefaultName);
        _virtualTree.SetExpanded(id, !_collapsedNodes.Contains(node.ID));
        SubscribeSourceNode(node);
        foreach (SceneNode child in GetSceneNodeChildren(node))
            AppendSourceNode(child, id);
    }

    private ulong GetOrCreateHierarchyId(SceneNode node)
    {
        if (_hierarchyIds.TryGetValue(node.ID, out ulong id))
            return id;
        id = ++_nextHierarchyId;
        _hierarchyIds.Add(node.ID, id);
        return id;
    }

    private ulong GetOrCreateSceneId(XRScene scene)
    {
        if (_sceneIds.TryGetValue(scene.ID, out ulong id))
            return id;
        id = ++_nextHierarchyId;
        _sceneIds.Add(scene.ID, id);
        return id;
    }

    private void SubscribeSourceNode(SceneNode node)
    {
        if (_sourceListeners.ContainsKey(node))
            return;
        EventList<TransformBase>.SingleHandler added = child => OnSourceChildAdded(node, child);
        EventList<TransformBase>.SingleHandler removed = child => OnSourceChildRemoved(child);
        node.Transform.Children.PostAnythingAdded += added;
        node.Transform.Children.PostAnythingRemoved += removed;
        _sourceListeners.Add(node, (added, removed));
    }

    private void UnsubscribeSourceNode(SceneNode node)
    {
        if (!_sourceListeners.Remove(node, out var callbacks))
            return;
        node.Transform.Children.PostAnythingAdded -= callbacks.Added;
        node.Transform.Children.PostAnythingRemoved -= callbacks.Removed;
        foreach (SceneNode child in GetSceneNodeChildren(node))
            UnsubscribeSourceNode(child);
    }

    private void UnsubscribeAllSourceNodes()
    {
        foreach (var pair in _sourceListeners)
        {
            pair.Key.Transform.Children.PostAnythingAdded -= pair.Value.Added;
            pair.Key.Transform.Children.PostAnythingRemoved -= pair.Value.Removed;
        }
        _sourceListeners.Clear();
    }

    private void OnSourceChildAdded(SceneNode parent, TransformBase transform)
    {
        if (_virtualTree is null || transform.SceneNode is not { } node || ReferenceEquals(node, parent))
            return;
        if (!_hierarchyIds.TryGetValue(parent.ID, out ulong parentId))
            return;
        ulong id = GetOrCreateHierarchyId(node);
        if (_virtualTree.TryGetNode(id, out _))
            return;
        int index = 0;
        foreach (TransformBase child in parent.Transform.Children)
        {
            if (ReferenceEquals(child, transform))
                break;
            if (child.SceneNode is { } sibling && !ReferenceEquals(sibling, parent))
                index++;
        }
        _virtualTree.Insert(id, parentId, index, node.Name ?? SceneNode.DefaultName);
        _virtualTree.SetExpanded(id, !_collapsedNodes.Contains(node.ID));
        _hierarchyNodes[id] = node;
        SubscribeSourceNode(node);
        foreach (SceneNode child in GetSceneNodeChildren(node))
            AppendSourceNode(child, id);
        NotifyVirtualTreeChanged();
    }

    private void OnSourceChildRemoved(TransformBase transform)
    {
        if (_virtualTree is null || transform.SceneNode is not { } node ||
            !_hierarchyIds.TryGetValue(node.ID, out ulong id))
            return;
        UnsubscribeSourceNode(node);
        RemoveNodeMappings(node);
        _virtualTree.Remove(id);
        NotifyVirtualTreeChanged();
    }

    private void RemoveNodeMappings(SceneNode node)
    {
        if (_hierarchyIds.TryGetValue(node.ID, out ulong id))
            _hierarchyNodes.Remove(id);
        foreach (SceneNode child in GetSceneNodeChildren(node))
            RemoveNodeMappings(child);
    }

    private void NotifyVirtualTreeChanged()
    {
        if (_virtualTree is null || _treeListTransform is null)
            return;
        _renderedNodeCount = _virtualTree.Count;
        _treeListTransform.InvalidateMeasure();
        UpdateScrollMetrics();
    }

    private void RemoveVirtualNode(SceneNode node)
    {
        if (_virtualTree is null || !_hierarchyIds.TryGetValue(node.ID, out ulong id))
            return;
        UnsubscribeSourceNode(node);
        RemoveNodeMappings(node);
        if (_virtualTree.Remove(id))
            NotifyVirtualTreeChanged();
    }

    private bool HandleVirtualRootDrop(EditorDragDropUtility.DragPayload payload)
    {
        if (EditorDragDropUtility.TryGetSceneNode(payload, out SceneNode? dragged) && dragged is not null)
        {
            if (TryGetActiveWorld() is not { } world)
                return false;
            QueueNativeHierarchyMoveToRoot(dragged, world);
            return true;
        }
        return HandleRootDrop(payload);
    }

    private bool HandleVirtualNodeDrop(SceneNode target, EditorDragDropUtility.DragPayload payload)
    {
        if (EditorDragDropUtility.TryGetAssetOrFilePath(payload, out string? assetPath) &&
            !string.IsNullOrWhiteSpace(assetPath))
            return TryGetActiveWorld() is { } assetWorld &&
                TryHandleDroppedSpawnableAsset(assetWorld, target, assetPath);

        if (!EditorDragDropUtility.TryGetSceneNode(payload, out SceneNode? dragged) || dragged is null ||
            !CanAcceptNodeDrop(target, dragged))
            return false;
        if (TryGetActiveWorld() is not { } world)
            return false;
        QueueNativeHierarchyReparent(dragged, target, world);
        return true;
    }

    private void RenameVirtualNode(SceneNode node, string name)
    {
        if (_virtualTree is null || !_hierarchyIds.TryGetValue(node.ID, out ulong id))
            return;
        _virtualTree.Rename(id, name);
        foreach (HierarchyRow row in _hierarchyRows.Values)
            if (ReferenceEquals(row.Node, node))
                row.Label.Text = name;
    }

    private void ShowVirtualRename(SceneNode node)
    {
        if (_virtualTree is null || !_hierarchyIds.TryGetValue(node.ID, out ulong id))
            return;
        int index = _virtualTree.FindVisibleIndex(id);
        if (index >= 0)
        {
            float top = index * ItemHeight;
            float bottom = top + ItemHeight;
            float viewport = GetViewportHeight();
            if (top < _scrollOffset)
                _scrollOffset = top;
            else if (bottom > _scrollOffset + viewport)
                _scrollOffset = bottom - viewport;
            UpdateScrollMetrics();
        }
        RefreshVisibleRenameRows();
    }

    private void RefreshVisibleRenameRows()
    {
        if (_virtualTree is null)
            return;
        foreach (HierarchyRow row in _hierarchyRows.Values)
            if (row.ItemId != 0 && _virtualTree.TryGetNode(row.ItemId, out UIVirtualTreeNode? item) && item is not null)
                BindHierarchyRow(row.Transform, item, 0, 0);
    }

    private UIBoundableTransform CreateHierarchyRow()
    {
        SceneNode rowNode = new("HierarchyRow");
        UIBoundableTransform rowTransform = rowNode.SetTransform<UIBoundableTransform>();
        rowTransform.MaxAnchor = Vector2.One;
        UIButtonComponent button = rowNode.AddComponent<UIButtonComponent>()!;
        UIMaterialComponent background = rowNode.AddComponent<UIMaterialComponent>()!;
        background.SetBatchSolidColor(ColorF4.Transparent);

        var labelNode = rowNode.NewChild<UITextComponent>(out var label);
        UIBoundableTransform labelTransform = labelNode.GetTransformAs<UIBoundableTransform>(true)!;
        labelTransform.MaxAnchor = new Vector2(1, 1);
        labelTransform.Margins = new Vector4(10, Margin, 54, Margin);
        label.FontSize = 14;
        label.Color = ColorF4.White;
        label.HorizontalAlignment = EHorizontalAlignment.Left;
        label.VerticalAlignment = EVerticalAlignment.Center;

        var arrowNode = rowNode.NewChild<UIButtonComponent>(out var arrow);
        UIBoundableTransform arrowTransform = arrowNode.GetTransformAs<UIBoundableTransform>(true)!;
        arrowTransform.MaxAnchor = new Vector2(0, 1);
        arrowTransform.Width = ArrowWidth;
        arrowTransform.BlocksInputBehind = true;
        arrowNode.NewChild<UITextComponent>(out var arrowGlyph);
        arrowGlyph.Font = NativeUiIconFont;
        arrowGlyph.FontSize = 10;
        arrowGlyph.Color = EditorUI.Styles.ExpandArrowColor;
        arrowGlyph.HorizontalAlignment = EHorizontalAlignment.Center;
        arrowGlyph.VerticalAlignment = EVerticalAlignment.Center;

        var toggleNode = rowNode.NewChild<UIButtonComponent>(out var toggle);
        UIBoundableTransform toggleTransform = toggleNode.GetTransformAs<UIBoundableTransform>(true)!;
        toggleTransform.MinAnchor = new Vector2(1, 0);
        toggleTransform.MaxAnchor = Vector2.One;
        toggleTransform.Width = 24;
        toggleTransform.Margins = new Vector4(2);
        toggleTransform.BlocksInputBehind = true;
        toggleNode.NewChild<UITextComponent>(out var toggleGlyph);
        toggleGlyph.Font = NativeUiIconFont;
        toggleGlyph.FontSize = 12;
        toggleGlyph.Color = ColorF4.White;
        toggleGlyph.HorizontalAlignment = EHorizontalAlignment.Center;
        toggleGlyph.VerticalAlignment = EVerticalAlignment.Center;

        var unloadNode = rowNode.NewChild<UIButtonComponent>(out var unload);
        UIBoundableTransform unloadTransform = unloadNode.GetTransformAs<UIBoundableTransform>(true)!;
        unloadTransform.MinAnchor = new Vector2(1, 0);
        unloadTransform.MaxAnchor = Vector2.One;
        unloadTransform.Width = 24;
        unloadTransform.Translation = new Vector2(-24, 0);
        unloadTransform.Margins = new Vector4(2);
        unloadTransform.BlocksInputBehind = true;
        unloadNode.NewChild<UITextComponent>(out var unloadGlyph);
        unloadGlyph.Font = NativeUiIconFont;
        unloadGlyph.FontSize = 10;
        unloadGlyph.Text = "\u2716";
        unloadGlyph.Color = ColorF4.White;
        unloadGlyph.HorizontalAlignment = EHorizontalAlignment.Center;
        unloadGlyph.VerticalAlignment = EVerticalAlignment.Center;

        var renameNode = rowNode.NewChild<UITextInputComponent, UITextComponent>(out var renameInput, out var renameText);
        UIBoundableTransform renameTransform = renameNode.GetTransformAs<UIBoundableTransform>(true)!;
        renameTransform.MaxAnchor = new Vector2(1, 1);
        renameTransform.Margins = new Vector4(26, Margin, 30, Margin);
        renameInput.SingleLineMode = true;
        renameText.FontSize = 14;
        renameText.Color = ColorF4.White;

        HierarchyRow state = new()
        {
            Transform = rowTransform, Button = button, Background = background,
            Label = label, LabelTransform = labelTransform, Arrow = arrow,
            ArrowTransform = arrowTransform, ArrowGlyph = arrowGlyph,
            Toggle = toggle, ToggleTransform = toggleTransform, ToggleGlyph = toggleGlyph,
            Unload = unload, UnloadTransform = unloadTransform,
            RenameInput = renameInput, RenameTransform = renameTransform
        };
        _hierarchyRows.Add(rowTransform, state);
        EditorUI.Styles.UpdateButton(button);
        button.InteractAction += _ =>
        {
            if (state.ItemId == EditorSceneToggleRowId)
            {
                _showEditorScene = !_showEditorScene;
                RebuildVirtualTree();
                SaveHierarchyState();
            }
            else if (state.Scene is not null)
                ToggleVirtualSceneSection(state.Scene);
            else if (state.Node is not null)
                HandleNodeButtonInteraction(button);
        };
        arrow.InteractAction += _ =>
        {
            if (state.Scene is not null)
                ToggleVirtualSceneSection(state.Scene);
            else if (state.Node is not null)
                ToggleCollapse(state.Node);
        };
        toggle.InteractAction += _ => ToggleVirtualNodeActive(state);
        unload.InteractAction += _ => UnloadVirtualScene(state);
        renameInput.Submitted += ApplyRename;
        renameInput.Cancelled += _ => CancelRename();
        RegisterVirtualDragHandlers(state);
        state.DropTarget = EditorDragDropUtility.RegisterDropTarget(
            rowTransform,
            payload => state.Node is not null && HandleVirtualNodeDrop(state.Node, payload),
            payload => state.Node is not null &&
                (CanAcceptNodeDrop(state.Node, payload) || EditorDragDropUtility.TryGetAssetOrFilePath(payload, out _)),
            hovering => background.SetBatchSolidColor(hovering ? DropTargetHighlight : Vector4.Zero));
        return rowTransform;
    }

    private void BindHierarchyRow(UIBoundableTransform row, UIVirtualTreeNode item, int index, ulong incarnation)
    {
        HierarchyRow state = _hierarchyRows[row];
        state.ItemId = item.Id;
        state.Node = _hierarchyNodes.GetValueOrDefault(item.Id);
        state.Scene = _hierarchyScenes.GetValueOrDefault(item.Id);
        state.Button.UserData = state.Node;
        bool isNode = state.Node is not null;
        state.Label.Text = item.Id == EditorSceneToggleRowId
            ? (_showEditorScene ? "Show Editor Scene: On" : "Show Editor Scene: Off") : item.Name;
        state.Label.Color = isNode ? ColorF4.White : EditorUI.Styles.SectionHeaderTextColor;
        state.LabelTransform.Translation = isNode
            ? new Vector2(state.Node!.Transform.Depth * DepthIncrement + ArrowWidth, 0)
            : Vector2.Zero;
        bool hasChildren = state.Scene is not null || isNode && HasSceneNodeChildren(state.Node!);
        state.ArrowTransform.Visibility = hasChildren ? EVisibility.Visible : EVisibility.Hidden;
        state.ArrowTransform.Translation = isNode
            ? new Vector2(state.Node!.Transform.Depth * DepthIncrement, 0) : Vector2.Zero;
        state.ArrowGlyph.Text = state.Scene is not null
            ? (_collapsedSections.Contains(state.Scene.ID) ? "\u25B6" : "\u25BC")
            : isNode && IsCollapsed(state.Node!) ? "\u25B6" : "\u25BC";
        state.ToggleTransform.Visibility = isNode || state.Scene is not null ? EVisibility.Visible : EVisibility.Hidden;
        state.UnloadTransform.Visibility = state.Scene is not null ? EVisibility.Visible : EVisibility.Hidden;
        if (isNode)
        {
            state.ToggleGlyph.Text = state.Node!.IsActiveSelf ? "\u2713" : string.Empty;
            state.ToggleGlyph.Color = state.Node.IsActiveSelf ? ColorF4.White : EditorUI.Styles.DisabledTextColor;
        }
        else if (state.Scene is not null)
        {
            state.ToggleGlyph.Text = state.Scene.IsVisible ? "\u2713" : string.Empty;
            state.ToggleGlyph.Color = state.Scene.IsVisible ? ColorF4.White : EditorUI.Styles.DisabledTextColor;
        }
        bool renaming = isNode && ReferenceEquals(_nodePendingRename, state.Node);
        state.LabelTransform.Visibility = renaming ? EVisibility.Hidden : EVisibility.Visible;
        state.RenameTransform.Visibility = renaming ? EVisibility.Visible : EVisibility.Hidden;
        if (renaming)
        {
            state.RenameInput.Text = state.Node!.Name ?? string.Empty;
            if (GetCanvasInput() is { } input)
                input.FocusedComponent = state.RenameInput;
        }
        state.Button.DefaultBackgroundColor = isNode && Selection.SceneNodes.Contains(state.Node!)
            ? SelectedNodeColor : EditorUI.Styles.ButtonBackgroundColor;
    }

    private void UnbindHierarchyRow(UIBoundableTransform row, ulong itemId, ulong incarnation)
    {
        HierarchyRow state = _hierarchyRows[row];
        _pendingDragStarts.Remove(state.Button);
        state.Node = null;
        state.Scene = null;
        state.ItemId = 0;
        state.Button.UserData = null;
    }

    private void OnHierarchyRowRecycling(UIBoundableTransform row, UIVirtualRowLease lease)
    {
        HierarchyRow state = _hierarchyRows[row];
        _pendingDragStarts.Remove(state.Button);
        if (GetCanvasInput() is { FocusedComponent: { } focused } input &&
            focused.SceneNode is { } focusedNode && row.SceneNode is { } rowNode &&
            (ReferenceEquals(focusedNode, rowNode) || IsDescendantOf(focusedNode, rowNode)))
            input.FocusedComponent = null;
        if (state.Node is not null && ReferenceEquals(state.Node, _nodePendingRename))
            _nodePendingRename = null;
    }

    private void ToggleVirtualNodeActive(HierarchyRow state)
    {
        if (state.Scene is not null)
        {
            XRScene scene = state.Scene;
            EnqueueSceneEdit(() =>
            {
                if (TryGetActiveWorld() is not { } world)
                    return;
                ToggleNativeSceneVisibility(scene, world, !scene.IsVisible);
                RebuildVirtualTree();
            });
            return;
        }
        SceneNode? node = state.Node;
        if (node is null || (!node.CanDeactivate && node.IsActiveSelf))
            return;
        EnqueueSceneEdit(() =>
        {
            using var _ = Undo.TrackChange("Toggle Node Active", node);
            node.IsActiveSelf = !node.IsActiveSelf;
            MarkSceneHierarchyDirty(node);
            if (ReferenceEquals(state.Node, node))
            {
                state.ToggleGlyph.Text = node.IsActiveSelf ? "\u2713" : string.Empty;
                state.ToggleGlyph.Color = node.IsActiveSelf ? ColorF4.White : EditorUI.Styles.DisabledTextColor;
            }
        });
    }

    private void ToggleVirtualSceneSection(XRScene scene)
    {
        if (!_collapsedSections.Remove(scene.ID))
            _collapsedSections.Add(scene.ID);
        if (_virtualTree is not null && _sceneIds.TryGetValue(scene.ID, out ulong id))
        {
            _virtualTree.SetExpanded(id, !_collapsedSections.Contains(scene.ID));
            NotifyVirtualTreeChanged();
        }
        SaveHierarchyState();
    }

    private void UnloadVirtualScene(HierarchyRow state)
    {
        if (state.Scene is not { } scene)
            return;
        EnqueueSceneEdit(() =>
        {
            if (TryGetActiveWorld() is not { } world)
                return;
            UnloadNativeScene(scene, world);
            RebuildVirtualTree();
        });
    }

    private void RegisterVirtualDragHandlers(HierarchyRow state)
    {
        state.Button.NeedsMouseMove = true;
        state.Button.MouseMove += (x, y, component) =>
        {
            SceneNode? node = state.Node;
            if (node is null || !EditorDragDropUtility.IsLeftMouseButtonHeldFor(component))
            {
                _pendingDragStarts.Remove(component);
                return;
            }
            Vector2 point = component.BoundableTransform.LocalToCanvas(new Vector2(x, y));
            if (!_pendingDragStarts.TryGetValue(component, out Vector2 start))
            {
                _pendingDragStarts[component] = point;
                return;
            }
            if (Vector2.Distance(start, point) < EditorDragDropUtility.DefaultActivationDistance)
                return;
            if (EditorDragDropUtility.BeginDrag(component, EditorDragDropUtility.CreateSceneNodePayload(node), point))
                _pendingDragStarts[component] = point;
        };
        state.Button.MouseDirectOverlapLeave += component => _pendingDragStarts.Remove(component);
    }

    private void ReleaseVirtualTree()
    {
        UnsubscribeAllSourceNodes();
        if (_treeListTransform is not null)
        {
            _treeListTransform.VirtualRowRecycling -= OnHierarchyRowRecycling;
            _treeListTransform.VirtualProvider = null;
        }
        foreach (HierarchyRow state in _hierarchyRows.Values)
            state.DropTarget?.Dispose();
        _hierarchyRows.Clear();
        _virtualTree = null;
    }
}
