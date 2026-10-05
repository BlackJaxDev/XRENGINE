using System.Numerics;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Components.Animation
{
    public interface IHumanoidVrCalibrationRig
    {
        SceneNode SceneNode { get; }
        EHumanoidPosePreviewMode PosePreviewMode { get; set; }
        TransformBase RootTransform { get; }
        SceneNode? HeadNode { get; }
        SceneNode? HipsNode { get; }
        SceneNode? ChestNode { get; }
        SceneNode? LeftFootNode { get; }
        SceneNode? RightFootNode { get; }
        SceneNode? LeftToesNode { get; }
        SceneNode? RightToesNode { get; }
        SceneNode? LeftElbowNode { get; }
        SceneNode? RightElbowNode { get; }
        SceneNode? LeftShoulderNode { get; }
        SceneNode? RightShoulderNode { get; }
        SceneNode? LeftUpperArmNode { get; }
        SceneNode? RightUpperArmNode { get; }
        SceneNode? LeftHandNode { get; }
        SceneNode? RightHandNode { get; }
        SceneNode? LeftMiddleFingertipNode { get; }
        SceneNode? RightMiddleFingertipNode { get; }
        bool TryGetVrBindBodyToEngine(out Matrix4x4 correction);
        bool TryGetVrSemanticForwardInRootBindSpace(out Vector3 forward);
        bool TryGetVrSemanticForwardInHipsBindSpace(out Vector3 forward);
        SceneNode? LeftKneeNode { get; }
        SceneNode? RightKneeNode { get; }
        (TransformBase? tfm, Matrix4x4 offset) GetIKTarget(EHumanoidIKTarget target);
        void SetIKTarget(EHumanoidIKTarget target, TransformBase? tfm, Matrix4x4 offset);
        void ClearIKTarget(EHumanoidIKTarget target);
        void ClearIKTargets();
        void ResetPose();
        bool SetCanonicalCalibrationPose() => false;
    }

    public interface IVRIKSolverHandle
    {
        bool IsActive { get; set; }
        void SuspendCalibrationAnimationWriters() { }
        void EndCalibrationPose() { }
        void ConfigureTrackingTransitions(float lossHoldSeconds, float crossfadeSeconds) { }
        void ClearTargets() { }
        VrCalibrationResult RestoreCalibration(ReadOnlySpan<VrCalibrationTarget> targets, object? settings) => VrCalibrationResult.Failure("This rig does not support calibration restoration.");
        TransformBase? GetCalibratedTarget(EHumanoidIKTarget slot) => null;
    }
}
