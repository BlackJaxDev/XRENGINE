namespace XREngine.Components;

/// <summary>
/// Creates immutable runtime templates from already-flattened authored data.
/// The supplied spans are copied so callers cannot mutate a live template.
/// </summary>
public static class PhysicsChainTemplateFactory
{
    public static PhysicsChainTemplate Create(
        ReadOnlySpan<PhysicsChainTemplateTree> trees,
        ReadOnlySpan<PhysicsChainTemplateParticle> particles,
        ReadOnlySpan<int> depthOrderedParticleIndices,
        ReadOnlySpan<PhysicsChainDepthRange> depthRanges,
        int freezeAxis = 0)
    {
        if (trees.IsEmpty)
            throw new ArgumentException("A physics-chain template requires at least one tree.", nameof(trees));
        if (particles.IsEmpty)
            throw new ArgumentException("A physics-chain template requires at least one particle.", nameof(particles));
        if (depthOrderedParticleIndices.Length != particles.Length)
            throw new ArgumentException("The depth-ordered stream must contain every particle exactly once.", nameof(depthOrderedParticleIndices));
        if (depthRanges.IsEmpty)
            throw new ArgumentException("A physics-chain template requires at least one depth range.", nameof(depthRanges));

        return new PhysicsChainTemplate(
            trees.ToArray(),
            particles.ToArray(),
            depthOrderedParticleIndices.ToArray(),
            depthRanges.ToArray(),
            freezeAxis);
    }
}
