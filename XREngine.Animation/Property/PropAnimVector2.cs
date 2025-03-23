using Extensions;
using System.Numerics;

namespace XREngine.Animation
{
    public class PropAnimVector2 : PropAnimVector<Vector2, Vector2Keyframe>
    {
        public PropAnimVector2() : base() { }
        public PropAnimVector2(float lengthInSeconds, bool looped, bool useKeyframes)
            : base(lengthInSeconds, looped, useKeyframes) { }
        public PropAnimVector2(int frameCount, float FPS, bool looped, bool useKeyframes)
            : base(frameCount, FPS, looped, useKeyframes) { }

        protected override Vector2 LerpValues(Vector2 t1, Vector2 t2, float time) => Vector2.Lerp(t1, t2, time);
        protected override float[] GetComponents(Vector2 value) => [value.X, value.Y];
        protected override Vector2 GetMaxValue() => new(float.MaxValue);
        protected override Vector2 GetMinValue() => new(float.MinValue);
        protected override float GetVelocityMagnitude()
        {
            Vector2 start = Vector2.Zero;
            Vector2 end = CurrentVelocity;
            return start.Distance(end);
        }
    }
}
