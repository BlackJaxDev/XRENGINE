using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using ImGuiNET;
using XREngine.Core.Files;
using XREngine.Scene;
using XREngine.Scene.Prefabs;
using XREngine.Scene.Transforms;

namespace XREngine.Editor.AssetEditors;

public sealed class XRPrefabSourceInspector : IXRAssetInspector
{
    [ThreadStatic]
    private static int _readOnlyScopeDepth;

    private const string PrefabNodePayloadType = "XR_PREFAB_SCENE_NODE";
    private const int RenameBufferSize = 256;
    private readonly ConditionalWeakTable<XRPrefabSource, EditorState> _stateCache = new();

    public static IDisposable EnterReadOnlyScope()
        => new ReadOnlyScope();

    private static bool IsReadOnly => _readOnlyScopeDepth > 0;

    public void DrawInspector(EditorImGuiUI.InspectorTargetSet targets, HashSet<object> visitedObjects)
    {
        var prefabs = targets.Targets.OfType<XRPrefabSource>().Cast<object>().ToList();
        if (prefabs.Count == 0)
        {
            foreach (var asset in targets.Targets.OfType<XRAsset>())
                EditorImGuiUI.DrawDefaultAssetInspector(asset, visitedObjects);

            return;
        }

        if (targets.HasMultipleTargets)
        {
            EditorImGuiUI.DrawDefaultAssetInspector(new EditorImGuiUI.InspectorTargetSet(prefabs, targets.CommonType), visitedObjects);
            return;
        }

        var prefab = (XRPrefabSource)prefabs[0];
        var state = _stateCache.GetValue(prefab, static _ => new EditorState());
        EnsureSelectedNode(prefab, state);

        DrawHeader(prefab);

        if (IsReadOnly)
            ImGui.TextDisabled("Prefab is still loading. Editing is disabled until referenced assets finish resolving.");

        if (prefab.RootNode is null)
        {
            ImGui.Separator();
            ImGui.TextDisabled("Prefab has no root scene node.");
            DrawRawAssetSection(prefab, visitedObjects);
            return;
        }

        ImGui.Separator();
        DrawSummary(prefab, state);
        ImGui.Separator();

        ImGui.PushID(prefab.ID.ToString());
        DrawHierarchyAndInspector(prefab, state);
        ImGui.PopID();

        DrawRawAssetSection(prefab, visitedObjects);
    }

    private static void DrawHeader(XRPrefabSource prefab)
    {
        string displayName = !string.IsNullOrWhiteSpace(prefab.Name)
            ? prefab.Name!
            : (!string.IsNullOrWhiteSpace(prefab.FilePath)
                ? Path.GetFileNameWithoutExtension(prefab.FilePath)
                : nameof(XRPrefabSource));

        ImGui.TextUnformatted(displayName);

        string path = prefab.FilePath ?? "<unsaved asset>";
        ImGui.TextDisabled(path);
        if (!string.IsNullOrWhiteSpace(prefab.FilePath))
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Copy Path##PrefabSource"))
                ImGui.SetClipboardText(prefab.FilePath);
        }
    }

    private static void DrawSummary(XRPrefabSource prefab, EditorState state)
    {
        SceneNode root = prefab.RootNode!;
        string selectedName = GetNodeDisplayName(state.SelectedNode ?? root);
        int totalNodeCount = CountNodes(root);
        int visibleNodeCount = CountVisibleNodes(root, state.SearchText);

        if (!ImGui.BeginTable("PrefabSourceSummary", 2, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg))
            return;

        DrawSummaryRow("Root Node", GetNodeDisplayName(root));
        DrawSummaryRow("Total Nodes", totalNodeCount.ToString());
        if (!string.IsNullOrWhiteSpace(state.SearchText))
            DrawSummaryRow("Visible Nodes", visibleNodeCount.ToString());
        DrawSummaryRow("Selected Node", selectedName);
        DrawSummaryRow("Prefab ID", prefab.ID.ToString());
        ImGui.EndTable();
    }

    private static void DrawHierarchyAndInspector(XRPrefabSource prefab, EditorState state)
    {
        Vector2 available = ImGui.GetContentRegionAvail();
        float spacing = ImGui.GetStyle().ItemSpacing.Y;
        const float minTreeHeight = 180.0f;
        const float minInspectorHeight = 220.0f;

        float treeHeight;
        if (available.Y > minTreeHeight + minInspectorHeight + spacing)
        {
            treeHeight = Math.Clamp(available.Y * 0.42f, minTreeHeight, available.Y - minInspectorHeight - spacing);
        }
        else
        {
            treeHeight = MathF.Max(120.0f, available.Y * 0.35f);
        }

        ImGui.SetNextItemWidth(-1.0f);
        ImGui.InputTextWithHint("##PrefabHierarchySearch", "Search prefab nodes...", ref state.SearchText, 256);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Filter nodes by name or hierarchy path.");

        bool searchActive = !string.IsNullOrWhiteSpace(state.SearchText);
        string hierarchyLabel = searchActive
            ? $"Hierarchy ({CountVisibleNodes(prefab.RootNode!, state.SearchText)}/{CountNodes(prefab.RootNode!)} visible)"
            : $"Hierarchy ({CountNodes(prefab.RootNode!)} nodes)";
        ImGui.SeparatorText(hierarchyLabel);
        if (ImGui.BeginChild("PrefabHierarchyTree", new Vector2(-1.0f, treeHeight), ImGuiChildFlags.Border))
            DrawHierarchyNode(prefab, prefab.RootNode!, state, depth: 0);
        ImGui.EndChild();

        ImGui.SeparatorText("Node Inspector");
        if (ImGui.BeginChild("PrefabHierarchyInspector", Vector2.Zero, ImGuiChildFlags.Border))
        {
            if (state.SelectedNode is null)
            {
                ImGui.TextDisabled("Select a node from the prefab hierarchy to inspect it.");
            }
            else
            {
                if (IsReadOnly)
                    ImGui.BeginDisabled();
                EditorImGuiUI.DrawSceneNodeInspectorInline(state.SelectedNode);
                if (IsReadOnly)
                    ImGui.EndDisabled();
            }
        }
        ImGui.EndChild();
    }

    private static void DrawHierarchyNode(XRPrefabSource prefab, SceneNode node, EditorState state, int depth)
    {
        if (!ShouldDrawNode(node, state.SearchText))
            return;

        int childCount = node.Transform.Children.Count;
        bool isRenaming = ReferenceEquals(state.RenamingNode, node);
        ImGuiTreeNodeFlags flags = ImGuiTreeNodeFlags.SpanAvailWidth | ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.OpenOnDoubleClick;
        if (depth == 0)
            flags |= ImGuiTreeNodeFlags.DefaultOpen;
        if (ReferenceEquals(state.SelectedNode, node))
            flags |= ImGuiTreeNodeFlags.Selected;
        if (childCount == 0)
            flags |= ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.Bullet | ImGuiTreeNodeFlags.NoTreePushOnOpen;

        string label = childCount > 0
            ? $"{GetNodeDisplayName(node)} ({childCount})"
            : GetNodeDisplayName(node);

        if (!string.IsNullOrWhiteSpace(state.SearchText) && NodeOrDescendantMatches(node, state.SearchText))
            ImGui.SetNextItemOpen(true, ImGuiCond.Once);

        ImGui.PushID(node.ID.ToString());
        bool open = ImGui.TreeNodeEx("##PrefabNode", flags, label);
        if (!isRenaming && ImGui.IsItemClicked(ImGuiMouseButton.Left))
            state.SelectedNode = node;

        if (!IsReadOnly)
            ImGui.OpenPopupOnItemClick("PrefabNodeContext", ImGuiPopupFlags.MouseButtonRight);

        if (!IsReadOnly && ImGui.BeginDragDropSource(ImGuiDragDropFlags.SourceAllowNullID))
        {
            SetPrefabNodePayload(node);
            ImGui.TextUnformatted(label);
            ImGui.EndDragDropSource();
        }

        if (!IsReadOnly && ImGui.BeginDragDropTarget())
        {
            if (AcceptPrefabNodePayload() is SceneNode droppedNode
                && !ReferenceEquals(droppedNode, node)
                && !ContainsNode(droppedNode, node)
                && ContainsNode(prefab.RootNode!, droppedNode))
            {
                ReparentPrefabNode(prefab, droppedNode, node.Transform, state);
            }

            ImGui.EndDragDropTarget();
        }

        ImGui.SameLine();
        if (isRenaming)
        {
            if (state.RenameFocusRequested)
            {
                ImGui.SetKeyboardFocusHere();
                state.RenameFocusRequested = false;
            }

            ImGui.SetNextItemWidth(-1.0f);
            bool submitted = ImGui.InputText("##RenamePrefabNode", state.RenameBuffer, (uint)state.RenameBuffer.Length,
                ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.AutoSelectAll);
            bool cancel = ImGui.IsKeyPressed(ImGuiKey.Escape);
            bool lostFocus = ImGui.IsItemDeactivated();
            if (cancel)
                CancelRename(state);
            else if (submitted || lostFocus)
                ApplyRename(state);
        }
        else
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(label);
        }

        if (!IsReadOnly && ImGui.BeginPopup("PrefabNodeContext"))
        {
            bool canDuplicate = !ReferenceEquals(prefab.RootNode, node);
            bool canDelete = !ReferenceEquals(prefab.RootNode, node);
            if (ImGui.MenuItem("Rename"))
                BeginRename(state, node);

            if (ImGui.MenuItem("Add Child Scene Node"))
                CreateChildPrefabNode(node, state);

            if (!canDuplicate)
                ImGui.BeginDisabled();
            if (ImGui.MenuItem("Duplicate"))
                DuplicatePrefabNode(node, state);
            if (!canDuplicate)
                ImGui.EndDisabled();

            if (!canDelete)
                ImGui.BeginDisabled();
            if (ImGui.MenuItem("Delete"))
                DeletePrefabNode(prefab, node, state);
            if (!canDelete)
                ImGui.EndDisabled();

            ImGui.EndPopup();
        }

        if (childCount > 0 && open)
        {
            var children = node.Transform.Children;
            for (int i = 0; i < children.Count; i++)
            {
                if (children[i]?.SceneNode is SceneNode childNode)
                    DrawHierarchyNode(prefab, childNode, state, depth + 1);
            }

            ImGui.TreePop();
        }

        ImGui.PopID();
    }

    private static void DrawRawAssetSection(XRPrefabSource prefab, HashSet<object> visitedObjects)
    {
        if (!ImGui.CollapsingHeader("Raw Prefab Asset", ImGuiTreeNodeFlags.None))
            return;

        ImGui.PushID("PrefabSourceRawAsset");
        if (IsReadOnly)
            ImGui.BeginDisabled();
        EditorImGuiUI.DrawDefaultAssetInspector(prefab, visitedObjects);
        if (IsReadOnly)
            ImGui.EndDisabled();
        ImGui.PopID();
    }

    private static void EnsureSelectedNode(XRPrefabSource prefab, EditorState state)
    {
        if (prefab.RootNode is null)
        {
            state.SelectedNode = null;
            CancelRename(state);
            return;
        }

        if (state.SelectedNode is null || !ContainsNode(prefab.RootNode, state.SelectedNode))
            state.SelectedNode = prefab.RootNode;

        if (state.RenamingNode is not null && !ContainsNode(prefab.RootNode, state.RenamingNode))
            CancelRename(state);
    }

    private static bool ContainsNode(SceneNode root, SceneNode candidate)
    {
        if (ReferenceEquals(root, candidate))
            return true;

        var children = root.Transform.Children;
        for (int i = 0; i < children.Count; i++)
        {
            if (children[i]?.SceneNode is SceneNode childNode && ContainsNode(childNode, candidate))
                return true;
        }

        return false;
    }

    private static int CountNodes(SceneNode root)
    {
        int count = 0;
        foreach (SceneNode _ in SceneNodePrefabUtility.EnumerateHierarchy(root))
            count++;
        return count;
    }

    private static int CountVisibleNodes(SceneNode root, string? searchText)
    {
        if (string.IsNullOrWhiteSpace(searchText))
            return CountNodes(root);

        int count = 0;
        foreach (SceneNode node in SceneNodePrefabUtility.EnumerateHierarchy(root))
        {
            if (ShouldDrawNode(node, searchText))
                count++;
        }

        return count;
    }

    private static bool ShouldDrawNode(SceneNode node, string? searchText)
    {
        if (string.IsNullOrWhiteSpace(searchText))
            return true;

        return NodeMatchesSearch(node, searchText) || NodeHasMatchingDescendant(node, searchText);
    }

    private static bool NodeHasMatchingDescendant(SceneNode node, string searchText)
    {
        var children = node.Transform.Children;
        for (int i = 0; i < children.Count; i++)
        {
            if (children[i]?.SceneNode is not SceneNode childNode)
                continue;

            if (NodeMatchesSearch(childNode, searchText) || NodeHasMatchingDescendant(childNode, searchText))
                return true;
        }

        return false;
    }

    private static bool NodeOrDescendantMatches(SceneNode node, string searchText)
        => NodeMatchesSearch(node, searchText) || NodeHasMatchingDescendant(node, searchText);

    private static bool NodeMatchesSearch(SceneNode node, string searchText)
    {
        string trimmed = searchText.Trim();
        if (trimmed.Length == 0)
            return true;

        string name = node.Name ?? string.Empty;
        if (name.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
            return true;

        string path = node.GetPath();
        return path.Contains(trimmed, StringComparison.OrdinalIgnoreCase);
    }

    private static void BeginRename(EditorState state, SceneNode node)
    {
        state.RenamingNode = node;
        state.RenameFocusRequested = true;
        PopulateRenameBuffer(state.RenameBuffer, node.Name ?? string.Empty);
        state.SelectedNode = node;
    }

    private static void ApplyRename(EditorState state)
    {
        if (state.RenamingNode is null)
            return;

        string newName = ExtractRenameBuffer(state.RenameBuffer);
        if (string.IsNullOrWhiteSpace(newName))
            newName = SceneNode.DefaultName;

        using var _ = Undo.TrackChange("Rename Prefab Node", state.RenamingNode);
        state.RenamingNode.Name = newName;
        CancelRename(state);
    }

    private static void CancelRename(EditorState state)
    {
        state.RenamingNode = null;
        state.RenameFocusRequested = false;
        Array.Clear(state.RenameBuffer, 0, state.RenameBuffer.Length);
    }

    private static void PopulateRenameBuffer(byte[] buffer, string source)
    {
        Array.Clear(buffer, 0, buffer.Length);
        if (string.IsNullOrEmpty(source))
            return;

        int written = Encoding.UTF8.GetBytes(source, 0, source.Length, buffer, 0);
        if (written < buffer.Length)
            buffer[written] = 0;
    }

    private static string ExtractRenameBuffer(byte[] buffer)
    {
        int length = Array.IndexOf(buffer, (byte)0);
        if (length < 0)
            length = buffer.Length;

        return Encoding.UTF8.GetString(buffer, 0, length).Trim();
    }

    private static void CreateChildPrefabNode(SceneNode parent, EditorState state)
    {
        SceneNode child = new(parent);
        Undo.TrackSceneNode(child);

        using var interaction = Undo.BeginUserInteraction();
        using var scope = Undo.BeginChange("Create Prefab Child Node");
        Undo.RecordStructuralChange("Create Prefab Child Node",
            undoAction: () =>
            {
                parent.Transform.RemoveChild(child.Transform, EParentAssignmentMode.Immediate);
                child.IsActiveSelf = false;
            },
            redoAction: () =>
            {
                child.Transform.SetParent(parent.Transform, false, EParentAssignmentMode.Immediate);
                child.IsActiveSelf = true;
                Undo.TrackSceneNode(child);
            });

        state.SelectedNode = child;
        BeginRename(state, child);
    }

    private static void DuplicatePrefabNode(SceneNode node, EditorState state)
    {
        SceneNode clone = SceneNodePrefabUtility.CloneHierarchy(node);
        clone.Name = GenerateDuplicateName(node);
        Undo.TrackSceneNode(clone);

        TransformBase? parentTransform = node.Transform.Parent;
        using var interaction = Undo.BeginUserInteraction();
        using var scope = Undo.BeginChange("Duplicate Prefab Node");

        AttachPrefabNode(clone, parentTransform);
        Undo.RecordStructuralChange("Duplicate Prefab Node",
            undoAction: () => DetachPrefabNode(clone, parentTransform),
            redoAction: () =>
            {
                AttachPrefabNode(clone, parentTransform);
                Undo.TrackSceneNode(clone);
            });

        state.SelectedNode = clone;
    }

    private static void DeletePrefabNode(XRPrefabSource prefab, SceneNode node, EditorState state)
    {
        if (ReferenceEquals(prefab.RootNode, node))
            return;

        TransformBase? parentTransform = node.Transform.Parent;
        if (parentTransform is null)
            return;

        bool wasActive = node.IsActiveSelf;
        using var interaction = Undo.BeginUserInteraction();
        using var scope = Undo.BeginChange($"Delete {GetNodeDisplayName(node)}");
        parentTransform.RemoveChild(node.Transform, EParentAssignmentMode.Immediate);
        node.IsActiveSelf = false;

        Undo.RecordStructuralChange($"Delete {GetNodeDisplayName(node)}",
            undoAction: () =>
            {
                node.Transform.SetParent(parentTransform, false, EParentAssignmentMode.Immediate);
                node.IsActiveSelf = wasActive;
                Undo.TrackSceneNode(node);
            },
            redoAction: () =>
            {
                parentTransform.RemoveChild(node.Transform, EParentAssignmentMode.Immediate);
                node.IsActiveSelf = false;
            });

        if (ReferenceEquals(state.SelectedNode, node) || ContainsNode(node, state.SelectedNode!))
            state.SelectedNode = parentTransform.SceneNode;
        if (ReferenceEquals(state.RenamingNode, node) || ContainsNode(node, state.RenamingNode!))
            CancelRename(state);
    }

    private static void ReparentPrefabNode(XRPrefabSource prefab, SceneNode droppedNode, TransformBase newParent, EditorState state)
    {
        var draggedTransform = droppedNode.Transform;
        TransformBase? oldParent = draggedTransform.Parent;
        if (ReferenceEquals(oldParent, newParent))
            return;

        bool wasRoot = oldParent is null && ReferenceEquals(prefab.RootNode, droppedNode);
        if (wasRoot)
            return;

        using var interaction = Undo.BeginUserInteraction();
        using var scope = Undo.BeginChange($"Reparent {GetNodeDisplayName(droppedNode)}");

        draggedTransform.SetParent(newParent, true, EParentAssignmentMode.Immediate);
        Undo.RecordStructuralChange($"Reparent {GetNodeDisplayName(droppedNode)}",
            undoAction: () => draggedTransform.SetParent(oldParent, true, EParentAssignmentMode.Immediate),
            redoAction: () => draggedTransform.SetParent(newParent, true, EParentAssignmentMode.Immediate));

        state.SelectedNode = droppedNode;
    }

    private static void AttachPrefabNode(SceneNode node, TransformBase? parentTransform)
    {
        if (parentTransform is not null)
            node.Transform.SetParent(parentTransform, false, EParentAssignmentMode.Immediate);
    }

    private static void DetachPrefabNode(SceneNode node, TransformBase? parentTransform)
    {
        if (parentTransform is not null)
            parentTransform.RemoveChild(node.Transform, EParentAssignmentMode.Immediate);
    }

    private static string GenerateDuplicateName(SceneNode node)
    {
        string baseName = GetNodeDisplayName(node);
        const string suffix = " Copy";
        if (baseName.EndsWith(suffix, StringComparison.Ordinal))
            return baseName + " 2";

        return baseName + suffix;
    }

    private static void SetPrefabNodePayload(SceneNode node)
    {
        string idText = node.ID.ToString("N") + '\0';
        byte[] bytes = Encoding.UTF8.GetBytes(idText);
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            ImGui.SetDragDropPayload(PrefabNodePayloadType, handle.AddrOfPinnedObject(), (uint)bytes.Length);
        }
        finally
        {
            if (handle.IsAllocated)
                handle.Free();
        }
    }

    private static SceneNode? AcceptPrefabNodePayload()
    {
        var payload = ImGui.AcceptDragDropPayload(PrefabNodePayloadType);
        if (payload.Data == IntPtr.Zero || payload.DataSize == 0)
            return null;

        try
        {
            string? guidText = Marshal.PtrToStringUTF8(payload.Data, (int)payload.DataSize);
            if (string.IsNullOrWhiteSpace(guidText))
                return null;

            guidText = guidText.TrimEnd('\0');
            if (!Guid.TryParse(guidText, out Guid id))
                return null;

            return XREngine.Data.Core.XRObjectBase.ObjectsCache.TryGetValue(id, out var obj) ? obj as SceneNode : null;
        }
        catch
        {
            return null;
        }
    }

    private static string GetNodeDisplayName(SceneNode node)
        => string.IsNullOrWhiteSpace(node.Name) ? SceneNode.DefaultName : node.Name!;

    private static void DrawSummaryRow(string label, string value)
    {
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.TextUnformatted(label);
        ImGui.TableSetColumnIndex(1);
        ImGui.TextUnformatted(value);
    }

    private sealed class EditorState
    {
        public SceneNode? SelectedNode;
        public SceneNode? RenamingNode;
        public bool RenameFocusRequested;
        public string SearchText = string.Empty;
        public byte[] RenameBuffer { get; } = new byte[RenameBufferSize];
    }

    private readonly struct ReadOnlyScope : IDisposable
    {
        public ReadOnlyScope()
            => _readOnlyScopeDepth++;

        public void Dispose()
            => _readOnlyScopeDepth--;
    }
}