using System.Numerics;

namespace XREngine.Animation
{
    public class PropAnimVector3 : PropAnimVector<Vector3, Vector3Keyframe>
    {
        public PropAnimVector3() : base() { }
        public PropAnimVector3(float lengthInSeconds, bool looped, bool useKeyframes)
            : base(lengthInSeconds, looped, useKeyframes) { }
        public PropAnimVector3(int frameCount, float FPS, bool looped, bool useKeyframes)
            : base(frameCount, FPS, looped, useKeyframes) { }

        protected override Vector3 LerpValues(Vector3 t1, Vector3 t2, float time) => Vector3.Lerp(t1, t2, time);
        protected override float[] GetComponents(Vector3 value) => [value.X, value.Y, value.Z];
        protected override Vector3 GetMaxValue() => new(float.MaxValue);
        protected override Vector3 GetMinValue() => new(float.MinValue);
        protected override float GetVelocityMagnitude()
        {
            Vector3 b = CurrentVelocity;
            float a = 1.0f;
            Vector4 start = Vector4.Zero;
            Vector4 end = new(a, b.X, b.Y, b.Z);
            return Vector4.Distance(start, end);
        }
    }
}
