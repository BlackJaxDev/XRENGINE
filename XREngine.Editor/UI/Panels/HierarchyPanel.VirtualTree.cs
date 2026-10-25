using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using XREngine.Components;
using XREngine.Data.Colors;
using XREngine.Data.Core;
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
        public required UIVirtualTreeRowButtonComponent Button;
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
        public Action<UIInteractableComponent>? Click;
        public Action<UIInteractableComponent>? ArrowClick;
        public Action<UIInteractableComponent>? ToggleClick;
        public Action<UIInteractableComponent>? UnloadClick;
        public Action<UITextInputComponent>? RenameSubmitted;
        public Action<UITextInputComponent>? RenameCancelled;
        public XRPropertyChangedEventHandler? RenameTextChanged;
        public ulong RenameSession;
        public DelMouseMove? DragMove;
        public Action<UIInteractableComponent>? DragLeave;
        public bool Released;
        public Action<ulong, ulong>? NavigationRequested;
        public Action<ulong, bool>? ExpansionChanged;
        public Action<UIInteractableComponent>? Focused;
        public SceneNode? Node;
        public XRScene? Scene;
        public ulong ItemId;
    }

    private UIVirtualTreeProvider? _virtualTree;
    private IDisposable? _virtualRootDropRegistration;
    private readonly NativeHierarchySelection _providerSelection = new();
    private readonly Dictionary<Guid, ulong> _hierarchyIds = [];
    private readonly Dictionary<ulong, SceneNode> _hierarchyNodes = [];
    private readonly Dictionary<Guid, ulong> _sceneIds = [];
    private readonly Dictionary<ulong, XRScene> _hierarchyScenes = [];
    private readonly Dictionary<UIBoundableTransform, HierarchyRow> _hierarchyRows = [];
    private readonly Dictionary<SceneNode, HierarchySourceBinding> _sourceListeners = [];
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
        if (!CanUseHierarchy)
            return;
        uint revision = _hierarchyRevision;
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
        if (!CanUseHierarchy || revision != _hierarchyRevision)
            return;
        _treeListTransform = list;

        UIVirtualTreeProvider provider = new(CreateHierarchyRow, BindHierarchyRow, UnbindHierarchyRow);
        _virtualTree = provider;
        Exception? bindingFailure = null;
        try { list.VirtualProvider = provider; }
        catch (Exception error) { bindingFailure = error; }
        if (!CanUseHierarchy || revision != _hierarchyRevision)
        {
            if (ReferenceEquals(list.VirtualProvider, provider))
                AttemptHierarchyRelease(list.DetachVirtualProviderForOwnerTeardown, ref bindingFailure);
            ThrowHierarchyFailure(bindingFailure);
            return;
        }
        ThrowHierarchyFailure(bindingFailure);
        list.VirtualRowRecycling += OnHierarchyRowRecycling;

        _virtualRootDropRegistration = EditorDragDropUtility.RegisterDropTarget(list,
            HandleVirtualRootDrop,
            payload => CanUseHierarchy && (EditorDragDropUtility.IsSceneNodePayload(payload) || EditorDragDropUtility.TryGetAssetOrFilePath(payload, out _)),
            hovering => menuMat.SetBatchSolidColor(hovering ? RootDropHighlight : Vector4.Zero));

        CreateVirtualScrollbar(parentNode);
        if (CanUseHierarchy && revision == _hierarchyRevision)
            RebuildVirtualTree();
    }

    private void CreateVirtualScrollbar(SceneNode parentNode)
    {
        uint revision = _hierarchyRevision;
        var trackNode = parentNode.NewChild<UIMaterialComponent>(out var trackMaterial);
        UIBoundableTransform track = trackNode.SetTransform<UIBoundableTransform>();
        track.MinAnchor = new Vector2(1, 0);
        track.MaxAnchor = Vector2.One;
        track.Width = ScrollbarWidth;
        track.Margins = new Vector4(0, ScrollbarPadding, ScrollbarPadding, ScrollbarPadding);
        trackMaterial.SetBatchSolidColor(new Vector4(0, 0, 0, 0.15f));
        if (!CanUseHierarchy || revision != _hierarchyRevision)
            return;
        _scrollbarTrackTransform = track;

        var thumbNode = trackNode.NewChild<UIMaterialComponent>(out var thumbMaterial);
        UIBoundableTransform thumb = thumbNode.SetTransform<UIBoundableTransform>();
        thumb.MinAnchor = new Vector2(0, 1);
        thumb.MaxAnchor = Vector2.One;
        thumb.Height = MinScrollbarThumbHeight;
        thumb.NormalizedPivot = new Vector2(0, 1);
        thumb.BlocksInputBehind = true;
        thumbMaterial.SetBatchSolidColor(new Vector4(1, 1, 1, 0.35f));
        if (CanUseHierarchy && revision == _hierarchyRevision)
            _scrollbarThumbTransform = thumb;
    }

    private void RebuildVirtualTree()
    {
        if (!CanUseHierarchy || _virtualTree is null)
            return;
        ClearPendingHierarchyNavigation();
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
        if (world is not null)
        {
            IReadOnlyList<XRScene> scenes = world.TargetWorld?.Scenes ?? (IReadOnlyList<XRScene>)Array.Empty<XRScene>();
            foreach (XRScene scene in scenes)
            {
                if (scene.IsEditorOnly && !_showEditorScene)
                    continue;
                AppendSourceScene(scene);
            }
            if (_showEditorScene && EditorWorldIntegrationRegistry.TryGet(world, out EditorWorldIntegration? integration) &&
                integration is not null)
                AppendSourceScene(integration.EditorScene);
            foreach (SceneNode root in CollectUnassignedRoots(world, scenes))
                AppendSourceNode(root, 0);
        }
        else
            foreach (SceneNode root in RootNodes)
                if (root is not null)
                    AppendSourceNode(root, 0);
        _virtualTree.EndBulkLoad();
        SynchronizeVirtualSelection(reset: true);
        ReconcileRenameSource();
        _sourceFingerprint = CaptureSourceFingerprint();
        NotifyVirtualTreeChanged();
    }

    private void AppendSourceScene(XRScene scene)
    {
        if (_virtualTree is null)
            return;
        ulong sceneId = GetOrCreateSceneId(scene);
        if (_hierarchyScenes.ContainsKey(sceneId))
            return;
        _hierarchyScenes.Add(sceneId, scene);
        SubscribeSourceScene(scene);
        _virtualTree.Insert(sceneId, 0, int.MaxValue, GetSceneRowName(scene), true);
        _virtualTree.SetExpanded(sceneId, !_collapsedSections.Contains(scene.ID));
        foreach (SceneNode root in scene.SceneRoots)
            if (root is not null)
                AppendSourceNode(root, sceneId);
    }

    private void SynchronizeVirtualSelection(bool reset = false)
    {
        if (_virtualTree is not null)
            _providerSelection.Synchronize(_virtualTree, _hierarchyIds, _hierarchyNodes,
                Selection.SceneNodes, reset);
    }

    private void PollVirtualTreeSources()
    {
        if (!CanUseHierarchy || _virtualTree is null)
            return;
        RestoreHierarchyRowFocus();
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
        if (world is not null)
        {
            if (world.TargetWorld is { } target)
            {
                hash = unchecked((hash ^ (uint)target.ID.GetHashCode()) * multiplier);
                hash = unchecked((hash ^ (uint)(target.Name?.GetHashCode() ?? 0)) * multiplier);
                hash = unchecked((hash ^ (uint)(target.FilePath?.GetHashCode() ?? 0)) * multiplier);
                hash = unchecked((hash ^ (uint)target.Scenes.Count) * multiplier);
                for (int sceneIndex = 0; sceneIndex < target.Scenes.Count; sceneIndex++)
                    hash = CaptureSceneFingerprint(target.Scenes[sceneIndex], hash);
            }
            hash = unchecked((hash ^ (uint)(world.GameMode?.GetType().GetHashCode() ?? 0)) * multiplier);
            if (_showEditorScene && EditorWorldIntegrationRegistry.TryGet(world, out EditorWorldIntegration? integration) &&
                integration is not null)
                hash = CaptureSceneFingerprint(integration.EditorScene, hash);
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

    private static ulong CaptureSceneFingerprint(XRScene scene, ulong hash)
    {
        const ulong multiplier = 1099511628211UL;
        hash = unchecked((hash ^ (uint)scene.ID.GetHashCode()) * multiplier);
        hash = unchecked((hash ^ (uint)scene.SceneRoots.Count) * multiplier);
        hash = unchecked((hash ^ (scene.IsEditorOnly ? 4UL : 0UL)) * multiplier);
        for (int index = 0; index < scene.SceneRoots.Count; index++)
            if (scene.SceneRoots[index] is { } root)
                hash = unchecked((hash ^ (uint)root.ID.GetHashCode()) * multiplier);
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

    private void OnSourceChildAdded(SceneNode parent, TransformBase transform)
    {
        if (!CanUseHierarchy || _virtualTree is null || transform.SceneNode is not { } node || ReferenceEquals(node, parent))
            return;
        if (!_hierarchyIds.TryGetValue(parent.ID, out ulong parentId))
            return;
        ulong id = GetOrCreateHierarchyId(node);
        int index = 0;
        foreach (TransformBase child in parent.Transform.Children)
        {
            if (ReferenceEquals(child, transform))
                break;
            if (child.SceneNode is { } sibling && !ReferenceEquals(sibling, parent))
                index++;
        }
        if (_virtualTree.TryGetNode(id, out _))
        {
            _virtualTree.Move(id, parentId, index);
            SynchronizeVirtualSelection(reset: true);
            NotifyVirtualTreeChanged();
            return;
        }
        _virtualTree.Insert(id, parentId, index, node.Name ?? SceneNode.DefaultName);
        _virtualTree.SetExpanded(id, !_collapsedNodes.Contains(node.ID));
        _hierarchyNodes[id] = node;
        SubscribeSourceNode(node);
        foreach (SceneNode child in GetSceneNodeChildren(node))
            AppendSourceNode(child, id);
        SynchronizeVirtualSelection(reset: true);
        NotifyVirtualTreeChanged();
    }

    private void OnSourceChildRemoved(TransformBase transform)
    {
        if (!CanUseHierarchy || _virtualTree is null || transform.SceneNode is not { } node ||
            !_hierarchyIds.TryGetValue(node.ID, out ulong id))
            return;
        UnsubscribeSourceNode(node);
        RemoveNodeMappings(node);
        _virtualTree.Remove(id);
        ReconcileHierarchyNavigationSource();
        SynchronizeVirtualSelection(reset: true);
        NotifyVirtualTreeChanged();
        if (_nodePendingRename is not null)
            ScheduleHierarchyWork();
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
            if (TryGetActiveWorld() is not { } world || !IsCurrentHierarchyWorld(world) ||
                !IsLiveHierarchyNode(dragged, world))
                return false;
            uint revision = _hierarchyRevision;
            QueueNativeHierarchyMoveToRoot(dragged, world,
                () => revision == _hierarchyRevision && IsCurrentHierarchyWorld(world));
            return true;
        }
        return HandleRootDrop(payload);
    }

    private bool HandleVirtualNodeDrop(SceneNode target, EditorDragDropUtility.DragPayload payload)
    {
        if (EditorDragDropUtility.TryGetAssetOrFilePath(payload, out string? assetPath) &&
            !string.IsNullOrWhiteSpace(assetPath))
            return TryGetActiveWorld() is { } assetWorld && IsCurrentHierarchyWorld(assetWorld) &&
                IsLiveHierarchyNode(target, assetWorld) &&
                TryHandleDroppedSpawnableAsset(assetWorld, target, assetPath,
                    canApply: () => IsCurrentHierarchyWorld(assetWorld) && IsLiveHierarchyNode(target, assetWorld));

        if (!EditorDragDropUtility.TryGetSceneNode(payload, out SceneNode? dragged) || dragged is null ||
            !CanAcceptNodeDrop(target, dragged))
            return false;
        if (TryGetActiveWorld() is not { } world || !IsCurrentHierarchyWorld(world) ||
            !IsLiveHierarchyNode(dragged, world) || !IsLiveHierarchyNode(target, world))
            return false;
        uint revision = _hierarchyRevision;
        QueueNativeHierarchyReparent(dragged, target, world,
            () => revision == _hierarchyRevision && IsCurrentHierarchyWorld(world));
        return true;
    }

    private void RenameVirtualNode(SceneNode node, string name)
    {
        if (_virtualTree is null || !_hierarchyIds.TryGetValue(node.ID, out ulong id))
            return;
        _virtualTree.Rename(id, name);
        uint revision = _hierarchyRevision;
        foreach (HierarchyRow row in _hierarchyRows.Values)
        {
            if (ReferenceEquals(row.Node, node))
                row.Label.Text = name;
            if (!CanUseHierarchy || revision != _hierarchyRevision)
                return;
        }
    }

    private void ShowVirtualRename(SceneNode node)
    {
        if (_virtualTree is null || !_hierarchyIds.TryGetValue(node.ID, out ulong id))
            return;
        if (_virtualTree.TryGetNode(id, out UIVirtualTreeNode? item) && item is not null)
            for (ulong parent = item.ParentId; parent != 0 && _virtualTree.TryGetNode(parent, out item) && item is not null;
                parent = item.ParentId)
            {
                _virtualTree.SetExpanded(parent, true);
                StoreHierarchyExpansion(parent, true);
            }
        _renderedNodeCount = _virtualTree.Count;
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
        uint revision = _hierarchyRevision;
        foreach (HierarchyRow row in _hierarchyRows.Values)
        {
            if (row.ItemId != 0 && _virtualTree.TryGetNode(row.ItemId, out UIVirtualTreeNode? item) && item is not null)
                BindHierarchyRow(row.Transform, item, 0, 0);
            if (!CanUseHierarchy || revision != _hierarchyRevision)
                return;
        }
    }

    private UIBoundableTransform CreateHierarchyRow()
    {
        uint revision = _hierarchyRevision;
        SceneNode rowNode = new("HierarchyRow");
        UIBoundableTransform rowTransform = rowNode.SetTransform<UIBoundableTransform>();
        rowTransform.MaxAnchor = Vector2.One;
        UIVirtualTreeRowButtonComponent button = rowNode.AddComponent<UIVirtualTreeRowButtonComponent>()!;
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
        arrow.IsTabStop = false;
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
        toggle.IsTabStop = false;
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
        unload.IsTabStop = false;
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
        EditorUI.Styles.UpdateButton(button);
        if (!CanUseHierarchy || revision != _hierarchyRevision)
        {
            state.Released = true;
            rowTransform.Visibility = EVisibility.Hidden;
            return rowTransform;
        }
        _hierarchyRows.Add(rowTransform, state);
        state.Click = _ =>
        {
            if (!IsHierarchyRowCurrent(state))
                return;
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
        state.ArrowClick = _ =>
        {
            if (!IsHierarchyRowCurrent(state))
                return;
            if (state.Scene is not null)
                ToggleVirtualSceneSection(state.Scene);
            else if (state.Node is not null)
                ToggleCollapse(state.Node);
        };
        state.ToggleClick = _ => { if (IsHierarchyRowCurrent(state)) ToggleVirtualNodeActive(state); };
        state.UnloadClick = _ => { if (IsHierarchyRowCurrent(state)) UnloadVirtualScene(state); };
        state.RenameSubmitted = input => { if (IsHierarchyRowCurrent(state)) ApplyRename(input); };
        state.RenameCancelled = input => { if (IsHierarchyRowCurrent(state) && IsCurrentRenameInput(input)) CancelRename(); };
        state.RenameTextChanged = XRPropertyNotificationHandlers.FilterChanged(
            (_, _) => UpdateRenameDraft(state), nameof(UITextInputComponent.Text));
        button.InteractAction += state.Click;
        arrow.InteractAction += state.ArrowClick;
        toggle.InteractAction += state.ToggleClick;
        unload.InteractAction += state.UnloadClick;
        renameInput.Submitted += state.RenameSubmitted;
        renameInput.Cancelled += state.RenameCancelled;
        renameInput.PropertyChanged += state.RenameTextChanged;
        state.NavigationRequested = (from, target) => OnHierarchyNavigationRequested(state, from, target);
        state.ExpansionChanged = (id, expanded) => OnHierarchyExpansionChanged(state, id, expanded);
        state.Focused = _ => OnHierarchyRowFocused(state);
        button.NavigationRequested += state.NavigationRequested;
        button.ExpansionChanged += state.ExpansionChanged;
        button.GotFocus += state.Focused;
        RegisterVirtualDragHandlers(state);
        if (!IsHierarchyRowCurrent(state))
            return rowTransform;
        state.DropTarget = EditorDragDropUtility.RegisterDropTarget(
            rowTransform,
            payload => IsHierarchyRowCurrent(state) && state.Node is not null && HandleVirtualNodeDrop(state.Node, payload),
            payload => IsHierarchyRowCurrent(state) && state.Node is not null &&
                (CanAcceptNodeDrop(state.Node, payload) || EditorDragDropUtility.TryGetAssetOrFilePath(payload, out _)),
            hovering => background.SetBatchSolidColor(hovering ? DropTargetHighlight : Vector4.Zero));
        return rowTransform;
    }

    private void BindHierarchyRow(UIBoundableTransform row, UIVirtualTreeNode item, int index, ulong incarnation)
    {
        if (!_hierarchyRows.TryGetValue(row, out HierarchyRow? state) || !IsHierarchyRowCurrent(state))
            return;
        state.ItemId = item.Id;
        state.Node = _hierarchyNodes.GetValueOrDefault(item.Id);
        state.Scene = _hierarchyScenes.GetValueOrDefault(item.Id);
        BindHierarchyNavigation(state, item.Id, incarnation);
        SceneNode? node = state.Node;
        XRScene? scene = state.Scene;
        state.Button.UserData = node;
        if (!IsHierarchyRowCurrent(state))
            return;
        bool isNode = node is not null;
        state.Label.Text = item.Id == EditorSceneToggleRowId
            ? (_showEditorScene ? "Show Editor Scene: On" : "Show Editor Scene: Off") : item.Name;
        state.Label.Color = isNode ? ColorF4.White : EditorUI.Styles.SectionHeaderTextColor;
        state.LabelTransform.Translation = isNode
            ? new Vector2(node!.Transform.Depth * DepthIncrement + ArrowWidth, 0)
            : Vector2.Zero;
        bool hasChildren = scene is not null || isNode && HasSceneNodeChildren(node!);
        state.ArrowTransform.Visibility = hasChildren ? EVisibility.Visible : EVisibility.Hidden;
        state.ArrowTransform.Translation = isNode
            ? new Vector2(node!.Transform.Depth * DepthIncrement, 0) : Vector2.Zero;
        state.ArrowGlyph.Text = scene is not null
            ? (_collapsedSections.Contains(scene.ID) ? "\u25B6" : "\u25BC")
            : isNode && IsCollapsed(node!) ? "\u25B6" : "\u25BC";
        bool managedScene = scene is not null && TryGetActiveWorld()?.TargetWorld?.Scenes.Contains(scene) == true;
        state.ToggleTransform.Visibility = isNode || managedScene ? EVisibility.Visible : EVisibility.Hidden;
        state.Toggle.IsEnabled = isNode ? node!.CanDeactivate || !node.IsActiveSelf : managedScene;
        state.UnloadTransform.Visibility = managedScene ? EVisibility.Visible : EVisibility.Hidden;
        if (isNode)
        {
            state.ToggleGlyph.Text = node!.IsActiveSelf ? "\u2713" : string.Empty;
            state.ToggleGlyph.Color = node.IsActiveSelf ? ColorF4.White : EditorUI.Styles.DisabledTextColor;
        }
        else if (scene is not null)
        {
            state.ToggleGlyph.Text = scene.IsVisible ? "\u2713" : string.Empty;
            state.ToggleGlyph.Color = scene.IsVisible ? ColorF4.White : EditorUI.Styles.DisabledTextColor;
        }
        if (!IsHierarchyRowCurrent(state))
            return;
        BindRenameSession(state, node);
        if (!IsHierarchyRowCurrent(state))
            return;
        state.Button.DefaultBackgroundColor = isNode && _virtualTree?.IsSelected(item.Id) == true
            ? SelectedNodeColor : EditorUI.Styles.ButtonBackgroundColor;
    }

    private void UnbindHierarchyRow(UIBoundableTransform row, ulong itemId, ulong incarnation)
    {
        if (!_hierarchyRows.TryGetValue(row, out HierarchyRow? state))
            return;
        _pendingDragStarts.Remove(state.Button);
        state.Button.UnbindNavigation();
        state.Node = null;
        state.Scene = null;
        state.ItemId = 0;
        state.RenameSession = 0;
        state.Button.UserData = null;
    }

    private void OnHierarchyRowRecycling(UIBoundableTransform row, UIVirtualRowLease lease)
    {
        if (!_hierarchyRows.TryGetValue(row, out HierarchyRow? state))
            return;
        _pendingDragStarts.Remove(state.Button);
        if (GetCanvasInput() is { FocusedComponent: { } focused } input &&
            focused.SceneNode is { } focusedNode && row.SceneNode is { } rowNode &&
            (ReferenceEquals(focusedNode, rowNode) || IsDescendantOf(focusedNode, rowNode)))
            input.FocusedComponent = null;
        if (state.Node is not null && ReferenceEquals(state.Node, _nodePendingRename))
            _renameFocusRequested = false;
    }

    private void ToggleVirtualNodeActive(HierarchyRow state)
    {
        if (state.Scene is not null)
        {
            XRScene scene = state.Scene;
            if (TryGetActiveWorld() is not { } world || !IsCurrentHierarchyWorld(world) ||
                world.TargetWorld?.Scenes.Contains(scene) != true)
                return;
            EnqueueSceneEdit(() =>
            {
                if (!IsCurrentHierarchyWorld(world) || world.TargetWorld?.Scenes.Contains(scene) != true)
                    return;
                ToggleNativeSceneVisibility(scene, world, !scene.IsVisible);
                RebuildVirtualTree();
            });
            return;
        }
        SceneNode? node = state.Node;
        if (node is null || (!node.CanDeactivate && node.IsActiveSelf) ||
            TryGetActiveWorld() is not { } nodeWorld || !IsCurrentHierarchyWorld(nodeWorld) ||
            !IsLiveHierarchyNode(node, nodeWorld))
            return;
        uint revision = _hierarchyRevision;
        EnqueueSceneEdit(() =>
        {
            if (revision != _hierarchyRevision || !IsCurrentHierarchyWorld(nodeWorld) ||
                !IsLiveHierarchyNode(node, nodeWorld) || !node.CanDeactivate && node.IsActiveSelf)
                return;
            using var _ = Undo.TrackChange("Toggle Node Active", node);
            node.IsActiveSelf = !node.IsActiveSelf;
            MarkSceneHierarchyDirty(node, nodeWorld);
            if (ReferenceEquals(state.Node, node))
            {
                state.ToggleGlyph.Text = node.IsActiveSelf ? "\u2713" : string.Empty;
                state.ToggleGlyph.Color = node.IsActiveSelf ? ColorF4.White : EditorUI.Styles.DisabledTextColor;
            }
        });
    }

    private void ToggleVirtualSceneSection(XRScene scene)
    {
        if (!CanUseHierarchy)
            return;
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
        if (TryGetActiveWorld() is not { } world || !IsCurrentHierarchyWorld(world) ||
            world.TargetWorld?.Scenes.Contains(scene) != true)
            return;
        EnqueueSceneEdit(() =>
        {
            if (!IsCurrentHierarchyWorld(world) || world.TargetWorld?.Scenes.Contains(scene) != true)
                return;
            UnloadNativeScene(scene, world);
            RebuildVirtualTree();
        });
    }

    private void RegisterVirtualDragHandlers(HierarchyRow state)
    {
        state.Button.NeedsMouseMove = true;
        if (!IsHierarchyRowCurrent(state))
            return;
        state.DragMove = (x, y, component) =>
        {
            if (!IsHierarchyRowCurrent(state))
                return;
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
        state.DragLeave = component => _pendingDragStarts.Remove(component);
        state.Button.MouseMove += state.DragMove;
        state.Button.MouseDirectOverlapLeave += state.DragLeave;
    }

    private bool IsHierarchyRowCurrent(HierarchyRow state)
        => CanUseHierarchy && !state.Released &&
            _hierarchyRows.TryGetValue(state.Transform, out HierarchyRow? current) && ReferenceEquals(current, state);

    private void ReleaseVirtualTree(IUICanvasInputSource? input, UIInteractableComponent? capturedFocusTarget)
    {
        UIListTransform? list = _treeListTransform;
        UIVirtualTreeProvider? provider = _virtualTree;
        IDisposable? rootDrop = _virtualRootDropRegistration;
        // This snapshot is limited to owner teardown. Callbacks can create a new owner.
        HierarchyRow[] rows = _hierarchyRows.Values.ToArray();
        _treeListTransform = null;
        _virtualTree = null;
        _virtualRootDropRegistration = null;
        _hierarchyRows.Clear();
        _hierarchyNodes.Clear();
        _hierarchyScenes.Clear();
        _hierarchyIds.Clear();
        _sceneIds.Clear();
        _providerSelection.Clear();
        _scrollbarTrackTransform = null;
        _scrollbarThumbTransform = null;
        _renderedNodeCount = 0;
        ClearPendingHierarchyNavigation();

        Exception? failure = null;
        AttemptHierarchyRelease(UnsubscribeAllSourceNodes, ref failure);
        foreach (HierarchyRow row in rows)
            DetachHierarchyRowCallbacks(row);
        if (list is not null)
        {
            list.VirtualRowRecycling -= OnHierarchyRowRecycling;
            if (ReferenceEquals(list.VirtualProvider, provider))
                AttemptHierarchyRelease(list.DetachVirtualProviderForOwnerTeardown, ref failure);
        }
        foreach (HierarchyRow row in rows)
            AttemptHierarchyRelease(() => EditorDragDropUtility.CancelDragsForSource(row.Button), ref failure);
        foreach (HierarchyRow row in rows)
        {
            IDisposable? rowDrop = row.DropTarget;
            row.DropTarget = null;
            if (rowDrop is not null)
                AttemptHierarchyRelease(rowDrop.Dispose, ref failure);
            // A replacement provider may have bound the same row during a callback.
            if (list?.TryGetVirtualRowLease(row.Transform, out _) != true)
            {
                AttemptHierarchyRelease(() => row.Button.UserData = null, ref failure);
                if (list?.TryGetVirtualRowLease(row.Transform, out _) != true)
                    AttemptHierarchyRelease(() => row.Transform.Visibility = EVisibility.Hidden, ref failure);
                if (input is not null && list?.TryGetVirtualRowLease(row.Transform, out _) != true)
                    AttemptHierarchyRelease(() => ClearHierarchyRowFocus(input, row, capturedFocusTarget), ref failure);
            }
        }
        if (rootDrop is not null)
            AttemptHierarchyRelease(rootDrop.Dispose, ref failure);
        ThrowHierarchyFailure(failure);
    }

    private static void DetachHierarchyRowCallbacks(HierarchyRow state)
    {
        state.Released = true;
        state.Button.InteractAction -= state.Click;
        state.Arrow.InteractAction -= state.ArrowClick;
        state.Toggle.InteractAction -= state.ToggleClick;
        state.Unload.InteractAction -= state.UnloadClick;
        state.RenameInput.Submitted -= state.RenameSubmitted;
        state.RenameInput.Cancelled -= state.RenameCancelled;
        state.RenameInput.PropertyChanged -= state.RenameTextChanged;
        state.Button.MouseMove -= state.DragMove;
        state.Button.MouseDirectOverlapLeave -= state.DragLeave;
        state.Button.NavigationRequested -= state.NavigationRequested;
        state.Button.ExpansionChanged -= state.ExpansionChanged;
        state.Button.GotFocus -= state.Focused;
        state.Button.UnbindNavigation();
        state.NavigationRequested = null;
        state.ExpansionChanged = null;
        state.Focused = null;
        state.Click = state.ArrowClick = state.ToggleClick = state.UnloadClick = state.DragLeave = null;
        state.RenameSubmitted = state.RenameCancelled = null;
        state.RenameTextChanged = null;
        state.RenameSession = 0;
        state.DragMove = null;
        state.Node = null;
        state.Scene = null;
        state.ItemId = 0;
    }

    private static void ClearHierarchyRowFocus(IUICanvasInputSource input, HierarchyRow row,
        UIInteractableComponent? capturedFocusTarget)
    {
        if (ReferenceEquals(capturedFocusTarget, row.Button))
            return;
        if (input.FocusedComponent is { SceneNode: { } focused } &&
            (ReferenceEquals(focused, row.Transform.SceneNode) || IsDescendantOf(focused, row.Transform.SceneNode!)))
            input.FocusedComponent = null;
    }
}
