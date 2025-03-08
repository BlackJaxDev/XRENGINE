using System.Numerics;
using XREngine.Data.Colors;

namespace XREngine.Physics.RayTracing
{
    /// <summary>
    /// Returns all intersected objects that specify collision with this ray.
    /// </summary>
    public class RayTraceMulti(Vector3 start, Vector3 end, ushort collisionGroupFlags, ushort collidesWithFlags, params XRCollisionObject[] ignored) : RayTrace(start, end, collisionGroupFlags, collidesWithFlags, ignored)
    {
        public override bool HasHit => Results.Count != 0;
        
        public List<RayCollisionResult> Results { get; } = [];
        internal override void Reset() => Results.Clear();

        protected internal override void AddResult(XRCollisionObject obj, Vector3 hitNormal, bool normalInWorldSpace, float hitFraction, int shapePart, int triangleIndex)
        {
            if (!CanAddCommon(obj))
                return;

            if (!normalInWorldSpace)
                hitNormal = Vector3.TransformNormal(hitNormal, obj.WorldTransform);

            Results.Add(new RayCollisionResult(obj, hitFraction, hitNormal, Vector3.Lerp(StartPointWorld, EndPointWorld, hitFraction), shapePart, triangleIndex));
        }

        public override void Render()
        {
            Engine.Rendering.Debug.RenderLine(StartPointWorld, EndPointWorld, DebugColor);

            if (Results.Count == 0)
                Engine.Rendering.Debug.RenderPoint(EndPointWorld, DebugColor);
            else
                foreach (var result in Results)
                    Engine.Rendering.Debug.RenderPoint(result.HitPointWorld, new ColorF4(1.0f, 0.0f, 0.0f));
        }
    }
}
