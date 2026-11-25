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

        DrawReadOnlyField("Layout", "Bounded worker scheduling");

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
            EnqueueSceneEdit(canvas.InvalidateLayout);
        }

        ImGui.SameLine();
        if (ImGui.Button("Request Layout"))
            EnqueueSceneEdit(canvas.UpdateLayoutAsync);

        ImGui.Spacing();
    }

    private static string DescribeCamera(XRCamera? camera)
        => camera is null
            ? "None"
            : $"{camera.Transform?.Name ?? "<unnamed>"} ({camera.GetType().Name})";
}