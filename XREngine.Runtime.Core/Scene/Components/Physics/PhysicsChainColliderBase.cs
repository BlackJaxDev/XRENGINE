using System.ComponentModel;
using System.Numerics;
using XREngine.Scene.Transforms;

namespace XREngine.Components;

public class PhysicsChainColliderBase : XRComponent
{
    private TransformBase? _rootTransformOverride;
    private Quaternion _localRotationOffset = Quaternion.Identity;
    private bool _debugDraw;
    private bool _particleContactOnly;

    /// <summary>
    /// Shows this collider's editor visualization. Collision remains active
    /// when the visualization is disabled.
    /// </summary>
    [Category("Debug")]
    [DisplayName("Draw Collider")]
    [Description("Shows the physics-chain collider gizmo. Disabled by default so runtime and imported-avatar views remain unobstructed.")]
    public bool DebugDraw
    {
        get => _debugDraw;
        set => SetField(ref _debugDraw, value);
    }

    /// <summary>
    /// Makes this collider test only the child end of each segment when the chain uses segment collision.
    /// </summary>
    public bool ParticleContactOnly
    {
        get => _particleContactOnly;
        set => SetField(ref _particleContactOnly, value);
    }

    /// <summary>
    /// Optional transform used by import adapters when the authored collider is
    /// attached to one object but explicitly evaluates relative to another.
    /// </summary>
    public TransformBase? RootTransformOverride
    {
        get => _rootTransformOverride;
        set => SetField(ref _rootTransformOverride, value);
    }

    /// <summary>
    /// Authored collider-shape rotation relative to the effective transform.
    /// </summary>
    public Quaternion LocalRotationOffset
    {
        get => _localRotationOffset;
        set => SetField(
            ref _localRotationOffset,
            value.LengthSquared() > 0.000001f ? Quaternion.Normalize(value) : Quaternion.Identity);
    }

    public enum Direction
    {
        X, Y, Z
    }

    public Direction _direction = Direction.Y;
    public Vector3 _center = Vector3.Zero;

    public enum EBound
    {
        Outside,
        Inside
    }

    public EBound _bound = EBound.Outside;

    public int PrepareFrame { set; get; }

    public virtual void Start()
    {

    }

    public virtual void Prepare()
    {

    }

    public virtual bool Collide(ref Vector3 particlePosition, float particleRadius)
    {
        return false;
    }

    /// <summary>
    /// Gets the position along a segment, from 0 at <paramref name="start"/> to 1 at <paramref name="end"/>, that segment collision tests.
    /// </summary>
    public float ClosestSegmentParameter(Vector3 start, Vector3 end)
        => ParticleContactOnly ? 1.0f : ComputeClosestSegmentParameter(start, end);

    /// <summary>
    /// Gets the position along a segment closest to this collider. The default tests the child end.
    /// </summary>
    protected virtual float ComputeClosestSegmentParameter(Vector3 start, Vector3 end)
        => 1.0f;

    protected bool TryResolveEffectiveTransform(TransformBase? overrideTransform, out TransformBase effectiveTransform)
    {
        TransformBase? resolvedTransform = overrideTransform ?? RootTransformOverride ?? DefaultTransform;
        if (resolvedTransform is null)
        {
            effectiveTransform = null!;
            return false;
        }

        effectiveTransform = resolvedTransform;
        return true;
    }
}
