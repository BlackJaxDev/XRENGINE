using System.ComponentModel;
using System.Numerics;
using XREngine.Components;
using XREngine.Scene.Transforms;

namespace XREngine.Components;

/// <summary>
/// Capsule collider for physics chain components
/// </summary>
public class PhysicsChainCapsuleCollider : PhysicsChainColliderBase
{
    [Description("Radius of the capsule collider")]
    public float Radius = 0.5f;

    [Description("Height of the capsule collider")]
    public float Height = 2.0f;

    [Description("Transform to use for the collider position (if null, uses this component's transform)")]
    public Transform? ColliderTransform;

    private TransformBase EffectiveTransform => ColliderTransform ?? Transform;

    public override bool Collide(ref Vector3 particlePosition, float particleRadius)
    {
        Vector3 center = EffectiveTransform.WorldTranslation;
        Vector3 up = EffectiveTransform.WorldUp;
        Vector3 halfHeight = up * (Height * 0.5f);
        
        Vector3 start = center - halfHeight;
        Vector3 end = center + halfHeight;
        
        Vector3 direction = end - start;
        float length = direction.Length();
        
        if (length < 1e-6f)
        {
            // Fallback to sphere collision if capsule has no height
            Vector3 dir = particlePosition - center;
            float dist = dir.Length();
            float minDist = Radius + particleRadius;
            
            if (dist < minDist)
            {
                Vector3 normal = dir / dist;
                particlePosition = center + normal * minDist;
                return true;
            }
            return false;
        }
        
        Vector3 normalizedDir = direction / length;
        Vector3 toParticle = particlePosition - start;
        
        // Project particle position onto capsule line
        float projection = Vector3.Dot(toParticle, normalizedDir);
        projection = Math.Clamp(projection, 0.0f, length);
        
        Vector3 closestPoint = start + normalizedDir * projection;
        
        // Check distance to closest point
        Vector3 toClosest = particlePosition - closestPoint;
        float distance = toClosest.Length();
        float minDistance = Radius + particleRadius;
        
        if (distance < minDistance)
        {
            Vector3 normal = toClosest / distance;
            particlePosition = closestPoint + normal * minDistance;
            return true;
        }
        
        return false;
    }
} 