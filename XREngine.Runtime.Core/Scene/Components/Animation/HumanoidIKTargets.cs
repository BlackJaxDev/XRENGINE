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
        /// <summary>Legacy serialized name for the left upper-arm tracker pose, not a positional elbow goal.</summary>
        LeftElbow,
        /// <summary>Legacy serialized name for the right upper-arm tracker pose, not a positional elbow goal.</summary>
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