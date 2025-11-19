using System;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;
using XREngine.Data.Transforms.Rotations;
using XREngine.Scene;
using XREngine.Scene.Transforms;
using XREngine.Editor;
using static XREngine.Editor.UnitTestingWorld.UserInterface;

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
        ImGuiUndoHelper.UpdateScope($"Change Interpolation Mode {transformLabel}", rigidBody);
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
        bool edited = ImGui.DragFloat3("Position Offset##RigidBodyPositionOffset", ref offset, 0.05f);
        ImGuiUndoHelper.UpdateScope($"Adjust Position Offset {transformLabel}", rigidBody);
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
            ? "Pre-Rotation Offset (Pitch/Yaw/Roll)##RigidBodyPreRot"
            : "Post-Rotation Offset (Pitch/Yaw/Roll)##RigidBodyPostRot";
        bool edited = ImGui.DragFloat3(label, ref euler, 0.5f);
        string action = preRotation ? "Pre-Rotation Offset" : "Post-Rotation Offset";
        ImGuiUndoHelper.UpdateScope($"Adjust {action} {transformLabel}", rigidBody);
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
