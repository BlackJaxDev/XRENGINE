using System;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;
using XREngine.Animation;
using XREngine.Scene.Transforms;
using XREngine.Editor;
using static XREngine.Editor.UnitTestingWorld.UserInterface;

namespace XREngine.Editor.TransformEditors;

public sealed class StandardTransformEditor : IXRTransformEditor
{
    private static readonly string[] OrderNames = Enum.GetNames<ETransformOrder>();
    private static readonly ETransformOrder[] OrderValues = Enum.GetValues<ETransformOrder>();

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
        bool edited = ImGui.DragFloat3("Translation##TransformTranslation", ref translation, 0.05f);
        ImGuiUndoHelper.UpdateScope($"Move {transformLabel}", standard);
        if (!edited)
            return;

        standard.Translation = translation;
        var queued = translation;
        EnqueueSceneEdit(() => standard.Translation = queued);
    }

    private static void DrawRotation(Transform standard, string transformLabel)
    {
        var rotator = standard.Rotator;
        Vector3 rotation = rotator.PitchYawRoll;
        ImGui.SetNextItemWidth(-1f);
        bool edited = ImGui.DragFloat3("Rotation (Pitch/Yaw/Roll)##TransformRotation", ref rotation, 0.5f);
        ImGuiUndoHelper.UpdateScope($"Rotate {transformLabel}", standard);
        if (!edited)
            return;

        rotator.Pitch = rotation.X;
        rotator.Yaw = rotation.Y;
        rotator.Roll = rotation.Z;
        var queued = rotator;
        standard.Rotator = queued;
        EnqueueSceneEdit(() => standard.Rotator = queued);
    }

    private static void DrawScale(Transform standard, string transformLabel)
    {
        Vector3 scale = standard.Scale;
        ImGui.SetNextItemWidth(-1f);
        bool edited = ImGui.DragFloat3("Scale##TransformScale", ref scale, 0.05f);
        ImGuiUndoHelper.UpdateScope($"Scale {transformLabel}", standard);
        if (!edited)
            return;

        standard.Scale = scale;
        var queued = scale;
        EnqueueSceneEdit(() => standard.Scale = queued);
    }

    private static void DrawTransformOrder(Transform standard, string transformLabel)
    {
        var order = standard.Order;
        int selectedIndex = Array.IndexOf(OrderValues, order);
        if (selectedIndex < 0)
            selectedIndex = 0;

        int orderIndex = selectedIndex;
        bool changed = ImGui.Combo("Order##TransformOrder", ref orderIndex, OrderNames, OrderNames.Length);
        ImGuiUndoHelper.UpdateScope($"Change Transform Order {transformLabel}", standard);
        if (!changed)
            return;

        if (orderIndex < 0 || orderIndex >= OrderValues.Length)
            return;

        var newOrder = OrderValues[orderIndex];
        standard.Order = newOrder;
        EnqueueSceneEdit(() => standard.Order = newOrder);
    }
}
