using XREngine.Animation;
using XREngine.Data.Core;

namespace XREngine.Components
{
    /// <summary>
    /// Used to retrieve a final skeletal animation pose.
    /// </summary>
    public abstract class PoseGenBase : XRBase
    {
        public AnimStateMachineComponent? Owner { get; internal set; }

        public abstract HumanoidPose? GetPose();
        public abstract void Tick(float delta);
    }
}
