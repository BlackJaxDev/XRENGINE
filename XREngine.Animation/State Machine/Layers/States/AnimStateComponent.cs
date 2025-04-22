using XREngine.Data.Core;
using static XREngine.Animation.AnimParameterDriverComponent;

namespace XREngine.Animation
{
    public abstract class AnimStateComponent : XRBase
    {
        public virtual void StateEntered(AnimState state, IDictionary<string, AnimVar> variables) { }
        public virtual void StateExited(AnimState state, IDictionary<string, AnimVar> variables) { }
        public virtual void StateTick(AnimState state, IDictionary<string, AnimVar> variables, float delta) { }
    }
}
