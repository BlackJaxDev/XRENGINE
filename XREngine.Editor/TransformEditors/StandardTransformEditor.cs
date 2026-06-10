using System;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;
using XREngine.Animation;
using XREngine.Scene.Transforms;
using XREngine.Editor;
using static XREngine.Editor.EditorImGuiUI;

namespace XREngine.Editor.TransformEditors;

public sealed class StandardTransformEditor : IXRTransformEditor
{
    private static readonly string[] OrderNames = Enum.GetNames<ETransformOrder>();
    private static readonly ETransformOrder[] OrderValues = Enum.GetValues<ETransformOrder>();
    private const string PreciseFloatFormat = "%.9f";
    private const ImGuiSliderFlags PreciseDragFlags = ImGuiSliderFlags.NoRoundToFormat;

    public void DrawInspector(TransformBase transform, HashSet<object> visited)
    {
        if (transform is not Transform standard)
        {
            DrawDefaultTransformInspector(transform, visited);
            return;
        }

        string transformLabel = TransformEditorUtil.GetTransformDisplayName(standard);
        DrawTranslation(standard, transformLabel);
        DrawRotation(standard, transformLabel);
        DrawScale(standard, transformLabel);
        DrawTransformOrder(standard, transformLabel);
    }

    private static void DrawTranslation(Transform standard, string transformLabel)
    {
        Vector3 translation = standard.Translation;
        ImGui.SetNextItemWidth(-1f);
        bool edited = ImGui.DragFloat3("Translation##TransformTranslation", ref translation, 0.05f, 0.0f, 0.0f, PreciseFloatFormat, PreciseDragFlags);
        ImGuiUndoHelper.TrackDragUndo($"Move {transformLabel}", standard);
        if (!edited)
            return;

        // Mutate + recalc on the update thread only (via the queue). Mutating on the render
        // thread here would make the queued set below a no-op (value already equal, so the
        // setter early-outs and never marks the transform dirty) and would race the update
        // thread, producing the "jumps randomly or not at all" symptom for editor-camera edits.
        var queued = translation;
        EnqueueSceneEdit(() =>
        {
            standard.Translation = queued;
            ApplyInspectorTransformEdit(standard);
        });
    }

    private static void DrawRotation(Transform standard, string transformLabel)
    {
        var rotator = standard.Rotator;
        Vector3 rotation = rotator.PitchYawRoll;
        ImGui.SetNextItemWidth(-1f);
        bool edited = ImGui.DragFloat3("Rotation (Pitch/Yaw/Roll)##TransformRotation", ref rotation, 0.5f, 0.0f, 0.0f, PreciseFloatFormat, PreciseDragFlags);
        ImGuiUndoHelper.TrackDragUndo($"Rotate {transformLabel}", standard);
        if (!edited)
            return;

        rotator.Pitch = rotation.X;
        rotator.Yaw = rotation.Y;
        rotator.Roll = rotation.Z;
        var queued = rotator;
        EnqueueSceneEdit(() =>
        {
            standard.Rotator = queued;
            ApplyInspectorTransformEdit(standard);
        });
    }

    private static void DrawScale(Transform standard, string transformLabel)
    {
        Vector3 scale = standard.Scale;
        ImGui.SetNextItemWidth(-1f);
        bool edited = ImGui.DragFloat3("Scale##TransformScale", ref scale, 0.05f, 0.0f, 0.0f, PreciseFloatFormat, PreciseDragFlags);
        ImGuiUndoHelper.TrackDragUndo($"Scale {transformLabel}", standard);
        if (!edited)
            return;

        var queued = scale;
        EnqueueSceneEdit(() =>
        {
            standard.Scale = queued;
            ApplyInspectorTransformEdit(standard);
        });
    }

    private static void DrawTransformOrder(Transform standard, string transformLabel)
    {
        var order = standard.Order;
        int selectedIndex = Array.IndexOf(OrderValues, order);
        if (selectedIndex < 0)
            selectedIndex = 0;

        int orderIndex = selectedIndex;
        bool changed = ImGui.Combo("Order##TransformOrder", ref orderIndex, OrderNames, OrderNames.Length);
        ImGuiUndoHelper.TrackDragUndo($"Change Transform Order {transformLabel}", standard);
        if (!changed)
            return;

        if (orderIndex < 0 || orderIndex >= OrderValues.Length)
            return;

        var newOrder = OrderValues[orderIndex];
        EnqueueSceneEdit(() =>
        {
            standard.Order = newOrder;
            ApplyInspectorTransformEdit(standard);
        });
    }

    // The Editor View camera lives in a separate editor-only world instance (its
    // TransformBase.World is non-null but distinct from the simulated scene world). A discrete
    // inspector edit pushed only through the deferred double-buffer (RecalcWorld -> enqueue ->
    // PostUpdate swap -> render consume) can land "randomly or not at all", so the camera's
    // cached view-projection matrix is never invalidated and the view stays frozen until later
    // input (a right-click drag) forces another recalc. Forcing setRenderMatrixNow makes
    // SetRenderMatrix run on this same update-thread tick, which fires RenderMatrixChanged
    // immediately and invalidates the camera VP - frame-accurate with no swap-chain dependency.
    // This runs on the update thread (queued via EnqueueSceneEdit), not the render thread, and
    // only on a discrete user edit (not a per-frame hot path), so the momentary render-thread
    // read race the pawn movement path avoids is not a concern here.
    private static void ApplyInspectorTransformEdit(Transform standard)
        => standard.RecalculateMatrices(forceWorldRecalc: true, setRenderMatrixNow: true);
}
