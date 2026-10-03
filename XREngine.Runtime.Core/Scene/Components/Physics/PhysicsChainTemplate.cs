using System.Numerics;

namespace XREngine.Components;

/// <summary>
/// Immutable, content-addressed topology and authored solver data shared by
/// compatible physics-chain instances.
/// </summary>
public sealed class PhysicsChainTemplate
{
    private readonly PhysicsChainTemplateTree[] _trees;
    private readonly PhysicsChainTemplateParticle[] _particles;
    private readonly int[] _depthOrderedParticleIndices;
    private readonly PhysicsChainDepthRange[] _depthRanges;
    private readonly PhysicsChainInfluenceBounds[] _influenceBounds;
    private readonly PhysicsChainCoefficientPack[] _coefficientPacks;

    internal PhysicsChainTemplate(
        PhysicsChainTemplateTree[] trees,
        PhysicsChainTemplateParticle[] particles,
        int[] depthOrderedParticleIndices,
        PhysicsChainDepthRange[] depthRanges,
        int freezeAxis)
    {
        _trees = trees;
        _particles = particles;
        _depthOrderedParticleIndices = depthOrderedParticleIndices;
        _depthRanges = depthRanges;
        FreezeAxis = freezeAxis;
        ContentHash = CalculateContentHash();
        FeatureMask = CalculateFeatureMask();
        _influenceBounds = CalculateInfluenceBounds();
        _coefficientPacks = CalculateCoefficientPacks();
    }

    public ReadOnlyMemory<PhysicsChainTemplateTree> Trees => _trees;
    public ReadOnlyMemory<PhysicsChainTemplateParticle> Particles => _particles;
    public ReadOnlyMemory<int> DepthOrderedParticleIndices => _depthOrderedParticleIndices;
    public ReadOnlyMemory<PhysicsChainDepthRange> DepthRanges => _depthRanges;
    public ReadOnlyMemory<PhysicsChainInfluenceBounds> InfluenceBounds => _influenceBounds;
    public ReadOnlyMemory<PhysicsChainCoefficientPack> CoefficientPacks => _coefficientPacks;
    public int FreezeAxis { get; }
    public long StableId { get; private set; }
    public PhysicsChainTemplateFeatureMask FeatureMask { get; }
    public ulong ContentHash { get; }

    internal void AssignStableId(long stableId)
    {
        if (stableId <= 0L)
            throw new ArgumentOutOfRangeException(nameof(stableId));
        if (StableId == 0L)
        {
            StableId = stableId;
            return;
        }
        if (StableId != stableId)
            throw new InvalidOperationException("A physics-chain template cannot be reassigned to another stable ID.");
    }

    internal bool ContentEquals(PhysicsChainTemplate other)
    {
        if (ReferenceEquals(this, other))
            return true;
        if (ContentHash != other.ContentHash || FreezeAxis != other.FreezeAxis)
            return false;

        return _trees.AsSpan().SequenceEqual(other._trees)
            && _particles.AsSpan().SequenceEqual(other._particles)
            && _depthOrderedParticleIndices.AsSpan().SequenceEqual(other._depthOrderedParticleIndices)
            && _depthRanges.AsSpan().SequenceEqual(other._depthRanges);
    }

    private PhysicsChainTemplateFeatureMask CalculateFeatureMask()
    {
        PhysicsChainTemplateFeatureMask mask = FreezeAxis == 0
            ? PhysicsChainTemplateFeatureMask.None
            : PhysicsChainTemplateFeatureMask.FreezeAxis;

        for (int i = 0; i < _particles.Length; ++i)
        {
            PhysicsChainTemplateParticle particle = _particles[i];
            if (particle.ChildCount > 1)
                mask |= PhysicsChainTemplateFeatureMask.BranchedTopology;
            if (particle.Radius > 0.0f)
                mask |= PhysicsChainTemplateFeatureMask.ParticleRadius;
            if (particle.Elasticity > 0.0f)
                mask |= PhysicsChainTemplateFeatureMask.Elasticity;
            if (particle.Stiffness > 0.0f)
                mask |= PhysicsChainTemplateFeatureMask.Stiffness;
            if (particle.Inert > 0.0f)
                mask |= PhysicsChainTemplateFeatureMask.Inertia;
            if (particle.Friction > 0.0f)
                mask |= PhysicsChainTemplateFeatureMask.Friction;
        }

        return mask;
    }

    private PhysicsChainInfluenceBounds[] CalculateInfluenceBounds()
    {
        var bounds = new PhysicsChainInfluenceBounds[_trees.Length];
        for (int treeIndex = 0; treeIndex < _trees.Length; ++treeIndex)
        {
            PhysicsChainTemplateTree tree = _trees[treeIndex];
            float maximumRadius = 0.0f;
            float maximumRestReach = 0.0f;
            int end = tree.ParticleStart + tree.ParticleCount;
            for (int particleIndex = tree.ParticleStart; particleIndex < end; ++particleIndex)
            {
                PhysicsChainTemplateParticle particle = _particles[particleIndex];
                maximumRadius = MathF.Max(maximumRadius, particle.Radius);
                maximumRestReach = MathF.Max(maximumRestReach, particle.RestOffset.Length());
            }

            // BoneTotalLength bounds articulated reach from the root; the
            // largest local rest offset also covers authored end particles.
            float influenceRadius = MathF.Max(tree.BoneTotalLength, maximumRestReach) + maximumRadius;
            bounds[treeIndex] = new PhysicsChainInfluenceBounds(Vector3.Zero, influenceRadius);
        }

        return bounds;
    }

    private PhysicsChainCoefficientPack[] CalculateCoefficientPacks()
    {
        var packs = new PhysicsChainCoefficientPack[_particles.Length];
        for (int i = 0; i < _particles.Length; ++i)
        {
            PhysicsChainTemplateParticle particle = _particles[i];
            packs[i] = new PhysicsChainCoefficientPack(
                new Vector4(
                    particle.Damping,
                    particle.Elasticity,
                    particle.Stiffness,
                    particle.Inert),
                new Vector4(
                    particle.Friction,
                    particle.Radius,
                    particle.SegmentLength,
                    particle.InverseSegmentLength),
                particle.BoneLength);
        }
        return packs;
    }

    private ulong CalculateContentHash()
    {
        var hash = new StableHash64();
        hash.Add(FreezeAxis);
        hash.Add(_trees.Length);
        for (int i = 0; i < _trees.Length; ++i)
        {
            PhysicsChainTemplateTree tree = _trees[i];
            hash.Add(tree.ParticleStart);
            hash.Add(tree.ParticleCount);
            hash.Add(tree.MaximumDepth);
            hash.Add(tree.BoneTotalLength);
        }

        hash.Add(_particles.Length);
        for (int i = 0; i < _particles.Length; ++i)
        {
            PhysicsChainTemplateParticle particle = _particles[i];
            hash.Add(particle.ParentIndex);
            hash.Add(particle.Depth);
            hash.Add(particle.BoneIndex);
            hash.Add(particle.ChildCount);
            hash.Add(particle.SegmentLength);
            hash.Add(particle.InverseSegmentLength);
            hash.Add(particle.BoneLength);
            hash.Add(particle.Damping);
            hash.Add(particle.Elasticity);
            hash.Add(particle.Stiffness);
            hash.Add(particle.Inert);
            hash.Add(particle.Friction);
            hash.Add(particle.Radius);
            hash.Add(particle.RestOffset);
            hash.Add(particle.RestRotation);
        }

        hash.Add(_depthOrderedParticleIndices.Length);
        for (int i = 0; i < _depthOrderedParticleIndices.Length; ++i)
            hash.Add(_depthOrderedParticleIndices[i]);

        hash.Add(_depthRanges.Length);
        for (int i = 0; i < _depthRanges.Length; ++i)
        {
            PhysicsChainDepthRange range = _depthRanges[i];
            hash.Add(range.TreeIndex);
            hash.Add(range.Depth);
            hash.Add(range.IndexStart);
            hash.Add(range.IndexCount);
        }

        return hash.Value;
    }

    private struct StableHash64
    {
        private const ulong Offset = 14695981039346656037UL;
        private const ulong Prime = 1099511628211UL;
        private ulong _value;

        public readonly ulong Value => _value == 0UL ? Offset : _value;

        public void Add(int value)
            => Add(unchecked((uint)value));

        public void Add(float value)
            => Add(BitConverter.SingleToUInt32Bits(value));

        public void Add(Vector3 value)
        {
            Add(value.X);
            Add(value.Y);
            Add(value.Z);
        }

        public void Add(Quaternion value)
        {
            Add(value.X);
            Add(value.Y);
            Add(value.Z);
            Add(value.W);
        }

        private void Add(uint value)
        {
            if (_value == 0UL)
                _value = Offset;

            for (int shift = 0; shift < 32; shift += 8)
            {
                _value ^= (byte)(value >> shift);
                _value *= Prime;
            }
        }
    }
}
