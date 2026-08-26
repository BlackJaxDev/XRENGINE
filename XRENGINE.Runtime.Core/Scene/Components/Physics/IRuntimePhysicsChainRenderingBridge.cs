using System.Numerics;
using XREngine.Scene.Transforms;

namespace XREngine.Components;

/// <summary>
/// Rendering-owned operations used by a physics chain.  Core intentionally only
/// publishes the simulation owner; renderer implementations install this bridge
/// when a graphics runtime is available.
/// </summary>
public interface IRuntimePhysicsChainRenderingBridge
{
    PhysicsChainGpuBackendState BackendState { get; }

    void Register(PhysicsChainComponent chain);
    void Unregister(PhysicsChainComponent chain);
    void Execute(PhysicsChainComponent chain, in PhysicsChainGpuDispatchSnapshot snapshot);
    void NotifyReadbackUnavailable(PhysicsChainComponent chain, string reason);
    void InvalidateGpuDrivenRenderers(PhysicsChainComponent chain);
    void RenderDebug(PhysicsChainComponent chain);
    void RecordHierarchyRecalculationTicks(long ticks);
}

/// <summary>Runtime registration point for the optional graphics physics-chain backend.</summary>
public static class RuntimePhysicsChainRendering
{
    private sealed class UnavailableBridge : IRuntimePhysicsChainRenderingBridge
    {
        public PhysicsChainGpuBackendState BackendState => PhysicsChainGpuBackendState.Unavailable;
        public void Register(PhysicsChainComponent chain) { }
        public void Unregister(PhysicsChainComponent chain) { }
        public void Execute(PhysicsChainComponent chain, in PhysicsChainGpuDispatchSnapshot snapshot) { }
        public void NotifyReadbackUnavailable(PhysicsChainComponent chain, string reason) { }
        public void InvalidateGpuDrivenRenderers(PhysicsChainComponent chain) { }
        public void RenderDebug(PhysicsChainComponent chain) { }
        public void RecordHierarchyRecalculationTicks(long ticks) { }
    }

    private static readonly IRuntimePhysicsChainRenderingBridge Default = new UnavailableBridge();
    private static IRuntimePhysicsChainRenderingBridge _current = Default;

    public static IRuntimePhysicsChainRenderingBridge Current => Volatile.Read(ref _current);

    public static IDisposable Install(IRuntimePhysicsChainRenderingBridge bridge)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        IRuntimePhysicsChainRenderingBridge previous = Interlocked.Exchange(ref _current, bridge);
        return new InstallationLease(bridge, previous);
    }

    private sealed class InstallationLease(
        IRuntimePhysicsChainRenderingBridge installed,
        IRuntimePhysicsChainRenderingBridge previous) : IDisposable
    {
        private IRuntimePhysicsChainRenderingBridge? _installed = installed;

        public void Dispose()
        {
            IRuntimePhysicsChainRenderingBridge? current = Interlocked.Exchange(ref _installed, null);
            if (current is not null)
                Interlocked.CompareExchange(ref _current, previous, current);
        }
    }
}

public readonly record struct PhysicsChainGpuParticle(Vector3 Position, Vector3 PrevPosition, int IsColliding, Vector3 PreviousPhysicsPosition);
public readonly record struct PhysicsChainGpuParticleStatic(Vector3 TransformLocalPosition, int ParentIndex, float Damping, float Elasticity, float Stiffness, float Inert, float Friction, float Radius, float BoneLength, int TreeIndex);
public readonly record struct PhysicsChainGpuTree(Vector3 RestGravity, int ParticleOffset, int ParticleCount);
public readonly record struct PhysicsChainGpuCollider(Vector4 Center, Vector4 Params, Vector4 Orientation, int Type);
public readonly record struct PhysicsChainGpuBone(Transform? Transform, int ParentIndex, Vector3 RestLocalDirection);

/// <summary>Borrowed, allocation-free simulation data valid only for the bridge call.</summary>
public readonly ref struct PhysicsChainGpuDispatchSnapshot(
    ReadOnlySpan<PhysicsChainGpuParticle> particles,
    ReadOnlySpan<PhysicsChainGpuParticleStatic> particleStatic,
    ReadOnlySpan<PhysicsChainGpuTree> trees,
    ReadOnlySpan<Matrix4x4> transforms,
    ReadOnlySpan<PhysicsChainGpuCollider> colliders,
    ReadOnlySpan<PhysicsChainGpuBone> bones,
    float deltaTime, float objectScale, float weight, Vector3 force, Vector3 gravity, Vector3 objectMove,
    int freezeAxis, int loopCount, float timeVar, int executionGeneration, long submissionId,
    int staticDataVersion, int particleStateVersion, int transformSignature, int colliderSignature)
{
    public ReadOnlySpan<PhysicsChainGpuParticle> Particles { get; } = particles;
    public ReadOnlySpan<PhysicsChainGpuParticleStatic> ParticleStatic { get; } = particleStatic;
    public ReadOnlySpan<PhysicsChainGpuTree> Trees { get; } = trees;
    public ReadOnlySpan<Matrix4x4> Transforms { get; } = transforms;
    public ReadOnlySpan<PhysicsChainGpuCollider> Colliders { get; } = colliders;
    public ReadOnlySpan<PhysicsChainGpuBone> Bones { get; } = bones;
    public float DeltaTime { get; } = deltaTime;
    public float ObjectScale { get; } = objectScale;
    public float Weight { get; } = weight;
    public Vector3 Force { get; } = force;
    public Vector3 Gravity { get; } = gravity;
    public Vector3 ObjectMove { get; } = objectMove;
    public int FreezeAxis { get; } = freezeAxis;
    public int LoopCount { get; } = loopCount;
    public float TimeVar { get; } = timeVar;
    public int ExecutionGeneration { get; } = executionGeneration;
    public long SubmissionId { get; } = submissionId;
    public int StaticDataVersion { get; } = staticDataVersion;
    public int ParticleStateVersion { get; } = particleStateVersion;
    public int TransformSignature { get; } = transformSignature;
    public int ColliderSignature { get; } = colliderSignature;
}

/// <summary>Graphics backend availability without importing rendering implementation types.</summary>
public enum PhysicsChainGpuBackendState
{
    NotEvaluated,
    Ready,
    Unavailable,
    Unsupported,
    Disabled,
}
