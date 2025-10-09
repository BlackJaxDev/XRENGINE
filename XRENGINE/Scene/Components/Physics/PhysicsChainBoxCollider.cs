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

    private TransformBase EffectiveTransform => ColliderTransform ?? Transform;

    public override bool Collide(ref Vector3 particlePosition, float particleRadius)
    {
        Vector3 center = EffectiveTransform.WorldTranslation;
        Vector3 halfExtents = Size * 0.5f;
        
        // Transform particle position to local space
        Vector3 localPos = EffectiveTransform.InverseTransformPoint(particlePosition);
        
        // Clamp to box bounds
        Vector3 clamped = Vector3.Clamp(localPos, -halfExtents, halfExtents);
        
        // Find closest point on box surface
        Vector3 closestLocal = clamped;
        Vector3 closestWorld = EffectiveTransform.TransformPoint(closestLocal);
        
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