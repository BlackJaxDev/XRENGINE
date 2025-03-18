using Extensions;
using XREngine.Animation;

namespace XREngine.Components
{
    public class PoseBlend : PoseGenBase
    {
        public PoseBlend() { }
        public PoseBlend(SkeletalAnimation from, SkeletalAnimation to)
        {
            FromAnimation = from;
            ToAnimation = to;
        }

        public SkeletalAnimation? FromAnimation { get; set; } = null;
        public SkeletalAnimation? ToAnimation { get; set; } = null;

        private float _interpValue;

        public float InterpValue
        {
            get => _interpValue;
            set => SetField(ref _interpValue, value.Clamp(0.0f, 1.0f));
        }

        public override HumanoidPose? GetPose()
        {
            HumanoidPose? fromPose = FromAnimation?.GetFrame();
            HumanoidPose? toPose = ToAnimation?.GetFrame();
            return fromPose?.BlendedWith(toPose, InterpValue);
        }
        public override void Tick(float delta)
        {
            FromAnimation?.Tick(delta);
            ToAnimation?.Tick(delta);
        }
    }
}
