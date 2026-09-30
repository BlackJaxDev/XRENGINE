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
        SceneNode? LeftElbowNode { get; }
        SceneNode? RightElbowNode { get; }
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
        void ClearTargets() { }
        VrCalibrationResult RestoreCalibration(ReadOnlySpan<VrCalibrationTarget> targets, object? settings) => VrCalibrationResult.Failure("This rig does not support calibration restoration.");
        TransformBase? GetCalibratedTarget(EHumanoidIKTarget slot) => null;
    }
}
