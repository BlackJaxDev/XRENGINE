using ImGuiNET;
using System;
using System.Linq;
using System.Numerics;
using System.Text;
using XREngine;
using XREngine.Data.Core;
using XREngine.Scene;
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
            var world = Engine.Rendering.State.RenderingWorld ?? Engine.WorldInstances.FirstOrDefault();
            if (world is null)
            {
                ImGui.Text("No world instance available.");
                return;
            }

            if (world.RootNodes.Count == 0)
            {
                ImGui.Text("World has no root nodes.");
                return;
            }

            ImGui.Text($"GameMode: {world.GameMode?.GetType().Name ?? "<none>"}");
            ImGui.Separator();

            ImGuiTableFlags tableFlags = ImGuiTableFlags.RowBg
                                        | ImGuiTableFlags.ScrollY
                                        | ImGuiTableFlags.Resizable
                                        | ImGuiTableFlags.SizingStretchProp
                                        | ImGuiTableFlags.BordersInnerV;

            Vector2 tableSize = ImGui.GetContentRegionAvail();
            ImGui.PushStyleVar(ImGuiStyleVar.IndentSpacing, 14.0f);
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(4.0f, 2.0f));
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(4.0f, 2.0f));
            ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, new Vector2(4.0f, 2.0f));
            ImGui.SetWindowFontScale(0.95f);
            if (ImGui.BeginTable("HierarchyTree", 2, tableFlags, tableSize))
            {
                ImGui.TableSetupColumn("Node", ImGuiTableColumnFlags.WidthStretch, 1.0f);
                ImGui.TableSetupColumn("Active", ImGuiTableColumnFlags.WidthFixed, 72.0f);
                ImGui.TableSetupScrollFreeze(0, 1);
                ImGui.TableHeadersRow();

                var rootSnapshot = world.RootNodes.ToArray();
                foreach (var root in rootSnapshot)
                    DrawSceneNodeTree(root, world);

                ImGui.EndTable();
            }
            ImGui.SetWindowFontScale(1.0f);
            ImGui.PopStyleVar(4);
        }

        private static void DrawSceneNodeTree(SceneNode node, XRWorldInstance world)
        {
            var transform = node.Transform;
            int childCount = transform.Children.Count;
            string nodeLabel = node.Name ?? "<unnamed>";
            if (childCount > 0)
                nodeLabel += $" ({childCount})";
            ImGuiTreeNodeFlags flags = childCount > 0
                ? ImGuiTreeNodeFlags.DefaultOpen
                : ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.Bullet | ImGuiTreeNodeFlags.NoTreePushOnOpen;

            bool nodeOpen = DrawSceneNodeEntry(node, world, nodeLabel, flags);

            if (childCount > 0 && nodeOpen)
            {
                var childSnapshot = transform.Children.ToArray();
                foreach (var child in childSnapshot)
                {
                    if (child?.SceneNode is SceneNode childNode)
                        DrawSceneNodeTree(childNode, world);
                }
                ImGui.TableSetColumnIndex(0);
                ImGui.TreePop();
            }
        }

        private static bool DrawSceneNodeEntry(SceneNode node, XRWorldInstance world, string displayLabel, ImGuiTreeNodeFlags flags)
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
                node.IsActiveSelf = activeSelf;
            ImGui.OpenPopupOnItemClick("Context", ImGuiPopupFlags.MouseButtonRight);

            if (ImGui.BeginPopup("Context"))
            {
                if (ImGui.MenuItem("Rename"))
                    BeginHierarchyNodeRename(node);

                if (ImGui.MenuItem("Delete"))
                    DeleteHierarchyNode(node, world);

                if (ImGui.MenuItem("Add Child Scene Node"))
                    CreateChildSceneNode(node);

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

        private static void DeleteHierarchyNode(SceneNode node, XRWorldInstance world)
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
                FinalizeSceneNodeDeletion(node);
            }
            else
            {
                world.RootNodes.Remove(node);
                FinalizeSceneNodeDeletion(node);
            }
        }

        private static void FinalizeSceneNodeDeletion(SceneNode node)
        {
            node.IsActiveSelf = false;
        }

        private static void CreateChildSceneNode(SceneNode parent)
        {
            SceneNode child = new(parent);
            BeginHierarchyNodeRename(child);
        }

        private static void ApplyHierarchyNodeRename()
        {
            if (_nodePendingRename is null)
                return;

            string newName = ExtractStringFromRenameBuffer();
            if (string.IsNullOrWhiteSpace(newName))
                newName = SceneNode.DefaultName;

            _nodePendingRename.Name = newName;
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
    }
}
