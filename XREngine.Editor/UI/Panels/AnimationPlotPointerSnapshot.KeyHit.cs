using System.Numerics;
using XREngine.Animation;

namespace XREngine.Editor;

internal sealed partial class AnimationPlotPointerSnapshot
{
    internal readonly record struct KeyHit(AnimationMember Member, Keyframe Key,
        BaseKeyframeTrack? Track, float Second, Vector2 Point);
}
