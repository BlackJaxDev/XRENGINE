using System.Globalization;
using ImGuiNET;
using XREngine.Components;

namespace XREngine.Editor.ComponentEditors;

public sealed class UICanvasInputComponentEditor : IXRComponentEditor
{
    public void DrawInspector(XRComponent component, HashSet<object> visited)
    {
        if (component is not UICanvasInputComponent input)
        {
            EditorImGuiUI.DrawDefaultComponentInspector(component, visited);
            ComponentEditorLayout.DrawActivePreviewDialog();
            return;
        }

        if (!ComponentEditorLayout.DrawInspectorModeToggle(input, visited, "Canvas Input"))
        {
            ComponentEditorLayout.DrawActivePreviewDialog();
            return;
        }

        ImGui.PushID(input.GetHashCode());

        DrawOverview(input);
        DrawInputState(input);
        DrawFocusControls(input);

        if (ImGui.CollapsingHeader("Advanced"))
            EditorImGuiUI.DrawDefaultComponentInspector(input, visited);

        ImGui.PopID();
        ComponentEditorLayout.DrawActivePreviewDialog();
    }

    private static void DrawOverview(UICanvasInputComponent input)
    {
        if (!ImGui.CollapsingHeader("Overview", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        EditorImGuiUI.DrawReadOnlyField("Explicit Canvas", DescribeComponent(input.Canvas));
        EditorImGuiUI.DrawReadOnlyField("Resolved Canvas", DescribeComponent(input.GetCameraCanvas()));
        EditorImGuiUI.DrawReadOnlyField("Owning Pawn", DescribeComponent(input.OwningPawn));
        EditorImGuiUI.DrawReadOnlyField("Focused", DescribeComponent(input.FocusedComponent));
        EditorImGuiUI.DrawReadOnlyField("Topmost Interactable", DescribeComponent(input.TopMostInteractable));
        EditorImGuiUI.DrawReadOnlyField("Topmost Element", DescribeComponent(input.TopMostElement));
        EditorImGuiUI.DrawReadOnlyField("Layout Invalidated", input.IsLayoutInvalidated ? "Yes" : "No");

        ImGui.Spacing();
    }

    private static void DrawInputState(UICanvasInputComponent input)
    {
        if (!ImGui.CollapsingHeader("Input State", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        EditorImGuiUI.DrawReadOnlyField("Ctrl", input.IsCtrlHeld ? "Held" : "Up");
        EditorImGuiUI.DrawReadOnlyField("Shift", input.IsShiftHeld ? "Held" : "Up");
        EditorImGuiUI.DrawReadOnlyField("Alt", input.IsAltHeld ? "Held" : "Up");
        EditorImGuiUI.DrawReadOnlyField(
            "Cursor",
            $"{input.CursorPositionWorld2D.X.ToString("0.##", CultureInfo.InvariantCulture)}, {input.CursorPositionWorld2D.Y.ToString("0.##", CultureInfo.InvariantCulture)}");
        EditorImGuiUI.DrawReadOnlyField(
            "Last Cursor",
            $"{input.LastCursorPositionWorld2D.X.ToString("0.##", CultureInfo.InvariantCulture)}, {input.LastCursorPositionWorld2D.Y.ToString("0.##", CultureInfo.InvariantCulture)}");

        ImGui.Spacing();
    }

    private static void DrawFocusControls(UICanvasInputComponent input)
    {
        if (!ImGui.CollapsingHeader("Focus Controls", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        bool canFocusTopmost = input.TopMostInteractable is not null;
        ImGui.BeginDisabled(!canFocusTopmost);
        if (ImGui.Button("Focus Topmost") && input.TopMostInteractable is not null)
        {
            input.FocusedComponent = input.TopMostInteractable;
            EditorImGuiUI.EnqueueSceneEdit(() => input.FocusedComponent = input.TopMostInteractable);
        }
        ImGui.EndDisabled();

        ImGui.SameLine();
        ImGui.BeginDisabled(input.FocusedComponent is null);
        if (ImGui.Button("Clear Focus"))
        {
            input.FocusedComponent = null;
            EditorImGuiUI.EnqueueSceneEdit(() => input.FocusedComponent = null);
        }
        ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button("Invalidate Layout"))
        {
            input.InvalidateLayout();
            EditorImGuiUI.EnqueueSceneEdit(input.InvalidateLayout);
        }

        ImGui.Spacing();
    }

    private static string DescribeComponent(XRComponent? component)
        => component is null
            ? "None"
            : $"{component.SceneNode?.Name ?? "<unnamed>"} ({component.GetType().Name})";
}