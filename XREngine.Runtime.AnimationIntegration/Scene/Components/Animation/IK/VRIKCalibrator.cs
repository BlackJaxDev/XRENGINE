using System.Numerics;
using XREngine.Data.Core;
using XREngine.Extensions;
using XREngine.Scene.Transforms;
using Transform = XREngine.Scene.Transforms.Transform;

namespace XREngine.Components.Animation;

/// <summary>Compatibility entry point and hand-axis helpers for the VRIK solver.</summary>
public static partial class VRIKCalibrator
{
    /// <summary>Capture the original six-device layout through the stable per-slot calibration rig.</summary>
    public static CalibrationData? Calibrate(
        VRIKSolverComponent ik,
        VRIKCalibrationSettings settings,
        TransformBase? headTracker,
        TransformBase? bodyTracker = null,
        TransformBase? leftHandTracker = null,
        TransformBase? rightHandTracker = null,
        TransformBase? leftFootTracker = null,
        TransformBase? rightFootTracker = null)
    {
        VrCalibrationRequest request = new() { Settings = settings, RequireLevelHead = false };
        Add(EHumanoidIKTarget.Head, headTracker);
        Add(EHumanoidIKTarget.Hips, bodyTracker);
        Add(EHumanoidIKTarget.LeftHand, leftHandTracker);
        Add(EHumanoidIKTarget.RightHand, rightHandTracker);
        Add(EHumanoidIKTarget.LeftFoot, leftFootTracker);
        Add(EHumanoidIKTarget.RightFoot, rightFootTracker);
        return ik.CalibrateLegacy(request) as CalibrationData;

        void Add(EHumanoidIKTarget slot, TransformBase? device)
        {
            if (device is not null)
                request.Slots[(int)slot] = new VrCalibrationCapture(device, device.WorldMatrix);
        }
    }

    public static Vector3 GuessWristToPalmAxis(Transform? hand, Transform? forearm)
    {
        if (hand is null || forearm is null)
        {
            Debug.LogWarning("Can not guess the hand bone's orientation without the hand and forearm transforms.");
            return Vector3.Zero;
        }

        Vector3 handToForearm = forearm.WorldTranslation - hand.WorldTranslation;
        var majorDir = XRMath.GetAxisToDirection(hand.WorldRotation, handToForearm);
        Vector3 axis = XRMath.AxisToVector(majorDir);
        if (Vector3.Dot(handToForearm, hand.WorldRotation.Rotate(axis)) > 0f)
            axis = -axis;
        return axis;
    }

    public static Vector3 GuessPalmToThumbAxis(Transform? hand, Transform? forearm)
    {
        if (hand is null || forearm is null)
            return Vector3.Zero;
        if (hand.ChildCount == 0)
        {
            Debug.LogWarning($"Hand {hand.Name} does not have any fingers, VRIK can not guess the hand bone's orientation. Please assign hand axes in VRIK settings.");
            return Vector3.Zero;
        }

        float closestSqrMag = float.PositiveInfinity;
        int thumbIndex = 0;
        for (int i = 0; i < hand.ChildCount; i++)
        {
            TransformBase? finger = hand.GetChild(i);
            if (finger is null)
                continue;
            float sqrMag = (finger.WorldTranslation - hand.WorldTranslation).LengthSquared();
            if (sqrMag < closestSqrMag)
            {
                closestSqrMag = sqrMag;
                thumbIndex = i;
            }
        }

        TransformBase? thumb = hand.GetChild(thumbIndex);
        if (thumb is null)
            return Vector3.Zero;
        Vector3 handNormal = Vector3.Cross(hand.WorldTranslation - forearm.WorldTranslation, thumb.WorldTranslation - hand.WorldTranslation);
        Vector3 toThumb = Vector3.Cross(handNormal, hand.WorldTranslation - forearm.WorldTranslation);
        Vector3 axis = XRMath.AxisToVector(XRMath.GetAxisToDirection(hand.WorldRotation, toThumb));
        if (Vector3.Dot(toThumb, hand.WorldRotation.Rotate(axis)) < 0f)
            axis = -axis;
        return axis;
    }
}
