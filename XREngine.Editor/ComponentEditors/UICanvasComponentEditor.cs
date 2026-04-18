using System.Globalization;
using ImGuiNET;
using XREngine.Components;
using static XREngine.Editor.EditorImGuiUI;

namespace XREngine.Editor.ComponentEditors;

public sealed class UICanvasComponentEditor : IXRComponentEditor
{
    public void DrawInspector(XRComponent component, HashSet<object> visited)
    {
        if (component is not UICanvasComponent canvas)
        {
            EditorImGuiUI.DrawDefaultComponentInspector(component, visited);
            ComponentEditorLayout.DrawActivePreviewDialog();
            return;
        }

        if (!ComponentEditorLayout.DrawInspectorModeToggle(canvas, visited, "Canvas Controls"))
        {
            ComponentEditorLayout.DrawActivePreviewDialog();
            return;
        }

        ImGui.PushID(canvas.GetHashCode());

        DrawOverview(canvas);
        DrawRendering(canvas);
        DrawLayoutControls(canvas);

        if (ImGui.CollapsingHeader("Advanced"))
            EditorImGuiUI.DrawDefaultComponentInspector(canvas, visited);

        ImGui.PopID();
        ComponentEditorLayout.DrawActivePreviewDialog();
    }

    private static void DrawOverview(UICanvasComponent canvas)
    {
        if (!ImGui.CollapsingHeader("Overview", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        DrawReadOnlyField("Draw Space", canvas.CanvasTransform.DrawSpace.ToString());
        DrawReadOnlyField(
            "Actual Size",
            $"{canvas.CanvasTransform.ActualWidth.ToString("0.##", CultureInfo.InvariantCulture)} x {canvas.CanvasTransform.ActualHeight.ToString("0.##", CultureInfo.InvariantCulture)}");
        DrawReadOnlyField("Input Component", DescribeComponent(canvas.GetInputComponent()));
        DrawReadOnlyField("Offscreen Active", canvas.UseOffscreenRenderingForNonScreenSpaces() ? "Yes" : "No");
        DrawReadOnlyField("Async Layout", canvas.CanvasTransform.UseAsyncLayout ? "Enabled" : "Disabled");

        ImGui.Spacing();
    }

    private static void DrawRendering(UICanvasComponent canvas)
    {
        if (!ImGui.CollapsingHeader("Rendering", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        DrawCheckbox(
            "Prefer Offscreen For World/Camera",
            canvas.PreferOffscreenRenderingForNonScreenSpaces,
            value => canvas.PreferOffscreenRenderingForNonScreenSpaces = value,
            "Toggle Offscreen Rendering Preference",
            canvas);
        DrawCheckbox(
            "Auto Disable For Backdrop Blur",
            canvas.AutoDisableOffscreenForBackdropBlur,
            value => canvas.AutoDisableOffscreenForBackdropBlur = value,
            "Toggle Backdrop Blur Offscreen Fallback",
            canvas);
        DrawCheckbox(
            "Strict 1x1 Draw Calls",
            canvas.StrictOneByOneRenderCalls,
            value => canvas.StrictOneByOneRenderCalls = value,
            "Toggle Strict Render Calls",
            canvas);
        DrawFloat("Near Z", canvas.NearZ, 0.01f, value => canvas.NearZ = value, "Set Canvas Near Z", canvas);
        DrawFloat("Far Z", canvas.FarZ, 0.01f, value => canvas.FarZ = value, "Set Canvas Far Z", canvas);

        ImGui.Spacing();
    }

    private static void DrawLayoutControls(UICanvasComponent canvas)
    {
        if (!ImGui.CollapsingHeader("Layout Controls", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        DrawReadOnlyField("Layout Invalidated", canvas.CanvasTransform.IsLayoutInvalidated ? "Yes" : "No");
        DrawReadOnlyField("Layout Updating", canvas.CanvasTransform.IsUpdatingLayout ? "Yes" : "No");

        if (ImGui.Button("Invalidate Layout"))
        {
            canvas.CanvasTransform.InvalidateLayout();
            EnqueueSceneEdit(canvas.CanvasTransform.InvalidateLayout);
        }

        ImGui.SameLine();
        if (ImGui.Button("Update Layout"))
        {
            canvas.CanvasTransform.UpdateLayout();
            EnqueueSceneEdit(canvas.CanvasTransform.UpdateLayout);
        }

        ImGui.SameLine();
        if (ImGui.Button("Update Async"))
        {
            canvas.CanvasTransform.UpdateLayoutAsync();
            EnqueueSceneEdit(canvas.CanvasTransform.UpdateLayoutAsync);
        }

        ImGui.Spacing();
    }

    private static string DescribeComponent(XRComponent? component)
        => component is null
            ? "None"
            : $"{component.SceneNode?.Name ?? "<unnamed>"} ({component.GetType().Name})";
}