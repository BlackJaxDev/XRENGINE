namespace XREngine.Components;

public partial class PhysicsChainComponent
{
    internal int RuntimeParticleCount
    {
        get
        {
            int count = 0;
            for (int treeIndex = 0; treeIndex < _particleTrees.Count; ++treeIndex)
                count += _particleTrees[treeIndex].Particles.Count;
            return count;
        }
    }

    internal void ApplyWorldStructuralCommand(
        PhysicsChainWorldCommandKind kind,
        PhysicsChainWorld world,
        PhysicsChainCpuBackend backend)
    {
        // Particle objects remain the authoring compatibility mirror. The
        // backend is structurally re-registered from their current positions,
        // preserving state while resetting unrelated temporal history.
        _cpuBackendParticleVersion = -1;
        _runtimeTemplateParticlesVersion = -1;
        if (kind == PhysicsChainWorldCommandKind.BackendSwitch)
        {
            DetachCpuBackend();
            AttachCpuBackend(world, backend);
        }
        Wake(PhysicsChainWakeReason.QualityPolicyChanged);
    }

    internal void ApplyWorldDynamicCommand(PhysicsChainWorldDynamicCommandKind kind)
    {
        if (kind is PhysicsChainWorldDynamicCommandKind.Root
            or PhysicsChainWorldDynamicCommandKind.Force
            or PhysicsChainWorldDynamicCommandKind.Parameters)
            _cpuBackendStateVersion = -1;
        Wake(kind == PhysicsChainWorldDynamicCommandKind.Root
            ? PhysicsChainWakeReason.RootMovement
            : PhysicsChainWakeReason.ExplicitRequest);
    }
}
