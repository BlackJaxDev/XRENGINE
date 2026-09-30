namespace XREngine.Components;

/// <summary>Resolves broadphase ownership without crossing synchronization domains.</summary>
public static class PhysicsChainColliderBroadphasePolicy
{
    public const int DirectColliderLimit = 4;

    public static PhysicsChainColliderBroadphaseDecision Resolve(
        PhysicsChainColliderPoseOwner poseOwner,
        int colliderCount,
        bool gpuBroadphaseAvailable)
    {
        if (colliderCount < 0)
            throw new ArgumentOutOfRangeException(nameof(colliderCount));
        if (!Enum.IsDefined(poseOwner))
            throw new ArgumentOutOfRangeException(nameof(poseOwner));
        if (colliderCount <= DirectColliderLimit)
            return new PhysicsChainColliderBroadphaseDecision(
                PhysicsChainColliderBroadphaseOwner.None,
                IsSupported: true,
                RequiresReadback: false);

        return poseOwner == PhysicsChainColliderPoseOwner.Cpu
            ? new PhysicsChainColliderBroadphaseDecision(
                PhysicsChainColliderBroadphaseOwner.Cpu,
                IsSupported: true,
                RequiresReadback: false)
            : new PhysicsChainColliderBroadphaseDecision(
                PhysicsChainColliderBroadphaseOwner.Gpu,
                IsSupported: gpuBroadphaseAvailable,
                RequiresReadback: false);
    }
}
