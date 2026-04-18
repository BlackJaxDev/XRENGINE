using XREngine.Extensions;
using System.Numerics;
using XREngine.Data;

namespace XREngine.Animation
{
    public class PropAnimFloat : PropAnimVector<float, FloatKeyframe>
    {
        public PropAnimFloat() : base() { }
        public PropAnimFloat(float lengthInSeconds, bool looped, bool useKeyframes)
            : base(lengthInSeconds, looped, useKeyframes) { }
        public PropAnimFloat(int frameCount, float FPS, bool looped, bool useKeyframes)
            : base(frameCount, FPS, looped, useKeyframes) { }
        
        protected override float LerpValues(float t1, float t2, float time) => Interp.Lerp(t1, t2, time);
        protected override float[] GetComponents(float value) => [value];
        protected override float GetMaxValue() => float.MaxValue;
        protected override float GetMinValue() => float.MinValue;
        protected override float GetVelocityMagnitude()
        {
            float b = CurrentVelocity;
            float a = 1.0f;
            Vector2 start = new(0.0f, 0.0f);
            Vector2 end = new(a, b);
            return start.Distance(end);
        }
    }
}
