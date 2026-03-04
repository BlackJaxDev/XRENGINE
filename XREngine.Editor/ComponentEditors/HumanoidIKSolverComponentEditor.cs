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

public sealed class HumanoidIKSolverComponentEditor : IXRComponentEditor
{
    private static readonly Vector4 ActiveColor = new(0.40f, 0.85f, 0.40f, 1.00f);
    private static readonly Vector4 InactiveColor = new(0.80f, 0.80f, 0.80f, 0.60f);
    private static readonly Vector4 WarningColor = new(0.90f, 0.70f, 0.30f, 1.00f);

    public void DrawInspector(XRComponent component, HashSet<object> visited)
    {
        if (component is not HumanoidIKSolverComponent solver)
        {
            DrawDefaultComponentInspector(component, visited);
            ComponentEditorLayout.DrawActivePreviewDialog();
            return;
        }

        if (!ComponentEditorLayout.DrawInspectorModeToggle(solver, visited, "IK Solver Editor"))
        {
            ComponentEditorLayout.DrawActivePreviewDialog();
            return;
        }

        DrawStatusSection(solver);
        ImGui.SeparatorText("Actions");
        DrawActionButtons(solver);
        ImGui.SeparatorText("Limb Solvers");
        DrawLimbSolversSection(solver);
        ImGui.SeparatorText("Spine Solver");
        DrawSpineSolverSection(solver);
        ImGui.SeparatorText("Hips Constraint");
        DrawHipsConstraintSection(solver);

        ComponentEditorLayout.DrawActivePreviewDialog();
    }

    // ── Status ──────────────────────────────────────────────────────

    private static void DrawStatusSection(HumanoidIKSolverComponent solver)
    {
        bool initialized = false;
        try { initialized = solver.Limbs.Length > 0 && solver.IKSolvers.Length > 0; }
        catch { /* not initialized yet */ }

        ImGui.TextColored(initialized ? ActiveColor : WarningColor,
            initialized ? "Solver Initialized" : "Solver Not Initialized");

        var humanoid = solver.Humanoid;
        if (humanoid is null)
        {
            ImGui.TextColored(WarningColor, "No HumanoidComponent found on this node.");
            return;
        }

        ImGui.TextDisabled($"Humanoid: {humanoid.SceneNode?.Name ?? "<unknown>"}");
        ImGui.TextDisabled($"IK Policy: {humanoid.Settings.IKGoalPolicy}  Calibrated: {humanoid.Settings.IsIKCalibrated}");
    }

    // ── Actions ─────────────────────────────────────────────────────

    private static void DrawActionButtons(HumanoidIKSolverComponent solver)
    {
        if (ImGui.Button("Initialize Chains"))
        {
            var humanoid = solver.Humanoid;
            if (humanoid is not null)
                EnqueueSceneEdit(() => solver.InitializeChains(humanoid));
        }

        ImGui.SameLine();
        if (ImGui.Button("Configure Anim Goals"))
            EnqueueSceneEdit(() => solver.ConfigureForAnimationDrivenGoals());

        ImGui.SameLine();
        if (ImGui.Button("Set Defaults"))
            EnqueueSceneEdit(() => solver.SetToDefaults());

        ImGui.SameLine();
        if (ImGui.Button("Clear Anim Goals"))
            EnqueueSceneEdit(() => solver.ClearAnimatedIKGoals());

        ImGui.Spacing();
    }

    // ── Limb Solvers ────────────────────────────────────────────────

    private static void DrawLimbSolversSection(HumanoidIKSolverComponent solver)
    {
        DrawLimbSolver("Left Hand", solver._leftHand, solver);
        DrawLimbSolver("Right Hand", solver._rightHand, solver);
        DrawLimbSolver("Left Foot", solver._leftFoot, solver);
        DrawLimbSolver("Right Foot", solver._rightFoot, solver);
    }

    private static void DrawLimbSolver(string label, IKSolverLimb limb, HumanoidIKSolverComponent solver)
    {
        bool open = ImGui.TreeNodeEx(label, ImGuiTreeNodeFlags.DefaultOpen);
        if (!open)
            return;

        ImGui.PushID(label);

        // Goal type
        ImGui.TextDisabled($"Goal: {limb._goal}");

        // Position weight
        float posWeight = limb.IKPositionWeight;
        ImGui.SetNextItemWidth(160f);
        if (ImGui.SliderFloat("Position Weight", ref posWeight, 0f, 1f, "%.3f"))
        {
            using var _ = Undo.TrackChange($"{label} Pos Weight", solver);
            limb.IKPositionWeight = posWeight;
        }
        ImGuiUndoHelper.TrackDragUndo($"{label} Pos Weight", solver);

        // Rotation weight
        float rotWeight = limb.IKRotationWeight;
        ImGui.SetNextItemWidth(160f);
        if (ImGui.SliderFloat("Rotation Weight", ref rotWeight, 0f, 1f, "%.3f"))
        {
            using var _ = Undo.TrackChange($"{label} Rot Weight", solver);
            limb.IKRotationWeight = rotWeight;
        }
        ImGuiUndoHelper.TrackDragUndo($"{label} Rot Weight", solver);

        // IK Position (raw)
        Vector3 ikPos = limb.RawIKPosition;
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.DragFloat3("IK Position", ref ikPos, 0.01f))
        {
            using var _ = Undo.TrackChange($"{label} IK Pos", solver);
            limb.RawIKPosition = ikPos;
        }
        ImGuiUndoHelper.TrackDragUndo($"{label} IK Pos", solver);

        // IK Rotation (raw, as euler for display)
        Quaternion ikRot = limb.RawIKRotation;
        Vector3 euler = QuaternionToEulerDeg(ikRot);
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.DragFloat3("IK Rotation", ref euler, 0.5f))
        {
            using var _ = Undo.TrackChange($"{label} IK Rot", solver);
            limb.RawIKRotation = EulerDegToQuaternion(euler);
        }
        ImGuiUndoHelper.TrackDragUndo($"{label} IK Rot", solver);

        // Bend modifier
        var bendModNames = Enum.GetNames<ELimbBendModifier>();
        int bendModIndex = (int)limb._bendModifier;
        ImGui.SetNextItemWidth(160f);
        if (ImGui.Combo("Bend Modifier", ref bendModIndex, bendModNames, bendModNames.Length))
        {
            using var _ = Undo.TrackChange($"{label} Bend Modifier", solver);
            limb._bendModifier = (ELimbBendModifier)bendModIndex;
        }

        // Bend modifier weight
        float bendWeight = limb._bendModifierWeight;
        ImGui.SetNextItemWidth(160f);
        if (ImGui.SliderFloat("Bend Modifier Weight", ref bendWeight, 0f, 1f, "%.3f"))
        {
            using var _ = Undo.TrackChange($"{label} Bend Weight", solver);
            limb._bendModifierWeight = bendWeight;
        }
        ImGuiUndoHelper.TrackDragUndo($"{label} Bend Weight", solver);

        // Maintain rotation weight
        float maintainRot = limb._maintainRotationWeight;
        ImGui.SetNextItemWidth(160f);
        if (ImGui.SliderFloat("Maintain Rotation Weight", ref maintainRot, 0f, 1f, "%.3f"))
        {
            using var _ = Undo.TrackChange($"{label} Maintain Rot", solver);
            limb._maintainRotationWeight = maintainRot;
        }
        ImGuiUndoHelper.TrackDragUndo($"{label} Maintain Rot", solver);

        // Bend normal
        Vector3 bendNormal = limb.BendNormal;
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.DragFloat3("Bend Normal", ref bendNormal, 0.01f))
        {
            using var _ = Undo.TrackChange($"{label} Bend Normal", solver);
            limb.BendNormal = bendNormal;
        }
        ImGuiUndoHelper.TrackDragUndo($"{label} Bend Normal", solver);

        // Target transform
        string targetName = limb.TargetIKTransform?.SceneNode?.Name ?? "<none>";
        ImGui.TextDisabled($"Target: {targetName}");

        // Bend goal transform
        string bendGoalName = limb._bendGoal?.SceneNode?.Name ?? "<none>";
        ImGui.TextDisabled($"Bend Goal: {bendGoalName}");

        // Bone chain info
        if (ImGui.TreeNode("Bone Chain"))
        {
            DrawBoneInfo("Bone 1 (Upper)", limb._bone1._transform);
            DrawBoneInfo("Bone 2 (Mid)", limb._bone2._transform);
            DrawBoneInfo("Bone 3 (End)", limb._bone3._transform);
            ImGui.TreePop();
        }

        ImGui.PopID();
        ImGui.TreePop();
    }

    // ── Spine Solver ────────────────────────────────────────────────

    private static void DrawSpineSolverSection(HumanoidIKSolverComponent solver)
    {
        var spine = solver._spine;

        float spineWeight = spine.IKPositionWeight;
        ImGui.SetNextItemWidth(160f);
        if (ImGui.SliderFloat("Spine Weight", ref spineWeight, 0f, 1f, "%.3f"))
        {
            using var _ = Undo.TrackChange("Spine Weight", solver);
            spine.IKPositionWeight = spineWeight;
        }
        ImGuiUndoHelper.TrackDragUndo("Spine Weight", solver);

        Vector3 spinePos = spine.RawIKPosition;
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.DragFloat3("Spine IK Position", ref spinePos, 0.01f))
        {
            using var _ = Undo.TrackChange("Spine IK Pos", solver);
            spine.RawIKPosition = spinePos;
        }
        ImGuiUndoHelper.TrackDragUndo("Spine IK Pos", solver);

        int maxIter = spine._maxIterations;
        ImGui.SetNextItemWidth(100f);
        if (ImGui.DragInt("Max Iterations", ref maxIter, 1f, 1, 50))
        {
            using var _ = Undo.TrackChange("Spine Max Iter", solver);
            spine._maxIterations = maxIter;
        }
        ImGuiUndoHelper.TrackDragUndo("Spine Max Iter", solver);

        float tolerance = spine._tolerance;
        ImGui.SetNextItemWidth(100f);
        if (ImGui.DragFloat("Tolerance", ref tolerance, 0.001f, 0f, 1f, "%.4f"))
        {
            using var _ = Undo.TrackChange("Spine Tolerance", solver);
            spine._tolerance = tolerance;
        }
        ImGuiUndoHelper.TrackDragUndo("Spine Tolerance", solver);

        bool useRotLimits = spine._useRotationLimits;
        if (ImGui.Checkbox("Use Rotation Limits", ref useRotLimits))
        {
            using var _ = Undo.TrackChange("Spine Rot Limits", solver);
            spine._useRotationLimits = useRotLimits;
        }
    }

    // ── Hips Constraint ─────────────────────────────────────────────

    private static void DrawHipsConstraintSection(HumanoidIKSolverComponent solver)
    {
        var hips = solver._hips;

        string hipsName = hips.Transform?.SceneNode?.Name ?? "<none>";
        ImGui.TextDisabled($"Hips Transform: {hipsName}");

        string targetName = hips.Target?.SceneNode?.Name ?? "<none>";
        ImGui.TextDisabled($"Target: {targetName}");

        float posWeight = hips.PositionWeight;
        ImGui.SetNextItemWidth(160f);
        if (ImGui.SliderFloat("Position Weight##Hips", ref posWeight, 0f, 1f, "%.3f"))
        {
            using var _ = Undo.TrackChange("Hips Pos Weight", solver);
            hips.PositionWeight = posWeight;
        }
        ImGuiUndoHelper.TrackDragUndo("Hips Pos Weight", solver);

        float rotWeight = hips.RotationWeight;
        ImGui.SetNextItemWidth(160f);
        if (ImGui.SliderFloat("Rotation Weight##Hips", ref rotWeight, 0f, 1f, "%.3f"))
        {
            using var _ = Undo.TrackChange("Hips Rot Weight", solver);
            hips.RotationWeight = rotWeight;
        }
        ImGuiUndoHelper.TrackDragUndo("Hips Rot Weight", solver);

        Vector3 posOffset = hips.PositionOffset;
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.DragFloat3("Position Offset##Hips", ref posOffset, 0.01f))
        {
            using var _ = Undo.TrackChange("Hips Pos Offset", solver);
            hips.PositionOffset = posOffset;
        }
        ImGuiUndoHelper.TrackDragUndo("Hips Pos Offset", solver);

        Vector3 rotOffset = hips.RotationOffset;
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.DragFloat3("Rotation Offset##Hips", ref rotOffset, 0.5f))
        {
            using var _ = Undo.TrackChange("Hips Rot Offset", solver);
            hips.RotationOffset = rotOffset;
        }
        ImGuiUndoHelper.TrackDragUndo("Hips Rot Offset", solver);
    }

    // ── Helpers ─────────────────────────────────────────────────────

    private static void DrawBoneInfo(string label, TransformBase? transform)
    {
        string name = transform?.SceneNode?.Name ?? "<unassigned>";
        Vector4 color = transform is null ? new(0.90f, 0.40f, 0.40f, 1.00f) : new(0.60f, 0.85f, 0.60f, 1.00f);
        ImGui.TextColored(color, $"  {label}: {name}");
    }

    private static Vector3 QuaternionToEulerDeg(Quaternion q)
    {
        // Convert quaternion to Euler angles in degrees (YXZ order).
        float sinX = 2f * (q.W * q.X - q.Y * q.Z);
        sinX = Math.Clamp(sinX, -1f, 1f);
        float pitch = MathF.Asin(sinX);

        float sinYCosX = 2f * (q.W * q.Y + q.X * q.Z);
        float cosYCosX = 1f - 2f * (q.X * q.X + q.Y * q.Y);
        float yaw = MathF.Atan2(sinYCosX, cosYCosX);

        float sinZCosX = 2f * (q.W * q.Z + q.X * q.Y);
        float cosZCosX = 1f - 2f * (q.X * q.X + q.Z * q.Z);
        float roll = MathF.Atan2(sinZCosX, cosZCosX);

        const float radToDeg = 180f / MathF.PI;
        return new Vector3(pitch * radToDeg, yaw * radToDeg, roll * radToDeg);
    }

    private static Quaternion EulerDegToQuaternion(Vector3 euler)
    {
        const float degToRad = MathF.PI / 180f;
        return Quaternion.CreateFromYawPitchRoll(
            euler.Y * degToRad,
            euler.X * degToRad,
            euler.Z * degToRad);
    }
}
