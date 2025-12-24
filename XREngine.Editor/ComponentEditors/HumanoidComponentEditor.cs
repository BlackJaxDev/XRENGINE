using System;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;
using XREngine.Components;
using XREngine.Components.Animation;
using XREngine.Editor;
using XREngine.Scene;
using XREngine.Scene.Transforms;
using static XREngine.Editor.EditorImGuiUI;

namespace XREngine.Editor.ComponentEditors;

public sealed class HumanoidComponentEditor : IXRComponentEditor
{
    private static readonly Vector4 MissingColor = new(0.90f, 0.40f, 0.40f, 1.00f);
    private static readonly Vector4 AssignedColor = new(0.60f, 0.85f, 0.60f, 1.00f);

    public void DrawInspector(XRComponent component, HashSet<object> visited)
    {
        if (component is not HumanoidComponent humanoid)
        {
            DrawDefaultComponentInspector(component, visited);
            ComponentEditorLayout.DrawActivePreviewDialog();
            return;
        }

        if (!ComponentEditorLayout.DrawInspectorModeToggle(humanoid, visited, "Humanoid Editor"))
        {
            ComponentEditorLayout.DrawActivePreviewDialog();
            return;
        }

        DrawActionButtons(humanoid);
        ImGui.SeparatorText("General");
        DrawGeneralSection(humanoid);
        ImGui.SeparatorText("IK Chains");
        DrawIkSection(humanoid);
        ImGui.SeparatorText("Targets");
        DrawTargetSection(humanoid);
        ImGui.SeparatorText("Bone Mapping");
        DrawBoneMappingSection(humanoid);

        ComponentEditorLayout.DrawActivePreviewDialog();
    }

    private static void DrawActionButtons(HumanoidComponent humanoid)
    {
        if (ImGui.Button("Reset Bind Pose"))
            RunSceneEdit(humanoid.ResetAllTransformsToBindPose);

        ImGui.SameLine();
        if (ImGui.Button("Reset Pose"))
            RunSceneEdit(humanoid.ResetPose);

        ImGui.SameLine();
        if (ImGui.Button("Auto Detect Rig"))
            RunSceneEdit(humanoid.SetFromNode);

        ImGui.SameLine();
        if (ImGui.Button("Clear IK Targets"))
            RunSceneEdit(humanoid.ClearIKTargets);

        ImGui.Spacing();
    }

    private static void DrawGeneralSection(HumanoidComponent humanoid)
    {
        bool solveIk = humanoid.SolveIK;
        if (ImGui.Checkbox("Solve IK", ref solveIk))
            humanoid.SolveIK = solveIk;

        bool debugVisibility = humanoid.RenderInfo.IsVisible;
        if (ImGui.Checkbox("Show Debug Skeleton", ref debugVisibility))
            humanoid.RenderInfo.IsVisible = debugVisibility;
    }

    private static void DrawIkSection(HumanoidComponent humanoid)
    {
        if (!ImGui.BeginTable("HumanoidIkToggles", 3, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg))
            return;

        ImGui.TableSetupColumn("Chain", ImGuiTableColumnFlags.WidthStretch, 0.4f);
        ImGui.TableSetupColumn("Left", ImGuiTableColumnFlags.WidthStretch, 0.3f);
        ImGui.TableSetupColumn("Right", ImGuiTableColumnFlags.WidthStretch, 0.3f);
        ImGui.TableHeadersRow();

        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.TextUnformatted("Arms");
        ImGui.TableSetColumnIndex(1);
        DrawIkToggle("LeftArm", humanoid.LeftArmIKEnabled, value => humanoid.LeftArmIKEnabled = value);
        ImGui.TableSetColumnIndex(2);
        DrawIkToggle("RightArm", humanoid.RightArmIKEnabled, value => humanoid.RightArmIKEnabled = value);

        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.TextUnformatted("Legs");
        ImGui.TableSetColumnIndex(1);
        DrawIkToggle("LeftLeg", humanoid.LeftLegIKEnabled, value => humanoid.LeftLegIKEnabled = value);
        ImGui.TableSetColumnIndex(2);
        DrawIkToggle("RightLeg", humanoid.RightLegIKEnabled, value => humanoid.RightLegIKEnabled = value);

        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.TextUnformatted("Spine");
        ImGui.TableSetColumnIndex(1);
        DrawIkToggle("HipToHead", humanoid.HipToHeadIKEnabled, value => humanoid.HipToHeadIKEnabled = value);
        ImGui.TableSetColumnIndex(2);
        ImGui.TextDisabled("--");

        ImGui.EndTable();
    }

    private static void DrawIkToggle(string id, bool value, Action<bool> setter)
    {
        ImGui.PushID(id);
        bool toggle = value;
        if (ImGui.Checkbox("##Toggle", ref toggle))
            setter(toggle);
        ImGui.PopID();
    }

    private static void DrawTargetSection(HumanoidComponent humanoid)
    {
        if (!ImGui.BeginTable("HumanoidTargets", 4, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH))
            return;

        ImGui.TableSetupColumn("Target", ImGuiTableColumnFlags.WidthStretch, 0.25f);
        ImGui.TableSetupColumn("Node", ImGuiTableColumnFlags.WidthStretch, 0.30f);
        ImGui.TableSetupColumn("Offset", ImGuiTableColumnFlags.WidthStretch, 0.25f);
        ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthStretch, 0.20f);
        ImGui.TableHeadersRow();

        DrawTargetRow("Head", humanoid.HeadTarget, value => humanoid.HeadTarget = value);
        DrawTargetRow("Chest", humanoid.ChestTarget, value => humanoid.ChestTarget = value);
        DrawTargetRow("Hips", humanoid.HipsTarget, value => humanoid.HipsTarget = value);
        DrawTargetRow("Left Hand", humanoid.LeftHandTarget, value => humanoid.LeftHandTarget = value);
        DrawTargetRow("Right Hand", humanoid.RightHandTarget, value => humanoid.RightHandTarget = value);
        DrawTargetRow("Left Foot", humanoid.LeftFootTarget, value => humanoid.LeftFootTarget = value);
        DrawTargetRow("Right Foot", humanoid.RightFootTarget, value => humanoid.RightFootTarget = value);
        DrawTargetRow("Left Elbow", humanoid.LeftElbowTarget, value => humanoid.LeftElbowTarget = value);
        DrawTargetRow("Right Elbow", humanoid.RightElbowTarget, value => humanoid.RightElbowTarget = value);
        DrawTargetRow("Left Knee", humanoid.LeftKneeTarget, value => humanoid.LeftKneeTarget = value);
        DrawTargetRow("Right Knee", humanoid.RightKneeTarget, value => humanoid.RightKneeTarget = value);

        ImGui.EndTable();
    }

    private static void DrawTargetRow(string label, (TransformBase? tfm, Matrix4x4 offset) target, Action<(TransformBase? tfm, Matrix4x4 offset)> setter)
    {
        ImGui.PushID(label);
        ImGui.TableNextRow();

        ImGui.TableSetColumnIndex(0);
        ImGui.TextUnformatted(label);

        ImGui.TableSetColumnIndex(1);
        string nodeName = target.tfm?.SceneNode?.Name ?? "<unassigned>";
        Vector4 nodeColor = target.tfm is null ? MissingColor : AssignedColor;
        ImGui.TextColored(nodeColor, nodeName);
        Vector2 nodeRectMin = ImGui.GetItemRectMin();
        Vector2 nodeRectMax = ImGui.GetItemRectMax();
        TryHandleSceneNodeDrop(sceneNode =>
        {
            setter((sceneNode.Transform, target.offset));
            return true;
        }, out bool targetPreviewActive);
        if (targetPreviewActive)
        {
            DrawDropHighlight(nodeRectMin, nodeRectMax);
            ImGui.SetTooltip("Drop a scene node to assign this target.");
        }

        ImGui.TableSetColumnIndex(2);
        Vector3 translation = target.offset.Translation;
        ImGui.SetNextItemWidth(-1f);
        bool offsetChanged = ImGui.DragFloat3("##Offset", ref translation, 0.01f);

        ImGui.TableSetColumnIndex(3);
        bool selectionAvailable = Selection.SceneNode?.Transform is not null;
        if (!selectionAvailable)
            ImGui.BeginDisabled();
        if (ImGui.Button("Use Selected"))
        {
            var selectedTransform = Selection.SceneNode?.Transform;
            if (selectedTransform is not null)
                setter((selectedTransform, target.offset));
        }
        if (!selectionAvailable)
            ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Clear"))
            setter((null, Matrix4x4.Identity));

        if (offsetChanged)
        {
            var offset = target.offset;
            offset.Translation = translation;
            setter((target.tfm, offset));
        }

        ImGui.PopID();
    }

    private static void DrawBoneMappingSection(HumanoidComponent humanoid)
    {
        DrawBoneGroup("Core", [("Hips", humanoid.Hips), ("Spine", humanoid.Spine), ("Chest", humanoid.Chest), ("Neck", humanoid.Neck), ("Head", humanoid.Head)]);

        if (ImGui.TreeNodeEx("Eyes", ImGuiTreeNodeFlags.DefaultOpen))
        {
            DrawBoneDef("Eyes Target", humanoid.EyesTarget);
            DrawBoneDef("Left Eye", humanoid.Left.Eye);
            DrawBoneDef("Right Eye", humanoid.Right.Eye);
            ImGui.TreePop();
        }

        DrawBodySide("Left Side", humanoid.Left);
        DrawBodySide("Right Side", humanoid.Right);
    }

    private static void DrawBoneGroup(string label, (string name, HumanoidComponent.BoneDef def)[] bones)
    {
        if (!ImGui.TreeNodeEx(label, ImGuiTreeNodeFlags.DefaultOpen))
            return;

        foreach (var (name, def) in bones)
            DrawBoneDef(name, def);

        ImGui.TreePop();
    }

    private static void DrawBoneDef(string label, HumanoidComponent.BoneDef def)
    {
        ImGui.PushID($"{label}_{def.GetHashCode()}");

        string nodeName = def.Node?.Name ?? "<unassigned>";
        Vector4 color = def.Node is null ? MissingColor : AssignedColor;
        ImGui.TextColored(color, $"{label}: {nodeName}");
        Vector2 labelRectMin = ImGui.GetItemRectMin();
        Vector2 labelRectMax = ImGui.GetItemRectMax();
        TryHandleSceneNodeDrop(sceneNode =>
        {
            var captured = sceneNode;
            RunSceneEdit(() => def.Node = captured);
            return true;
        }, out bool bonePreviewActive);
        if (bonePreviewActive)
        {
            DrawDropHighlight(labelRectMin, labelRectMax);
            ImGui.SetTooltip("Drop a scene node to assign this bone.");
        }

        ImGui.SameLine();
        bool selectionAvailable = Selection.SceneNode is not null;
        if (!selectionAvailable)
            ImGui.BeginDisabled();
        if (ImGui.SmallButton("Use Selected"))
        {
            var selectedNode = Selection.SceneNode;
            if (selectedNode is not null)
            {
                var capturedNode = selectedNode;
                RunSceneEdit(() => def.Node = capturedNode);
            }
        }
        if (!selectionAvailable)
            ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.SmallButton("Clear"))
            RunSceneEdit(() => def.Node = null);

        ImGui.PopID();
    }

    private static void DrawBodySide(string label, HumanoidComponent.BodySide side)
    {
        if (!ImGui.TreeNodeEx(label, ImGuiTreeNodeFlags.DefaultOpen))
            return;

        DrawBoneDef("Shoulder", side.Shoulder);
        DrawBoneDef("Arm", side.Arm);
        DrawBoneDef("Elbow", side.Elbow);
        DrawBoneDef("Wrist", side.Wrist);
        DrawBoneDef("Leg", side.Leg);
        DrawBoneDef("Knee", side.Knee);
        DrawBoneDef("Foot", side.Foot);
        DrawBoneDef("Toes", side.Toes);

        if (ImGui.TreeNodeEx("Fingers", ImGuiTreeNodeFlags.DefaultOpen))
        {
            DrawFinger("Thumb", side.Hand.Thumb);
            DrawFinger("Index", side.Hand.Index);
            DrawFinger("Middle", side.Hand.Middle);
            DrawFinger("Ring", side.Hand.Ring);
            DrawFinger("Pinky", side.Hand.Pinky);
            ImGui.TreePop();
        }

        ImGui.TreePop();
    }

    private static void DrawFinger(string label, HumanoidComponent.BodySide.Fingers.Finger finger)
    {
        if (!ImGui.TreeNodeEx(label, ImGuiTreeNodeFlags.DefaultOpen))
            return;

        DrawBoneDef("Proximal", finger.Proximal);
        DrawBoneDef("Intermediate", finger.Intermediate);
        DrawBoneDef("Distal", finger.Distal);

        ImGui.TreePop();
    }

    private static bool TryHandleSceneNodeDrop(Func<SceneNode, bool> onDrop, out bool previewActive)
    {
        previewActive = false;
        if (!ImGui.BeginDragDropTarget())
            return false;

        bool handled = false;
        if (ImGuiSceneNodeDragDrop.Accept(peekOnly: true) is not null)
            previewActive = true;

        if (ImGuiSceneNodeDragDrop.Accept() is SceneNode dropped && onDrop(dropped))
            handled = true;

        ImGui.EndDragDropTarget();
        return handled;
    }

    private static void DrawDropHighlight(Vector2 min, Vector2 max)
    {
        var drawList = ImGui.GetWindowDrawList();
        Vector4 baseColor = ImGui.GetStyle().Colors[(int)ImGuiCol.DragDropTarget];
        Vector4 fillColor = baseColor;
        fillColor.W *= 0.25f;
        uint fill = ImGui.ColorConvertFloat4ToU32(fillColor);
        uint border = ImGui.ColorConvertFloat4ToU32(baseColor);
        drawList.AddRectFilled(min, max, fill, 4.0f);
        drawList.AddRect(min, max, border, 4.0f, ImDrawFlags.None, 1.5f);
    }

    private static void RunSceneEdit(Action edit)
    {
        if (edit is null)
            return;

        edit();
        EnqueueSceneEdit(edit);
    }
}
