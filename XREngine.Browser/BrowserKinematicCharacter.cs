using System.Numerics;

namespace XREngine.Browser;

/// <summary>Bounded swept-box character against static boxes. This is not a rigid-body engine.</summary>
public sealed class BrowserKinematicCharacter
{
    private const float Skin = 0.001f;
    private static readonly Vector3 Extents = new(0.25f, 0.85f, 0.25f);
    private readonly BrowserCollisionBox[] _boxes;
    private float _verticalVelocity;

    public BrowserKinematicCharacter(Vector3 center, BrowserCollisionBox[] boxes)
    {
        ArgumentNullException.ThrowIfNull(boxes);
        if (!Finite(center) || boxes.Length is < 1 or > 64)
            throw new ArgumentException("The character requires a finite center and one to 64 static boxes.");
        _boxes = (BrowserCollisionBox[])boxes.Clone();
        foreach (BrowserCollisionBox box in _boxes)
        {
            if (!Finite(box.Minimum) || !Finite(box.Maximum) ||
                box.Minimum.X >= box.Maximum.X || box.Minimum.Y >= box.Maximum.Y || box.Minimum.Z >= box.Maximum.Z)
                throw new ArgumentException("Collision boxes must have finite positive extents.");
            Vector3 minimum = box.Minimum - Extents, maximum = box.Maximum + Extents;
            if (center.X > minimum.X && center.X < maximum.X && center.Y > minimum.Y &&
                center.Y < maximum.Y && center.Z > minimum.Z && center.Z < maximum.Z)
                throw new ArgumentException("Character spawn overlaps a static collider.");
        }
        Center = center;
    }

    public Vector3 Center { get; private set; }
    public Vector3 Eye => Center + new Vector3(0, 0.7f, 0);
    public bool Grounded { get; private set; }
    public float Speed { get; private set; }

    public void ResetVelocity()
    {
        _verticalVelocity = 0;
        Speed = 0;
    }

    public void Step(float deltaSeconds, Vector2 localMove, float yaw, bool jump)
    {
        if (!float.IsFinite(deltaSeconds) || deltaSeconds <= 0 || deltaSeconds > 1f / 30 ||
            !float.IsFinite(localMove.X) || !float.IsFinite(localMove.Y) || !float.IsFinite(yaw))
            throw new ArgumentException("Character integration requires finite input and a bounded fixed step.");
        if (localMove.LengthSquared() > 1)
            localMove = Vector2.Normalize(localMove);
        if (jump && Grounded)
            _verticalVelocity = 4.5f;
        _verticalVelocity = MathF.Max(-20, _verticalVelocity - 12 * deltaSeconds);
        Vector3 right = new(MathF.Cos(yaw), 0, MathF.Sin(yaw));
        Vector3 forward = new(MathF.Sin(yaw), 0, -MathF.Cos(yaw));
        Vector3 remaining = ((right * localMove.X + forward * localMove.Y) * 2.5f +
            Vector3.UnitY * _verticalVelocity) * deltaSeconds;
        Vector3 start = Center;
        Grounded = false;
        // Three dimensions need at most three independent contact planes; the spare
        // iterations handle tied contacts. Discarding an exhausted remainder is conservative.
        for (int iteration = 0; iteration < 5 && remaining.LengthSquared() > 1e-12f; iteration++)
        {
            float earliest = 1;
            Vector3 normal = default;
            bool hit = false;
            foreach (BrowserCollisionBox box in _boxes)
            {
                if (Sweep(Center, remaining, box.Minimum - Extents, box.Maximum + Extents,
                    out float fraction, out Vector3 candidateNormal) && (!hit || fraction < earliest))
                {
                    earliest = fraction;
                    normal = candidateNormal;
                    hit = true;
                }
            }
            if (!hit)
            {
                Center += remaining;
                break;
            }
            Center += remaining * earliest + normal * Skin;
            remaining *= 1 - earliest;
            float intoPlane = Vector3.Dot(remaining, normal);
            if (intoPlane < 0)
                remaining -= normal * intoPlane;
            if (normal.Y > 0.5f)
                Grounded = true;
            if (normal.Y != 0)
                _verticalVelocity = 0;
        }
        Vector3 traveled = Center - start;
        Speed = MathF.Sqrt(traveled.X * traveled.X + traveled.Z * traveled.Z) / deltaSeconds;
    }

    private static bool Sweep(Vector3 start, Vector3 delta, Vector3 minimum, Vector3 maximum,
        out float fraction, out Vector3 normal)
    {
        float entry = float.NegativeInfinity, exit = 1;
        normal = default;
        fraction = 0;
        for (int axis = 0; axis < 3; axis++)
        {
            float origin = axis == 0 ? start.X : axis == 1 ? start.Y : start.Z;
            float direction = axis == 0 ? delta.X : axis == 1 ? delta.Y : delta.Z;
            float low = axis == 0 ? minimum.X : axis == 1 ? minimum.Y : minimum.Z;
            float high = axis == 0 ? maximum.X : axis == 1 ? maximum.Y : maximum.Z;
            if (MathF.Abs(direction) < 1e-9f)
            {
                // Strict tangency permits movement along an existing contact plane.
                if (origin <= low || origin >= high)
                    return false;
                continue;
            }
            float near = (low - origin) / direction, far = (high - origin) / direction;
            if (near > far)
                (near, far) = (far, near);
            if (near > entry)
            {
                entry = near;
                float sign = direction > 0 ? -1 : 1;
                normal = axis == 0 ? new(sign, 0, 0) : axis == 1 ? new(0, sign, 0) : new(0, 0, sign);
            }
            exit = MathF.Min(exit, far);
            if (entry > exit)
                return false;
        }
        // An initially penetrating volume is rejected at construction, not silently repaired.
        // Negative entry here is an outward move from an existing contact.
        if (entry < 0 || entry > 1 || exit < 0 || Vector3.Dot(delta, normal) >= 0)
            return false;
        fraction = entry;
        return true;
    }

    private static bool Finite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z) &&
            MathF.Abs(value.X) <= 10000 && MathF.Abs(value.Y) <= 10000 && MathF.Abs(value.Z) <= 10000;
}
