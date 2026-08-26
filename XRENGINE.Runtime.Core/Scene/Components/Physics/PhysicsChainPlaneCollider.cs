using System.Numerics;
using XREngine.Components.Scene.Transforms;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Scene.Transforms;

namespace XREngine.Components;

public class PhysicsChainPlaneCollider : PhysicsChainColliderBase
{
    public Plane _plane;

    public override void Prepare()
    {
        if (!TryResolveEffectiveTransform(null, out TransformBase effectiveTransform))
        {
            _plane = default;
            return;
        }

        Vector3 localNormal = _direction switch
        {
            Direction.X => Vector3.UnitX,
            Direction.Z => Vector3.UnitZ,
            _ => Vector3.UnitY,
        };
        localNormal = Vector3.Transform(localNormal, LocalRotationOffset);
        Vector3 normal = Vector3.Normalize(effectiveTransform.TransformDirection(localNormal));

        Vector3 p = effectiveTransform.TransformPoint(_center);
        _plane = XRMath.CreatePlaneFromPointAndNormal(p, normal);
    }

    public override bool Collide(ref Vector3 particlePosition, float particleRadius)
    {
        if (!TryResolveEffectiveTransform(null, out _))
            return false;

        float d = GeoUtil.DistanceFrom.PlaneToPoint(_plane, particlePosition);

        if (_bound == EBound.Outside)
        {
            if (d < 0)
            {
                particlePosition -= _plane.Normal * d;
                return true;
            }
        }
        else
        {
            if (d > 0)
            {
                particlePosition -= _plane.Normal * d;
                return true;
            }
        }

        return false;
    }

}
