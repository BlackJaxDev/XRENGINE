using System.Numerics;
using XREngine.Data;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;

namespace XREngine.Components;

public class PhysicsChainCollider : PhysicsChainColliderBase, IRenderable
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

    public PhysicsChainCollider()
    {
        RenderedObjects =
        [
            RenderInfo3D.New(this, new RenderCommandMethod3D((int)EDefaultRenderPass.OpaqueForward, RenderGizmos))
        ];
    }

    public RenderInfo[] RenderedObjects { get; }

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
        if (Transform is null)
        {
            _scaledRadius = 0.0f;
            _scaledRadius2 = 0.0f;
            _center0 = Vector3.Zero;
            _center1 = Vector3.Zero;
            _centersDistance = 0.0f;
            _collideType = -1;
            return;
        }

        float scale = MathF.Abs(Transform.LossyWorldScale.X);
        float halfHeight = _height * 0.5f;

        if (_radius2 <= 0 || MathF.Abs(_radius - _radius2) < 0.01f)
        {
            _scaledRadius = _radius * scale;

            float h = halfHeight - _radius;
            if (h <= 0)
            {
                _center0 = Transform.TransformPoint(_center);
                _collideType = _bound switch
                {
                    EBound.Outside => 0,
                    _ => 1,
                };
            }
            else
            {
                Vector3 c0 = _center;
                Vector3 c1 = _center;
                switch (_direction)
                {
                    case Direction.X:
                        c0.X += h;
                        c1.X -= h;
                        break;
                    case Direction.Y:
                        c0.Y += h;
                        c1.Y -= h;
                        break;
                    case Direction.Z:
                        c0.Z += h;
                        c1.Z -= h;
                        break;
                }

                _center0 = Transform.TransformPoint(c0);
                _center1 = Transform.TransformPoint(c1);
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
                _center0 = Transform.TransformPoint(_center);
                _collideType = _bound == EBound.Outside ? 0 : 1;
            }
            else
            {
                _scaledRadius = _radius * scale;
                _scaledRadius2 = _radius2 * scale;

                float h0 = halfHeight - _radius;
                float h1 = halfHeight - _radius2;
                Vector3 c0 = _center;
                Vector3 c1 = _center;

                switch (_direction)
                {
                    case Direction.X:
                        c0.X += h0;
                        c1.X -= h1;
                        break;
                    case Direction.Y:
                        c0.Y += h0;
                        c1.Y -= h1;
                        break;
                    case Direction.Z:
                        c0.Z += h0;
                        c1.Z -= h1;
                        break;
                }

                _center0 = Transform.TransformPoint(c0);
                _center1 = Transform.TransformPoint(c1);
                _centersDistance = (_center1 - _center0).Length();
                _collideType = _bound == EBound.Outside ? 4 : 5;
            }
        }
    }

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

    private void RenderGizmos()
    {
        if (!IsActiveInHierarchy || Engine.Rendering.State.IsShadowPass)
            return;

        Prepare();

        ColorF4 color = _bound == EBound.Outside ? ColorF4.Yellow : ColorF4.Magenta;
        switch (_collideType)
        {
            case 0:
            case 1:
                Engine.Rendering.Debug.RenderSphere(_center0, _scaledRadius, false, color);
                break;
            case 2:
            case 3:
                DrawCapsule(_center0, _center1, _scaledRadius, _scaledRadius, color);
                break;
            case 4:
            case 5:
                DrawCapsule(_center0, _center1, _scaledRadius, _scaledRadius2, color);
                break;
        }
    }

    static void DrawCapsule(Vector3 c0, Vector3 c1, float radius0, float radius1, ColorF4 color)
    {
        if (radius0 != radius1)
        {
            Engine.Rendering.Debug.RenderLine(c0, c1, color);
            Engine.Rendering.Debug.RenderSphere(c0, radius0, false, color);
            Engine.Rendering.Debug.RenderSphere(c1, radius1, false, color);
        }
        else
        {
            Engine.Rendering.Debug.RenderCapsule(c0, c1, radius0, false, color);
        }
    }
}
