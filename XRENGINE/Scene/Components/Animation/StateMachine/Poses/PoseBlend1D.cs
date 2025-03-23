using Extensions;
using XREngine.Animation;

namespace XREngine.Components
{
    public class PoseBlend1D : PoseGenBase
    {
        public PoseBlend1D() { }
        public PoseBlend1D(params PoseKeyframe[] keyframes)
            => _poses.AddRange(keyframes);
        public PoseBlend1D(IEnumerable<PoseKeyframe> keyframes)
            => _poses.AddRange(keyframes);

        private KeyframeTrack<PoseKeyframe> _poses = [];
        private float _time = 0.0f;

        public float InterpolationTime
        {
            get => _time;
            set => SetField(ref _time, value);
        }
        public KeyframeTrack<PoseKeyframe> Poses
        {
            get => _poses;
            set => SetField(ref _poses, value);
        }

        public override HumanoidPose? GetPose()
        {
            PoseKeyframe? kf = Poses.GetKeyBefore(InterpolationTime);
            if (kf is null)
                return null;

            HumanoidPose? frame = kf.Animation?.GetFrame();
            if (kf.Next is PoseKeyframe kf2)
            {
                HumanoidPose? frame2 = kf2.Animation?.GetFrame();
                if (frame2 != null)
                {
                    float diff = InterpolationTime - kf.Second;
                    float span = kf2.Second - kf.Second;
                    float weight = span < float.Epsilon ? 0.0f : diff / span;
                    return frame?.BlendedWith(frame2, weight);
                }
            }
            return frame;
        }
        public override void Tick(float delta)
        {
            foreach (PoseKeyframe pose in Poses.Cast<PoseKeyframe>())
                pose?.Animation?.Tick(delta);
        }
    }
}
