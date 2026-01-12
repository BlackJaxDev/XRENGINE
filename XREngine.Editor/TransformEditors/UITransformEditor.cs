using System;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;
using XREngine.Editor;
using XREngine.Rendering.UI;
using XREngine.Scene.Transforms;
using static XREngine.Editor.EditorImGuiUI;

namespace XREngine.Editor.TransformEditors;

public sealed class UITransformEditor : IXRTransformEditor
{
    public void DrawInspector(TransformBase transform, HashSet<object> visited)
    {
        if (transform is not UITransform ui)
        {
            DrawDefaultTransformInspector(transform, visited);
            return;
        }

        string transformLabel = TransformEditorUtil.GetTransformDisplayName(ui);

        DrawStyling(ui, transformLabel);
        DrawTranslation(ui, transformLabel);
        DrawDepth(ui, transformLabel);
        DrawScale(ui, transformLabel);
        DrawRotation(ui, transformLabel);
    }

    private static void DrawStyling(UITransform ui, string transformLabel)
    {
        if (!ImGui.CollapsingHeader("UI Styling", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        string stylingId = ui.StylingID ?? string.Empty;
        if (ImGui.InputText("Styling ID", ref stylingId, 256u))
        {
            ImGuiUndoHelper.UpdateScope($"Edit Styling ID {transformLabel}", ui);
            ui.StylingID = stylingId;
            var queued = stylingId;
            EnqueueSceneEdit(() => ui.StylingID = queued);
        }
        else
        {
            ImGuiUndoHelper.UpdateScope($"Edit Styling ID {transformLabel}", ui);
        }

        string stylingClass = ui.StylingClass ?? string.Empty;
        if (ImGui.InputText("Styling Class", ref stylingClass, 256u))
        {
            ImGuiUndoHelper.UpdateScope($"Edit Styling Class {transformLabel}", ui);
            ui.StylingClass = stylingClass;
            var queued = stylingClass;
            EnqueueSceneEdit(() => ui.StylingClass = queued);
        }
        else
        {
            ImGuiUndoHelper.UpdateScope($"Edit Styling Class {transformLabel}", ui);
        }

        ImGui.Spacing();
    }

    private static void DrawTranslation(UITransform ui, string transformLabel)
    {
        Vector2 translation = ui.Translation;
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Translation");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(-1f);
        bool edited = ImGui.DragFloat2("##Translation", ref translation, 0.05f);
        ImGuiUndoHelper.UpdateScope($"Move {transformLabel}", ui);
        if (!edited)
            return;

        ui.Translation = translation;
        var queued = translation;
        EnqueueSceneEdit(() => ui.Translation = queued);
    }

    private static void DrawDepth(UITransform ui, string transformLabel)
    {
        float depth = ui.DepthTranslation;
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Depth");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(-1f);
        bool edited = ImGui.DragFloat("##Depth", ref depth, 0.01f);
        ImGuiUndoHelper.UpdateScope($"Adjust Depth {transformLabel}", ui);
        if (!edited)
            return;

        ui.DepthTranslation = depth;
        var queued = depth;
        EnqueueSceneEdit(() => ui.DepthTranslation = queued);
    }

    private static void DrawScale(UITransform ui, string transformLabel)
    {
        Vector3 scale = ui.Scale;
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Scale");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(-1f);
        bool edited = ImGui.DragFloat3("##Scale", ref scale, 0.05f);
        ImGuiUndoHelper.UpdateScope($"Scale {transformLabel}", ui);
        if (!edited)
            return;

        ui.Scale = scale;
        var queued = scale;
        EnqueueSceneEdit(() => ui.Scale = queued);
    }

    private static void DrawRotation(UITransform ui, string transformLabel)
    {
        float rotation = ui.RotationDegrees;
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Rotation (deg)");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(-1f);
        bool edited = ImGui.DragFloat("##RotationDegrees", ref rotation, 0.5f);
        ImGuiUndoHelper.UpdateScope($"Rotate {transformLabel}", ui);
        if (!edited)
            return;

        ui.RotationDegrees = rotation;
        var queued = rotation;
        EnqueueSceneEdit(() => ui.RotationDegrees = queued);
    }
}
