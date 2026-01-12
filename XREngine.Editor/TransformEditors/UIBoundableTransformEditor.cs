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
    }

    private static void DrawLayout(UIBoundableTransform ui, string transformLabel)
    {
        if (!ImGui.CollapsingHeader("Layout", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        ImGui.TextDisabled($"Actual Size: {ui.ActualWidth.ToString("0.##", CultureInfo.InvariantCulture)} x {ui.ActualHeight.ToString("0.##", CultureInfo.InvariantCulture)}");

        DrawOptionalFloat("Width", ui.Width, set => ui.Width = set, $"Set Width {transformLabel}", ui);
        DrawOptionalFloat("Height", ui.Height, set => ui.Height = set, $"Set Height {transformLabel}", ui);

        DrawOptionalFloat("Min Width", ui.MinWidth, set => ui.MinWidth = set, $"Set Min Width {transformLabel}", ui);
        DrawOptionalFloat("Min Height", ui.MinHeight, set => ui.MinHeight = set, $"Set Min Height {transformLabel}", ui);
        DrawOptionalFloat("Max Width", ui.MaxWidth, set => ui.MaxWidth = set, $"Set Max Width {transformLabel}", ui);
        DrawOptionalFloat("Max Height", ui.MaxHeight, set => ui.MaxHeight = set, $"Set Max Height {transformLabel}", ui);

        Vector2 pivot = ui.NormalizedPivot;
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Pivot (normalized)");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(-1f);
        bool pivotEdited = ImGui.DragFloat2("##PivotNormalized", ref pivot, 0.005f, 0f, 1f);
        ImGuiUndoHelper.UpdateScope($"Set Pivot {transformLabel}", ui);
        if (pivotEdited)
        {
            pivot = new Vector2(Math.Clamp(pivot.X, 0f, 1f), Math.Clamp(pivot.Y, 0f, 1f));
            ui.NormalizedPivot = pivot;
            var queued = pivot;
            EnqueueSceneEdit(() => ui.NormalizedPivot = queued);
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
        ImGuiUndoHelper.UpdateScope($"Set Min Anchor {transformLabel}", ui);
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
        ImGuiUndoHelper.UpdateScope($"Set Max Anchor {transformLabel}", ui);
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
        ImGuiUndoHelper.UpdateScope($"Set Margins {transformLabel}", ui);
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
        ImGuiUndoHelper.UpdateScope($"Set Padding {transformLabel}", ui);
        if (paddingEdited)
        {
            ui.Padding = padding;
            var queued = padding;
            EnqueueSceneEdit(() => ui.Padding = queued);
        }

        ImGui.Spacing();
    }

    private readonly struct ImGuiDisabledScope : IDisposable
    {
        private readonly bool _disabled;

        public ImGuiDisabledScope(bool disabled)
        {
            _disabled = disabled;
            if (_disabled)
                ImGui.BeginDisabled();
        }

        public void Dispose()
        {
            if (_disabled)
                ImGui.EndDisabled();
        }
    }

    private static void DrawOptionalFloat(string label, float? value, Action<float?> setValue, string undoLabel, UIBoundableTransform target)
    {
        ImGui.PushID(label);

        bool enabled = value.HasValue;
        if (ImGui.Checkbox("##Enabled", ref enabled))
        {
            ImGuiUndoHelper.UpdateScope(undoLabel, target);
            float? next = enabled ? 0f : null;
            setValue(next);
            var queued = next;
            EnqueueSceneEdit(() => setValue(queued));
        }
        else
        {
            ImGuiUndoHelper.UpdateScope(undoLabel, target);
        }

        ImGui.SameLine();

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(label);
        ImGui.SameLine();

        float v = value ?? 0f;
        using (new ImGuiDisabledScope(!enabled))
        {
            ImGui.SetNextItemWidth(-1f);
            bool edited = ImGui.DragFloat("##Value", ref v, LayoutDragSpeed);
            ImGuiUndoHelper.UpdateScope(undoLabel, target);
            if (edited)
            {
                float? next = enabled ? v : null;
                setValue(next);
                var queued = next;
                EnqueueSceneEdit(() => setValue(queued));
            }
        }

        ImGui.PopID();
    }
}
