using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
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
    private static bool _linkScaleAxes = true;
    private const string PreciseFloatFormat = "%.9g";
    private const ImGuiSliderFlags PreciseDragFlags = ImGuiSliderFlags.NoRoundToFormat;
    private static readonly Vector4 XAxisColor = new(0.92f, 0.40f, 0.40f, 1.0f);
    private static readonly Vector4 YAxisColor = new(0.42f, 0.82f, 0.48f, 1.0f);
    private static readonly Vector4 ZAxisColor = new(0.42f, 0.62f, 0.95f, 1.0f);
    private static readonly ConditionalWeakTable<Transform, TransformInspectorState> StateByTransform = new();

    private sealed class TransformInspectorState
    {
        public Vector3 ScaleGestureStart;
        public Vector3 ScaleGestureValue;
        public uint ScaleGestureItemId;
        public int LastScaleFrame;
        public string? Name { get; private set; }
        public string Move { get; private set; } = string.Empty;
        public string Rotate { get; private set; } = string.Empty;
        public string Scale { get; private set; } = string.Empty;
        public string Order { get; private set; } = string.Empty;

        public void Update(string name)
        {
            if (string.Equals(Name, name, StringComparison.Ordinal))
                return;
            Name = name;
            Move = $"Move {name}";
            Rotate = $"Rotate {name}";
            Scale = $"Scale {name}";
            Order = $"Change Transform Order {name}";
        }
    }

    public void DrawInspector(TransformBase transform, HashSet<object> visited)
    {
        if (transform is not Transform standard)
        {
            DrawDefaultTransformInspector(transform, visited);
            return;
        }

        var labels = StateByTransform.GetValue(standard, static _ => new TransformInspectorState());
        labels.Update(TransformEditorUtil.GetTransformDisplayName(standard));
        DrawTransformTable(standard, labels);
        DrawTransformOrder(standard, labels);
    }

    /// <summary>Shares one fixed-weight axis layout across all transform rows, independent of prior frame widths.</summary>
    private static void DrawTransformTable(Transform standard, TransformInspectorState labels)
    {
        const ImGuiTableFlags tableFlags = ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.NoSavedSettings;
        if (!ImGui.BeginTable("TransformAxes", 4, tableFlags))
            return;

        float labelWidth = MathF.Max(ImGui.CalcTextSize("Translation").X,
            ImGui.CalcTextSize("Scale").X + ImGui.GetStyle().ItemInnerSpacing.X + ImGui.GetFrameHeight());
        ImGui.TableSetupColumn("##Label", ImGuiTableColumnFlags.WidthFixed, labelWidth);
        // Explicit weights prevent fill-cell inputs from feeding their previous widths back into auto-sizing.
        ImGui.TableSetupColumn("X", ImGuiTableColumnFlags.WidthStretch, 1.0f);
        ImGui.TableSetupColumn("Y", ImGuiTableColumnFlags.WidthStretch, 1.0f);
        ImGui.TableSetupColumn("Z", ImGuiTableColumnFlags.WidthStretch, 1.0f);
        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
        DrawAxisHeader(1, "X", XAxisColor);
        DrawAxisHeader(2, "Y", YAxisColor);
        DrawAxisHeader(3, "Z", ZAxisColor);

        DrawTranslation(standard, labels);
        DrawRotation(standard, labels);
        DrawScale(standard, labels);
        ImGui.EndTable();
    }

    private static void DrawAxisHeader(int column, string label, Vector4 color)
    {
        ImGui.TableSetColumnIndex(column);
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        ImGui.TableHeader(label);
        ImGui.PopStyleColor();
    }

    private static void DrawTranslation(Transform standard, TransformInspectorState labels)
    {
        Vector3 translation = standard.Translation;
        bool edited = DrawVector3Row("Translation", "TransformTranslation", ref translation, 0.05f, labels.Move, standard);
        if (!edited)
            return;
        var queued = translation;
        EnqueueSceneEdit(() =>
        {
            standard.Translation = queued;
            ApplyInspectorTransformEdit(standard);
        });
    }

    private static void DrawRotation(Transform standard, TransformInspectorState labels)
    {
        var rotator = standard.Rotator;
        Vector3 rotation = rotator.PitchYawRoll;
        bool edited = DrawVector3Row("Rotation", "TransformRotation", ref rotation, 0.5f, labels.Rotate, standard, "Pitch, yaw, and roll in degrees.");
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

    private static void DrawScale(Transform standard, TransformInspectorState state)
    {
        int frame = ImGui.GetFrameCount();
        if (state.LastScaleFrame != frame - 1)
            state.ScaleGestureItemId = 0;
        state.LastScaleFrame = frame;

        // The scene edit is queued; show the latest gesture value even before the scene consumes it.
        Vector3 scale = state.ScaleGestureItemId != 0 ? state.ScaleGestureValue : standard.Scale;
        BeginTransformRow("Scale", "TransformScale");
        ImGui.SameLine(0.0f, ImGui.GetStyle().ItemInnerSpacing.X);
        ImGui.Checkbox("##LinkScaleAxes", ref _linkScaleAxes);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Link scale axes: preserve their proportions.\nZero axes edit independently; an all-zero scale grows uniformly.");

        ImGui.TableSetColumnIndex(1);
        bool edited = DrawScaleAxis("##X", 0, ref scale, standard, state);
        ImGui.TableSetColumnIndex(2);
        edited |= DrawScaleAxis("##Y", 1, ref scale, standard, state);
        ImGui.TableSetColumnIndex(3);
        edited |= DrawScaleAxis("##Z", 2, ref scale, standard, state);
        ImGui.PopID();
        if (!edited)
            return;
        var queued = scale;
        EnqueueSceneEdit(() =>
        {
            standard.Scale = queued;
            ApplyInspectorTransformEdit(standard);
        });
    }

    private static bool DrawScaleAxis(string id, int axis, ref Vector3 scale, Transform standard, TransformInspectorState state)
    {
        float value = scale[axis];
        ImGui.SetNextItemWidth(MathF.Max(1.0f, ImGui.GetContentRegionAvail().X));
        bool edited = ImGui.DragFloat(id, ref value, 0.05f, 0.0f, 0.0f, PreciseFloatFormat, PreciseDragFlags);
        uint itemId = ImGui.GetItemID();
        bool activated = ImGui.IsItemActivated();
        bool active = ImGui.IsItemActive();
        ImGuiUndoHelper.TrackDragUndo(state.Scale, standard);
        if (activated)
        {
            state.ScaleGestureStart = scale;
            state.ScaleGestureValue = scale;
            state.ScaleGestureItemId = itemId;
        }

        if (edited)
        {
            Vector3 basis = state.ScaleGestureItemId == itemId ? state.ScaleGestureStart : scale;
            edited = TrySetScaleAxis(basis, scale, axis, value, _linkScaleAxes, out Vector3 candidate);
            if (edited)
            {
                scale = candidate;
                state.ScaleGestureValue = candidate;
            }
        }

        if (!active && state.ScaleGestureItemId == itemId)
            state.ScaleGestureItemId = 0;
        return edited;
    }

    /// <summary>Preserves signed scale ratios from the gesture start, including a drag through zero.</summary>
    private static bool TrySetScaleAxis(Vector3 basis, Vector3 current, int axis, float value, bool linked, out Vector3 result)
    {
        result = current;
        if (!float.IsFinite(value))
            return false;

        if (!linked || basis[axis] == 0.0f)
        {
            // A zero source axis has no ratio. Fully collapsed scales can still recover uniformly.
            result = linked && basis == Vector3.Zero ? new Vector3(value) : current;
            result[axis] = value;
            return true;
        }

        double ratio = value / (double)basis[axis];
        Vector3 candidate = new((float)(basis.X * ratio), (float)(basis.Y * ratio), (float)(basis.Z * ratio));
        candidate[axis] = value;
        if (!float.IsFinite(candidate.X) || !float.IsFinite(candidate.Y) || !float.IsFinite(candidate.Z))
            return false;

        result = candidate;
        return true;
    }
    private static void DrawTransformOrder(Transform standard, TransformInspectorState labels)
    {
        var order = standard.Order;
        int orderIndex = Math.Max(0, Array.IndexOf(OrderValues, order));
        bool changed = ImGui.Combo("Order##TransformOrder", ref orderIndex, OrderNames, OrderNames.Length);
        ImGuiUndoHelper.TrackDragUndo(labels.Order, standard);
        if (!changed || orderIndex < 0 || orderIndex >= OrderValues.Length)
            return;
        var newOrder = OrderValues[orderIndex];
        EnqueueSceneEdit(() =>
        {
            standard.Order = newOrder;
            ApplyInspectorTransformEdit(standard);
        });
    }

    private static bool DrawVector3Row(string label, string id, ref Vector3 value, float speed, string undoCaption, Transform undoTarget, string? tooltip = null)
    {
        BeginTransformRow(label, id, tooltip);
        ImGui.TableSetColumnIndex(1);
        bool edited = DrawAxisDrag("##X", ref value.X, speed, undoCaption, undoTarget, tooltip);
        ImGui.TableSetColumnIndex(2);
        edited |= DrawAxisDrag("##Y", ref value.Y, speed, undoCaption, undoTarget, tooltip);
        ImGui.TableSetColumnIndex(3);
        edited |= DrawAxisDrag("##Z", ref value.Z, speed, undoCaption, undoTarget, tooltip);
        ImGui.PopID();
        return edited;
    }

    private static void BeginTransformRow(string label, string id, string? tooltip = null)
    {
        ImGui.PushID(id);
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(label);
        if (!string.IsNullOrWhiteSpace(tooltip) && ImGui.IsItemHovered())
            ImGui.SetTooltip(tooltip);
    }
    private static bool DrawAxisDrag(string id, ref float value, float speed, string undoCaption, Transform undoTarget, string? tooltip)
    {
        ImGui.SetNextItemWidth(MathF.Max(1.0f, ImGui.GetContentRegionAvail().X));
        bool edited = ImGui.DragFloat(id, ref value, speed, 0.0f, 0.0f, PreciseFloatFormat, PreciseDragFlags);
        ImGuiUndoHelper.TrackDragUndo(undoCaption, undoTarget);
        if (!string.IsNullOrWhiteSpace(tooltip) && ImGui.IsItemHovered())
            ImGui.SetTooltip(tooltip);
        return edited;
    }

    private static void ApplyInspectorTransformEdit(Transform standard)
        => standard.RecalculateMatrices(forceWorldRecalc: true, setRenderMatrixNow: true);
}