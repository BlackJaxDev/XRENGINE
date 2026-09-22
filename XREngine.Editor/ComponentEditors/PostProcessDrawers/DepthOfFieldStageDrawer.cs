using ImGuiNET;
using System.Numerics;
using XREngine.Components;
using XREngine.Editor.IMGUI;
using XREngine.Editor.Services;
using XREngine.Rendering;
using XREngine.Rendering.PostProcessing;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Editor.ComponentEditors.PostProcessDrawers;

/// <summary>
/// Custom stage drawer for Depth of Field, handling interactive focus target scene-node drag/drop.
/// </summary>
public sealed class DepthOfFieldStageDrawer : IPostProcessStageCustomDrawer
{
    public void DrawStageFooter(PostProcessStageCustomDrawerContext context)
    {
        if (context.StageState.BackingInstance is not DepthOfFieldSettings dof)
            return;

        if (dof.Mode != DepthOfFieldSettings.DepthOfFieldControlMode.TargetTransform)
            return;

        ImGui.Separator();

        TransformBase? current = dof.FocusTarget;
        string label = current != null
            ? (current.SceneNode?.Name ?? current.GetType().Name)
            : "(none — drag a scene node here)";

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Focus Target");
        ImGui.SameLine();

        float availW = ImGui.GetContentRegionAvail().X;
        float clearW = current != null ? ImGui.CalcTextSize("Clear").X + ImGui.GetStyle().FramePadding.X * 2 + ImGui.GetStyle().ItemSpacing.X : 0;
        ImGui.SetNextItemWidth(availW - clearW);
        ImGui.InputText("##focusTarget", ref label, 256, ImGuiInputTextFlags.ReadOnly);

        if (ImGui.BeginDragDropTarget())
        {
            if (ImGuiSceneNodeDragDrop.Accept() is SceneNode dropped)
            {
                using var _ = Undo.TrackChange("Set DoF Focus Target", dof);
                dof.FocusTarget = dropped.Transform;
            }
            ImGui.EndDragDropTarget();
        }

        if (current != null)
        {
            ImGui.SameLine();
            if (ImGui.Button("Clear"))
            {
                using var _ = Undo.TrackChange("Clear DoF Focus Target", dof);
                dof.FocusTarget = null;
            }

            // Show computed distance as read-only info
            Vector3 targetPos = current.WorldTranslation + dof.FocusTargetOffset;
            Vector3 cameraPos = context.Camera.Transform.WorldTranslation;
            float dist = Vector3.Distance(cameraPos, targetPos);
            ImGui.TextDisabled($"Computed Distance: {dist:F2}");
        }
    }
}
