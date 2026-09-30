namespace XREngine.Components;

/// <summary>
/// Explicit general-topology kernel. The scalar oracle already traverses
/// immutable depth ranges parent-before-child, so this entry point retains the
/// same collision and numerical contract while allowing independent tuning.
/// </summary>
public static class PhysicsChainDepthOrderedBranchedKernel
{
    public static bool TryStep(
        PhysicsChainTemplate template,
        in PhysicsChainCpuInput input,
        ReadOnlySpan<PhysicsChainCpuTreeInput> treeInputs,
        ReadOnlySpan<PhysicsChainCpuParticleInput> particleInputs,
        ReadOnlySpan<PhysicsChainCpuCollider> colliders,
        Span<PhysicsChainCpuState> states,
        Span<PhysicsChainCpuOutput> outputs)
        => PhysicsChainScalarReferenceKernel.TryStep(
            template, input, treeInputs, particleInputs, colliders, states, outputs);
}
