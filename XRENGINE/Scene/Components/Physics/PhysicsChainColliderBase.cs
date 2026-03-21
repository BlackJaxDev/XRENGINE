using System.Numerics;
using XREngine.Components;
using XREngine.Scene.Transforms;

public class PhysicsChainColliderBase : XRComponent
{
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
        effectiveTransform = overrideTransform ?? Transform!;
        return effectiveTransform is not null;
    }
}
