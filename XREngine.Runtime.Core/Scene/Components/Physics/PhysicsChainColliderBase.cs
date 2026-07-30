using System.ComponentModel;
using System.Numerics;
using XREngine.Scene.Transforms;

namespace XREngine.Components;

public class PhysicsChainColliderBase : XRComponent
{
    private TransformBase? _rootTransformOverride;
    private Quaternion _localRotationOffset = Quaternion.Identity;
    private bool _debugDraw;

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
