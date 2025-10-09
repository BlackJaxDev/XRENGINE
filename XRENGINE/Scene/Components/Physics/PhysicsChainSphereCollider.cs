using System.ComponentModel;
using System.Numerics;
using XREngine.Components;
using XREngine.Scene.Transforms;

namespace XREngine.Components;

/// <summary>
/// Sphere collider for physics chain components
/// </summary>
public class PhysicsChainSphereCollider : PhysicsChainColliderBase
{
    [Description("Radius of the sphere collider")]
    public float Radius = 1.0f;

    [Description("Transform to use for the collider position (if null, uses this component's transform)")]
    public Transform? ColliderTransform;

    private TransformBase EffectiveTransform => ColliderTransform ?? Transform;

    public override bool Collide(ref Vector3 particlePosition, float particleRadius)
    {
        Vector3 center = EffectiveTransform.WorldTranslation;
        Vector3 direction = particlePosition - center;
        float distance = direction.Length();
        
        if (distance < 1e-6f)
            return false;
            
        float minDistance = Radius + particleRadius;
        
        if (distance < minDistance)
        {
            Vector3 normal = direction / distance;
            particlePosition = center + normal * minDistance;
            return true;
        }
        
        return false;
    }
} 