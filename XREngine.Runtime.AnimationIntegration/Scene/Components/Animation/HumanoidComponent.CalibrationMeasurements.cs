using System.Numerics;
using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Components.Animation;

public partial class HumanoidComponent
{
    /// <summary>Builds a straight-arm calibration T-pose from mapped bones, including A-pose imports, without writing avatar scale.</summary>
    public bool SetCanonicalCalibrationPose()
    {
        if (Head.Node is null || Hips.Node is null || Left.Arm.Node is null || Right.Arm.Node is null ||
            Left.Elbow.Node is null || Right.Elbow.Node is null || Left.Wrist.Node is null || Right.Wrist.Node is null)
            return false;

        var uniqueRoles = new HashSet<SceneNode>();
        foreach (SceneNode? node in new[] { Head.Node, Hips.Node, Left.Arm.Node, Left.Elbow.Node, Left.Wrist.Node,
            Right.Arm.Node, Right.Elbow.Node, Right.Wrist.Node, Left.Leg.Node, Left.Knee.Node, Left.Foot.Node,
            Right.Leg.Node, Right.Knee.Node, Right.Foot.Node })
            if (node is not null && !uniqueRoles.Add(node))
                return false;

        ResetRuntimeAnimationDiagnostics();
        ClearRuntimeMuscleState();
        ResetMappedTransformsToBindPose(includeEyesTarget: false);
        RecalculateCalibrationHierarchy(Transform);
        Vector3 up = Head.Node.Transform.WorldTranslation - Hips.Node.Transform.WorldTranslation;
        Vector3 left = Left.Arm.Node.Transform.WorldTranslation - Right.Arm.Node.Transform.WorldTranslation;
        if (!TryNormalizeCalibrationVector(up, out up))
            return false;
        left -= Vector3.Dot(left, up) * up;
        if (!TryNormalizeCalibrationVector(left, out left))
            return false;
        Vector3 forward = Vector3.Normalize(Vector3.Cross(left, up));

        SceneNode?[] spine = [Hips.Node, Spine.Node, Chest.Node, UpperChest.Node, Neck.Node, Head.Node];
        SceneNode? previous = null;
        foreach (SceneNode? bone in spine)
        {
            if (bone is null)
                continue;
            if (previous is not null)
                AimCalibrationBone(previous, bone, up);
            previous = bone;
        }
        StraightenCalibrationSide(Left, left, up, forward);
        StraightenCalibrationSide(Right, -left, up, forward);
        RecalculateCalibrationHierarchy(Transform);
        return true;
    }

    private static void StraightenCalibrationSide(BodySide side, Vector3 outward, Vector3 up, Vector3 forward)
    {
        AimCalibrationBone(side.Shoulder.Node, side.Arm.Node, outward);
        AimCalibrationBone(side.Arm.Node, side.Elbow.Node, outward);
        AimCalibrationBone(side.Elbow.Node, side.Wrist.Node, outward);
        AimCalibrationBone(side.Wrist.Node, side.Hand.Middle.Proximal.Node, outward);
        foreach (BodySide.Fingers.Finger finger in new[] { side.Hand.Thumb, side.Hand.Index, side.Hand.Middle, side.Hand.Ring, side.Hand.Pinky })
        {
            AimCalibrationBone(finger.Proximal.Node, finger.Intermediate.Node, outward);
            AimCalibrationBone(finger.Intermediate.Node, finger.Distal.Node, outward);
        }
        AimCalibrationBone(side.Leg.Node, side.Knee.Node, -up);
        AimCalibrationBone(side.Knee.Node, side.Foot.Node, -up);
        AimCalibrationBone(side.Foot.Node, side.Toes.Node, forward);
    }

    private static void AimCalibrationBone(SceneNode? bone, SceneNode? child, Vector3 direction)
    {
        if (bone?.Transform is not Transform transform || child is null)
            return;
        Vector3 current = child.Transform.WorldTranslation - transform.WorldTranslation;
        if (!TryNormalizeCalibrationVector(current, out current))
            return;
        Quaternion delta = XRMath.RotationBetweenVectors(current, direction);
        transform.SetWorldRotation(Quaternion.Normalize(delta * transform.WorldRotation));
        RecalculateCalibrationHierarchy(transform);
    }

    private static void RecalculateCalibrationHierarchy(TransformBase transform)
    {
        transform.RecalculateMatrices(true);
        foreach (TransformBase child in transform.Children)
            RecalculateCalibrationHierarchy(child);
    }

    private static bool TryNormalizeCalibrationVector(Vector3 value, out Vector3 normalized)
    {
        float lengthSquared = value.LengthSquared();
        normalized = Vector3.Zero;
        if (!float.IsFinite(lengthSquared) || lengthSquared < 1e-10f)
            return false;
        normalized = value / MathF.Sqrt(lengthSquared);
        return true;
    }

    /// <summary>Measures canonical straightened lengths from immutable bind geometry, independent of live pose and root scale.</summary>
    public bool TryGetCanonicalBodyMeasurements(Vector3 eyeOffsetFromHead, out AvatarBodyMeasurements measurements)
    {
        measurements = default;
        if (Head.Node is null || Hips.Node is null || Left.Wrist.Node is null || Right.Wrist.Node is null ||
            !Matrix4x4.Invert(GetHumanoidBindWorldPose(SceneNode), out Matrix4x4 rootInverse))
            return false;
        Matrix4x4 head = GetHumanoidBindWorldPose(Head.Node) * rootInverse;
        Vector3 hips = BindPosition(Hips.Node, rootInverse);
        if (!TryNormalizeCalibrationVector(head.Translation - hips, out Vector3 up))
            return false;
        float eyeHeight = Vector3.Dot(Vector3.Transform(eyeOffsetFromHead, head), up);
        SceneNode? leftBase = Left.Shoulder.Node ?? Left.Arm.Node;
        SceneNode? rightBase = Right.Shoulder.Node ?? Right.Arm.Node;
        if (leftBase is null || rightBase is null)
            return false;
        float shoulderWidth = Vector3.Distance(BindPosition(leftBase, rootInverse), BindPosition(rightBase, rootInverse));
        float leftArm = CalibrationArmLength(Left, rootInverse, eyeHeight, out bool leftEstimated);
        float rightArm = CalibrationArmLength(Right, rootInverse, eyeHeight, out bool rightEstimated);
        float span = shoulderWidth + leftArm + rightArm;
        bool estimated = leftEstimated || rightEstimated;
        if (!float.IsFinite(eyeHeight) || eyeHeight <= 0.0001f || !float.IsFinite(span) || span <= 0.0001f)
            return false;
        measurements = new AvatarBodyMeasurements(eyeHeight, span, estimated,
            estimated ? "Avatar fingertip endpoints are incomplete; arm span uses an estimated fingertip or hand length." : null);
        return true;
    }

    private Vector3 BindPosition(SceneNode node, Matrix4x4 inverseRoot)
        => Vector3.Transform(GetHumanoidBindWorldPose(node).Translation, inverseRoot);

    private float CalibrationArmLength(BodySide side, Matrix4x4 inverseRoot, float eyeHeight, out bool estimated)
    {
        estimated = true;
        if (side.Arm.Node is null || side.Elbow.Node is null || side.Wrist.Node is null)
            return float.NaN;
        float length = Vector3.Distance(BindPosition(side.Arm.Node, inverseRoot), BindPosition(side.Elbow.Node, inverseRoot))
            + Vector3.Distance(BindPosition(side.Elbow.Node, inverseRoot), BindPosition(side.Wrist.Node, inverseRoot));
        if (side.Shoulder.Node is not null)
            length += Vector3.Distance(BindPosition(side.Shoulder.Node, inverseRoot), BindPosition(side.Arm.Node, inverseRoot));
        float longest = 0.0f;
        foreach (BodySide.Fingers.Finger finger in new[] { side.Hand.Index, side.Hand.Middle, side.Hand.Ring, side.Hand.Pinky, side.Hand.Thumb })
        {
            if (finger.Proximal.Node is null || finger.Intermediate.Node is null || finger.Distal.Node is null)
                continue;
            Vector3 wrist = BindPosition(side.Wrist.Node, inverseRoot);
            Vector3 proximal = BindPosition(finger.Proximal.Node, inverseRoot);
            Vector3 intermediate = BindPosition(finger.Intermediate.Node, inverseRoot);
            Vector3 distal = BindPosition(finger.Distal.Node, inverseRoot);
            float fingertipExtension = 0.0f;
            foreach (TransformBase child in finger.Distal.Node.Transform.Children)
            {
                if (child.SceneNode is not SceneNode tip || tip.Name is not string name ||
                    (!name.Contains("tip", StringComparison.OrdinalIgnoreCase) && !name.Contains("end", StringComparison.OrdinalIgnoreCase)))
                    continue;
                fingertipExtension = MathF.Max(fingertipExtension, Vector3.Distance(distal, BindPosition(tip, inverseRoot)));
            }
            bool fingerEstimated = fingertipExtension <= 0.0001f;
            if (fingerEstimated)
                fingertipExtension = Vector3.Distance(intermediate, distal) * 0.7f;
            float handLength = Vector3.Distance(wrist, proximal) + Vector3.Distance(proximal, intermediate)
                + Vector3.Distance(intermediate, distal) + fingertipExtension;
            if (handLength > longest)
            {
                longest = handLength;
                estimated = fingerEstimated;
            }
        }
        // Missing finger chains use a declared heuristic: each hand is 11% of model eye height.
        return length + (longest > 0.0001f ? longest : eyeHeight * 0.11f);
    }
}
