using System.Collections.Generic;
using System.Globalization;
using ImGuiNET;
using XREngine.Rendering;
using XREngine.Rendering.UI;
using XREngine.Scene.Transforms;
using static XREngine.Editor.EditorImGuiUI;

namespace XREngine.Editor.TransformEditors;

public sealed class UICanvasTransformEditor : IXRTransformEditor
{
    public void DrawInspector(TransformBase transform, HashSet<object> visited)
    {
        if (transform is not UICanvasTransform canvas)
        {
            DrawDefaultTransformInspector(transform, visited);
            return;
        }

        new UIBoundableTransformEditor().DrawInspector(canvas, visited);

        string transformLabel = TransformEditorUtil.GetTransformDisplayName(canvas);
        DrawCanvas(canvas, transformLabel);
    }

    private static void DrawCanvas(UICanvasTransform canvas, string transformLabel)
    {
        if (!ImGui.CollapsingHeader("Canvas", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        DrawEnumCombo("Draw Space", canvas.DrawSpace, value => canvas.DrawSpace = value, $"Set Canvas Draw Space {transformLabel}", canvas);

        if (canvas.CameraSpaceCamera is not null || canvas.DrawSpace == ECanvasDrawSpace.Camera)
            DrawReadOnlyField("Camera Space Camera", DescribeCamera(canvas.CameraSpaceCamera));

        if (canvas.DrawSpace == ECanvasDrawSpace.Camera)
            DrawFloat("Camera Distance", canvas.CameraDrawSpaceDistance, 0.05f, value => canvas.CameraDrawSpaceDistance = value, $"Set Canvas Camera Distance {transformLabel}", canvas);

        DrawCheckbox("Use Async Layout", canvas.UseAsyncLayout, value => canvas.UseAsyncLayout = value, $"Toggle Async Layout {transformLabel}", canvas);
        if (canvas.UseAsyncLayout)
            DrawInt("Max Layout Items / Frame", canvas.MaxLayoutItemsPerFrame, 1f, value => canvas.MaxLayoutItemsPerFrame = value, $"Set Async Layout Budget {transformLabel}", canvas);

        DrawReadOnlyField("Layout Invalidated", canvas.IsLayoutInvalidated ? "Yes" : "No");
        DrawReadOnlyField("Layout Updating", canvas.IsUpdatingLayout ? "Yes" : "No");
        DrawReadOnlyField("Nested Canvas", canvas.IsNestedCanvas ? "Yes" : "No");

        var rootBounds = canvas.GetRootCanvasBounds();
        DrawReadOnlyField(
            "Root Bounds",
            $"{rootBounds.Width.ToString("0.##", CultureInfo.InvariantCulture)} x {rootBounds.Height.ToString("0.##", CultureInfo.InvariantCulture)}");

        if (ImGui.Button("Invalidate Layout"))
        {
            ImGuiUndoHelper.TrackDragUndo($"Invalidate Layout {transformLabel}", canvas);
            canvas.InvalidateLayout();
            EnqueueSceneEdit(canvas.InvalidateLayout);
        }

        ImGui.SameLine();
        if (ImGui.Button("Update Layout"))
        {
            canvas.UpdateLayout();
            EnqueueSceneEdit(canvas.UpdateLayout);
        }

        ImGui.SameLine();
        if (ImGui.Button("Update Async"))
        {
            canvas.UpdateLayoutAsync();
            EnqueueSceneEdit(canvas.UpdateLayoutAsync);
        }

        ImGui.Spacing();
    }

    private static string DescribeCamera(XRCamera? camera)
        => camera is null
            ? "None"
            : $"{camera.Transform?.Name ?? "<unnamed>"} ({camera.GetType().Name})";
}