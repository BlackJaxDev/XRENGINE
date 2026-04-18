using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using ImGuiNET;
using XREngine.Editor;
using XREngine.Rendering.UI;
using XREngine.Scene.Transforms;
using static XREngine.Editor.EditorImGuiUI;

namespace XREngine.Editor.TransformEditors;

public sealed class UIBoundableTransformEditor : IXRTransformEditor
{
    private const float LayoutDragSpeed = 0.25f;

    public void DrawInspector(TransformBase transform, HashSet<object> visited)
    {
        if (transform is not UIBoundableTransform ui)
        {
            DrawDefaultTransformInspector(transform, visited);
            return;
        }

        // Draw base UITransform controls first.
        new UITransformEditor().DrawInspector(ui, visited);

        string transformLabel = TransformEditorUtil.GetTransformDisplayName(ui);
        DrawLayout(ui, transformLabel);
        DrawAnchors(ui, transformLabel);
        DrawBoxModel(ui, transformLabel);
        DrawBehavior(ui, transformLabel);
        DrawAdvanced(ui, visited);
    }

    private static void DrawLayout(UIBoundableTransform ui, string transformLabel)
    {
        if (!ImGui.CollapsingHeader("Layout", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        DrawReadOnlyField(
            "Actual Size",
            $"{ui.ActualWidth.ToString("0.##", CultureInfo.InvariantCulture)} x {ui.ActualHeight.ToString("0.##", CultureInfo.InvariantCulture)}");
        DrawReadOnlyField(
            "Actual Position",
            $"{ui.ActualLocalBottomLeftTranslation.X.ToString("0.##", CultureInfo.InvariantCulture)}, {ui.ActualLocalBottomLeftTranslation.Y.ToString("0.##", CultureInfo.InvariantCulture)}");

        DrawOptionalFloat("Width", ui.Width, set => ui.Width = set, $"Set Width {transformLabel}", ui, LayoutDragSpeed);
        DrawOptionalFloat("Height", ui.Height, set => ui.Height = set, $"Set Height {transformLabel}", ui, LayoutDragSpeed);

        DrawOptionalFloat("Min Width", ui.MinWidth, set => ui.MinWidth = set, $"Set Min Width {transformLabel}", ui, LayoutDragSpeed);
        DrawOptionalFloat("Min Height", ui.MinHeight, set => ui.MinHeight = set, $"Set Min Height {transformLabel}", ui, LayoutDragSpeed);
        DrawOptionalFloat("Max Width", ui.MaxWidth, set => ui.MaxWidth = set, $"Set Max Width {transformLabel}", ui, LayoutDragSpeed);
        DrawOptionalFloat("Max Height", ui.MaxHeight, set => ui.MaxHeight = set, $"Set Max Height {transformLabel}", ui, LayoutDragSpeed);

        Vector2 pivot = ui.NormalizedPivot;
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Pivot (normalized)");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(-1f);
        bool pivotEdited = ImGui.DragFloat2("##PivotNormalized", ref pivot, 0.005f, 0f, 1f);
        ImGuiUndoHelper.TrackDragUndo($"Set Pivot {transformLabel}", ui);
        if (pivotEdited)
        {
            pivot = new Vector2(Math.Clamp(pivot.X, 0f, 1f), Math.Clamp(pivot.Y, 0f, 1f));
            ui.NormalizedPivot = pivot;
            var queued = pivot;
            EnqueueSceneEdit(() => ui.NormalizedPivot = queued);
        }

        if (ImGui.Button("Stretch To Parent"))
        {
            ImGuiUndoHelper.TrackDragUndo($"Stretch {transformLabel} To Parent", ui);
            ui.StretchToParent();
            EnqueueSceneEdit(ui.StretchToParent);
        }

        ImGui.Spacing();
    }

    private static void DrawAnchors(UIBoundableTransform ui, string transformLabel)
    {
        if (!ImGui.CollapsingHeader("Anchors", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        Vector2 minAnchor = ui.MinAnchor;
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Min Anchor");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(-1f);
        bool minEdited = ImGui.DragFloat2("##MinAnchor", ref minAnchor, 0.005f, 0f, 1f);
        ImGuiUndoHelper.TrackDragUndo($"Set Min Anchor {transformLabel}", ui);
        if (minEdited)
        {
            minAnchor = new Vector2(Math.Clamp(minAnchor.X, 0f, 1f), Math.Clamp(minAnchor.Y, 0f, 1f));
            ui.MinAnchor = minAnchor;
            var queued = minAnchor;
            EnqueueSceneEdit(() => ui.MinAnchor = queued);
        }

        Vector2 maxAnchor = ui.MaxAnchor;
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Max Anchor");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(-1f);
        bool maxEdited = ImGui.DragFloat2("##MaxAnchor", ref maxAnchor, 0.005f, 0f, 1f);
        ImGuiUndoHelper.TrackDragUndo($"Set Max Anchor {transformLabel}", ui);
        if (maxEdited)
        {
            maxAnchor = new Vector2(Math.Clamp(maxAnchor.X, 0f, 1f), Math.Clamp(maxAnchor.Y, 0f, 1f));
            ui.MaxAnchor = maxAnchor;
            var queued = maxAnchor;
            EnqueueSceneEdit(() => ui.MaxAnchor = queued);
        }

        ImGui.Spacing();
    }

    private static void DrawBoxModel(UIBoundableTransform ui, string transformLabel)
    {
        if (!ImGui.CollapsingHeader("Box Model", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        Vector4 margins = ui.Margins;
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Margins (L,B,R,T)");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(-1f);
        bool marginsEdited = ImGui.DragFloat4("##Margins", ref margins, LayoutDragSpeed);
        ImGuiUndoHelper.TrackDragUndo($"Set Margins {transformLabel}", ui);
        if (marginsEdited)
        {
            ui.Margins = margins;
            var queued = margins;
            EnqueueSceneEdit(() => ui.Margins = queued);
        }

        Vector4 padding = ui.Padding;
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Padding (L,B,R,T)");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(-1f);
        bool paddingEdited = ImGui.DragFloat4("##Padding", ref padding, LayoutDragSpeed);
        ImGuiUndoHelper.TrackDragUndo($"Set Padding {transformLabel}", ui);
        if (paddingEdited)
        {
            ui.Padding = padding;
            var queued = padding;
            EnqueueSceneEdit(() => ui.Padding = queued);
        }

        ImGui.Spacing();
    }

    private static void DrawBehavior(UIBoundableTransform ui, string transformLabel)
    {
        if (!ImGui.CollapsingHeader("Behavior"))
            return;

        DrawCheckbox("Blocks Input Behind", ui.BlocksInputBehind, value => ui.BlocksInputBehind = value, $"Set Input Blocking {transformLabel}", ui);
        DrawCheckbox("Exclude From Auto Width", ui.ExcludeFromParentAutoCalcWidth, value => ui.ExcludeFromParentAutoCalcWidth = value, $"Exclude Width Auto Calc {transformLabel}", ui);
        DrawCheckbox("Exclude From Auto Height", ui.ExcludeFromParentAutoCalcHeight, value => ui.ExcludeFromParentAutoCalcHeight = value, $"Exclude Height Auto Calc {transformLabel}", ui);

        ImGui.Spacing();
    }

    private static void DrawAdvanced(UIBoundableTransform ui, HashSet<object> visited)
    {
        if (ImGui.CollapsingHeader("Advanced"))
            DrawDefaultTransformInspector(ui, visited);
    }
}
