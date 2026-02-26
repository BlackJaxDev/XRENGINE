using System;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;
using XREngine.Components;
using XREngine.Components.Physics;
using XREngine.Scene.Physics.Joints;

namespace XREngine.Editor.ComponentEditors;

#region Shared Helpers

internal static class JointEditorShared
{
    /// <summary>
    /// Draws common properties shared by all joint components: anchor setup, break thresholds, flags, and status.
    /// </summary>
    public static void DrawCommonJointProperties(PhysicsJointComponent joint)
    {
        DrawStatus(joint);
        DrawConnectedBody(joint);
        DrawAnchors(joint);
        DrawBreakSettings(joint);
        DrawFlags(joint);
        DrawDebug(joint);
    }

    private static void DrawStatus(PhysicsJointComponent joint)
    {
        if (!ImGui.CollapsingHeader("Status", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        bool connected = joint.NativeJoint is not null;
        Vector4 statusColor = connected
            ? new Vector4(0.2f, 1f, 0.2f, 1f)
            : new Vector4(1f, 0.8f, 0.2f, 1f);
        ImGui.TextColored(statusColor, connected ? "Joint active" : "Joint not created");
        ImGui.TextUnformatted($"Active in hierarchy: {joint.IsActiveInHierarchy}");
    }

    private static void DrawConnectedBody(PhysicsJointComponent joint)
    {
        if (!ImGui.CollapsingHeader("Connected Body", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        string bodyName = joint.ConnectedBody is not null
            ? (joint.ConnectedBody.SceneNode?.Name ?? "(unnamed)")
            : "(world anchor)";
        ImGui.TextUnformatted($"Connected to: {bodyName}");

        if (joint.ConnectedBody is not null && ImGui.Button("Disconnect"))
            joint.ConnectedBody = null;
    }

    private static void DrawAnchors(PhysicsJointComponent joint)
    {
        if (!ImGui.CollapsingHeader("Anchors", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        Vector3 pos = joint.AnchorPosition;
        if (ImGui.DragFloat3("Anchor Position", ref pos, 0.01f))
            joint.AnchorPosition = pos;

        Vector4 rot = QuatToVec4(joint.AnchorRotation);
        if (ImGui.DragFloat4("Anchor Rotation (xyzw)", ref rot, 0.01f))
            joint.AnchorRotation = Vec4ToQuat(rot);

        bool autoConnect = joint.AutoConfigureConnectedAnchor;
        if (ImGui.Checkbox("Auto-Configure Connected Anchor", ref autoConnect))
            joint.AutoConfigureConnectedAnchor = autoConnect;

        if (!autoConnect)
        {
            Vector3 cPos = joint.ConnectedAnchorPosition;
            if (ImGui.DragFloat3("Connected Anchor Position", ref cPos, 0.01f))
                joint.ConnectedAnchorPosition = cPos;

            Vector4 cRot = QuatToVec4(joint.ConnectedAnchorRotation);
            if (ImGui.DragFloat4("Connected Anchor Rot (xyzw)", ref cRot, 0.01f))
                joint.ConnectedAnchorRotation = Vec4ToQuat(cRot);
        }
    }

    private static void DrawBreakSettings(PhysicsJointComponent joint)
    {
        if (!ImGui.CollapsingHeader("Break Thresholds"))
            return;

        float breakForce = joint.BreakForce;
        if (ImGui.DragFloat("Break Force", ref breakForce, 1f, 0f, float.MaxValue, "%.1f"))
            joint.BreakForce = MathF.Max(0f, breakForce);

        bool unbreakableForce = breakForce >= float.MaxValue * 0.5f;
        if (ImGui.Checkbox("Unbreakable (Force)", ref unbreakableForce))
            joint.BreakForce = unbreakableForce ? float.MaxValue : 1000f;

        float breakTorque = joint.BreakTorque;
        if (ImGui.DragFloat("Break Torque", ref breakTorque, 1f, 0f, float.MaxValue, "%.1f"))
            joint.BreakTorque = MathF.Max(0f, breakTorque);

        bool unbreakableTorque = breakTorque >= float.MaxValue * 0.5f;
        if (ImGui.Checkbox("Unbreakable (Torque)", ref unbreakableTorque))
            joint.BreakTorque = unbreakableTorque ? float.MaxValue : 1000f;
    }

    private static void DrawFlags(PhysicsJointComponent joint)
    {
        if (!ImGui.CollapsingHeader("Flags"))
            return;

        bool enableCollision = joint.EnableCollision;
        if (ImGui.Checkbox("Enable Collision Between Bodies", ref enableCollision))
            joint.EnableCollision = enableCollision;

        bool enablePreprocessing = joint.EnablePreprocessing;
        if (ImGui.Checkbox("Enable Preprocessing", ref enablePreprocessing))
            joint.EnablePreprocessing = enablePreprocessing;
    }

    private static void DrawDebug(PhysicsJointComponent joint)
    {
        if (!ImGui.CollapsingHeader("Debug"))
            return;

        bool drawGizmos = joint.DrawGizmos;
        if (ImGui.Checkbox("Draw Gizmos", ref drawGizmos))
            joint.DrawGizmos = drawGizmos;
    }

    #region Limit/Drive Helpers

    public static void DrawAngularLimitPair(
        string sectionLabel,
        ref bool enable, string enableLabel,
        ref float lower, ref float upper,
        ref float restitution, ref float bounceThreshold,
        ref float stiffness, ref float damping)
    {
        if (!ImGui.CollapsingHeader(sectionLabel, ImGuiTreeNodeFlags.DefaultOpen))
            return;

        ImGui.Checkbox(enableLabel, ref enable);

        if (enable)
        {
            float lowerDeg = lower * (180f / MathF.PI);
            float upperDeg = upper * (180f / MathF.PI);

            if (ImGui.DragFloat("Lower Angle (deg)", ref lowerDeg, 0.5f, -360f, 360f))
                lower = lowerDeg * (MathF.PI / 180f);
            if (ImGui.DragFloat("Upper Angle (deg)", ref upperDeg, 0.5f, -360f, 360f))
                upper = upperDeg * (MathF.PI / 180f);

            ImGui.DragFloat("Restitution", ref restitution, 0.01f, 0f, 1f);
            ImGui.DragFloat("Bounce Threshold", ref bounceThreshold, 0.01f, 0f, 100f);
            ImGui.DragFloat("Stiffness", ref stiffness, 1f, 0f, 1e6f);
            ImGui.DragFloat("Damping", ref damping, 1f, 0f, 1e6f);
        }
    }

    public static void DrawLinearLimitPair(
        string sectionLabel,
        ref bool enable, string enableLabel,
        ref float lower, ref float upper,
        ref float restitution, ref float bounceThreshold,
        ref float stiffness, ref float damping)
    {
        if (!ImGui.CollapsingHeader(sectionLabel, ImGuiTreeNodeFlags.DefaultOpen))
            return;

        ImGui.Checkbox(enableLabel, ref enable);

        if (enable)
        {
            ImGui.DragFloat("Lower Limit", ref lower, 0.01f);
            ImGui.DragFloat("Upper Limit", ref upper, 0.01f);
            ImGui.DragFloat("Restitution", ref restitution, 0.01f, 0f, 1f);
            ImGui.DragFloat("Bounce Threshold", ref bounceThreshold, 0.01f, 0f, 100f);
            ImGui.DragFloat("Stiffness", ref stiffness, 1f, 0f, 1e6f);
            ImGui.DragFloat("Damping", ref damping, 1f, 0f, 1e6f);
        }
    }

    public static void DrawDriveEditor(string label, ref JointDrive drive)
    {
        ImGui.PushID(label);
        if (ImGui.TreeNode(label))
        {
            float stiffness = drive.Stiffness;
            float damping = drive.Damping;
            float forceLimit = drive.ForceLimit;
            bool accel = drive.IsAcceleration;

            if (ImGui.DragFloat("Stiffness", ref stiffness, 1f, 0f, 1e6f))
                drive = new JointDrive(stiffness, damping, forceLimit, accel);
            if (ImGui.DragFloat("Damping", ref damping, 1f, 0f, 1e6f))
                drive = new JointDrive(stiffness, damping, forceLimit, accel);
            if (ImGui.DragFloat("Force Limit", ref forceLimit, 1f, 0f, float.MaxValue))
                drive = new JointDrive(stiffness, damping, forceLimit, accel);
            if (ImGui.Checkbox("Is Acceleration", ref accel))
                drive = new JointDrive(stiffness, damping, forceLimit, accel);

            ImGui.TreePop();
        }
        ImGui.PopID();
    }

    public static void DrawMotionSelector(string label, JointMotion current, Action<JointMotion> setter)
    {
        int selected = (int)current;
        string[] options = ["Locked", "Limited", "Free"];
        if (ImGui.Combo(label, ref selected, options, options.Length))
            setter((JointMotion)selected);
    }

    #endregion

    public static Vector4 QuatToVec4(Quaternion q)
        => new(q.X, q.Y, q.Z, q.W);

    public static Quaternion Vec4ToQuat(Vector4 v)
        => Quaternion.Normalize(new Quaternion(v.X, v.Y, v.Z, v.W));
}

#endregion

#region Fixed Joint Editor

public sealed class FixedJointComponentEditor : IXRComponentEditor
{
    public void DrawInspector(XRComponent component, HashSet<object> visited)
    {
        if (component is not FixedJointComponent joint)
        {
            EditorImGuiUI.DrawDefaultComponentInspector(component, visited);
            ComponentEditorLayout.DrawActivePreviewDialog();
            return;
        }

        if (!ComponentEditorLayout.DrawInspectorModeToggle(joint, visited, "Fixed Joint Editor"))
        {
            ComponentEditorLayout.DrawActivePreviewDialog();
            return;
        }

        ImGui.PushID(joint.GetHashCode());
        JointEditorShared.DrawCommonJointProperties(joint);
        ImGui.PopID();
        ComponentEditorLayout.DrawActivePreviewDialog();
    }
}

#endregion

#region Distance Joint Editor

public sealed class DistanceJointComponentEditor : IXRComponentEditor
{
    public void DrawInspector(XRComponent component, HashSet<object> visited)
    {
        if (component is not DistanceJointComponent joint)
        {
            EditorImGuiUI.DrawDefaultComponentInspector(component, visited);
            ComponentEditorLayout.DrawActivePreviewDialog();
            return;
        }

        if (!ComponentEditorLayout.DrawInspectorModeToggle(joint, visited, "Distance Joint Editor"))
        {
            ComponentEditorLayout.DrawActivePreviewDialog();
            return;
        }

        ImGui.PushID(joint.GetHashCode());
        JointEditorShared.DrawCommonJointProperties(joint);
        DrawDistanceSettings(joint);
        DrawSpringSettings(joint);
        ImGui.PopID();
        ComponentEditorLayout.DrawActivePreviewDialog();
    }

    private static void DrawDistanceSettings(DistanceJointComponent joint)
    {
        if (!ImGui.CollapsingHeader("Distance Settings", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        float minDist = joint.MinDistance;
        if (ImGui.DragFloat("Min Distance", ref minDist, 0.01f, 0f, float.MaxValue))
            joint.MinDistance = MathF.Max(0f, minDist);

        bool enableMin = joint.EnableMinDistance;
        if (ImGui.Checkbox("Enable Min Distance", ref enableMin))
            joint.EnableMinDistance = enableMin;

        float maxDist = joint.MaxDistance;
        if (ImGui.DragFloat("Max Distance", ref maxDist, 0.01f, 0f, float.MaxValue))
            joint.MaxDistance = MathF.Max(0f, maxDist);

        bool enableMax = joint.EnableMaxDistance;
        if (ImGui.Checkbox("Enable Max Distance", ref enableMax))
            joint.EnableMaxDistance = enableMax;

        float tolerance = joint.Tolerance;
        if (ImGui.DragFloat("Tolerance", ref tolerance, 0.001f, 0f, 10f))
            joint.Tolerance = MathF.Max(0f, tolerance);

        // Show current distance if joint is live
        if (joint.NativeJoint is IAbstractDistanceJoint dj)
            ImGui.TextDisabled($"Current distance: {dj.Distance:F3}");
    }

    private static void DrawSpringSettings(DistanceJointComponent joint)
    {
        if (!ImGui.CollapsingHeader("Spring"))
            return;

        float stiffness = joint.Stiffness;
        if (ImGui.DragFloat("Stiffness", ref stiffness, 1f, 0f, 1e6f))
            joint.Stiffness = MathF.Max(0f, stiffness);

        float damping = joint.Damping;
        if (ImGui.DragFloat("Damping", ref damping, 1f, 0f, 1e6f))
            joint.Damping = MathF.Max(0f, damping);
    }
}

#endregion

#region Hinge Joint Editor

public sealed class HingeJointComponentEditor : IXRComponentEditor
{
    public void DrawInspector(XRComponent component, HashSet<object> visited)
    {
        if (component is not HingeJointComponent joint)
        {
            EditorImGuiUI.DrawDefaultComponentInspector(component, visited);
            ComponentEditorLayout.DrawActivePreviewDialog();
            return;
        }

        if (!ComponentEditorLayout.DrawInspectorModeToggle(joint, visited, "Hinge Joint Editor"))
        {
            ComponentEditorLayout.DrawActivePreviewDialog();
            return;
        }

        ImGui.PushID(joint.GetHashCode());
        JointEditorShared.DrawCommonJointProperties(joint);
        DrawRuntimeInfo(joint);
        DrawLimits(joint);
        DrawDrive(joint);
        ImGui.PopID();
        ComponentEditorLayout.DrawActivePreviewDialog();
    }

    private static void DrawRuntimeInfo(HingeJointComponent joint)
    {
        if (joint.NativeJoint is null)
            return;

        if (!ImGui.CollapsingHeader("Runtime", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        ImGui.TextDisabled($"Angle: {joint.AngleRadians * (180f / MathF.PI):F1}°");
        ImGui.TextDisabled($"Velocity: {joint.Velocity:F3} rad/s");
    }

    private static void DrawLimits(HingeJointComponent joint)
    {
        if (!ImGui.CollapsingHeader("Angular Limits", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        bool enable = joint.EnableLimit;
        if (ImGui.Checkbox("Enable Limit", ref enable))
            joint.EnableLimit = enable;

        if (!enable)
            return;

        float lower = joint.LowerAngleRadians;
        float upper = joint.UpperAngleRadians;
        float lowerDeg = lower * (180f / MathF.PI);
        float upperDeg = upper * (180f / MathF.PI);

        if (ImGui.DragFloat("Lower Angle (deg)", ref lowerDeg, 0.5f, -360f, 360f))
            joint.LowerAngleRadians = lowerDeg * (MathF.PI / 180f);
        if (ImGui.DragFloat("Upper Angle (deg)", ref upperDeg, 0.5f, -360f, 360f))
            joint.UpperAngleRadians = upperDeg * (MathF.PI / 180f);

        float restitution = joint.LimitRestitution;
        if (ImGui.DragFloat("Restitution", ref restitution, 0.01f, 0f, 1f))
            joint.LimitRestitution = Math.Clamp(restitution, 0f, 1f);

        float bounce = joint.LimitBounceThreshold;
        if (ImGui.DragFloat("Bounce Threshold", ref bounce, 0.01f, 0f, 100f))
            joint.LimitBounceThreshold = MathF.Max(0f, bounce);

        float stiffness = joint.LimitStiffness;
        if (ImGui.DragFloat("Stiffness", ref stiffness, 1f, 0f, 1e6f))
            joint.LimitStiffness = MathF.Max(0f, stiffness);

        float damping = joint.LimitDamping;
        if (ImGui.DragFloat("Damping", ref damping, 1f, 0f, 1e6f))
            joint.LimitDamping = MathF.Max(0f, damping);
    }

    private static void DrawDrive(HingeJointComponent joint)
    {
        if (!ImGui.CollapsingHeader("Motor"))
            return;

        bool enable = joint.EnableDrive;
        if (ImGui.Checkbox("Enable Motor", ref enable))
            joint.EnableDrive = enable;

        if (!enable)
            return;

        float velocity = joint.DriveVelocity;
        if (ImGui.DragFloat("Target Velocity (rad/s)", ref velocity, 0.1f))
            joint.DriveVelocity = velocity;

        float forceLimit = joint.DriveForceLimit;
        if (ImGui.DragFloat("Force Limit", ref forceLimit, 1f, 0f, float.MaxValue))
            joint.DriveForceLimit = MathF.Max(0f, forceLimit);

        float gearRatio = joint.DriveGearRatio;
        if (ImGui.DragFloat("Gear Ratio", ref gearRatio, 0.01f, 0.001f, 100f))
            joint.DriveGearRatio = MathF.Max(0.001f, gearRatio);

        bool freeSpin = joint.DriveIsFreeSpin;
        if (ImGui.Checkbox("Free Spin", ref freeSpin))
            joint.DriveIsFreeSpin = freeSpin;
    }
}

#endregion

#region Prismatic Joint Editor

public sealed class PrismaticJointComponentEditor : IXRComponentEditor
{
    public void DrawInspector(XRComponent component, HashSet<object> visited)
    {
        if (component is not PrismaticJointComponent joint)
        {
            EditorImGuiUI.DrawDefaultComponentInspector(component, visited);
            ComponentEditorLayout.DrawActivePreviewDialog();
            return;
        }

        if (!ComponentEditorLayout.DrawInspectorModeToggle(joint, visited, "Prismatic Joint Editor"))
        {
            ComponentEditorLayout.DrawActivePreviewDialog();
            return;
        }

        ImGui.PushID(joint.GetHashCode());
        JointEditorShared.DrawCommonJointProperties(joint);
        DrawRuntimeInfo(joint);
        DrawLimits(joint);
        ImGui.PopID();
        ComponentEditorLayout.DrawActivePreviewDialog();
    }

    private static void DrawRuntimeInfo(PrismaticJointComponent joint)
    {
        if (joint.NativeJoint is null)
            return;

        if (!ImGui.CollapsingHeader("Runtime", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        ImGui.TextDisabled($"Position: {joint.Position:F3}");
        ImGui.TextDisabled($"Velocity: {joint.Velocity:F3}");
    }

    private static void DrawLimits(PrismaticJointComponent joint)
    {
        if (!ImGui.CollapsingHeader("Linear Limits", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        bool enable = joint.EnableLimit;
        if (ImGui.Checkbox("Enable Limit", ref enable))
            joint.EnableLimit = enable;

        if (!enable)
            return;

        float lower = joint.LowerLimit;
        if (ImGui.DragFloat("Lower Limit", ref lower, 0.01f))
            joint.LowerLimit = lower;

        float upper = joint.UpperLimit;
        if (ImGui.DragFloat("Upper Limit", ref upper, 0.01f))
            joint.UpperLimit = upper;

        float restitution = joint.LimitRestitution;
        if (ImGui.DragFloat("Restitution", ref restitution, 0.01f, 0f, 1f))
            joint.LimitRestitution = Math.Clamp(restitution, 0f, 1f);

        float bounce = joint.LimitBounceThreshold;
        if (ImGui.DragFloat("Bounce Threshold", ref bounce, 0.01f, 0f, 100f))
            joint.LimitBounceThreshold = MathF.Max(0f, bounce);

        float stiffness = joint.LimitStiffness;
        if (ImGui.DragFloat("Stiffness", ref stiffness, 1f, 0f, 1e6f))
            joint.LimitStiffness = MathF.Max(0f, stiffness);

        float damping = joint.LimitDamping;
        if (ImGui.DragFloat("Damping", ref damping, 1f, 0f, 1e6f))
            joint.LimitDamping = MathF.Max(0f, damping);
    }
}

#endregion

#region Spherical Joint Editor

public sealed class SphericalJointComponentEditor : IXRComponentEditor
{
    public void DrawInspector(XRComponent component, HashSet<object> visited)
    {
        if (component is not SphericalJointComponent joint)
        {
            EditorImGuiUI.DrawDefaultComponentInspector(component, visited);
            ComponentEditorLayout.DrawActivePreviewDialog();
            return;
        }

        if (!ComponentEditorLayout.DrawInspectorModeToggle(joint, visited, "Spherical Joint Editor"))
        {
            ComponentEditorLayout.DrawActivePreviewDialog();
            return;
        }

        ImGui.PushID(joint.GetHashCode());
        JointEditorShared.DrawCommonJointProperties(joint);
        DrawRuntimeInfo(joint);
        DrawLimits(joint);
        ImGui.PopID();
        ComponentEditorLayout.DrawActivePreviewDialog();
    }

    private static void DrawRuntimeInfo(SphericalJointComponent joint)
    {
        if (joint.NativeJoint is null)
            return;

        if (!ImGui.CollapsingHeader("Runtime", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        ImGui.TextDisabled($"Swing Y: {joint.SwingYAngle * (180f / MathF.PI):F1}°");
        ImGui.TextDisabled($"Swing Z: {joint.SwingZAngle * (180f / MathF.PI):F1}°");
    }

    private static void DrawLimits(SphericalJointComponent joint)
    {
        if (!ImGui.CollapsingHeader("Cone Limits", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        bool enable = joint.EnableLimitCone;
        if (ImGui.Checkbox("Enable Limit Cone", ref enable))
            joint.EnableLimitCone = enable;

        if (!enable)
            return;

        float yAngle = joint.LimitConeYAngleRadians;
        float zAngle = joint.LimitConeZAngleRadians;
        float yDeg = yAngle * (180f / MathF.PI);
        float zDeg = zAngle * (180f / MathF.PI);

        if (ImGui.DragFloat("Cone Y Angle (deg)", ref yDeg, 0.5f, 0f, 180f))
            joint.LimitConeYAngleRadians = yDeg * (MathF.PI / 180f);
        if (ImGui.DragFloat("Cone Z Angle (deg)", ref zDeg, 0.5f, 0f, 180f))
            joint.LimitConeZAngleRadians = zDeg * (MathF.PI / 180f);

        float restitution = joint.LimitRestitution;
        if (ImGui.DragFloat("Restitution", ref restitution, 0.01f, 0f, 1f))
            joint.LimitRestitution = Math.Clamp(restitution, 0f, 1f);

        float bounce = joint.LimitBounceThreshold;
        if (ImGui.DragFloat("Bounce Threshold", ref bounce, 0.01f, 0f, 100f))
            joint.LimitBounceThreshold = MathF.Max(0f, bounce);

        float stiffness = joint.LimitStiffness;
        if (ImGui.DragFloat("Stiffness", ref stiffness, 1f, 0f, 1e6f))
            joint.LimitStiffness = MathF.Max(0f, stiffness);

        float damping = joint.LimitDamping;
        if (ImGui.DragFloat("Damping", ref damping, 1f, 0f, 1e6f))
            joint.LimitDamping = MathF.Max(0f, damping);
    }
}

#endregion

#region D6 Joint Editor

public sealed class D6JointComponentEditor : IXRComponentEditor
{
    public void DrawInspector(XRComponent component, HashSet<object> visited)
    {
        if (component is not D6JointComponent joint)
        {
            EditorImGuiUI.DrawDefaultComponentInspector(component, visited);
            ComponentEditorLayout.DrawActivePreviewDialog();
            return;
        }

        if (!ComponentEditorLayout.DrawInspectorModeToggle(joint, visited, "D6 Joint Editor"))
        {
            ComponentEditorLayout.DrawActivePreviewDialog();
            return;
        }

        ImGui.PushID(joint.GetHashCode());
        JointEditorShared.DrawCommonJointProperties(joint);
        DrawRuntimeInfo(joint);
        DrawMotionAxes(joint);
        DrawTwistLimit(joint);
        DrawSwingLimit(joint);
        DrawDistanceLimit(joint);
        DrawDrives(joint);
        DrawDriveTarget(joint);
        ImGui.PopID();
        ComponentEditorLayout.DrawActivePreviewDialog();
    }

    private static void DrawRuntimeInfo(D6JointComponent joint)
    {
        if (joint.NativeJoint is null)
            return;

        if (!ImGui.CollapsingHeader("Runtime", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        ImGui.TextDisabled($"Twist: {joint.TwistAngle * (180f / MathF.PI):F1}°");
        ImGui.TextDisabled($"Swing Y: {joint.SwingYAngle * (180f / MathF.PI):F1}°");
        ImGui.TextDisabled($"Swing Z: {joint.SwingZAngle * (180f / MathF.PI):F1}°");
    }

    private static void DrawMotionAxes(D6JointComponent joint)
    {
        if (!ImGui.CollapsingHeader("Motion Axes", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        JointEditorShared.DrawMotionSelector("X", joint.MotionX, v => joint.MotionX = v);
        JointEditorShared.DrawMotionSelector("Y", joint.MotionY, v => joint.MotionY = v);
        JointEditorShared.DrawMotionSelector("Z", joint.MotionZ, v => joint.MotionZ = v);
        ImGui.Separator();
        JointEditorShared.DrawMotionSelector("Twist", joint.MotionTwist, v => joint.MotionTwist = v);
        JointEditorShared.DrawMotionSelector("Swing 1", joint.MotionSwing1, v => joint.MotionSwing1 = v);
        JointEditorShared.DrawMotionSelector("Swing 2", joint.MotionSwing2, v => joint.MotionSwing2 = v);
    }

    private static void DrawTwistLimit(D6JointComponent joint)
    {
        if (!ImGui.CollapsingHeader("Twist Limit"))
            return;

        float lower = joint.TwistLowerRadians;
        float upper = joint.TwistUpperRadians;
        float lowerDeg = lower * (180f / MathF.PI);
        float upperDeg = upper * (180f / MathF.PI);

        if (ImGui.DragFloat("Lower (deg)", ref lowerDeg, 0.5f, -360f, 360f))
            joint.TwistLowerRadians = lowerDeg * (MathF.PI / 180f);
        if (ImGui.DragFloat("Upper (deg)", ref upperDeg, 0.5f, -360f, 360f))
            joint.TwistUpperRadians = upperDeg * (MathF.PI / 180f);

        float restitution = joint.TwistLimitRestitution;
        if (ImGui.DragFloat("Restitution", ref restitution, 0.01f, 0f, 1f))
            joint.TwistLimitRestitution = Math.Clamp(restitution, 0f, 1f);

        float stiffness = joint.TwistLimitStiffness;
        if (ImGui.DragFloat("Stiffness", ref stiffness, 1f, 0f, 1e6f))
            joint.TwistLimitStiffness = MathF.Max(0f, stiffness);

        float damping = joint.TwistLimitDamping;
        if (ImGui.DragFloat("Damping", ref damping, 1f, 0f, 1e6f))
            joint.TwistLimitDamping = MathF.Max(0f, damping);
    }

    private static void DrawSwingLimit(D6JointComponent joint)
    {
        if (!ImGui.CollapsingHeader("Swing Limit (Cone)"))
            return;

        float yDeg = joint.SwingLimitYAngle * (180f / MathF.PI);
        float zDeg = joint.SwingLimitZAngle * (180f / MathF.PI);

        if (ImGui.DragFloat("Y Angle (deg)", ref yDeg, 0.5f, 0f, 180f))
            joint.SwingLimitYAngle = yDeg * (MathF.PI / 180f);
        if (ImGui.DragFloat("Z Angle (deg)", ref zDeg, 0.5f, 0f, 180f))
            joint.SwingLimitZAngle = zDeg * (MathF.PI / 180f);

        float restitution = joint.SwingLimitRestitution;
        if (ImGui.DragFloat("Restitution", ref restitution, 0.01f, 0f, 1f))
            joint.SwingLimitRestitution = Math.Clamp(restitution, 0f, 1f);

        float stiffness = joint.SwingLimitStiffness;
        if (ImGui.DragFloat("Stiffness", ref stiffness, 1f, 0f, 1e6f))
            joint.SwingLimitStiffness = MathF.Max(0f, stiffness);

        float damping = joint.SwingLimitDamping;
        if (ImGui.DragFloat("Damping", ref damping, 1f, 0f, 1e6f))
            joint.SwingLimitDamping = MathF.Max(0f, damping);
    }

    private static void DrawDistanceLimit(D6JointComponent joint)
    {
        if (!ImGui.CollapsingHeader("Distance Limit"))
            return;

        float value = joint.DistanceLimitValue;
        if (ImGui.DragFloat("Max Distance", ref value, 0.01f, 0f, float.MaxValue))
            joint.DistanceLimitValue = MathF.Max(0f, value);

        float stiffness = joint.DistanceLimitStiffness;
        if (ImGui.DragFloat("Stiffness", ref stiffness, 1f, 0f, 1e6f))
            joint.DistanceLimitStiffness = MathF.Max(0f, stiffness);

        float damping = joint.DistanceLimitDamping;
        if (ImGui.DragFloat("Damping", ref damping, 1f, 0f, 1e6f))
            joint.DistanceLimitDamping = MathF.Max(0f, damping);
    }

    private static void DrawDrives(D6JointComponent joint)
    {
        if (!ImGui.CollapsingHeader("Drives"))
            return;

        JointDrive dx = joint.DriveX;
        JointEditorShared.DrawDriveEditor("Drive X", ref dx);
        if (!dx.Equals(joint.DriveX)) joint.DriveX = dx;

        JointDrive dy = joint.DriveY;
        JointEditorShared.DrawDriveEditor("Drive Y", ref dy);
        if (!dy.Equals(joint.DriveY)) joint.DriveY = dy;

        JointDrive dz = joint.DriveZ;
        JointEditorShared.DrawDriveEditor("Drive Z", ref dz);
        if (!dz.Equals(joint.DriveZ)) joint.DriveZ = dz;

        JointDrive dSwing = joint.DriveSwing;
        JointEditorShared.DrawDriveEditor("Drive Swing", ref dSwing);
        if (!dSwing.Equals(joint.DriveSwing)) joint.DriveSwing = dSwing;

        JointDrive dTwist = joint.DriveTwist;
        JointEditorShared.DrawDriveEditor("Drive Twist", ref dTwist);
        if (!dTwist.Equals(joint.DriveTwist)) joint.DriveTwist = dTwist;

        JointDrive dSlerp = joint.DriveSlerp;
        JointEditorShared.DrawDriveEditor("Drive Slerp", ref dSlerp);
        if (!dSlerp.Equals(joint.DriveSlerp)) joint.DriveSlerp = dSlerp;
    }

    private static void DrawDriveTarget(D6JointComponent joint)
    {
        if (!ImGui.CollapsingHeader("Drive Target"))
            return;

        Vector3 pos = joint.DriveTargetPosition;
        if (ImGui.DragFloat3("Position", ref pos, 0.01f))
            joint.DriveTargetPosition = pos;

        Vector4 rot = JointEditorShared.QuatToVec4(joint.DriveTargetRotation);
        if (ImGui.DragFloat4("Rotation (xyzw)", ref rot, 0.01f))
            joint.DriveTargetRotation = JointEditorShared.Vec4ToQuat(rot);

        Vector3 linVel = joint.DriveLinearVelocity;
        if (ImGui.DragFloat3("Linear Velocity", ref linVel, 0.01f))
            joint.DriveLinearVelocity = linVel;

        Vector3 angVel = joint.DriveAngularVelocity;
        if (ImGui.DragFloat3("Angular Velocity", ref angVel, 0.01f))
            joint.DriveAngularVelocity = angVel;
    }
}

#endregion
