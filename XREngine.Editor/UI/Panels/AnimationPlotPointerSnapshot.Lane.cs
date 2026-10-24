using XREngine.Animation;

namespace XREngine.Editor;

internal sealed partial class AnimationPlotPointerSnapshot
{
    internal readonly record struct Lane(AnimationMember Member, BasePropAnim? Animation,
        float MinY, float MaxY);
}
