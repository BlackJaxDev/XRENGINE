using System.Numerics;
using XREngine.Data.Colors;

namespace XREngine.Physics.RayTracing
{
    /// <summary>
    /// Returns a single collision result. Use RayTraceClosest if you're looking for a basic single result.
    /// </summary>
    public abstract class RayTraceSingle(Vector3 start, Vector3 end, ushort collisionGroupFlags, ushort collidesWithFlags, params XRCollisionObject[] ignored) : RayTrace(start, end, collisionGroupFlags, collidesWithFlags, ignored)
    {
        public override bool HasHit => Result != null;

        protected RayCollisionResult? Result { get; set; } = null;
        internal override void Reset() => Result = null;

        public XRCollisionObject? CollisionObject => Result?.CollisionObject;
        public float HitFraction => Result is null ? 1.0f : Result.HitFraction;
        public Vector3 HitNormalWorld => Result is null ? Vector3.Zero : Result.HitNormalWorld;
        public Vector3 HitPointWorld => Result is null ? Vector3.Zero : Result.HitPointWorld;
        public int ShapePart => Result is null ? -1 : Result.ShapePart;
        public int TriangleIndex => Result is null ? -1 : Result.TriangleIndex;

        public override void Render()
        {
            Vector3 end = Result?.HitPointWorld ?? EndPointWorld;
            Engine.Rendering.Debug.RenderLine(StartPointWorld, end, DebugColor);
            Engine.Rendering.Debug.RenderPoint(end, HasHit ? new ColorF4(1.0f, 0.0f, 0.0f) : DebugColor);
        }
    }
}
