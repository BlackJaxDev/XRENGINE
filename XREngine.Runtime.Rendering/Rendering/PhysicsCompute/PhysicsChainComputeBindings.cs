using System.Numerics;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Compute;

/// <summary>
/// Reusable resource set for the main physics-chain simulation pass.
/// </summary>
public readonly record struct PhysicsChainComputeBindings(
    XRDataBuffer<GPUPhysicsChainDispatcher.GPUParticleData> Particles,
    XRDataBuffer<GPUPhysicsChainDispatcher.GPUParticleStaticData> ParticleStatic,
    XRDataBuffer<Matrix4x4> Transforms,
    XRDataBuffer<GPUPhysicsChainDispatcher.GPUColliderData> Colliders,
    XRDataBuffer<GPUPhysicsChainDispatcher.GPUPerTreeParams> PerTreeParams,
    int ResourceGeneration)
{
    public bool IsCurrent(int resourceGeneration)
        => ResourceGeneration == resourceGeneration;
}
