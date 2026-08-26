using System.Numerics;
using XREngine.Components;
using XREngine.Rendering.Compute;

namespace XREngine.UnitTests.Physics;

internal static class GPUPhysicsChainDispatcherTestExtensions
{
    public static IPhysicsChainComputeSource AsComputeSource(this PhysicsChainComponent component)
        => RuntimePhysicsChainRenderingBridge.Instance.GetOrCreateComputeSource(component);

    public static void Register(this GPUPhysicsChainDispatcher dispatcher, PhysicsChainComponent component)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        IPhysicsChainComputeSource source = component.AsComputeSource();
        if (!dispatcher.IsRegistered(source))
            dispatcher.Register(source);
    }

    public static void Unregister(this GPUPhysicsChainDispatcher dispatcher, PhysicsChainComponent component)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        if (RuntimePhysicsChainRenderingBridge.Instance.TryGetComputeSource(component, out IPhysicsChainComputeSource? source)
            && source is not null)
        {
            dispatcher.Unregister(source);
        }
    }

    public static bool IsRegistered(this GPUPhysicsChainDispatcher dispatcher, PhysicsChainComponent component)
        => RuntimePhysicsChainRenderingBridge.Instance.TryGetComputeSource(component, out IPhysicsChainComputeSource? source)
            && source is not null
            && dispatcher.IsRegistered(source);

    public static void SubmitData(
        this GPUPhysicsChainDispatcher dispatcher,
        PhysicsChainComponent component,
        IReadOnlyList<GPUPhysicsChainDispatcher.GPUParticleData> particles,
        IReadOnlyList<GPUPhysicsChainDispatcher.GPUParticleStaticData> particleStaticData,
        IReadOnlyList<GPUPhysicsChainDispatcher.GPUParticleTreeData> trees,
        IReadOnlyList<Matrix4x4> transforms,
        IReadOnlyList<GPUPhysicsChainDispatcher.GPUColliderData> colliders,
        float deltaTime,
        float objectScale,
        float weight,
        Vector3 force,
        Vector3 gravity,
        Vector3 objectMove,
        int freezeAxis,
        int loopCount,
        float timeVar,
        int executionGeneration,
        long submissionId,
        int staticDataVersion,
        int particleStateVersion,
        int transformDataSignature,
        int colliderDataSignature)
        => dispatcher.SubmitData(
            component.AsComputeSource(),
            particles,
            particleStaticData,
            trees,
            transforms,
            colliders,
            deltaTime,
            objectScale,
            weight,
            force,
            gravity,
            objectMove,
            freezeAxis,
            loopCount,
            timeVar,
            executionGeneration,
            submissionId,
            staticDataVersion,
            particleStateVersion,
            transformDataSignature,
            colliderDataSignature);
}
