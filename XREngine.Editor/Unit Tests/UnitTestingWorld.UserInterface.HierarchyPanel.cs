using ImGuiNET;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using XREngine;
using XREngine.Data.Core;
using XREngine.Scene;
using XREngine.Scene.Transforms;
using XREngine.Rendering;

namespace XREngine.Editor;

public static partial class UnitTestingWorld
{
    public static partial class UserInterface
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
            ImGui.End();
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

            ImGui.Text($"GameMode: {world.GameMode?.GetType().Name ?? "<none>"}");
            ImGui.Separator();

            var targetWorld = world.TargetWorld;
            bool drewAnySection = false;

            if (targetWorld?.Scenes.Count > 0)
            {
                var sceneSnapshot = targetWorld.Scenes.ToArray();
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

            if (!drewAnySection)
                ImGui.Text("World has no root nodes.");
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
                Selection.SceneNode = node;
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
            if (node == _nodePendingAddComponent)
                _nodePendingAddComponent = null;

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
            bool open = ImGui.CollapsingHeader(headerLabel, ImGuiTreeNodeFlags.DefaultOpen);
            if (ImGui.IsItemHovered() && !string.IsNullOrWhiteSpace(scene.FilePath))
                ImGui.SetTooltip(scene.FilePath);

            ImGui.SameLine();
            bool visible = scene.IsVisible;
            if (ImGui.Checkbox("Visible##SceneVisible", ref visible))
                ToggleSceneVisibility(scene, world, visible);

            ImGui.SameLine();
            if (ImGui.SmallButton("Unload"))
                UnloadSceneFromWorld(scene, world);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Unload this scene from the active world");

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

            var unassigned = new List<SceneNode>();
            foreach (var root in world.RootNodes)
            {
                if (root is null)
                    continue;
                if (!assigned.Contains(root))
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
    }
}
