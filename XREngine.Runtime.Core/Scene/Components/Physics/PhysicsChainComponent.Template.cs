namespace XREngine.Components;

public partial class PhysicsChainComponent
{
    private PhysicsChainWorld? _runtimeTemplateWorld;
    private PhysicsChainTemplate? _runtimeTemplate;
    private int _runtimeTemplateParticlesVersion = -1;

    internal PhysicsChainTemplate GetOrCreateRuntimeTemplate(PhysicsChainWorld world)
    {
        if (ReferenceEquals(_runtimeTemplateWorld, world)
            && _runtimeTemplate is not null
            && _runtimeTemplateParticlesVersion == _particlesVersion)
            return _runtimeTemplate;

        PhysicsChainTemplate candidate = BuildRuntimeTemplate();
        _runtimeTemplate = PhysicsChainTemplateCache.ForWorld(world).GetOrAdd(candidate);
        _runtimeTemplateWorld = world;
        _runtimeTemplateParticlesVersion = _particlesVersion;
        return _runtimeTemplate;
    }

    private PhysicsChainTemplate BuildRuntimeTemplate()
    {
        int treeCount = _particleTrees.Count;
        int particleCount = 0;
        int depthRangeCount = 0;
        for (int treeIndex = 0; treeIndex < treeCount; ++treeIndex)
        {
            List<Particle> particles = _particleTrees[treeIndex].Particles;
            particleCount += particles.Count;
            depthRangeCount += CalculateMaximumDepth(particles) + 1;
        }

        var trees = new PhysicsChainTemplateTree[treeCount];
        var particlesData = new PhysicsChainTemplateParticle[particleCount];
        var depthOrderedIndices = new int[particleCount];
        var depthRanges = new PhysicsChainDepthRange[depthRangeCount];

        int particleStart = 0;
        int depthIndex = 0;
        int rangeIndex = 0;
        for (int treeIndex = 0; treeIndex < treeCount; ++treeIndex)
        {
            ParticleTree tree = _particleTrees[treeIndex];
            List<Particle> particles = tree.Particles;
            int maximumDepth = CalculateMaximumDepth(particles);
            trees[treeIndex] = new PhysicsChainTemplateTree(
                particleStart,
                particles.Count,
                maximumDepth,
                tree.BoneTotalLength);

            for (int particleIndex = 0; particleIndex < particles.Count; ++particleIndex)
            {
                Particle particle = particles[particleIndex];
                int depth = CalculateParticleDepth(particles, particleIndex);
                float inverseSegmentLength = particle.SegmentLength > 1e-8f
                    ? 1.0f / particle.SegmentLength
                    : 0.0f;
                int parentIndex = particle.ParentIndex < 0 ? -1 : particleStart + particle.ParentIndex;
                int boneIndex = particle.Transform is null ? -1 : particleStart + particleIndex;
                particlesData[particleStart + particleIndex] = new PhysicsChainTemplateParticle(
                    parentIndex,
                    depth,
                    boneIndex,
                    particle.ChildCount,
                    particle.SegmentLength,
                    inverseSegmentLength,
                    particle.BoneLength,
                    particle.Damping,
                    particle.Elasticity,
                    particle.Stiffness,
                    particle.Inert,
                    particle.Friction,
                    particle.Radius,
                    particle.Transform is null ? particle.EndOffset : particle.InitLocalPosition,
                    particle.InitLocalRotation);
            }

            for (int depth = 0; depth <= maximumDepth; ++depth)
            {
                int rangeStart = depthIndex;
                for (int particleIndex = 0; particleIndex < particles.Count; ++particleIndex)
                {
                    if (particlesData[particleStart + particleIndex].Depth == depth)
                        depthOrderedIndices[depthIndex++] = particleStart + particleIndex;
                }

                depthRanges[rangeIndex++] = new PhysicsChainDepthRange(
                    treeIndex,
                    depth,
                    rangeStart,
                    depthIndex - rangeStart);
            }

            particleStart += particles.Count;
        }

        return new PhysicsChainTemplate(
            trees,
            particlesData,
            depthOrderedIndices,
            depthRanges,
            (int)FreezeAxis);
    }

    private static int CalculateMaximumDepth(List<Particle> particles)
    {
        int maximumDepth = -1;
        for (int particleIndex = 0; particleIndex < particles.Count; ++particleIndex)
            maximumDepth = Math.Max(maximumDepth, CalculateParticleDepth(particles, particleIndex));
        return maximumDepth;
    }

    private static int CalculateParticleDepth(List<Particle> particles, int particleIndex)
    {
        int depth = 0;
        int parentIndex = particles[particleIndex].ParentIndex;
        while (parentIndex >= 0)
        {
            if ((uint)parentIndex >= (uint)particleIndex)
                throw new InvalidOperationException("Physics-chain topology must store every parent before its children.");

            ++depth;
            parentIndex = particles[parentIndex].ParentIndex;
        }

        return depth;
    }
}
