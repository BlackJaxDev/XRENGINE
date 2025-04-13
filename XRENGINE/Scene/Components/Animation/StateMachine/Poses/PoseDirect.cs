using XREngine.Animation;

namespace XREngine.Components
{
    /// <summary>
    /// Retrieves a pose directly from a skeletal animation.
    /// </summary>
    public class PoseDirect : HumanoidPoseGenBase
    {
        public SkeletalAnimation? Animation { get; set; } = null;

        public PoseDirect() { }
        public PoseDirect(SkeletalAnimation anim)
            => Animation = anim;

        public override HumanoidPose? GetPose()
            => Animation?.GetFrame();

        public override void Tick(float delta)
            => Animation?.Tick(delta);
    }
}
