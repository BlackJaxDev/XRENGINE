using MemoryPack;

namespace XREngine.Animation
{
    [MemoryPackable]
    [MemoryPackUnion(0, typeof(BlendTree1D))]
    [MemoryPackUnion(1, typeof(BlendTree2D))]
    [MemoryPackUnion(2, typeof(BlendTreeDirect))]
    public abstract partial class BlendTree : MotionBase
    {
        public override void GetAnimationValues(MotionBase? parentMotion, IDictionary<string, AnimVar> variables, float weight)
        {
            BlendChildMotionAnimationValues(variables, weight);
            parentMotion?.CopyAnimationValuesFrom(this);
        }

        public abstract void BlendChildMotionAnimationValues(IDictionary<string, AnimVar> variables, float weight);
    }
}
