using ImGuiNET;
using System.Linq;
using System.Numerics;
using System.Text;
using XREngine.Rendering;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Editor;

public static partial class EditorImGuiUI
{
    private const float HierarchyFocusCameraDurationSeconds = 0.35f;

    private static void DrawHierarchyPanel()
    {
        if (!_showHierarchy) return;
        if (!ImGui.Begin("Hierarchy", ref _showHierarchy))
        {
            ImGui.End();
            return;
        }

        DrawWorldHierarchyTab();

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
        // Check if mouse was released while dragging a valid payload
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

        // Only process on mouse release
        if (!ImGui.IsMouseReleased(ImGuiMouseButton.Left))
            return;

        string? path = ImGuiAssetUtilities.GetPathFromPayload(payload);
        if (string.IsNullOrWhiteSpace(path))
            return;

        SceneNode? parent = Selection.SceneNode;
        if (TryLoadPrefabAsset(path, out var prefab))
            EnqueueSceneEdit(() => SpawnPrefabNode(world, parent, prefab!));
        else if (TryLoadModelAsset(path, out var model))
            EnqueueSceneEdit(() => SpawnModelNode(world, parent, model!, path));
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
        if (!isRenaming && ImGui.IsItemClicked(ImGuiMouseButton.Left))
            UpdateHierarchySelection(node);
        ImGui.OpenPopupOnItemClick("Context", ImGuiPopupFlags.MouseButtonRight);

        if (ImGui.BeginDragDropSource(ImGuiDragDropFlags.SourceAllowNullID))
        {
            ImGuiSceneNodeDragDrop.SetPayload(node);
            ImGui.TextUnformatted(displayLabel);
            ImGui.EndDragDropSource();
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
            node.IsActiveSelf = activeSelf;
            MarkSceneHierarchyDirty(node, owningScene, world);
        }
        ImGui.OpenPopupOnItemClick("Context", ImGuiPopupFlags.MouseButtonRight);

        if (ImGui.BeginPopup("Context"))
        {
            if (ImGui.MenuItem("Rename"))
                BeginHierarchyNodeRename(node);

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
        if (parentTransform is not null)
        {
            parentTransform.RemoveChild(tfm, true);
        }
        else
        {
            world.RootNodes.Remove(node);
            owningScene?.RootNodes.Remove(node);
        }

        FinalizeSceneNodeDeletion(node);
        MarkSceneHierarchyDirty(node, owningScene, world);
    }

    private static void FinalizeSceneNodeDeletion(SceneNode node)
    {
        node.IsActiveSelf = false;
    }

    private static void CreateChildSceneNode(SceneNode parent, XRScene? owningScene, XRWorldInstance world)
    {
        SceneNode child = new(parent);
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
        if (player?.ControlledPawn is EditorFlyingCameraPawnComponent pawn)
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

        scene.IsVisible = visible;
        scene.MarkDirty();
    }

    private static void UnloadSceneFromWorld(XRScene scene, XRWorldInstance world)
    {
        var targetWorld = world.TargetWorld;
        if (targetWorld is null)
            return;

        scene.IsVisible = false;
        world.UnloadScene(scene);
        targetWorld.Scenes.Remove(scene);
        ClearSelectionForScene(scene, world);
        scene.MarkDirty();
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

    private static void DrawWorldHeader(XRWorldInstance world)
    {
        var targetWorld = world.TargetWorld;
        string worldName = targetWorld?.Name ?? "World";
        string filePath = targetWorld?.FilePath;
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
