using System.Numerics;

namespace XREngine.Components;

/// <summary>
/// Deterministic scalar correctness oracle for the data-oriented physics-chain
/// contract. Optimized scalar, SIMD, and GPU kernels are compared against this
/// implementation rather than against scene-transform side effects.
/// </summary>
public static class PhysicsChainScalarReferenceKernel
{
    private const float LengthEpsilonSquared = 1e-12f;

    /// <summary>
    /// Advances one fixed step. Invalid contracts are rejected before state or
    /// output spans are mutated. The method allocates no managed memory.
    /// </summary>
    public static bool TryStep(
        PhysicsChainTemplate template,
        in PhysicsChainCpuInput input,
        ReadOnlySpan<PhysicsChainCpuTreeInput> treeInputs,
        ReadOnlySpan<PhysicsChainCpuParticleInput> particleInputs,
        Span<PhysicsChainCpuState> states,
        Span<PhysicsChainCpuOutput> outputs)
        => TryStepCore(template, input, treeInputs, particleInputs, default, states, outputs);

    /// <summary>Advances one fixed step while testing the supplied compact colliders.</summary>
    public static bool TryStep(
        PhysicsChainTemplate template,
        in PhysicsChainCpuInput input,
        ReadOnlySpan<PhysicsChainCpuTreeInput> treeInputs,
        ReadOnlySpan<PhysicsChainCpuParticleInput> particleInputs,
        ReadOnlySpan<PhysicsChainCpuCollider> colliders,
        Span<PhysicsChainCpuState> states,
        Span<PhysicsChainCpuOutput> outputs)
        => TryStepCore(template, input, treeInputs, particleInputs, colliders, states, outputs);

    /// <summary>Explicit zero-collider specialization for callers that know the set is empty.</summary>
    public static bool TryStepNoColliders(
        PhysicsChainTemplate template,
        in PhysicsChainCpuInput input,
        ReadOnlySpan<PhysicsChainCpuTreeInput> treeInputs,
        ReadOnlySpan<PhysicsChainCpuParticleInput> particleInputs,
        Span<PhysicsChainCpuState> states,
        Span<PhysicsChainCpuOutput> outputs)
        => TryStepCore(template, input, treeInputs, particleInputs, default, states, outputs);

    /// <summary>
    /// Explicit unrolled specialization for one through four colliders. Returns
    /// false when the caller supplies a larger set.
    /// </summary>
    public static bool TryStepSmallColliderSet(
        PhysicsChainTemplate template,
        in PhysicsChainCpuInput input,
        ReadOnlySpan<PhysicsChainCpuTreeInput> treeInputs,
        ReadOnlySpan<PhysicsChainCpuParticleInput> particleInputs,
        ReadOnlySpan<PhysicsChainCpuCollider> colliders,
        Span<PhysicsChainCpuState> states,
        Span<PhysicsChainCpuOutput> outputs)
    {
        if ((uint)colliders.Length > 4u)
            return false;

        return TryStepCore(template, input, treeInputs, particleInputs, colliders, states, outputs);
    }

    private static bool TryStepCore(
        PhysicsChainTemplate template,
        in PhysicsChainCpuInput input,
        ReadOnlySpan<PhysicsChainCpuTreeInput> treeInputs,
        ReadOnlySpan<PhysicsChainCpuParticleInput> particleInputs,
        ReadOnlySpan<PhysicsChainCpuCollider> colliders,
        Span<PhysicsChainCpuState> states,
        Span<PhysicsChainCpuOutput> outputs)
    {
        ArgumentNullException.ThrowIfNull(template);

        ReadOnlySpan<PhysicsChainTemplateTree> trees = template.Trees.Span;
        ReadOnlySpan<PhysicsChainTemplateParticle> particles = template.Particles.Span;
        if (!ValidateContract(input, trees, particles, treeInputs, particleInputs, states, outputs))
            return false;

        if (input.ResetState != 0u)
        {
            Reset(particleInputs, states, outputs);
            return true;
        }

        float time = input.DeltaTime * input.Speed;
        for (int treeIndex = 0; treeIndex < trees.Length; ++treeIndex)
            IntegrateTree(trees[treeIndex], treeInputs[treeIndex], particles, particleInputs, states, input, time);

        ApplyConstraints(template, particleInputs, colliders, states, input, time);
        Publish(states, outputs);
        return true;
    }

    internal static bool ValidateContract(
        in PhysicsChainCpuInput input,
        ReadOnlySpan<PhysicsChainTemplateTree> trees,
        ReadOnlySpan<PhysicsChainTemplateParticle> particles,
        ReadOnlySpan<PhysicsChainCpuTreeInput> treeInputs,
        ReadOnlySpan<PhysicsChainCpuParticleInput> particleInputs,
        Span<PhysicsChainCpuState> states,
        Span<PhysicsChainCpuOutput> outputs)
    {
        if (!float.IsFinite(input.DeltaTime) || input.DeltaTime <= 0.0f
            || !float.IsFinite(input.Speed) || input.Speed < 0.0f
            || !float.IsFinite(input.ObjectScale) || input.ObjectScale < 0.0f
            || !float.IsFinite(input.Weight)
            || !IsFinite(input.Gravity)
            || !IsFinite(input.ExternalForce)
            || !IsFinite(input.ObjectMove))
            return false;
        if (treeInputs.Length != trees.Length
            || particleInputs.Length != particles.Length
            || states.Length != particles.Length
            || outputs.Length != particles.Length)
            return false;

        int expectedParticleStart = 0;
        for (int treeIndex = 0; treeIndex < trees.Length; ++treeIndex)
        {
            PhysicsChainTemplateTree tree = trees[treeIndex];
            if (tree.ParticleStart != expectedParticleStart || tree.ParticleCount <= 0
                || tree.ParticleStart > particles.Length - tree.ParticleCount
                || !IsFinite(treeInputs[treeIndex].RestGravity))
                return false;

            int treeEnd = tree.ParticleStart + tree.ParticleCount;
            for (int particleIndex = tree.ParticleStart; particleIndex < treeEnd; ++particleIndex)
            {
                PhysicsChainTemplateParticle particle = particles[particleIndex];
                int parentIndex = particle.ParentIndex;
                if (parentIndex >= 0 && (parentIndex < tree.ParticleStart || parentIndex >= particleIndex))
                    return false;
                if (!float.IsFinite(particle.SegmentLength) || particle.SegmentLength < 0.0f
                    || !float.IsFinite(particle.Damping)
                    || !float.IsFinite(particle.Elasticity)
                    || !float.IsFinite(particle.Stiffness)
                    || !float.IsFinite(particle.Inert)
                    || !IsFinite(particleInputs[particleIndex].LocalToWorld))
                    return false;
            }

            expectedParticleStart = treeEnd;
        }

        return expectedParticleStart == particles.Length;
    }

    private static void Reset(
        ReadOnlySpan<PhysicsChainCpuParticleInput> particleInputs,
        Span<PhysicsChainCpuState> states,
        Span<PhysicsChainCpuOutput> outputs)
    {
        for (int particleIndex = 0; particleIndex < states.Length; ++particleIndex)
        {
            Vector3 position = particleInputs[particleIndex].LocalToWorld.Translation;
            states[particleIndex].Position = position;
            states[particleIndex].PreviousPosition = position;
            states[particleIndex].IsColliding = 0u;
            outputs[particleIndex].CurrentPosition = position;
            outputs[particleIndex].PreviousPosition = position;
            outputs[particleIndex].IsColliding = 0u;
        }
    }

    private static void IntegrateTree(
        in PhysicsChainTemplateTree tree,
        in PhysicsChainCpuTreeInput treeInput,
        ReadOnlySpan<PhysicsChainTemplateParticle> particles,
        ReadOnlySpan<PhysicsChainCpuParticleInput> particleInputs,
        Span<PhysicsChainCpuState> states,
        in PhysicsChainCpuInput input,
        float time)
    {
        Vector3 force = input.Gravity;
        float gravityLengthSquared = force.LengthSquared();
        if (gravityLengthSquared > LengthEpsilonSquared)
        {
            Vector3 direction = force / MathF.Sqrt(gravityLengthSquared);
            force -= direction * MathF.Max(Vector3.Dot(treeInput.RestGravity, direction), 0.0f);
        }
        force = (force + input.ExternalForce) * (input.ObjectScale * time);

        int particleEnd = tree.ParticleStart + tree.ParticleCount;
        for (int particleIndex = tree.ParticleStart; particleIndex < particleEnd; ++particleIndex)
        {
            ref PhysicsChainCpuState state = ref states[particleIndex];
            PhysicsChainTemplateParticle particle = particles[particleIndex];
            if (particle.ParentIndex < 0)
            {
                state.PreviousPosition = state.Position;
                state.Position = particleInputs[particleIndex].LocalToWorld.Translation;
                continue;
            }

            Vector3 velocity = state.Position - state.PreviousPosition;
            Vector3 rootMove = input.ObjectMove * particle.Inert;
            float damping = particle.Damping;
            if (state.IsColliding != 0u)
                damping += particle.Friction;
            state.IsColliding = 0u;
            state.PreviousPosition = state.Position + rootMove;
            state.Position += velocity * (1.0f - Math.Clamp(damping, 0.0f, 1.0f)) + force + rootMove;
        }
    }

    internal static void ApplyConstraints(
        PhysicsChainTemplate template,
        ReadOnlySpan<PhysicsChainCpuParticleInput> particleInputs,
        ReadOnlySpan<PhysicsChainCpuCollider> colliders,
        Span<PhysicsChainCpuState> states,
        in PhysicsChainCpuInput input,
        float time)
    {
        ReadOnlySpan<PhysicsChainTemplateParticle> particles = template.Particles.Span;
        ReadOnlySpan<int> orderedIndices = template.DepthOrderedParticleIndices.Span;
        ReadOnlySpan<PhysicsChainDepthRange> ranges = template.DepthRanges.Span;
        for (int rangeIndex = 0; rangeIndex < ranges.Length; ++rangeIndex)
        {
            PhysicsChainDepthRange range = ranges[rangeIndex];
            if (range.Depth == 0)
                continue;

            int rangeEnd = range.IndexStart + range.IndexCount;
            for (int orderedIndex = range.IndexStart; orderedIndex < rangeEnd; ++orderedIndex)
            {
                int particleIndex = orderedIndices[orderedIndex];
                PhysicsChainTemplateParticle particle = particles[particleIndex];
                int parentIndex = particle.ParentIndex;
                ref PhysicsChainCpuState state = ref states[particleIndex];
                ref PhysicsChainCpuState parentState = ref states[parentIndex];

                Matrix4x4 restMatrix = particleInputs[parentIndex].LocalToWorld;
                restMatrix.Translation = parentState.Position;
                Vector3 restPosition = Vector3.Transform(particle.RestOffset, restMatrix);
                float stiffness = Lerp(1.0f, particle.Stiffness, input.Weight);
                if (stiffness > 0.0f || particle.Elasticity > 0.0f)
                {
                    state.Position += (restPosition - state.Position) * (particle.Elasticity * time);
                    if (stiffness > 0.0f)
                    {
                        Vector3 delta = restPosition - state.Position;
                        float lengthSquared = delta.LengthSquared();
                        float maximumLength = particle.SegmentLength * (1.0f - stiffness) * 2.0f;
                        if (lengthSquared > maximumLength * maximumLength && lengthSquared > LengthEpsilonSquared)
                        {
                            float length = MathF.Sqrt(lengthSquared);
                            state.Position += delta * ((length - maximumLength) / length);
                        }
                    }
                }

                state.IsColliding = input.CollisionEnabled != 0u
                    && Collide(ref state.Position, particle.Radius * input.ObjectScale, colliders) ? 1u : 0u;
                ApplyLengthConstraint(ref state.Position, parentState.Position, particle.SegmentLength);
            }
        }
    }

    private static void ApplyLengthConstraint(ref Vector3 position, Vector3 parentPosition, float segmentLength)
    {
        Vector3 toParent = parentPosition - position;
        float lengthSquared = toParent.LengthSquared();
        if (lengthSquared <= LengthEpsilonSquared)
            return;

        float length = MathF.Sqrt(lengthSquared);
        position += toParent * ((length - segmentLength) / length);
    }

    internal static void Publish(ReadOnlySpan<PhysicsChainCpuState> states, Span<PhysicsChainCpuOutput> outputs)
    {
        for (int particleIndex = 0; particleIndex < states.Length; ++particleIndex)
        {
            outputs[particleIndex].CurrentPosition = states[particleIndex].Position;
            outputs[particleIndex].PreviousPosition = states[particleIndex].PreviousPosition;
            outputs[particleIndex].IsColliding = states[particleIndex].IsColliding;
        }
    }

    private static bool Collide(
        ref Vector3 position,
        float particleRadius,
        ReadOnlySpan<PhysicsChainCpuCollider> colliders)
    {
        bool collided = false;
        switch (colliders.Length)
        {
            case 0:
                return false;
            case 1:
                return colliders[0].TryCollide(ref position, particleRadius);
            case 2:
                collided |= colliders[0].TryCollide(ref position, particleRadius);
                collided |= colliders[1].TryCollide(ref position, particleRadius);
                return collided;
            case 3:
                collided |= colliders[0].TryCollide(ref position, particleRadius);
                collided |= colliders[1].TryCollide(ref position, particleRadius);
                collided |= colliders[2].TryCollide(ref position, particleRadius);
                return collided;
            case 4:
                collided |= colliders[0].TryCollide(ref position, particleRadius);
                collided |= colliders[1].TryCollide(ref position, particleRadius);
                collided |= colliders[2].TryCollide(ref position, particleRadius);
                collided |= colliders[3].TryCollide(ref position, particleRadius);
                return collided;
            default:
                for (int colliderIndex = 0; colliderIndex < colliders.Length; ++colliderIndex)
                    collided |= colliders[colliderIndex].TryCollide(ref position, particleRadius);
                return collided;
        }
    }

    private static float Lerp(float from, float to, float amount)
        => from + ((to - from) * amount);

    private static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static bool IsFinite(Matrix4x4 value)
        => float.IsFinite(value.M11) && float.IsFinite(value.M12) && float.IsFinite(value.M13) && float.IsFinite(value.M14)
        && float.IsFinite(value.M21) && float.IsFinite(value.M22) && float.IsFinite(value.M23) && float.IsFinite(value.M24)
        && float.IsFinite(value.M31) && float.IsFinite(value.M32) && float.IsFinite(value.M33) && float.IsFinite(value.M34)
        && float.IsFinite(value.M41) && float.IsFinite(value.M42) && float.IsFinite(value.M43) && float.IsFinite(value.M44);
}
