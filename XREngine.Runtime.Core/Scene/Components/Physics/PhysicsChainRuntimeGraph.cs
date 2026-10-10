using System.Numerics;
using XREngine.Scene.Transforms;

namespace XREngine.Components;

/// <summary>
/// Holds the mutable particle graph and the first authored local pose.
/// A world binding keeps this graph while a component registration is live.
/// </summary>
internal sealed class PhysicsChainRuntimeGraph
{
    private long _readbackSourceGeneration = 1L;
    private PhysicsChainReadbackBinding? _readbackBinding;

    internal long ReadbackSourceGeneration => Volatile.Read(ref _readbackSourceGeneration);
    internal PhysicsChainReadbackBinding? ReadbackBinding => Volatile.Read(ref _readbackBinding);

    internal void BindReadbackSource(PhysicsChainWorld world, PhysicsChainRuntimeHandle handle)
        => Volatile.Write(ref _readbackBinding, new(world, handle));

    internal void UnbindReadbackSource() => Volatile.Write(ref _readbackBinding, null);

    internal void InvalidateReadbackSource()
    {
        if (Interlocked.Increment(ref _readbackSourceGeneration) <= 0L)
            throw new InvalidOperationException("The physics-chain readback source generation is exhausted.");
    }

    internal readonly List<PhysicsChainComponent.ParticleTree> ParticleTrees = [];
    internal readonly Dictionary<Transform, (Vector3 LocalPosition, Quaternion LocalRotation)> InitialLocalStates = [];
}
