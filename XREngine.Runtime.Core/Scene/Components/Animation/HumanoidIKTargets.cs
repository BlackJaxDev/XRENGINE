using System.Numerics;
using XREngine.Scene.Transforms;

namespace XREngine.Components.Animation
{
    public enum EHumanoidIKTarget
    {
        Head,
        Hips,
        LeftHand,
        RightHand,
        LeftFoot,
        RightFoot,
        LeftElbow,
        RightElbow,
        LeftKnee,
        RightKnee,
        Chest,
    }

    public enum EHumanoidPosePreviewMode
    {
        AnimatedPose,
        MeshBindPose,
        TPose,
        NeutralMusclePose,
    }

    public static class HumanoidIKTargetDefaults
    {
        public static (TransformBase? tfm, Matrix4x4 offset) Empty
            => (null, Matrix4x4.Identity);
    }
}