using ImGuiNET;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text;
using XREngine;
using XREngine.Data.Core;
using XREngine.Rendering;
using XREngine.Scene;
using XREngine.Scene.Prefabs;
using XREngine.Scene.Transforms;
using YamlDotNet.Serialization;
using XREngine.Core.Files;

namespace XREngine.Editor;

public static partial class EditorImGuiUI
{
    private const float HierarchyFocusCameraDurationSeconds = 0.35f;
    private const string HierarchyDeepDuplicatePopupId = "Deep Duplicate Scene Nodes?";

    private static void DrawHierarchyPanel()
    {
        if (!_showHierarchy) return;
        if (!ImGui.Begin("Hierarchy", ref _showHierarchy))
        {
            ImGui.End();
            return;
        }

        DrawWorldHierarchyTab();
        DrawHierarchyDeepDuplicateConfirmation();

        // Handle asset drop on the entire hierarchy panel window.
        // Use GetWindowContentRegionMin/Max to create an invisible drop zone.
        var world = TryGetActiveWorldInstance();
        if (world is not null)
        {
            // Check if we're dragging over this window
            if (ImGui.IsWindowHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem))
            {
                var data = ImGui.GetDragDropPayload();
                unsafe
                {
                    if ((nint)data.NativePtr != IntPtr.Zero)
                        HandleHierarchyModelAssetDrop(world);
                }
            }
        }

        ImGui.End();
    }

    private static void HandleHierarchyModelAssetDrop(XRWorldInstance world)
    {
        var payload = ImGui.GetDragDropPayload();
        if (payload.Data == IntPtr.Zero || payload.DataSize == 0)
            return;

        // Verify this is our asset payload type by checking the DataType bytes
        unsafe
        {
            ReadOnlySpan<byte> typeSpan = new(payload.DataType.Data, payload.DataType.Count);
            int nullIndex = typeSpan.IndexOf((byte)0);
            if (nullIndex >= 0)
                typeSpan = typeSpan.Slice(0, nullIndex);
            string payloadType = Encoding.ASCII.GetString(typeSpan);
            if (!string.Equals(payloadType, ImGuiAssetUtilities.AssetPayloadType, StringComparison.Ordinal))
                return;
        }

        string? path = ImGuiAssetUtilities.GetPathFromPayload(payload);
        if (string.IsNullOrWhiteSpace(path))
            return;

        SceneNode? parent = Selection.SceneNode;
        if (TryLoadPrefabAsset(path, out var prefab))
        {
            UpdatePrefabPreview(world, parent, prefab!);
            if (ImGui.IsMouseReleased(ImGuiMouseButton.Left) && !TryFinalizePrefabPreview(world, parent, prefab!))
                EnqueueSceneEdit(() => SpawnPrefabNode(world, parent, prefab!));
        }
        else if (TryLoadModelAsset(path, out var model))
        {
            if (_prefabPreviewActive)
                RevertPrefabPreview();
            if (ImGui.IsMouseReleased(ImGuiMouseButton.Left))
            EnqueueSceneEdit(() => SpawnModelNode(world, parent, model!, path));
        }
    }

    private static void DrawWorldHierarchyTab()
    {
        using var profilerScope = Engine.Profiler.Start("UI.DrawWorldHierarchyTab");
        var world = TryGetActiveWorldInstance();
        if (world is null)
        {
            ImGui.Text("No world instance available.");
            return;
        }

        DrawWorldHeader(world);

        // Editor-only content lives in a hidden scene (gizmos, tools, UI, etc.)
        // Keep it hidden by default, but allow toggling for debugging.
        ImGui.Spacing();
        if (ImGui.Checkbox("Show Editor Scene##HierarchyShowEditorScene", ref _showEditorSceneHierarchy))
        {
            // no-op; state is stored in the static flag
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Show the hidden editor scene hierarchy (editor-only gizmos/tools/UI).");
        ImGui.Separator();

        ImGui.Text($"GameMode: {world.GameMode?.GetType().Name ?? "<none>"}");
        ImGui.Separator();

        var targetWorld = world.TargetWorld;
        bool drewAnySection = false;

        if (targetWorld?.Scenes.Count > 0)
        {
            // Filter out editor-only scenes from the hierarchy display
            var sceneSnapshot = targetWorld.Scenes.Where(s => !s.IsEditorOnly).ToArray();
            foreach (var scene in sceneSnapshot)
            {
                DrawSceneHierarchySection(scene, world);
                drewAnySection = true;
                ImGui.Spacing();
            }

            var unassignedRoots = CollectUnassignedRoots(world, sceneSnapshot);
            if (unassignedRoots.Count > 0)
            {
                ImGui.Spacing();
                DrawUnassignedHierarchy(unassignedRoots, world);
                drewAnySection = true;
            }
        }
        else
        {
            drewAnySection = DrawRuntimeHierarchy(world);
        }

        if (_showEditorSceneHierarchy)
        {
            ImGui.Spacing();
            DrawEditorSceneHierarchy(world);
            drewAnySection = true;
        }

        if (!drewAnySection)
            ImGui.Text("World has no root nodes.");
    }

    private static void DrawEditorSceneHierarchy(XRWorldInstance world)
    {
        var editorScene = world.EditorScene;
        if (editorScene is null)
            return;

        ImGui.PushID("__EditorSceneHierarchy__");
        bool open = ImGui.CollapsingHeader("Editor Scene (Hidden)##EditorScene", ImGuiTreeNodeFlags.DefaultOpen);
        if (open)
        {
            ImGui.TextDisabled("Editor-only content (not saved with the world).");
            var roots = editorScene.RootNodes.Where(r => r is not null).ToArray();
            DrawSceneHierarchyNodes(roots, world, editorScene);
        }
        ImGui.PopID();
    }

    private static void DrawSceneNodeTree(SceneNode node, XRWorldInstance world, XRScene? owningScene)
    {
        var transform = node.Transform;
        int childCount = transform.Children.Count;
        string nodeLabel = node.Name ?? "<unnamed>";
        if (childCount > 0)
            nodeLabel += $" ({childCount})";
        ImGuiTreeNodeFlags flags = childCount > 0
            ? ImGuiTreeNodeFlags.DefaultOpen
            : ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.Bullet | ImGuiTreeNodeFlags.NoTreePushOnOpen;

        bool nodeOpen = DrawSceneNodeEntry(node, world, nodeLabel, flags, owningScene);

        if (childCount > 0 && nodeOpen)
        {
            var childSnapshot = transform.Children.ToArray();
            foreach (var child in childSnapshot)
            {
                if (child?.SceneNode is SceneNode childNode)
                    DrawSceneNodeTree(childNode, world, owningScene);
            }
            ImGui.TableSetColumnIndex(0);
            ImGui.TreePop();
        }
    }

    private static bool DrawSceneNodeEntry(SceneNode node, XRWorldInstance world, string displayLabel, ImGuiTreeNodeFlags flags, XRScene? owningScene)
    {
        bool isRenaming = ReferenceEquals(_nodePendingRename, node);
        bool isSelected = Selection.SceneNodes.Contains(node) || ReferenceEquals(Selection.LastSceneNode, node);

        ImGui.PushID(node.ID.ToString());

        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);

        ImGuiTreeNodeFlags fullFlags = flags
                                        | ImGuiTreeNodeFlags.SpanFullWidth
                                        | ImGuiTreeNodeFlags.FramePadding
                                        | ImGuiTreeNodeFlags.OpenOnArrow
                                        | ImGuiTreeNodeFlags.OpenOnDoubleClick;
        if (isSelected)
            fullFlags |= ImGuiTreeNodeFlags.Selected;
        bool nodeOpen = ImGui.TreeNodeEx("##TreeNode", fullFlags);
        bool treeItemHovered = ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
        if (!isRenaming && ImGui.IsItemClicked(ImGuiMouseButton.Left))
            _nodePendingSelection = node;

        if (!isRenaming
            && ReferenceEquals(_nodePendingSelection, node)
            && ImGui.IsMouseReleased(ImGuiMouseButton.Left))
        {
            if (treeItemHovered && !ImGui.IsMouseDragging(ImGuiMouseButton.Left))
                UpdateHierarchySelection(node);

            _nodePendingSelection = null;
        }
        ImGui.OpenPopupOnItemClick("Context", ImGuiPopupFlags.MouseButtonRight);

        if (ImGui.BeginDragDropSource(ImGuiDragDropFlags.SourceAllowNullID))
        {
            if (ReferenceEquals(_nodePendingSelection, node))
                _nodePendingSelection = null;

            ImGuiSceneNodeDragDrop.SetPayload(node);
            ImGui.TextUnformatted(displayLabel);
            ImGui.EndDragDropSource();
        }

        if (ImGui.BeginDragDropTarget())
        {
            if (ImGuiSceneNodeDragDrop.Accept() is SceneNode droppedNode
                && !ReferenceEquals(droppedNode, node)
                && !IsHierarchyDescendantOf(node, droppedNode))
            {
                ReparentHierarchyNode(droppedNode, node.Transform, world, owningScene);
            }
            ImGui.EndDragDropTarget();
        }

        ImGui.SameLine();

        if (isRenaming)
        {
            if (_renameInputFocusRequested)
            {
                ImGui.SetKeyboardFocusHere();
                _renameInputFocusRequested = false;
            }

            ImGui.SetNextItemWidth(-1f);
            bool submitted = ImGui.InputText("##Rename", _renameBuffer, (uint)_renameBuffer.Length,
                ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.AutoSelectAll);
            ImGui.OpenPopupOnItemClick("Context", ImGuiPopupFlags.MouseButtonRight);
            bool cancel = ImGui.IsKeyPressed(ImGuiKey.Escape);
            bool lostFocus = ImGui.IsItemDeactivated();

            if (cancel)
                CancelHierarchyNodeRename();
            else if (submitted || lostFocus)
                ApplyHierarchyNodeRename();
        }
        else
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(displayLabel);
            ImGui.OpenPopupOnItemClick("Context", ImGuiPopupFlags.MouseButtonRight);
        }

        ImGui.TableSetColumnIndex(1);

        bool activeSelf = node.IsActiveSelf;
        bool checkboxToggled = ImGui.Checkbox("##ActiveSelf", ref activeSelf);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Toggle node active state");
        if (checkboxToggled)
        {
            using var _ = Undo.TrackChange("Toggle Node Active", node);
            node.IsActiveSelf = activeSelf;
            MarkSceneHierarchyDirty(node, owningScene, world);
        }
        ImGui.OpenPopupOnItemClick("Context", ImGuiPopupFlags.MouseButtonRight);

        if (ImGui.BeginPopup("Context"))
        {
            if (ImGui.MenuItem("Rename"))
                BeginHierarchyNodeRename(node);

            if (ImGui.MenuItem("Duplicate"))
                DuplicateHierarchyNodes(GetHierarchyDuplicateTargets(node), world, preserveAssetReferences: true);

            if (ImGui.MenuItem("Deep Duplicate..."))
                RequestHierarchyDeepDuplicate(GetHierarchyDuplicateTargets(node));

            if (ImGui.MenuItem("Delete"))
                DeleteHierarchyNode(node, world, owningScene);

            if (ImGui.MenuItem("Add Child Scene Node"))
                CreateChildSceneNode(node, owningScene, world);

            if (ImGui.MenuItem("Focus Camera"))
                FocusCameraOnHierarchyNode(node);

            ImGui.EndPopup();
        }

        ImGui.TableSetColumnIndex(0);
        ImGui.PopID();

        return nodeOpen;
    }

    private static IReadOnlyList<SceneNode> GetHierarchyDuplicateTargets(SceneNode clickedNode)
    {
        IReadOnlyList<SceneNode> candidates = Selection.SceneNodes.Contains(clickedNode)
            ? Selection.SceneNodes
            : [clickedNode];

        return FilterTopLevelHierarchyNodes(candidates);
    }

    private static IReadOnlyList<SceneNode> FilterTopLevelHierarchyNodes(IReadOnlyList<SceneNode> nodes)
    {
        if (nodes.Count == 0)
            return Array.Empty<SceneNode>();

        var uniqueNodes = new HashSet<SceneNode>(ReferenceEqualityComparer.Instance);
        var orderedNodes = new List<SceneNode>(nodes.Count);
        foreach (var node in nodes)
        {
            if (node is null || !uniqueNodes.Add(node))
                continue;

            orderedNodes.Add(node);
        }

        if (orderedNodes.Count <= 1)
            return orderedNodes;

        var selectedNodes = new HashSet<SceneNode>(orderedNodes, ReferenceEqualityComparer.Instance);
        var topLevelNodes = new List<SceneNode>(orderedNodes.Count);
        foreach (var node in orderedNodes)
        {
            if (!HasSelectedHierarchyAncestor(node, selectedNodes))
                topLevelNodes.Add(node);
        }

        return topLevelNodes;
    }

    private static bool HasSelectedHierarchyAncestor(SceneNode node, HashSet<SceneNode> selectedNodes)
    {
        TransformBase? current = node.Transform.Parent;
        while (current is not null)
        {
            if (current.SceneNode is SceneNode ancestor && selectedNodes.Contains(ancestor))
                return true;

            current = current.Parent;
        }

        return false;
    }

    private static void RequestHierarchyDeepDuplicate(IReadOnlyList<SceneNode> nodes)
    {
        var targets = FilterTopLevelHierarchyNodes(nodes);
        if (targets.Count == 0)
            return;

        _nodesPendingDeepDuplicate = targets;
        _assetsPendingDeepDuplicate = CollectDeepDuplicateAssets(targets);
        _hierarchyDeepDuplicatePopupRequested = true;
    }

    private static void DrawHierarchyDeepDuplicateConfirmation()
    {
        if (_nodesPendingDeepDuplicate is null)
            return;

        if (_hierarchyDeepDuplicatePopupRequested)
        {
            ImGui.OpenPopup(HierarchyDeepDuplicatePopupId);
            _hierarchyDeepDuplicatePopupRequested = false;
        }

        bool open = true;
        if (ImGui.BeginPopupModal(HierarchyDeepDuplicatePopupId, ref open, ImGuiWindowFlags.AlwaysAutoResize))
        {
            IReadOnlyList<SceneNode> nodes = _nodesPendingDeepDuplicate;
            IReadOnlyList<XRAsset> assets = _assetsPendingDeepDuplicate ?? Array.Empty<XRAsset>();
            int nodeCount = nodes.Count;
            int assetCount = assets.Count;
            bool canExecute = TryGetActiveWorldInstance() is not null;

            ImGui.TextUnformatted(nodeCount == 1
                ? $"Deep duplicate '{GetHierarchyNodeDisplayName(nodes[0])}'?"
                : $"Deep duplicate {nodeCount} scene nodes?");

            ImGui.PushTextWrapPos(520.0f);
            if (assetCount == 0)
            {
                ImGui.TextDisabled("No nested assets will be cloned by this duplicate.");
            }
            else
            {
                ImGui.TextUnformatted(assetCount == 1
                    ? "This duplicate will also clone the following asset:"
                    : "This duplicate will also clone the following assets:");
            }
            ImGui.PopTextWrapPos();

            if (assetCount > 0)
            {
                ImGui.Spacing();
                if (ImGui.BeginChild("##DeepDuplicateAssets", new Vector2(560.0f, 220.0f), ImGuiChildFlags.Border))
                {
                    foreach (var asset in assets)
                    {
                        ImGui.BulletText(GetHierarchyDuplicateAssetSummary(asset));
                    }
                }
                ImGui.EndChild();
            }

            if (!canExecute)
            {
                ImGui.Spacing();
                ImGui.TextDisabled("No active world instance is available.");
            }

            ImGui.Separator();

            if (!canExecute)
                ImGui.BeginDisabled();

            if (ImGui.Button("Deep Duplicate"))
            {
                var world = TryGetActiveWorldInstance();
                if (world is not null)
                    DuplicateHierarchyNodes(nodes, world, preserveAssetReferences: false);

                ClearHierarchyDeepDuplicateRequest();
                ImGui.CloseCurrentPopup();
                if (!canExecute)
                    ImGui.EndDisabled();
                ImGui.EndPopup();
                return;
            }

            if (!canExecute)
                ImGui.EndDisabled();

            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
            {
                ImGui.CloseCurrentPopup();
                ClearHierarchyDeepDuplicateRequest();
            }

            ImGui.EndPopup();
        }

        if (!open)
            ClearHierarchyDeepDuplicateRequest();
    }

    private static void ClearHierarchyDeepDuplicateRequest()
    {
        _nodesPendingDeepDuplicate = null;
        _assetsPendingDeepDuplicate = null;
        _hierarchyDeepDuplicatePopupRequested = false;
    }

    private static IReadOnlyList<XRAsset> CollectDeepDuplicateAssets(IReadOnlyList<SceneNode> nodes)
    {
        var assets = new List<XRAsset>();
        var discovered = new HashSet<XRAsset>(ReferenceEqualityComparer.Instance);
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);

        foreach (var node in nodes)
            CollectDeepDuplicateAssets(node, assets, discovered, visited);

        return assets;
    }

    private static void CollectDeepDuplicateAssets(object? value, List<XRAsset> assets, HashSet<XRAsset> discovered, HashSet<object> visited)
    {
        if (value is null)
            return;

        if (value is XRAsset asset)
        {
            if (ShouldDeepDuplicateAsset(asset) && discovered.Add(asset))
                assets.Add(asset);
        }

        Type type = value.GetType();
        if (IsHierarchyTraversalLeafType(type) || !visited.Add(value))
            return;

        if (value is IDictionary dictionary)
        {
            foreach (DictionaryEntry entry in dictionary)
            {
                CollectDeepDuplicateAssets(entry.Key, assets, discovered, visited);
                CollectDeepDuplicateAssets(entry.Value, assets, discovered, visited);
            }

            return;
        }

        if (value is IEnumerable enumerable && value is not string)
        {
            foreach (var item in enumerable)
                CollectDeepDuplicateAssets(item, assets, discovered, visited);

            return;
        }

        foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (!ShouldTraverseHierarchyProperty(property))
                continue;

            object? propertyValue;
            try
            {
                propertyValue = property.GetValue(value);
            }
            catch
            {
                continue;
            }

            CollectDeepDuplicateAssets(propertyValue, assets, discovered, visited);
        }

        foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (!ShouldTraverseHierarchyField(field))
                continue;

            CollectDeepDuplicateAssets(field.GetValue(value), assets, discovered, visited);
        }
    }

    private static void DuplicateHierarchyNodes(IReadOnlyList<SceneNode> nodes, XRWorldInstance world, bool preserveAssetReferences)
    {
        var targets = FilterTopLevelHierarchyNodes(nodes);
        if (targets.Count == 0)
            return;

        using var interaction = Undo.BeginUserInteraction();
        using var scope = Undo.BeginChange(targets.Count == 1 ? "Duplicate Node" : "Duplicate Nodes");

        var previousSelection = Selection.SceneNodes;
        var operations = new List<HierarchyDuplicateOperation>(targets.Count);
        foreach (var node in targets)
        {
            var clone = SceneNodePrefabUtility.CloneHierarchy(node);
            clone.Name = GenerateHierarchyDuplicateName(node);
            if (preserveAssetReferences)
                RestoreHierarchyAssetReferences(node, clone);

            var operation = new HierarchyDuplicateOperation(
                node,
                clone,
                node.Transform.Parent,
                FindSceneForNode(node, world));

            AttachDuplicatedNode(operation, world);
            Undo.TrackSceneNode(clone);
            MarkSceneHierarchyDirty(clone, operation.OwningScene, world);
            operations.Add(operation);
        }

        Selection.SceneNodes = [.. operations.Select(static operation => operation.DuplicateNode)];

        Undo.RecordStructuralChange(targets.Count == 1 ? "Duplicate Node" : "Duplicate Nodes",
            undoAction: () =>
            {
                foreach (var operation in operations)
                {
                    DetachDuplicatedNode(operation, world);
                    operation.DuplicateNode.IsActiveSelf = false;
                }

                Selection.SceneNodes = previousSelection;
            },
            redoAction: () =>
            {
                foreach (var operation in operations)
                {
                    AttachDuplicatedNode(operation, world);
                    operation.DuplicateNode.IsActiveSelf = operation.Source.IsActiveSelf;
                    Undo.TrackSceneNode(operation.DuplicateNode);
                }

                Selection.SceneNodes = [.. operations.Select(static operation => operation.DuplicateNode)];
            });
    }

    private static void AttachDuplicatedNode(HierarchyDuplicateOperation operation, XRWorldInstance world)
    {
        if (operation.ParentTransform is not null)
        {
            operation.DuplicateNode.Transform.SetParent(operation.ParentTransform, false, EParentAssignmentMode.Immediate);
            return;
        }

        operation.OwningScene?.RootNodes.Add(operation.DuplicateNode);
        world.RootNodes.Add(operation.DuplicateNode);
    }

    private static void DetachDuplicatedNode(HierarchyDuplicateOperation operation, XRWorldInstance world)
    {
        if (operation.ParentTransform is not null)
        {
            operation.ParentTransform.RemoveChild(operation.DuplicateNode.Transform, EParentAssignmentMode.Immediate);
            return;
        }

        world.RootNodes.Remove(operation.DuplicateNode);
        operation.OwningScene?.RootNodes.Remove(operation.DuplicateNode);
    }

    private static void RestoreHierarchyAssetReferences(SceneNode source, SceneNode clone)
    {
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        RestoreHierarchyAssetReferences(source, clone, visited);
    }

    private static void RestoreHierarchyAssetReferences(object? source, object? clone, HashSet<object> visited)
    {
        if (source is null || clone is null)
            return;

        Type type = source.GetType();
        if (IsHierarchyTraversalLeafType(type) || !visited.Add(source))
            return;

        if (source is IDictionary sourceDictionary && clone is IDictionary cloneDictionary)
        {
            var sourceEntries = sourceDictionary.Cast<DictionaryEntry>().ToArray();
            var cloneEntries = cloneDictionary.Cast<DictionaryEntry>().ToArray();
            int count = Math.Min(sourceEntries.Length, cloneEntries.Length);
            for (int i = 0; i < count; i++)
            {
                object? sourceValue = sourceEntries[i].Value;
                object? cloneKey = cloneEntries[i].Key;
                object? cloneValue = cloneEntries[i].Value;
                if (sourceValue is XRAsset asset)
                {
                    cloneDictionary[cloneKey] = asset;
                }
                else
                {
                    RestoreHierarchyAssetReferences(sourceValue, cloneValue, visited);
                }
            }

            return;
        }

        if (source is IList sourceList && clone is IList cloneList)
        {
            int count = Math.Min(sourceList.Count, cloneList.Count);
            for (int i = 0; i < count; i++)
            {
                object? sourceItem = sourceList[i];
                object? cloneItem = cloneList[i];
                if (sourceItem is XRAsset asset)
                {
                    cloneList[i] = asset;
                }
                else
                {
                    RestoreHierarchyAssetReferences(sourceItem, cloneItem, visited);
                }
            }

            return;
        }

        // Non-indexed enumerables (ISet<T>, ICollection<T> that aren't IList, etc.)
        // We can't replace elements by index, but we can still walk the parallel
        // enumerators and recurse into non-asset elements so asset references
        // reachable *inside* set members are restored.
        if (source is IEnumerable sourceEnumerable && clone is IEnumerable cloneEnumerable
            && source is not string)
        {
            var sourceIter = sourceEnumerable.GetEnumerator();
            var cloneIter = cloneEnumerable.GetEnumerator();
            try
            {
                while (sourceIter.MoveNext() && cloneIter.MoveNext())
                    RestoreHierarchyAssetReferences(sourceIter.Current, cloneIter.Current, visited);
            }
            finally
            {
                (sourceIter as IDisposable)?.Dispose();
                (cloneIter as IDisposable)?.Dispose();
            }

            return;
        }

        foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (!ShouldTraverseHierarchyProperty(property))
                continue;

            object? sourceValue;
            object? cloneValue;
            try
            {
                sourceValue = property.GetValue(source);
                cloneValue = property.GetValue(clone);
            }
            catch
            {
                continue;
            }

            if (sourceValue is XRAsset asset)
            {
                if (property.SetMethod is not null)
                {
                    try
                    {
                        property.SetValue(clone, asset);
                    }
                    catch
                    {
                    }
                }
            }
            else
            {
                RestoreHierarchyAssetReferences(sourceValue, cloneValue, visited);
            }
        }

        foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (!ShouldTraverseHierarchyField(field))
                continue;

            object? sourceValue = field.GetValue(source);
            object? cloneValue = field.GetValue(clone);
            if (sourceValue is XRAsset asset)
            {
                try
                {
                    field.SetValue(clone, asset);
                }
                catch
                {
                }
            }
            else
            {
                RestoreHierarchyAssetReferences(sourceValue, cloneValue, visited);
            }
        }
    }

    private static bool ShouldDeepDuplicateAsset(XRAsset asset)
    {
        if (!ReferenceEquals(asset.SourceAsset, asset))
            return true;

        return string.IsNullOrWhiteSpace(asset.FilePath) || !File.Exists(asset.FilePath);
    }

    private static bool IsHierarchyTraversalLeafType(Type type)
    {
        if (type.IsPrimitive || type.IsEnum)
            return true;

        if (type == typeof(string)
            || type == typeof(decimal)
            || type == typeof(Guid)
            || type == typeof(DateTime)
            || type == typeof(DateTimeOffset)
            || type == typeof(TimeSpan))
        {
            return true;
        }

        if (typeof(Delegate).IsAssignableFrom(type) || type.IsPointer || type.IsByRef)
            return true;

        if (type.Namespace?.StartsWith("System", StringComparison.Ordinal) == true
            && !typeof(IEnumerable).IsAssignableFrom(type)
            && !typeof(XRAsset).IsAssignableFrom(type))
        {
            return true;
        }

        return false;
    }

    private static bool ShouldTraverseHierarchyProperty(PropertyInfo property)
    {
        if (!property.CanRead
            || property.GetIndexParameters().Length != 0
            || property.IsDefined(typeof(YamlIgnoreAttribute), true))
        {
            return false;
        }

        Type propertyType = property.PropertyType;
        return !typeof(Delegate).IsAssignableFrom(propertyType);
    }

    private static bool ShouldTraverseHierarchyField(FieldInfo field)
    {
        if (field.IsStatic || field.IsDefined(typeof(YamlIgnoreAttribute), true) || typeof(Delegate).IsAssignableFrom(field.FieldType))
            return false;

        return !field.IsInitOnly;
    }

    private static string GenerateHierarchyDuplicateName(SceneNode source)
    {
        string baseName = string.IsNullOrWhiteSpace(source.Name) ? SceneNode.DefaultName : source.Name!;

        // Collect sibling names for uniqueness check
        HashSet<string> siblingNames;
        if (source.Transform.Parent is TransformBase parent)
        {
            siblingNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var child in parent.Children)
            {
                if (child?.SceneNode is SceneNode sibling && !ReferenceEquals(sibling, source))
                    siblingNames.Add(sibling.Name ?? SceneNode.DefaultName);
            }
        }
        else
        {
            siblingNames = [];
        }

        // Strip any existing " (N)" suffix to find the true base name
        string coreName = baseName;
        if (baseName.EndsWith(')'))
        {
            int parenOpen = baseName.LastIndexOf('(');
            if (parenOpen > 0
                && baseName[parenOpen - 1] == ' '
                && int.TryParse(baseName.AsSpan(parenOpen + 1, baseName.Length - parenOpen - 2), out _))
            {
                coreName = baseName[..(parenOpen - 1)];
            }
        }

        // Find the lowest available suffix
        for (int i = 1; ; i++)
        {
            string candidate = $"{coreName} ({i})";
            if (!siblingNames.Contains(candidate))
                return candidate;
        }
    }

    private static string GetHierarchyNodeDisplayName(SceneNode node)
        => string.IsNullOrWhiteSpace(node.Name) ? SceneNode.DefaultName : node.Name!;

    private static string GetHierarchyDuplicateAssetSummary(XRAsset asset)
    {
        string name = string.IsNullOrWhiteSpace(asset.Name) ? asset.GetType().Name : asset.Name!;
        string location = string.IsNullOrWhiteSpace(asset.FilePath)
            ? (ReferenceEquals(asset.SourceAsset, asset) ? "unsaved asset" : "embedded asset")
            : asset.FilePath!;

        return $"{name} ({asset.GetType().Name}) - {location}";
    }

    private sealed record HierarchyDuplicateOperation(
        SceneNode Source,
        SceneNode DuplicateNode,
        TransformBase? ParentTransform,
        XRScene? OwningScene);

    private static void UpdateHierarchySelection(SceneNode node)
    {
        var io = ImGui.GetIO();
        if (io.KeyCtrl)
        {
            ToggleNodeSelection(node);
            return;
        }

        if (io.KeyAlt)
        {
            RemoveNodeFromSelection(node);
            return;
        }

        if (io.KeyShift)
        {
            AddNodeToSelection(node);
            return;
        }

        Selection.SceneNodes = [node];
    }

    private static void AddNodeToSelection(SceneNode node)
    {
        var existing = Selection.SceneNodes;
        if (existing.Contains(node))
            return;

        Selection.SceneNodes = [node, .. existing];
    }

    private static void ToggleNodeSelection(SceneNode node)
    {
        var existing = Selection.SceneNodes;
        if (existing.Contains(node))
        {
            Selection.SceneNodes = [.. existing.Where(n => n != node)];
        }
        else
        {
            Selection.SceneNodes = [node, .. existing];
        }
    }

    private static void RemoveNodeFromSelection(SceneNode node)
    {
        var existing = Selection.SceneNodes;
        if (!existing.Contains(node))
            return;

        Selection.SceneNodes = [.. existing.Where(n => n != node)];
    }

    private static void BeginHierarchyNodeRename(SceneNode node)
    {
        _nodePendingRename = node;
        _renameInputFocusRequested = true;
        PopulateRenameBuffer(node.Name ?? string.Empty);
    }

    private static void DeleteHierarchyNode(SceneNode node, XRWorldInstance world, XRScene? owningScene)
    {
        if (node == _nodePendingRename)
            _nodePendingRename = null;
        if (_nodesPendingAddComponent?.Contains(node) == true)
            _nodesPendingAddComponent = null;

        var tfm = node.Transform;
        var parentTransform = tfm.Parent;
        bool wasActive = node.IsActiveSelf;
        string nodeName = node.Name ?? SceneNode.DefaultName;

        // Capture state for undo before making changes
        var worldCapture = world;
        var sceneCapture = owningScene;

        // Capture undo context before making changes
        using var interaction = Undo.BeginUserInteraction();
        using var scope = Undo.BeginChange($"Delete {nodeName}");

        if (parentTransform is not null)
        {
            parentTransform.RemoveChild(tfm, EParentAssignmentMode.Immediate);
        }
        else
        {
            world.RootNodes.Remove(node);
            owningScene?.RootNodes.Remove(node);
        }

        FinalizeSceneNodeDeletion(node);

        Undo.RecordStructuralChange($"Delete {nodeName}",
            undoAction: () =>
            {
                // Restore: reattach to parent or root, reactivate
                if (parentTransform is not null)
                    tfm.SetParent(parentTransform, false, EParentAssignmentMode.Immediate);
                else
                {
                    sceneCapture?.RootNodes.Add(node);
                    worldCapture.RootNodes.Add(node);
                }
                node.IsActiveSelf = wasActive;
                Undo.TrackSceneNode(node);
            },
            redoAction: () =>
            {
                // Re-delete
                if (parentTransform is not null)
                    parentTransform.RemoveChild(tfm, EParentAssignmentMode.Immediate);
                else
                {
                    worldCapture.RootNodes.Remove(node);
                    sceneCapture?.RootNodes.Remove(node);
                }
                node.IsActiveSelf = false;
            });

        MarkSceneHierarchyDirty(node, owningScene, world);
    }

    private static void FinalizeSceneNodeDeletion(SceneNode node)
    {
        node.IsActiveSelf = false;
    }

    private static void CreateChildSceneNode(SceneNode parent, XRScene? owningScene, XRWorldInstance world)
    {
        SceneNode child = new(parent);
        Undo.TrackSceneNode(child);

        // Record structural undo
        var parentCapture = parent;
        using var interaction = Undo.BeginUserInteraction();
        using var scope = Undo.BeginChange("Create Child Node");
        Undo.RecordStructuralChange("Create Child Node",
            undoAction: () =>
            {
                parentCapture.Transform.RemoveChild(child.Transform, EParentAssignmentMode.Immediate);
                child.IsActiveSelf = false;
            },
            redoAction: () =>
            {
                child.Transform.SetParent(parentCapture.Transform, false, EParentAssignmentMode.Immediate);
                child.IsActiveSelf = true;
                Undo.TrackSceneNode(child);
            });

        MarkSceneHierarchyDirty(child, owningScene, world);
        BeginHierarchyNodeRename(child);
    }

    private static void ApplyHierarchyNodeRename()
    {
        if (_nodePendingRename is null)
            return;

        string newName = ExtractStringFromRenameBuffer();
        if (string.IsNullOrWhiteSpace(newName))
            newName = SceneNode.DefaultName;

        var node = _nodePendingRename;
        using var _ = Undo.TrackChange("Rename Node", node);
        node.Name = newName;
        MarkSceneHierarchyDirty(node, null, TryGetActiveWorldInstance());
        CancelHierarchyNodeRename();
    }

    private static void CancelHierarchyNodeRename()
    {
        _nodePendingRename = null;
        _renameInputFocusRequested = false;
        Array.Clear(_renameBuffer, 0, _renameBuffer.Length);
    }

    private static void PopulateRenameBuffer(string source)
    {
        Array.Clear(_renameBuffer, 0, _renameBuffer.Length);
        if (string.IsNullOrEmpty(source))
            return;

        int written = Encoding.UTF8.GetBytes(source, 0, source.Length, _renameBuffer, 0);
        if (written < _renameBuffer.Length)
            _renameBuffer[written] = 0;
    }

    private static string ExtractStringFromRenameBuffer()
    {
        int length = Array.IndexOf(_renameBuffer, (byte)0);
        if (length < 0)
            length = _renameBuffer.Length;

        return Encoding.UTF8.GetString(_renameBuffer, 0, length).Trim();
    }

    private static void FocusCameraOnHierarchyNode(SceneNode node)
    {
        var player = Engine.State.MainPlayer ?? Engine.State.GetOrCreateLocalPlayer(ELocalPlayerIndex.One);
        if (player?.ControlledPawnComponent is EditorFlyingCameraPawnComponent pawn)
            pawn.FocusOnNode(node, HierarchyFocusCameraDurationSeconds);
    }

    private static void DrawSceneHierarchySection(XRScene scene, XRWorldInstance world)
    {
        if (scene is null)
            return;

        ImGui.PushID(scene.ID.ToString());
        string sceneName = string.IsNullOrWhiteSpace(scene.Name) ? "Untitled Scene" : scene.Name!;
        string headerLabel = $"{sceneName}{(scene.IsDirty ? " *" : string.Empty)}##SceneHeader";
        bool open;
        // Use a two-column layout so the collapsing header can't steal clicks
        // from the controls rendered to its right.
        if (ImGui.BeginTable("SceneHeaderRow", 2, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoSavedSettings))
        {
            ImGui.TableSetupColumn("Header", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Controls", ImGuiTableColumnFlags.WidthFixed);
            ImGui.TableNextRow();

            ImGui.TableSetColumnIndex(0);
            open = ImGui.CollapsingHeader(headerLabel, ImGuiTreeNodeFlags.DefaultOpen);
            if (ImGui.IsItemHovered() && !string.IsNullOrWhiteSpace(scene.FilePath))
                ImGui.SetTooltip(scene.FilePath);

            ImGui.TableSetColumnIndex(1);
            bool visible = scene.IsVisible;
            if (ImGui.Checkbox("Visible##SceneVisible", ref visible))
                ToggleSceneVisibility(scene, world, visible);

            ImGui.SameLine();
            if (ImGui.SmallButton("Unload"))
                UnloadSceneFromWorld(scene, world);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Unload this scene from the active world");

            ImGui.EndTable();
        }
        else
        {
            open = ImGui.CollapsingHeader(headerLabel, ImGuiTreeNodeFlags.DefaultOpen);
        }

        if (open)
        {
            if (!scene.IsVisible)
                ImGui.TextColored(new Vector4(0.9f, 0.7f, 0.2f, 1.0f), "Scene is hidden");

            var rootSnapshot = scene.RootNodes.Where(r => r is not null).ToArray();
            DrawSceneHierarchyNodes(rootSnapshot, world, scene);
        }

        ImGui.PopID();
    }

    private static void DrawSceneHierarchyNodes(IReadOnlyList<SceneNode> roots, XRWorldInstance world, XRScene? owningScene)
    {
        if (roots.Count == 0)
        {
            ImGui.TextDisabled("No nodes in this scene.");
            return;
        }

        ImGuiTableFlags tableFlags = ImGuiTableFlags.RowBg
                                    | ImGuiTableFlags.Resizable
                                    | ImGuiTableFlags.SizingStretchProp
                                    | ImGuiTableFlags.BordersInnerV;

        ImGui.PushStyleVar(ImGuiStyleVar.IndentSpacing, 14.0f);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(4.0f, 2.0f));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(4.0f, 2.0f));
        ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, new Vector2(4.0f, 2.0f));
        ImGui.SetWindowFontScale(0.95f);
        if (ImGui.BeginTable("HierarchyTree", 2, tableFlags))
        {
            ImGui.TableSetupColumn("Node", ImGuiTableColumnFlags.WidthStretch, 1.0f);
            ImGui.TableSetupColumn("Active", ImGuiTableColumnFlags.WidthFixed, 72.0f);
            ImGui.TableHeadersRow();

            foreach (var root in roots)
                DrawSceneNodeTree(root, world, owningScene);

            ImGui.EndTable();
        }
        ImGui.SetWindowFontScale(1.0f);
        ImGui.PopStyleVar(4);
    }

    private static void DrawUnassignedHierarchy(IReadOnlyList<SceneNode> roots, XRWorldInstance world)
    {
        ImGui.PushID("WorldRootNodes");
        bool open = ImGui.CollapsingHeader("World Root Nodes##WorldRoot", ImGuiTreeNodeFlags.DefaultOpen);
        if (open)
            DrawSceneHierarchyNodes(roots, world, null);
        ImGui.PopID();
    }

    private static bool DrawRuntimeHierarchy(XRWorldInstance world)
    {
        var roots = world.RootNodes.ToArray();
        if (roots.Length == 0)
            return false;

        ImGui.PushID("RuntimeWorldNodes");
        bool open = ImGui.CollapsingHeader("World Nodes##RuntimeWorld", ImGuiTreeNodeFlags.DefaultOpen);
        if (open)
            DrawSceneHierarchyNodes(roots, world, null);
        ImGui.PopID();
        return true;
    }

    private static List<SceneNode> CollectUnassignedRoots(XRWorldInstance world, IReadOnlyList<XRScene> scenes)
    {
        var assigned = new HashSet<SceneNode>();
        foreach (var scene in scenes)
        {
            foreach (var root in scene.RootNodes)
            {
                if (root is not null)
                    assigned.Add(root);
            }
        }

        // Also mark nodes from editor-only scenes as assigned so they don't appear as unassigned
        var targetWorld = world.TargetWorld;
        if (targetWorld is not null)
        {
            foreach (var scene in targetWorld.Scenes.Where(s => s.IsEditorOnly))
            {
                foreach (var root in scene.RootNodes)
                {
                    if (root is not null)
                        assigned.Add(root);
                }
            }
        }

        // Also exclude nodes in the world instance's editor scene
        if (world.IsInEditorScene(null) is false) // Just to initialize editor scene reference if needed
        {
            // Check editor scene nodes
        }

        var unassigned = new List<SceneNode>();
        foreach (var root in world.RootNodes)
        {
            if (root is null)
                continue;
            if (!assigned.Contains(root) && !world.IsInEditorScene(root))
                unassigned.Add(root);
        }

        return unassigned;
    }

    private static void ToggleSceneVisibility(XRScene scene, XRWorldInstance world, bool visible)
    {
        if (scene.IsVisible == visible)
            return;

        using var _ = Undo.TrackChange("Toggle Scene Visibility", scene);
        scene.IsVisible = visible;
        scene.MarkDirty();
    }

    private static void UnloadSceneFromWorld(XRScene scene, XRWorldInstance world)
    {
        var targetWorld = world.TargetWorld;
        if (targetWorld is null)
            return;

        bool wasVisible = scene.IsVisible;

        scene.IsVisible = false;
        world.UnloadScene(scene);
        targetWorld.Scenes.Remove(scene);
        ClearSelectionForScene(scene, world);
        scene.MarkDirty();

        // Record structural undo
        using var interaction = Undo.BeginUserInteraction();
        using var scope = Undo.BeginChange("Unload Scene");
        Undo.RecordStructuralChange("Unload Scene",
            undoAction: () =>
            {
                targetWorld.Scenes.Add(scene);
                world.LoadScene(scene);
                scene.IsVisible = wasVisible;
                Undo.TrackScene(scene);
            },
            redoAction: () =>
            {
                scene.IsVisible = false;
                world.UnloadScene(scene);
                targetWorld.Scenes.Remove(scene);
            });
    }

    private static void ClearSelectionForScene(XRScene scene, XRWorldInstance world)
    {
        if (Selection.SceneNodes.Length == 0)
            return;

        var filtered = Selection.SceneNodes
            .Where(node => FindSceneForNode(node, world) != scene)
            .ToArray();

        if (filtered.Length == Selection.SceneNodes.Length)
            return;

        Selection.SceneNodes = filtered;
    }

    private static XRScene? FindSceneForNode(SceneNode? node, XRWorldInstance? world)
    {
        if (node is null)
            return null;

        var instance = world ?? TryGetActiveWorldInstance();
        var targetWorld = instance?.TargetWorld;
        if (targetWorld is null)
            return null;

        SceneNode? root = GetHierarchyRoot(node);
        if (root is null)
            return null;

        foreach (var scene in targetWorld.Scenes)
        {
            if (scene.RootNodes.Contains(root))
                return scene;
        }

        return null;
    }

    private static SceneNode? GetHierarchyRoot(SceneNode node)
    {
        TransformBase? transform = node.Transform;
        while (transform?.Parent is TransformBase parent)
            transform = parent;
        return transform?.SceneNode;
    }

    private static void MarkSceneHierarchyDirty(SceneNode? node, XRScene? owningScene, XRWorldInstance? world)
    {
        if (node is null)
            return;

        var scene = owningScene ?? FindSceneForNode(node, world);
        if (scene is not null)
        {
            scene.MarkDirty();
        }
        else
        {
            world?.TargetWorld?.MarkDirty();
        }
    }

    /// <summary>
    /// Reparent a dragged node under a new parent with structural undo.
    /// </summary>
    private static void ReparentHierarchyNode(SceneNode droppedNode, TransformBase newParent, XRWorldInstance world, XRScene? owningScene)
    {
        var draggedTfm = droppedNode.Transform;
        var oldParent = draggedTfm.Parent;
        bool wasRoot = oldParent is null;
        string nodeName = droppedNode.Name ?? SceneNode.DefaultName;

        using var interaction = Undo.BeginUserInteraction();
        using var scope = Undo.BeginChange($"Reparent {nodeName}");

        // Remove from root lists if it was a root node
        if (wasRoot)
        {
            world.RootNodes.Remove(droppedNode);
            owningScene?.RootNodes.Remove(droppedNode);
        }

        draggedTfm.SetParent(newParent, true, EParentAssignmentMode.Immediate);

        var worldCapture = world;
        var sceneCapture = owningScene;
        Undo.RecordStructuralChange($"Reparent {nodeName}",
            undoAction: () =>
            {
                if (wasRoot)
                {
                    draggedTfm.SetParent(null, true, EParentAssignmentMode.Immediate);
                    sceneCapture?.RootNodes.Add(droppedNode);
                    worldCapture.RootNodes.Add(droppedNode);
                }
                else
                {
                    draggedTfm.SetParent(oldParent, true, EParentAssignmentMode.Immediate);
                }
            },
            redoAction: () =>
            {
                if (wasRoot)
                {
                    worldCapture.RootNodes.Remove(droppedNode);
                    sceneCapture?.RootNodes.Remove(droppedNode);
                }
                draggedTfm.SetParent(newParent, true, EParentAssignmentMode.Immediate);
            });

        MarkSceneHierarchyDirty(droppedNode, owningScene, world);
    }

    /// <summary>
    /// Returns true if <paramref name="node"/> is a descendant of <paramref name="potentialAncestor"/>.
    /// Used to prevent circular reparenting during drag-drop.
    /// </summary>
    private static bool IsHierarchyDescendantOf(SceneNode node, SceneNode potentialAncestor)
    {
        var current = node.Transform.Parent;
        while (current is not null)
        {
            if (ReferenceEquals(current.SceneNode, potentialAncestor))
                return true;
            current = current.Parent;
        }
        return false;
    }

    private static void DrawWorldHeader(XRWorldInstance world)
    {
        var targetWorld = world.TargetWorld;
        string worldName = targetWorld?.Name ?? "World";
        string? filePath = targetWorld?.FilePath;
        string displayPath = string.IsNullOrEmpty(filePath) ? "(unsaved)" : filePath;

        // World name/file path header
        ImGui.TextUnformatted($"World: {worldName}");
        ImGui.TextDisabled(displayPath);

        // Settings button
        if (targetWorld is not null && ImGui.SmallButton("Settings##WorldSettings"))
        {
            _showInspector = true;
            SetInspectorStandaloneTarget(targetWorld.Settings, $"World Settings: {worldName}");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Show world settings in the inspector panel");
    }
}
