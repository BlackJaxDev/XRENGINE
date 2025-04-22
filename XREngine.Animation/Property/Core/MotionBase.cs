using XREngine.Core.Files;

namespace XREngine.Animation
{
    public abstract class MotionBase : XRAsset
    {
        public abstract void Tick(object? rootObject, float delta, IDictionary<string, AnimVar> variables, float weight);
    }
}