
using Extensions;
using XREngine.Animation;

namespace XREngine.Scene.Components.Animation
{
    public class AnimationCurve : PropAnimFloat
    {
        public static AnimationCurve Linear { get; }
        public static AnimationCurve Smooth { get; }
        public static AnimationCurve Step { get; }
        public static AnimationCurve EaseOut { get; }
        public static AnimationCurve EaseIn { get; }

        static AnimationCurve()
        {
            Linear = new();
            Smooth = new();
            Step = new();
            EaseOut = new();
            EaseIn = new();

            Linear.Keyframes.Add(
                new FloatKeyframe(0.0f, 0.0f, 0.0f, EVectorInterpType.Linear),
                new FloatKeyframe(1.0f, 1.0f, 0.0f, EVectorInterpType.Linear));
            Smooth.Keyframes.Add(
                new FloatKeyframe(0.0f, 0.0f, 0.0f, EVectorInterpType.Smooth),
                new FloatKeyframe(1.0f, 1.0f, 0.0f, EVectorInterpType.Smooth));
            Step.Keyframes.Add(
                new FloatKeyframe(0.0f, 0.0f, 0.0f, EVectorInterpType.Step),
                new FloatKeyframe(1.0f, 1.0f, 0.0f, EVectorInterpType.Step));
            EaseOut.Keyframes.Add(
                new FloatKeyframe(0.0f, 0.0f, 0.0f, EVectorInterpType.Smooth),
                new FloatKeyframe(1.0f, 1.0f, 0.0f, EVectorInterpType.Smooth));
            EaseIn.Keyframes.Add(
                new FloatKeyframe(0.0f, 0.0f, 0.0f, EVectorInterpType.Smooth),
                new FloatKeyframe(1.0f, 1.0f, 0.0f, EVectorInterpType.Smooth));
        }

        public float Evaluate(float t)
            => GetValue(t.Clamp(0.0f, 1.0f));
    }
}