using System.ComponentModel;
using System.Numerics;
using XREngine.Components;
using XREngine.Scene.Transforms;

namespace XREngine.Components;

/// <summary>
/// Box collider for physics chain components
/// </summary>
public class PhysicsChainBoxCollider : PhysicsChainColliderBase
{
    [Description("Size of the box collider")]
    public Vector3 Size = new(2.0f, 2.0f, 2.0f);

    [Description("Transform to use for the collider position (if null, uses this component's transform)")]
    public Transform? ColliderTransform;

    public override bool Collide(ref Vector3 particlePosition, float particleRadius)
    {
        if (!TryResolveEffectiveTransform(ColliderTransform, out TransformBase effectiveTransform))
            return false;

        Vector3 center = effectiveTransform.WorldTranslation;
        Vector3 halfExtents = Size * 0.5f;
        
        // Transform particle position to local space
        Vector3 localPos = effectiveTransform.InverseTransformPoint(particlePosition);
        
        // Clamp to box bounds
        Vector3 clamped = Vector3.Clamp(localPos, -halfExtents, halfExtents);
        
        // Find closest point on box surface
        Vector3 closestLocal = clamped;
        Vector3 closestWorld = effectiveTransform.TransformPoint(closestLocal);
        
        // Check distance to closest point
        Vector3 toClosest = particlePosition - closestWorld;
        float distance = toClosest.Length();
        float minDistance = particleRadius;
        
        if (distance < minDistance)
        {
            Vector3 normal = toClosest / distance;
            particlePosition = closestWorld + normal * minDistance;
            return true;
        }
        
        return false;
    }
} 