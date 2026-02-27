using System;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;
using MagicPhysX;
using XREngine.Data.Transforms.Rotations;
using XREngine.Rendering.Physics.Physx;
using XREngine.Scene;
using XREngine.Scene.Transforms;
using XREngine.Editor;
using static XREngine.Editor.EditorImGuiUI;

namespace XREngine.Editor.TransformEditors;

public sealed class RigidBodyTransformEditor : IXRTransformEditor
{
    private static readonly string[] InterpolationNames = Enum.GetNames<RigidBodyTransform.EInterpolationMode>();
    private static readonly RigidBodyTransform.EInterpolationMode[] InterpolationValues = Enum.GetValues<RigidBodyTransform.EInterpolationMode>();

    public void DrawInspector(TransformBase transform, HashSet<object> visited)
    {
        if (transform is not RigidBodyTransform rigidBody)
        {
            DrawDefaultTransformInspector(transform, visited);
            return;
        }

        string transformLabel = TransformEditorUtil.GetTransformDisplayName(rigidBody);

        DrawInterpolationMode(rigidBody, transformLabel);
        DrawPositionOffset(rigidBody, transformLabel);
        DrawRotationOffset(rigidBody, transformLabel, true);
        DrawRotationOffset(rigidBody, transformLabel, false);

        ImGui.Separator();
        DrawRigidBodyControls(rigidBody, transformLabel);
        ImGui.Separator();
        DrawPhysicsSummary(rigidBody);
    }

    private static void DrawInterpolationMode(RigidBodyTransform rigidBody, string transformLabel)
    {
        var mode = rigidBody.InterpolationMode;
        int selectedIndex = Array.IndexOf(InterpolationValues, mode);
        if (selectedIndex < 0)
            selectedIndex = 0;

        int modeIndex = selectedIndex;
        bool changed = ImGui.Combo("Interpolation Mode##RigidBodyInterpolation", ref modeIndex, InterpolationNames, InterpolationNames.Length);
        ImGuiUndoHelper.TrackDragUndo($"Change Interpolation Mode {transformLabel}", rigidBody);
        if (!changed)
            return;

        if (modeIndex < 0 || modeIndex >= InterpolationValues.Length)
            return;

        var newMode = InterpolationValues[modeIndex];
        rigidBody.InterpolationMode = newMode;
        EnqueueSceneEdit(() => rigidBody.InterpolationMode = newMode);
    }

    private static void DrawPositionOffset(RigidBodyTransform rigidBody, string transformLabel)
    {
        Vector3 offset = rigidBody.PositionOffset;
        ImGui.SetNextItemWidth(-1f);
        bool edited = ImGui.DragFloat3("Position Offset (World X/Y/Z)##RigidBodyPositionOffset", ref offset, 0.05f);
        ImGuiUndoHelper.TrackDragUndo($"Adjust Position Offset {transformLabel}", rigidBody);
        if (!edited)
            return;

        rigidBody.PositionOffset = offset;
        var queued = offset;
        EnqueueSceneEdit(() => rigidBody.PositionOffset = queued);
    }

    private static void DrawRotationOffset(RigidBodyTransform rigidBody, string transformLabel, bool preRotation)
    {
        Quaternion current = preRotation ? rigidBody.PreRotationOffset : rigidBody.PostRotationOffset;
        Vector3 euler = Rotator.FromQuaternion(current).PitchYawRoll;
        ImGui.SetNextItemWidth(-1f);
        string label = preRotation
            ? "Pre-Rotation Offset (Pitch/Yaw/Roll Degrees)##RigidBodyPreRot"
            : "Post-Rotation Offset (Pitch/Yaw/Roll Degrees)##RigidBodyPostRot";
        bool edited = ImGui.DragFloat3(label, ref euler, 0.5f);
        string action = preRotation ? "Pre-Rotation Offset" : "Post-Rotation Offset";
        ImGuiUndoHelper.TrackDragUndo($"Adjust {action} {transformLabel}", rigidBody);
        if (!edited)
            return;

        Quaternion updated = Rotator.ToQuaternion(euler);
        if (preRotation)
            rigidBody.PreRotationOffset = updated;
        else
            rigidBody.PostRotationOffset = updated;

        var queued = updated;
        if (preRotation)
            EnqueueSceneEdit(() => rigidBody.PreRotationOffset = queued);
        else
            EnqueueSceneEdit(() => rigidBody.PostRotationOffset = queued);
    }

    private static void DrawRigidBodyControls(RigidBodyTransform rigidBody, string transformLabel)
    {
        var actor = rigidBody.RigidBody;
        if (actor is null)
        {
            ImGui.TextDisabled("Rigid body not initialized.");
            return;
        }

        if (actor is not PhysxRigidActor physxActor)
        {
            ImGui.TextDisabled("Rigid body editing requires a PhysX actor.");
            return;
        }

        var (currentPosition, currentRotation) = physxActor.Transform;
        Vector3 editedPosition = currentPosition;
        Vector3 rotationEuler = Rotator.FromQuaternion(currentRotation).PitchYawRoll;
        Quaternion editedRotation = currentRotation;

        float fieldWidth = MathF.Max(140f, ImGui.GetContentRegionAvail().X * 0.55f);
        bool changed = false;
        ImGui.PushItemWidth(fieldWidth);
        if (ImGui.DragFloat3("Body Position##RigidBodyWorldPosition", ref editedPosition, 0.01f))
            changed = true;
        ImGui.PopItemWidth();
        ImGuiUndoHelper.TrackDragUndo($"Adjust Rigid Body Position {transformLabel}", rigidBody);

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("World-space XYZ position");

        ImGui.PushItemWidth(fieldWidth);
        if (ImGui.DragFloat3("Body Rotation (deg)##RigidBodyWorldRotation", ref rotationEuler, 0.1f))
        {
            editedRotation = Rotator.ToQuaternion(rotationEuler);
            changed = true;
        }
        ImGui.PopItemWidth();

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("World-space pitch/yaw/roll (degrees)");

        ImGuiUndoHelper.TrackDragUndo($"Adjust Rigid Body Rotation {transformLabel}", rigidBody);
        if (!changed)
            return;

        var queuedPosition = editedPosition;
        var queuedRotation = editedRotation;
        ApplyRigidBodyTransform(rigidBody, physxActor, queuedPosition, queuedRotation);
        EnqueueSceneEdit(() => ApplyRigidBodyTransform(rigidBody, physxActor, queuedPosition, queuedRotation));
    }

    private static void ApplyRigidBodyTransform(RigidBodyTransform transform, PhysxRigidActor actor, Vector3 position, Quaternion rotation)
    {
        if (actor.IsReleased)
            return;

        transform.SetPositionAndRotation(position, rotation);

        if (actor is PhysxDynamicRigidBody dynamicBody)
        {
            bool isKinematic = dynamicBody.Flags.HasFlag(PxRigidBodyFlags.Kinematic);
            if (isKinematic)
                dynamicBody.KinematicTarget = (position, rotation);
            else
                actor.Transform = (position, rotation);
            return;
        }

        actor.Transform = (position, rotation);
    }

    private static void DrawPhysicsSummary(RigidBodyTransform rigidBody)
    {
        ImGui.TextDisabled($"Rigid Body: {GetRigidBodyLabel(rigidBody.RigidBody)}");
        TransformEditorUtil.DrawReadOnlyVector3("World Position", rigidBody.Position);
        var worldRotation = Rotator.FromQuaternion(rigidBody.Rotation).PitchYawRoll;
        TransformEditorUtil.DrawReadOnlyVector3("World Rotation (Pitch/Yaw/Roll)", worldRotation);

        var (lastPosition, lastRotation) = rigidBody.LastPhysicsTransform;
        TransformEditorUtil.DrawReadOnlyVector3("Last Physics Position", lastPosition);
        var lastRotationEuler = Rotator.FromQuaternion(lastRotation).PitchYawRoll;
        TransformEditorUtil.DrawReadOnlyVector3("Last Physics Rotation (Pitch/Yaw/Roll)", lastRotationEuler);

        TransformEditorUtil.DrawReadOnlyVector3("Linear Velocity", rigidBody.LastPhysicsLinearVelocity);
        TransformEditorUtil.DrawReadOnlyVector3("Angular Velocity", rigidBody.LastPhysicsAngularVelocity);
    }

    private static string GetRigidBodyLabel(IAbstractRigidPhysicsActor? actor)
    {
        return actor is null ? "<none>" : actor.GetType().Name;
    }
}
