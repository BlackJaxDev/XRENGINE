using System.Numerics;
using XREngine.Components.Scene.Transforms;
using XREngine.Data;
using XREngine.Scene.Transforms;

namespace XREngine.Components;

public class PhysicsChainCollider : PhysicsChainColliderBase
{
    public float _radius = 0.5f;
    public float _height = 0;
    public float _radius2 = 0;

    private float _scaledRadius;
    private float _scaledRadius2;
    private Vector3 _center0;
    private Vector3 _center1;
    private float _centersDistance;
    private int _collideType;

    protected override void OnComponentActivated()
    {
        base.OnComponentActivated();
        OnValidate();
    }

    void OnValidate()
    {
        _radius = MathF.Max(_radius, 0);
        _height = MathF.Max(_height, 0);
        _radius2 = MathF.Max(_radius2, 0);
    }

    public override void Prepare()
    {
        if (!TryResolveEffectiveTransform(null, out TransformBase effectiveTransform))
        {
            _scaledRadius = 0.0f;
            _scaledRadius2 = 0.0f;
            _center0 = Vector3.Zero;
            _center1 = Vector3.Zero;
            _centersDistance = 0.0f;
            _collideType = -1;
            return;
        }

        float scale = MathF.Abs(effectiveTransform.LossyWorldScale.X);
        float halfHeight = _height * 0.5f;

        if (_radius2 <= 0 || MathF.Abs(_radius - _radius2) < 0.01f)
        {
            _scaledRadius = _radius * scale;

            float h = halfHeight - _radius;
            if (h <= 0)
            {
                _center0 = effectiveTransform.TransformPoint(_center);
                _collideType = _bound switch
                {
                    EBound.Outside => 0,
                    _ => 1,
                };
            }
            else
            {
                Vector3 axis = Vector3.Transform(GetLocalAxis(), LocalRotationOffset);
                Vector3 c0 = _center + (axis * h);
                Vector3 c1 = _center - (axis * h);

                _center0 = effectiveTransform.TransformPoint(c0);
                _center1 = effectiveTransform.TransformPoint(c1);
                _centersDistance = (_center1 - _center0).Length();
                _collideType = _bound == EBound.Outside ? 2 : 3;
            }
        }
        else
        {
            float r = MathF.Max(_radius, _radius2);
            if (halfHeight - r <= 0)
            {
                _scaledRadius = r * scale;
                _center0 = effectiveTransform.TransformPoint(_center);
                _collideType = _bound == EBound.Outside ? 0 : 1;
            }
            else
            {
                _scaledRadius = _radius * scale;
                _scaledRadius2 = _radius2 * scale;

                float h0 = halfHeight - _radius;
                float h1 = halfHeight - _radius2;
                Vector3 axis = Vector3.Transform(GetLocalAxis(), LocalRotationOffset);
                Vector3 c0 = _center + (axis * h0);
                Vector3 c1 = _center - (axis * h1);

                _center0 = effectiveTransform.TransformPoint(c0);
                _center1 = effectiveTransform.TransformPoint(c1);
                _centersDistance = (_center1 - _center0).Length();
                _collideType = _bound == EBound.Outside ? 4 : 5;
            }
        }
    }

    private Vector3 GetLocalAxis()
        => _direction switch
        {
            Direction.X => Vector3.UnitX,
            Direction.Z => Vector3.UnitZ,
            _ => Vector3.UnitY,
        };

    public override bool Collide(ref Vector3 particlePosition, float particleRadius)
        => _collideType switch
        {
            0 => OutsideSphere(ref particlePosition, particleRadius, _center0, _scaledRadius),
            1 => InsideSphere(ref particlePosition, particleRadius, _center0, _scaledRadius),
            2 => OutsideCapsule(ref particlePosition, particleRadius, _center0, _center1, _scaledRadius, _centersDistance),
            3 => InsideCapsule(ref particlePosition, particleRadius, _center0, _center1, _scaledRadius, _centersDistance),
            4 => OutsideCapsule2(ref particlePosition, particleRadius, _center0, _center1, _scaledRadius, _scaledRadius2, _centersDistance),
            5 => InsideCapsule2(ref particlePosition, particleRadius, _center0, _center1, _scaledRadius, _scaledRadius2, _centersDistance),
            _ => false,
        };

    static bool OutsideSphere(ref Vector3 particlePosition, float particleRadius, Vector3 sphereCenter, float sphereRadius)
    {
        float r = sphereRadius + particleRadius;
        float r2 = r * r;
        Vector3 d = particlePosition - sphereCenter;
        float dlen2 = d.LengthSquared();

        // if is inside sphere, project onto sphere surface
        if (dlen2 > 0 && dlen2 < r2)
        {
            float dlen = MathF.Sqrt(dlen2);
            particlePosition = sphereCenter + d * (r / dlen);
            return true;
        }

        return false;
    }

    static bool InsideSphere(ref Vector3 particlePosition, float particleRadius, Vector3 sphereCenter, float sphereRadius)
    {
        float r = sphereRadius - particleRadius;
        float r2 = r * r;
        Vector3 d = particlePosition - sphereCenter;
        float dlen2 = d.LengthSquared();

        // if is outside sphere, project onto sphere surface
        if (dlen2 > r2)
        {
            float dlen = MathF.Sqrt(dlen2);
            particlePosition = sphereCenter + d * (r / dlen);
            return true;
        }

        return false;
    }

    static bool OutsideCapsule(ref Vector3 particlePosition, float particleRadius, Vector3 capsuleP0, Vector3 capsuleP1, float capsuleRadius, float dirlen)
    {
        float r = capsuleRadius + particleRadius;
        float r2 = r * r;
        Vector3 dir = capsuleP1 - capsuleP0;
        Vector3 d = particlePosition - capsuleP0;
        float t = Vector3.Dot(d, dir);

        if (t <= 0)
        {
            // check sphere1
            float dlen2 = d.LengthSquared();
            if (dlen2 > 0 && dlen2 < r2)
            {
                float dlen = MathF.Sqrt(dlen2);
                particlePosition = capsuleP0 + d * (r / dlen);
                return true;
            }
        }
        else
        {
            float dirlen2 = dirlen * dirlen;
            if (t >= dirlen2)
            {
                // check sphere2
                d = particlePosition - capsuleP1;
                float dlen2 = d.LengthSquared();
                if (dlen2 > 0 && dlen2 < r2)
                {
                    float dlen = MathF.Sqrt(dlen2);
                    particlePosition = capsuleP1 + d * (r / dlen);
                    return true;
                }
            }
            else
            {
                // check cylinder
                Vector3 q = d - dir * (t / dirlen2);
                float qlen2 = q.LengthSquared();
                if (qlen2 > 0 && qlen2 < r2)
                {
                    float qlen = MathF.Sqrt(qlen2);
                    particlePosition += q * ((r - qlen) / qlen);
                    return true;
                }
            }
        }
        return false;
    }

    static bool InsideCapsule(ref Vector3 particlePosition, float particleRadius, Vector3 capsuleP0, Vector3 capsuleP1, float capsuleRadius, float dirlen)
    {
        float r = capsuleRadius - particleRadius;
        float r2 = r * r;
        Vector3 dir = capsuleP1 - capsuleP0;
        Vector3 d = particlePosition - capsuleP0;
        float t = Vector3.Dot(d, dir);

        if (t <= 0)
        {
            // check sphere1
            float dlen2 = d.LengthSquared();
            if (dlen2 > r2)
            {
                float dlen = MathF.Sqrt(dlen2);
                particlePosition = capsuleP0 + d * (r / dlen);
                return true;
            }
        }
        else
        {
            float dirlen2 = dirlen * dirlen;
            if (t >= dirlen2)
            {
                // check sphere2
                d = particlePosition - capsuleP1;
                float dlen2 = d.LengthSquared();
                if (dlen2 > r2)
                {
                    float dlen = MathF.Sqrt(dlen2);
                    particlePosition = capsuleP1 + d * (r / dlen);
                    return true;
                }
            }
            else
            {
                // check cylinder
                Vector3 q = d - dir * (t / dirlen2);
                float qlen2 = q.LengthSquared();
                if (qlen2 > r2)
                {
                    float qlen = MathF.Sqrt(qlen2);
                    particlePosition += q * ((r - qlen) / qlen);
                    return true;
                }
            }
        }
        return false;
    }

    static bool OutsideCapsule2(ref Vector3 particlePosition, float particleRadius, Vector3 capsuleP0, Vector3 capsuleP1, float capsuleRadius0, float capsuleRadius1, float dirlen)
    {
        Vector3 dir = capsuleP1 - capsuleP0;
        Vector3 d = particlePosition - capsuleP0;
        float t = Vector3.Dot(d, dir);

        if (t <= 0)
        {
            // check sphere1
            float r = capsuleRadius0 + particleRadius;
            float r2 = r * r;
            float dlen2 = d.LengthSquared();
            if (dlen2 > 0 && dlen2 < r2)
            {
                float dlen = MathF.Sqrt(dlen2);
                particlePosition = capsuleP0 + d * (r / dlen);
                return true;
            }
        }
        else
        {
            float dirlen2 = dirlen * dirlen;
            if (t >= dirlen2)
            {
                // check sphere2
                float r = capsuleRadius1 + particleRadius;
                float r2 = r * r;
                d = particlePosition - capsuleP1;
                float dlen2 = d.LengthSquared();
                if (dlen2 > 0 && dlen2 < r2)
                {
                    float dlen = MathF.Sqrt(dlen2);
                    particlePosition = capsuleP1 + d * (r / dlen);
                    return true;
                }
            }
            else
            {
                // check cylinder
                Vector3 q = d - dir * (t / dirlen2);
                float qlen2 = q.LengthSquared();

                float klen = Vector3.Dot(d, dir / dirlen);
                float r = Interp.Lerp(capsuleRadius0, capsuleRadius1, klen / dirlen) + particleRadius;
                float r2 = r * r;

                if (qlen2 > 0 && qlen2 < r2)
                {
                    float qlen = MathF.Sqrt(qlen2);
                    particlePosition += q * ((r - qlen) / qlen);
                    return true;
                }
            }
        }
        return false;
    }

    static bool InsideCapsule2(ref Vector3 particlePosition, float particleRadius, Vector3 capsuleP0, Vector3 capsuleP1, float capsuleRadius0, float capsuleRadius1, float dirlen)
    {
        Vector3 dir = capsuleP1 - capsuleP0;
        Vector3 d = particlePosition - capsuleP0;
        float t = Vector3.Dot(d, dir);

        if (t <= 0)
        {
            // check sphere1
            float r = capsuleRadius0 - particleRadius;
            float r2 = r * r;
            float dlen2 = d.LengthSquared();
            if (dlen2 > r2)
            {
                float dlen = MathF.Sqrt(dlen2);
                particlePosition = capsuleP0 + d * (r / dlen);
                return true;
            }
        }
        else
        {
            float dirlen2 = dirlen * dirlen;
            if (t >= dirlen2)
            {
                // check sphere2
                float r = capsuleRadius1 - particleRadius;
                float r2 = r * r;
                d = particlePosition - capsuleP1;
                float dlen2 = d.LengthSquared();
                if (dlen2 > r2)
                {
                    float dlen = MathF.Sqrt(dlen2);
                    particlePosition = capsuleP1 + d * (r / dlen);
                    return true;
                }
            }
            else
            {
                // check cylinder
                Vector3 q = d - dir * (t / dirlen2);
                float qlen2 = q.LengthSquared();

                float klen = Vector3.Dot(d, dir / dirlen);
                float r = Interp.Lerp(capsuleRadius0, capsuleRadius1, klen / dirlen) - particleRadius;
                float r2 = r * r;

                if (qlen2 > r2)
                {
                    float qlen = MathF.Sqrt(qlen2);
                    particlePosition += q * ((r - qlen) / qlen);
                    return true;
                }
            }
        }
        return false;
    }

}
