using ImGuiNET;
using System;
using System.Collections.Generic;
using System.Numerics;
using XREngine.Rendering;
using XREngine.Scene;

namespace XREngine.Editor;

public static partial class EditorImGuiUI
{
    /// <summary>
    /// Keeps world identity and settings together without letting long names widen the dock.
    /// </summary>
    private static void DrawWorldHeader(RuntimeWorld world)
    {
        var targetWorld = world.TargetWorld;
        string worldName = targetWorld?.Name ?? "World";
        string? filePath = targetWorld?.FilePath;

        if (ImGui.BeginTable("HierarchyWorldHeader", 2, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.NoSavedSettings))
        {
            ImGui.TableSetupColumn("World", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Settings", ImGuiTableColumnFlags.WidthFixed,
                ImGui.CalcTextSize("Settings").X + ImGui.GetStyle().FramePadding.X * 2.0f);
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(worldName);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(worldName);

            ImGui.TableSetColumnIndex(1);
            ImGui.BeginDisabled(targetWorld is null);
            if (ImGui.Button("Settings##WorldSettings") && targetWorld is not null)
            {
                _showInspector = true;
                SetInspectorStandaloneTarget(targetWorld.Settings, $"World Settings: {worldName}");
            }
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Show world settings in the inspector");
            ImGui.EndTable();
        }

        ImGui.TextDisabled(string.IsNullOrEmpty(filePath) ? "Unsaved world" : "World asset");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(string.IsNullOrEmpty(filePath) ? "This world has not been saved to an asset." : filePath);
        ImGui.Spacing();
    }

    /// <summary>
    /// Presents frequent navigation actions at a stable width; view metadata lives in the menu.
    /// </summary>
    private static void DrawHierarchyToolbar(RuntimeWorld world)
    {
        float spacing = ImGui.GetStyle().ItemSpacing.X;
        float availableWidth = ImGui.GetContentRegionAvail().X;
        float horizontalPadding = ImGui.GetStyle().FramePadding.X * 2.0f;
        float minimumWidth = MathF.Max(ImGui.GetFrameHeight(), ImGui.CalcTextSize("...").X + horizontalPadding);
        int columns = availableWidth >= minimumWidth * 4.0f + spacing * 3.0f ? 4
            : availableWidth >= minimumWidth * 2.0f + spacing ? 2 : 1;
        float buttonWidth = MathF.Max(minimumWidth, (availableWidth - spacing * (columns - 1)) / columns);
        Vector2 buttonSize = new(buttonWidth, 0.0f);

        // Keep IDs stable when labels shorten or controls wrap as the dock is resized.
        bool compactExpand = buttonWidth < ImGui.CalcTextSize("Expand").X + horizontalPadding;
        if (ImGui.Button(compactExpand ? "+###HierarchyExpandAll" : "Expand###HierarchyExpandAll", buttonSize))
            SetHierarchyExpansion(world, true);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Expand all nodes");

        AdvanceHierarchyToolbarColumn(1, columns);
        bool compactCollapse = buttonWidth < ImGui.CalcTextSize("Collapse").X + horizontalPadding;
        if (ImGui.Button(compactCollapse ? "-###HierarchyCollapseAll" : "Collapse###HierarchyCollapseAll", buttonSize))
            SetHierarchyExpansion(world, false);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Collapse all nodes");

        AdvanceHierarchyToolbarColumn(2, columns);
        ImGui.BeginDisabled(Selection.SceneNodes.Length == 0);
        bool compactFocus = buttonWidth < ImGui.CalcTextSize("Focus").X + horizontalPadding;
        if (ImGui.Button(compactFocus ? "F###HierarchyFocusSelected" : "Focus###HierarchyFocusSelected", buttonSize))
            _pendingHierarchyScrollNode = Selection.LastSceneNode;
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Scroll to the selected node");

        AdvanceHierarchyToolbarColumn(3, columns);
        bool compactView = buttonWidth < ImGui.CalcTextSize("View").X + horizontalPadding;
        if (ImGui.Button(compactView ? "...###HierarchyViewOptions" : "View###HierarchyViewOptions", buttonSize))
            ImGui.OpenPopup("HierarchyViewOptions");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Hierarchy display options and world information");

        if (!ImGui.BeginPopup("HierarchyViewOptions"))
            return;

        ImGui.MenuItem("Show Editor Scene", null, ref _showEditorSceneHierarchy);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Include editor-only gizmos, tools and UI in the hierarchy.");
        ImGui.Separator();
        ImGui.TextDisabled("Game mode");
        ImGui.TextUnformatted(world.GameMode?.GetType().Name ?? "None");
        ImGui.EndPopup();
    }

    private static void AdvanceHierarchyToolbarColumn(int index, int columns)
    {
        if (index % columns != 0)
            ImGui.SameLine();
    }

    /// <summary>
    /// Reserves only two compact controls beside the scene name, even in a narrow dock.
    /// </summary>
    private static void DrawSceneHierarchySection(XRScene scene, RuntimeWorld world)
    {
        ImGui.PushID(scene.ID.GetHashCode());
        string sceneName = string.IsNullOrWhiteSpace(scene.Name) ? "Untitled Scene" : scene.Name!;
        bool open = false;
        float controlWidth = ImGui.GetFrameHeight();
        ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, new Vector2(2.0f, 2.0f));
        if (ImGui.BeginTable("SceneHeaderRow", 3, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.NoSavedSettings))
        {
            // Separate cells prevent the collapsing header from stealing control clicks.
            ImGui.TableSetupColumn("Scene", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Visible", ImGuiTableColumnFlags.WidthFixed, controlWidth);
            ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthFixed, controlWidth);
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            open = ImGui.CollapsingHeader(sceneName, ImGuiTreeNodeFlags.DefaultOpen);
            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();
                ImGui.TextUnformatted(sceneName);
                if (scene.IsDirty)
                    ImGui.TextUnformatted("Unsaved scene changes");
                if (!string.IsNullOrWhiteSpace(scene.FilePath))
                    ImGui.TextUnformatted(scene.FilePath);
                ImGui.EndTooltip();
            }
            ImGui.OpenPopupOnItemClick("SceneActions", ImGuiPopupFlags.MouseButtonRight);

            ImGui.TableSetColumnIndex(1);
            bool visible = scene.IsVisible;
            if (ImGui.Checkbox("##SceneVisible", ref visible))
                ToggleSceneVisibility(scene, world, visible);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(visible ? "Hide scene" : "Show scene");

            ImGui.TableSetColumnIndex(2);
            if (DrawHierarchySceneActionsButton(scene.IsDirty, controlWidth))
                ImGui.OpenPopup("SceneActions");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(scene.IsDirty ? "Scene actions (unsaved changes)" : "Scene actions");

            // Popup creation and rendering must share the table's ID scope.
            if (ImGui.BeginPopup("SceneActions"))
            {
                ImGui.TextUnformatted(sceneName);
                ImGui.Separator();
                if (ImGui.MenuItem("Visible", null, scene.IsVisible))
                    ToggleSceneVisibility(scene, world, !scene.IsVisible);
                if (ImGui.MenuItem("Unload Scene"))
                    UnloadSceneFromWorld(scene, world);
                ImGui.EndPopup();
            }
            ImGui.EndTable();
        }
        ImGui.PopStyleVar();

        if (open)
        {
            if (!scene.IsVisible)
                ImGui.TextDisabled("Hidden scene");
            DrawSceneHierarchyNodes(scene.RootNodes, world, scene);
        }
        ImGui.PopID();
    }

    /// <summary>
    /// Draws an ellipsis that remains legible at compact checkbox height without an icon texture.
    /// </summary>
    private static bool DrawHierarchySceneActionsButton(bool dirty, float size)
    {
        bool clicked = ImGui.Button("##SceneActions", new Vector2(size, size));
        Vector2 center = (ImGui.GetItemRectMin() + ImGui.GetItemRectMax()) * 0.5f;
        uint color = dirty
            ? ImGui.ColorConvertFloat4ToU32(new Vector4(0.95f, 0.7f, 0.2f, 1.0f))
            : ImGui.GetColorU32(ImGuiCol.Text);
        var drawList = ImGui.GetWindowDrawList();
        for (int i = -1; i <= 1; i++)
            drawList.AddCircleFilled(center + new Vector2(i * 4.0f, 0.0f), 1.0f, color, 8);
        if (dirty)
            drawList.AddCircleFilled(ImGui.GetItemRectMax() - new Vector2(3.0f, size - 3.0f), 2.0f, color, 8);
        return clicked;
    }

    /// <summary>
    /// Gives node names the remaining width and aligns active controls at the right edge.
    /// </summary>
    private static void DrawSceneHierarchyNodes(IReadOnlyList<SceneNode> roots, RuntimeWorld world, XRScene? owningScene)
    {
        if (roots.Count == 0)
        {
            ImGui.TextDisabled("No nodes in this scene.");
            return;
        }

        ImGui.PushStyleVar(ImGuiStyleVar.IndentSpacing, 14.0f);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(3.0f, 1.0f));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(4.0f, 2.0f));
        ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, new Vector2(4.0f, 2.0f));
        ImGuiTableFlags tableFlags = ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.NoSavedSettings;
        if (ImGui.BeginTable("HierarchyTree", 2, tableFlags))
        {
            ImGui.TableSetupColumn("Node", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Active", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoResize,
                ImGui.GetFrameHeight() + 4.0f);

            // Scene headers already identify each group; repeated table headers only add noise.
            for (int i = 0; i < roots.Count; i++)
            {
                var root = roots[i];
                if (root is not null)
                    DrawSceneNodeTree(root, world, owningScene, depth: 0);
            }
            ImGui.EndTable();
        }
        ImGui.PopStyleVar(4);
    }

    private static void CenterHierarchyActiveCheckbox()
        => ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0.0f, (ImGui.GetContentRegionAvail().X - ImGui.GetFrameHeight()) * 0.5f));

    private static void DrawEditorSceneHierarchy(RuntimeWorld world)
    {
        var editorScene = EditorWorldIntegrationRegistry.GetOrAttach(world).EditorScene;
        if (editorScene is null)
            return;

        ImGui.PushID("__EditorSceneHierarchy__");
        ImGui.TextDisabled("EDITOR ONLY");
        bool open = ImGui.CollapsingHeader("Editor Scene##EditorScene", ImGuiTreeNodeFlags.DefaultOpen);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Editor-only gizmos, tools and UI. Not saved with the world.");
        if (open)
            DrawSceneHierarchyNodes(editorScene.RootNodes, world, editorScene);
        ImGui.PopID();
    }

    private static void DrawUnassignedHierarchy(IReadOnlyList<SceneNode> roots, RuntimeWorld world)
    {
        ImGui.PushID("WorldRootNodes");
        bool open = ImGui.CollapsingHeader("World Root Nodes##WorldRoot", ImGuiTreeNodeFlags.DefaultOpen);
        if (open)
            DrawSceneHierarchyNodes(roots, world, null);
        ImGui.PopID();
    }

    private static bool DrawRuntimeHierarchy(RuntimeWorld world)
    {
        if (world.RootNodes.Count == 0)
            return false;

        ImGui.PushID("RuntimeWorldNodes");
        bool open = ImGui.CollapsingHeader("World Nodes##RuntimeWorld", ImGuiTreeNodeFlags.DefaultOpen);
        if (open)
            DrawSceneHierarchyNodes(world.RootNodes, world, null);
        ImGui.PopID();
        return true;
    }
}
