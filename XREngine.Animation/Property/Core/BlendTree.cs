namespace XREngine.Animation
{
    public abstract class BlendTree : MotionBase
    {
        public override void GetAnimationValues(MotionBase? parentMotion, IDictionary<string, AnimVar> variables, float weight)
        {
            BlendChildMotionAnimationValues(variables, weight);
            parentMotion?.CopyAnimationValuesFrom(this);
        }

        public abstract void BlendChildMotionAnimationValues(IDictionary<string, AnimVar> variables, float weight);
    }
}
