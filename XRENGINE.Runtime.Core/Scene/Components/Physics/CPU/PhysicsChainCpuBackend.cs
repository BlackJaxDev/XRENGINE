using System.Diagnostics;
using System.Numerics;

namespace XREngine.Components;

/// <summary>
/// World-owned generational CPU runtime built on stable instance slots and the
/// deterministic scalar kernel. Structural APIs may allocate; steady stepping
/// and requested output generation mutate only preallocated storage.
/// </summary>
public sealed partial class PhysicsChainCpuBackend : IPhysicsChainCpuBatchExecutor
{
    private readonly PhysicsChainSlotArena<PhysicsChainCpuRuntimeInstance> _instances;
    private int _liveParticleCount;
    private int _liveColliderCount;
    private int _sharedColliderReferenceCount;
    private long _sharedColliderQueryCount;
    private long _sharedColliderFullSetFallbackCount;

    public PhysicsChainCpuBackend(int initialInstanceCapacity = 16)
        => _instances = new PhysicsChainSlotArena<PhysicsChainCpuRuntimeInstance>(initialInstanceCapacity);

    public PhysicsChainArenaHandle Register(
        PhysicsChainTemplate template,
        in PhysicsChainCpuInput input,
        ReadOnlySpan<PhysicsChainCpuTreeInput> treeInputs,
        ReadOnlySpan<PhysicsChainCpuParticleInput> particleInputs,
        ReadOnlySpan<PhysicsChainCpuState> initialStates = default,
        ReadOnlySpan<PhysicsChainCpuCollider> colliders = default,
        PhysicsChainCpuConsumerFlags consumerFlags = PhysicsChainCpuConsumerFlags.None,
        ReadOnlySpan<float> influenceRadii = default,
        PhysicsChainCpuMirrorPolicy mirrorPolicy = default)
    {
        ArgumentNullException.ThrowIfNull(template);
        int treeCount = template.Trees.Length;
        int particleCount = template.Particles.Length;
        if (treeCount <= 0 || particleCount <= 0)
            throw new ArgumentException("CPU physics-chain templates must contain at least one tree and particle.", nameof(template));
        if (treeInputs.Length != treeCount)
            throw new ArgumentException("Tree input count must match the template.", nameof(treeInputs));
        if (particleInputs.Length != particleCount)
            throw new ArgumentException("Particle input count must match the template.", nameof(particleInputs));
        if (!initialStates.IsEmpty && initialStates.Length != particleCount)
            throw new ArgumentException("Initial state count must be empty or match the template.", nameof(initialStates));
        if (!influenceRadii.IsEmpty && influenceRadii.Length != particleCount)
            throw new ArgumentException("Influence-radius count must be empty or match the template.", nameof(influenceRadii));
        if ((consumerFlags & ~(PhysicsChainCpuConsumerFlags.Palette | PhysicsChainCpuConsumerFlags.Bounds | PhysicsChainCpuConsumerFlags.TransformMirror)) != 0)
            throw new ArgumentOutOfRangeException(nameof(consumerFlags));
        if (!mirrorPolicy.IsValid)
            throw new ArgumentOutOfRangeException(nameof(mirrorPolicy));
        if ((consumerFlags & PhysicsChainCpuConsumerFlags.TransformMirror) != 0 && !mirrorPolicy.Enabled)
            mirrorPolicy = PhysicsChainCpuMirrorPolicy.EveryFrame;


        PhysicsChainCpuTreeInput[] ownedTreeInputs = treeInputs.ToArray();
        PhysicsChainCpuParticleInput[] ownedParticleInputs = particleInputs.ToArray();
        PhysicsChainCpuCollider[] ownedColliders = colliders.ToArray();
        float[] ownedInfluenceRadii = CreateInfluenceRadii(template, influenceRadii);
        var ownedStates = new PhysicsChainCpuState[particleCount];
        var ownedOutputs = new PhysicsChainCpuOutput[particleCount];
        PhysicsChainCpuInput resetInput = input with { ResetState = 1u };
        if (!PhysicsChainScalarReferenceKernel.TryStep(
            template, resetInput, ownedTreeInputs, ownedParticleInputs, ownedColliders, ownedStates, ownedOutputs))
            throw new ArgumentException("The initial CPU physics-chain inputs are invalid.", nameof(input));

        if (!initialStates.IsEmpty)
        {
            initialStates.CopyTo(ownedStates);
            PublishInitialOutputs(ownedStates, ownedOutputs);
        }

        var instance = new PhysicsChainCpuRuntimeInstance(
            template, input with { ResetState = 0u }, ownedTreeInputs, ownedParticleInputs,
            ownedColliders, null, ownedInfluenceRadii, consumerFlags, mirrorPolicy, ownedStates, ownedOutputs);
        GenerateRequestedOutputs(instance, resetHistory: true);
        PhysicsChainArenaHandle handle = _instances.Allocate(instance);
        _liveParticleCount = checked(_liveParticleCount + particleCount);
        _liveColliderCount = checked(_liveColliderCount + ownedColliders.Length);
        return handle;
    }


    /// <summary>
    /// Registers an instance against a world-shared collider resource. Only
    /// bounded candidate/traversal scratch is instance-owned; collider records
    /// and broadphase topology remain shared.
    /// </summary>
    public PhysicsChainArenaHandle RegisterShared(
        PhysicsChainTemplate template,
        in PhysicsChainCpuInput input,
        ReadOnlySpan<PhysicsChainCpuTreeInput> treeInputs,
        ReadOnlySpan<PhysicsChainCpuParticleInput> particleInputs,
        PhysicsChainCpuSharedColliderSet sharedColliderSet,
        ReadOnlySpan<PhysicsChainCpuState> initialStates = default,
        PhysicsChainCpuConsumerFlags consumerFlags = PhysicsChainCpuConsumerFlags.None,
        ReadOnlySpan<float> influenceRadii = default,
        PhysicsChainCpuMirrorPolicy mirrorPolicy = default)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(sharedColliderSet);
        int treeCount = template.Trees.Length;
        int particleCount = template.Particles.Length;
        if (treeCount <= 0 || particleCount <= 0)
            throw new ArgumentException("CPU physics-chain templates must contain at least one tree and particle.", nameof(template));
        if (treeInputs.Length != treeCount)
            throw new ArgumentException("Tree input count must match the template.", nameof(treeInputs));
        if (particleInputs.Length != particleCount)
            throw new ArgumentException("Particle input count must match the template.", nameof(particleInputs));
        if (!initialStates.IsEmpty && initialStates.Length != particleCount)
            throw new ArgumentException("Initial state count must be empty or match the template.", nameof(initialStates));
        if (!influenceRadii.IsEmpty && influenceRadii.Length != particleCount)
            throw new ArgumentException("Influence-radius count must be empty or match the template.", nameof(influenceRadii));
        if ((consumerFlags & ~(PhysicsChainCpuConsumerFlags.Palette | PhysicsChainCpuConsumerFlags.Bounds | PhysicsChainCpuConsumerFlags.TransformMirror)) != 0)
            throw new ArgumentOutOfRangeException(nameof(consumerFlags));
        if (!mirrorPolicy.IsValid)
            throw new ArgumentOutOfRangeException(nameof(mirrorPolicy));
        if ((consumerFlags & PhysicsChainCpuConsumerFlags.TransformMirror) != 0 && !mirrorPolicy.Enabled)
            mirrorPolicy = PhysicsChainCpuMirrorPolicy.EveryFrame;

        PhysicsChainCpuTreeInput[] ownedTreeInputs = treeInputs.ToArray();
        PhysicsChainCpuParticleInput[] ownedParticleInputs = particleInputs.ToArray();
        float[] ownedInfluenceRadii = CreateInfluenceRadii(template, influenceRadii);
        var ownedStates = new PhysicsChainCpuState[particleCount];
        var ownedOutputs = new PhysicsChainCpuOutput[particleCount];
        PhysicsChainCpuInput resetInput = input with { ResetState = 1u };
        if (!PhysicsChainScalarReferenceKernel.TryStep(
            template, resetInput, ownedTreeInputs, ownedParticleInputs, sharedColliderSet.WorldColliders, ownedStates, ownedOutputs))
            throw new ArgumentException("The initial CPU physics-chain inputs are invalid.", nameof(input));

        if (!initialStates.IsEmpty)
        {
            initialStates.CopyTo(ownedStates);
            PublishInitialOutputs(ownedStates, ownedOutputs);
        }

        var instance = new PhysicsChainCpuRuntimeInstance(
            template, input with { ResetState = 0u }, ownedTreeInputs, ownedParticleInputs,
            [], sharedColliderSet, ownedInfluenceRadii, consumerFlags, mirrorPolicy, ownedStates, ownedOutputs);
        GenerateRequestedOutputs(instance, resetHistory: true);
        PhysicsChainArenaHandle handle = _instances.Allocate(instance);
        _liveParticleCount = checked(_liveParticleCount + particleCount);
        ++_sharedColliderReferenceCount;
        return handle;
    }
    public bool Remove(PhysicsChainArenaHandle handle)
    {
        if (!_instances.TryGet(handle, out PhysicsChainCpuRuntimeInstance? instance) || !_instances.Free(handle))
            return false;
        _liveParticleCount -= instance.States.Length;
        _liveColliderCount -= instance.Colliders.Length;
        if (instance.SharedColliderSet is not null)
            --_sharedColliderReferenceCount;
        return true;
    }

    public bool IsCurrent(PhysicsChainArenaHandle handle) => _instances.IsCurrent(handle);

    public bool TryUpdateInput(PhysicsChainArenaHandle handle, in PhysicsChainCpuInput input)
    {
        if (!_instances.TryGet(handle, out PhysicsChainCpuRuntimeInstance? instance))
            return false;
        instance.Input = input with { ResetState = 0u };
        return true;
    }

    public bool TryUpdateTreeInputs(PhysicsChainArenaHandle handle, ReadOnlySpan<PhysicsChainCpuTreeInput> inputs)
    {
        if (!_instances.TryGet(handle, out PhysicsChainCpuRuntimeInstance? instance) || inputs.Length != instance.TreeInputs.Length)
            return false;
        inputs.CopyTo(instance.TreeInputs);
        return true;
    }

    public bool TryUpdateParticleInputs(PhysicsChainArenaHandle handle, ReadOnlySpan<PhysicsChainCpuParticleInput> inputs)
    {
        if (!_instances.TryGet(handle, out PhysicsChainCpuRuntimeInstance? instance) || inputs.Length != instance.ParticleInputs.Length)
            return false;
        inputs.CopyTo(instance.ParticleInputs);
        return true;
    }

    public bool TryUpdateColliders(PhysicsChainArenaHandle handle, ReadOnlySpan<PhysicsChainCpuCollider> colliders)
    {
        if (!_instances.TryGet(handle, out PhysicsChainCpuRuntimeInstance? instance) || colliders.Length != instance.Colliders.Length)
            return false;
        colliders.CopyTo(instance.Colliders);
        return true;
    }

    public bool TryReset(PhysicsChainArenaHandle handle)
    {
        if (!_instances.TryGet(handle, out PhysicsChainCpuRuntimeInstance? instance))
            return false;
        if (!TryResolveStepColliders(instance, out ReadOnlySpan<PhysicsChainCpuCollider> colliders))
            return false;
        PhysicsChainCpuInput resetInput = instance.Input with { ResetState = 1u };
        if (!PhysicsChainScalarReferenceKernel.TryStep(
            instance.Template, resetInput, instance.TreeInputs, instance.ParticleInputs,
            colliders, instance.States, instance.Outputs))
            return false;
        instance.SimulationFrame = 0L;
        GenerateRequestedOutputs(instance, resetHistory: true);
        instance.OutputGeneration = NextGeneration(instance.OutputGeneration);
        return true;
    }

    public bool TryStep(PhysicsChainArenaHandle handle)
    {
        if (!_instances.TryGet(handle, out PhysicsChainCpuRuntimeInstance? instance))
            return false;
        return StepInstance(instance);
    }

    /// <summary>
    /// Steps a coarse stable-handle range. Compatible linear runs are consumed
    /// eight at a time by AVX2; branched, collider, reset, capability, and tail
    /// cases select explicit scalar families without allocating.
    /// </summary>
    public bool TryStepBatch(ReadOnlySpan<PhysicsChainArenaHandle> handles)
    {
        // Validate the complete coarse range before mutating any lane so a
        // rejected component input cannot cause successful neighbors to step
        // twice when the world falls back to per-component execution.
        for (int handleIndex = 0; handleIndex < handles.Length; ++handleIndex)
        {
            if (!_instances.TryGet(handles[handleIndex], out PhysicsChainCpuRuntimeInstance? candidate)
                || !ValidateInstance(candidate))
                return false;
        }
        bool allSucceeded = true;
        StepSharedColliderGroups(handles, ref allSucceeded);


        int index = 0;
        while (index <= handles.Length - PhysicsChainCpuKernelSelector.Avx2BatchWidth)
        {
            if (TryResolveAvx2Group(handles.Slice(index, PhysicsChainCpuKernelSelector.Avx2BatchWidth),
                out PhysicsChainCpuRuntimeInstance? a, out PhysicsChainCpuRuntimeInstance? b,
                out PhysicsChainCpuRuntimeInstance? c, out PhysicsChainCpuRuntimeInstance? d,
                out PhysicsChainCpuRuntimeInstance? e, out PhysicsChainCpuRuntimeInstance? f,
                out PhysicsChainCpuRuntimeInstance? g, out PhysicsChainCpuRuntimeInstance? h)
                && PhysicsChainAvx2LinearBatchKernel.TryStep8(a, b, c, d, e, f, g, h))
            {
                CompleteStep(a, PhysicsChainCpuKernelFamily.Avx2LinearBatch);
                CompleteStep(b, PhysicsChainCpuKernelFamily.Avx2LinearBatch);
                CompleteStep(c, PhysicsChainCpuKernelFamily.Avx2LinearBatch);
                CompleteStep(d, PhysicsChainCpuKernelFamily.Avx2LinearBatch);
                CompleteStep(e, PhysicsChainCpuKernelFamily.Avx2LinearBatch);
                CompleteStep(f, PhysicsChainCpuKernelFamily.Avx2LinearBatch);
                CompleteStep(g, PhysicsChainCpuKernelFamily.Avx2LinearBatch);
                CompleteStep(h, PhysicsChainCpuKernelFamily.Avx2LinearBatch);
                index += PhysicsChainCpuKernelSelector.Avx2BatchWidth;
                continue;
            }

            if (!_instances.TryGet(handles[index], out PhysicsChainCpuRuntimeInstance? scalar)
                || (scalar.SharedColliderSet is null && !StepInstance(scalar)))
                allSucceeded = false;
            ++index;
        }

        for (; index < handles.Length; ++index)
            if (!_instances.TryGet(handles[index], out PhysicsChainCpuRuntimeInstance? scalar)
                || (scalar.SharedColliderSet is null && !StepInstance(scalar)))
                allSucceeded = false;
        return allSucceeded;
    }

    public bool TryGetInstance(PhysicsChainArenaHandle handle, out PhysicsChainCpuInstance instance)
    {
        if (!_instances.TryGet(handle, out PhysicsChainCpuRuntimeInstance? runtime))
        {
            instance = default;
            return false;
        }
        instance = new PhysicsChainCpuInstance(
            handle, runtime.Template.ContentHash, runtime.TreeInputs.Length, runtime.States.Length,
            runtime.EffectiveColliderCount, runtime.SimulationFrame, runtime.OutputGeneration,
            runtime.LastKernelFamily);
        return true;
    }

    public bool TryGetState(PhysicsChainArenaHandle handle, int particleIndex, out PhysicsChainCpuState state)
    {
        if (!_instances.TryGet(handle, out PhysicsChainCpuRuntimeInstance? instance) || (uint)particleIndex >= (uint)instance.States.Length)
        {
            state = default;
            return false;
        }
        state = instance.States[particleIndex];
        return true;
    }

    public bool TryGetOutput(PhysicsChainArenaHandle handle, int particleIndex, out PhysicsChainCpuOutput output)
    {
        if (!_instances.TryGet(handle, out PhysicsChainCpuRuntimeInstance? instance) || (uint)particleIndex >= (uint)instance.Outputs.Length)
        {
            output = default;
            return false;
        }
        output = instance.Outputs[particleIndex];
        return true;
    }

    public bool TryCopyOutputs(PhysicsChainArenaHandle handle, Span<PhysicsChainCpuOutput> destination)
    {
        if (!_instances.TryGet(handle, out PhysicsChainCpuRuntimeInstance? instance) || destination.Length < instance.Outputs.Length)
            return false;
        instance.Outputs.CopyTo(destination);
        return true;
    }

    public bool TryCopyCurrentPalette(PhysicsChainArenaHandle handle, Span<Matrix4x4> destination)
        => TryCopyPalette(handle, destination, previous: false);

    public bool TryCopyPreviousPalette(PhysicsChainArenaHandle handle, Span<Matrix4x4> destination)
        => TryCopyPalette(handle, destination, previous: true);

    public bool TryUpdateOutputPolicy(PhysicsChainArenaHandle handle, in PhysicsChainCpuOutputPolicy policy)
    {
        if (!policy.IsValid || !_instances.TryGet(handle, out PhysicsChainCpuRuntimeInstance? instance))
            return false;
        instance.OutputPolicy = policy;
        if (policy.PaletteCadence == PhysicsChainOutputCadence.Hold && instance.PreviousPalette.Length != 0)
            instance.CurrentPalette.CopyTo(instance.PreviousPalette, 0);
        return true;
    }

    public bool TryCopyInterpolatedPalette(
        PhysicsChainArenaHandle handle,
        Span<Matrix4x4> destination,
        float alpha)
    {
        if (!_instances.TryGet(handle, out PhysicsChainCpuRuntimeInstance? instance)
            || (instance.ConsumerFlags & PhysicsChainCpuConsumerFlags.Palette) == 0
            || destination.Length < instance.CurrentPalette.Length)
            return false;
        for (int i = 0; i < instance.CurrentPalette.Length; ++i)
            destination[i] = PhysicsChainPaletteInterpolation.Interpolate(instance.PreviousPalette[i], instance.CurrentPalette[i], alpha);
        return true;
    }

    /// <summary>
    /// Collapses palette history to the current pose when simulation is held,
    /// preventing interpolation and motion consumers from observing stale motion.
    /// </summary>
    public bool HoldOutputHistory(PhysicsChainArenaHandle handle)
    {
        if (!_instances.TryGet(handle, out PhysicsChainCpuRuntimeInstance? instance)
            || (instance.ConsumerFlags & PhysicsChainCpuConsumerFlags.Palette) == 0)
            return false;
        instance.CurrentPalette.CopyTo(instance.PreviousPalette, 0);
        return true;
    }

    public bool TryCopyTransformMirror(PhysicsChainArenaHandle handle, Span<Matrix4x4> destination)
    {
        if (!_instances.TryGet(handle, out PhysicsChainCpuRuntimeInstance? instance)
            || (instance.ConsumerFlags & PhysicsChainCpuConsumerFlags.TransformMirror) == 0
            || destination.Length < instance.TransformMirror.Length)
            return false;
        instance.TransformMirror.CopyTo(destination);
        return true;
    }

    public bool TryGetBounds(PhysicsChainArenaHandle handle, out PhysicsChainCpuBounds bounds)
    {
        if (!_instances.TryGet(handle, out PhysicsChainCpuRuntimeInstance? instance)
            || (instance.ConsumerFlags & PhysicsChainCpuConsumerFlags.Bounds) == 0)
        {
            bounds = PhysicsChainCpuBounds.Invalid;
            return false;
        }
        bounds = instance.Bounds;
        return bounds.IsValid;
    }

    /// <summary>Returns stable shared palette slices without copying.</summary>
    public bool TryGetRenderOutput(PhysicsChainArenaHandle handle, out PhysicsChainCpuRenderOutput output)
    {
        if (!_instances.TryGet(handle, out PhysicsChainCpuRuntimeInstance? instance))
        {
            output = default;
            return false;
        }
        output = new PhysicsChainCpuRenderOutput(
            instance.CurrentPalette,
            instance.PreviousPalette,
            instance.Bounds,
            instance.ConsumerFlags,
            instance.SimulationFrame,
            instance.OutputGeneration,
            instance.TransformMirrorAgeFrames,
            instance.TransformMirrorCostTicks);
        return instance.ConsumerFlags != PhysicsChainCpuConsumerFlags.None;
    }

    public PhysicsChainCpuBackendSnapshot GetSnapshot()
    {
        PhysicsChainArenaSnapshot arena = _instances.GetSnapshot();
        return new PhysicsChainCpuBackendSnapshot(
            arena.Capacity,
            arena.LiveCount,
            arena.GrowthCount,
            _liveParticleCount,
            _liveColliderCount,
            _sharedColliderReferenceCount,
            Interlocked.Read(ref _sharedColliderQueryCount),
            Interlocked.Read(ref _sharedColliderFullSetFallbackCount),
            Interlocked.Read(ref _sharedColliderBatchGroupCount),
            Interlocked.Read(ref _sharedColliderGroupedInstanceCount));
    }

    private bool TryCopyPalette(PhysicsChainArenaHandle handle, Span<Matrix4x4> destination, bool previous)
    {
        if (!_instances.TryGet(handle, out PhysicsChainCpuRuntimeInstance? instance)
            || (instance.ConsumerFlags & PhysicsChainCpuConsumerFlags.Palette) == 0)
            return false;
        Matrix4x4[] source = previous ? instance.PreviousPalette : instance.CurrentPalette;
        if (destination.Length < source.Length)
            return false;
        source.CopyTo(destination);
        return true;
    }

    private bool StepInstance(PhysicsChainCpuRuntimeInstance instance)
    {
        if (!TryResolveStepColliders(instance, out ReadOnlySpan<PhysicsChainCpuCollider> colliders))
            return false;
        PhysicsChainCpuKernelFamily family = PhysicsChainCpuKernelSelector.Select(
            instance.Template, compatibleInstanceCount: 1, colliders.Length);
        bool succeeded = family == PhysicsChainCpuKernelFamily.DepthOrderedBranched
            ? PhysicsChainDepthOrderedBranchedKernel.TryStep(
                instance.Template, instance.Input, instance.TreeInputs, instance.ParticleInputs,
                colliders, instance.States, instance.Outputs)
            : PhysicsChainScalarReferenceKernel.TryStep(
                instance.Template, instance.Input, instance.TreeInputs, instance.ParticleInputs,
                colliders, instance.States, instance.Outputs);
        if (!succeeded)
            return false;
        CompleteStep(instance, family);
        return true;
    }

    private static bool ValidateInstance(PhysicsChainCpuRuntimeInstance instance)
        => PhysicsChainScalarReferenceKernel.ValidateContract(
            instance.Input, instance.Template.Trees.Span, instance.Template.Particles.Span,
            instance.TreeInputs, instance.ParticleInputs, instance.States, instance.Outputs);

    private bool TryResolveAvx2Group(
        ReadOnlySpan<PhysicsChainArenaHandle> handles,
        out PhysicsChainCpuRuntimeInstance a, out PhysicsChainCpuRuntimeInstance b,
        out PhysicsChainCpuRuntimeInstance c, out PhysicsChainCpuRuntimeInstance d,
        out PhysicsChainCpuRuntimeInstance e, out PhysicsChainCpuRuntimeInstance f,
        out PhysicsChainCpuRuntimeInstance g, out PhysicsChainCpuRuntimeInstance h)
    {
        a = b = c = d = e = f = g = h = null!;
        if (!_instances.TryGet(handles[0], out a!) || !_instances.TryGet(handles[1], out b!)
            || !_instances.TryGet(handles[2], out c!) || !_instances.TryGet(handles[3], out d!)
            || !_instances.TryGet(handles[4], out e!) || !_instances.TryGet(handles[5], out f!)
            || !_instances.TryGet(handles[6], out g!) || !_instances.TryGet(handles[7], out h!))
            return false;
        PhysicsChainTemplate template = a.Template;
        if (!ReferenceEquals(template, b.Template) || !ReferenceEquals(template, c.Template)
            || !ReferenceEquals(template, d.Template) || !ReferenceEquals(template, e.Template)
            || !ReferenceEquals(template, f.Template) || !ReferenceEquals(template, g.Template)
            || !ReferenceEquals(template, h.Template))
            return false;
        if (a.EffectiveColliderCount != 0 || b.EffectiveColliderCount != 0 || c.EffectiveColliderCount != 0 || d.EffectiveColliderCount != 0
            || e.EffectiveColliderCount != 0 || f.EffectiveColliderCount != 0 || g.EffectiveColliderCount != 0 || h.EffectiveColliderCount != 0)
            return false;
        return PhysicsChainCpuKernelSelector.Select(template, PhysicsChainCpuKernelSelector.Avx2BatchWidth, 0)
            == PhysicsChainCpuKernelFamily.Avx2LinearBatch;
    }

    private bool TryResolveStepColliders(
        PhysicsChainCpuRuntimeInstance instance,
        out ReadOnlySpan<PhysicsChainCpuCollider> colliders)
    {
        PhysicsChainCpuSharedColliderSet? shared = instance.SharedColliderSet;
        if (shared is null)
        {
            colliders = instance.Colliders;
            return true;
        }

        PhysicsChainAabb sweptBounds = CalculateSweptBounds(instance);
        bool built = shared.TryBuildCandidates(
            sweptBounds,
            instance.CandidateColliders,
            instance.CandidateIndices,
            instance.TraversalStack,
            out int candidateCount,
            out bool usedFullSetFallback);
        Interlocked.Increment(ref _sharedColliderQueryCount);
        if (usedFullSetFallback)
        {
            Interlocked.Increment(ref _sharedColliderFullSetFallbackCount);
            colliders = shared.WorldColliders;
            return true;
        }
        if (!built)
        {
            colliders = default;
            return false;
        }
        colliders = instance.CandidateColliders.AsSpan(0, candidateCount);
        return true;
    }

    private static PhysicsChainAabb CalculateSweptBounds(PhysicsChainCpuRuntimeInstance instance)
    {
        Vector3 minimum = new(float.PositiveInfinity);
        Vector3 maximum = new(float.NegativeInfinity);
        float scale = MathF.Abs(instance.Input.ObjectScale);
        for (int particleIndex = 0; particleIndex < instance.States.Length; ++particleIndex)
        {
            PhysicsChainCpuState state = instance.States[particleIndex];
            Vector3 rootInput = instance.ParticleInputs[particleIndex].LocalToWorld.Translation;
            float radius = instance.InfluenceRadii[particleIndex] * scale;
            Vector3 extent = new(radius);
            minimum = Vector3.Min(minimum, Vector3.Min(state.Position, Vector3.Min(state.PreviousPosition, rootInput)) - extent);
            maximum = Vector3.Max(maximum, Vector3.Max(state.Position, Vector3.Max(state.PreviousPosition, rootInput)) + extent);
        }
        float deltaTime = MathF.Abs(instance.Input.DeltaTime * instance.Input.Speed);
        float accelerationMargin = (instance.Input.Gravity.Length() + instance.Input.ExternalForce.Length())
            * deltaTime * deltaTime;
        float motionMargin = instance.Input.ObjectMove.Length() + accelerationMargin;
        return new PhysicsChainAabb(minimum, maximum).Expanded(motionMargin);
    }

    private static void CompleteStep(PhysicsChainCpuRuntimeInstance instance, PhysicsChainCpuKernelFamily family)
    {
        ++instance.SimulationFrame;
        GenerateRequestedOutputs(instance, resetHistory: false);
        instance.OutputGeneration = NextGeneration(instance.OutputGeneration);
        instance.LastKernelFamily = family;
    }

    private static float[] CreateInfluenceRadii(PhysicsChainTemplate template, ReadOnlySpan<float> influenceRadii)
    {
        int particleCount = template.Particles.Length;
        var owned = new float[particleCount];
        ReadOnlySpan<PhysicsChainTemplateParticle> particles = template.Particles.Span;
        for (int particleIndex = 0; particleIndex < particleCount; ++particleIndex)
        {
            float radius = influenceRadii.IsEmpty
                ? particles[particleIndex].Radius
                : influenceRadii[particleIndex];
            if (!float.IsFinite(radius) || radius < 0.0f)
                throw new ArgumentOutOfRangeException(nameof(influenceRadii), "Influence radii must be finite and non-negative.");
            owned[particleIndex] = radius;
        }

        if (influenceRadii.IsEmpty)
        {
            ReadOnlySpan<PhysicsChainTemplateTree> trees = template.Trees.Span;
            ReadOnlySpan<PhysicsChainInfluenceBounds> treeBounds = template.InfluenceBounds.Span;
            for (int treeIndex = 0; treeIndex < trees.Length; ++treeIndex)
                owned[trees[treeIndex].ParticleStart] = MathF.Max(owned[trees[treeIndex].ParticleStart], treeBounds[treeIndex].Radius);
        }

        return owned;
    }

    private static void GenerateRequestedOutputs(PhysicsChainCpuRuntimeInstance instance, bool resetHistory)
    {
        PhysicsChainCpuConsumerFlags flags = instance.ConsumerFlags;
        bool paletteDue = (flags & PhysicsChainCpuConsumerFlags.Palette) != 0 && (resetHistory
            || instance.OutputPolicy.PaletteCadence == PhysicsChainOutputCadence.EverySimulationStep);
        bool mirrorRequested = (flags & PhysicsChainCpuConsumerFlags.TransformMirror) != 0;
        bool mirrorDue = mirrorRequested
            && instance.OutputPolicy.TransformMirrorCadence == PhysicsChainOutputCadence.EverySimulationStep
            && (resetHistory || instance.LastTransformMirrorFrame < 0L
                || instance.SimulationFrame - instance.LastTransformMirrorFrame >= instance.MirrorPolicy.NormalizedInterval);
        if (paletteDue || mirrorDue)
        {
            if (!resetHistory && paletteDue)
                instance.CurrentPalette.CopyTo(instance.PreviousPalette, 0);

            long mirrorStart = mirrorDue ? Stopwatch.GetTimestamp() : 0L;
            for (int particleIndex = 0; particleIndex < instance.States.Length; ++particleIndex)
            {
                Matrix4x4 matrix = instance.ParticleInputs[particleIndex].LocalToWorld;
                matrix.Translation = instance.States[particleIndex].Position;
                if (paletteDue)
                    instance.CurrentPalette[particleIndex] = matrix;
                if (mirrorDue)
                    instance.TransformMirror[particleIndex] = matrix;
            }

            if (resetHistory && paletteDue)
                instance.CurrentPalette.CopyTo(instance.PreviousPalette, 0);
            if (mirrorDue)
            {
                instance.TransformMirrorCostTicks = Stopwatch.GetTimestamp() - mirrorStart;
                instance.LastTransformMirrorFrame = instance.SimulationFrame;
                instance.TransformMirrorAgeFrames = 0;
            }
        }
        if (mirrorRequested && !mirrorDue)
            ++instance.TransformMirrorAgeFrames;

        if ((flags & PhysicsChainCpuConsumerFlags.Bounds) != 0 && (resetHistory
            || instance.OutputPolicy.BoundsCadence == PhysicsChainOutputCadence.EverySimulationStep))
            instance.Bounds = CalculateBounds(instance.States, instance.InfluenceRadii, instance.Input.ObjectScale);
    }

    private static PhysicsChainCpuBounds CalculateBounds(
        ReadOnlySpan<PhysicsChainCpuState> states,
        ReadOnlySpan<float> influenceRadii,
        float objectScale)
    {
        Vector3 minimum = new(float.PositiveInfinity);
        Vector3 maximum = new(float.NegativeInfinity);
        for (int particleIndex = 0; particleIndex < states.Length; ++particleIndex)
        {
            Vector3 position = states[particleIndex].Position;
            float radius = influenceRadii[particleIndex] * objectScale;
            Vector3 extent = new(radius);
            minimum = Vector3.Min(minimum, position - extent);
            maximum = Vector3.Max(maximum, position + extent);
        }
        return new PhysicsChainCpuBounds(minimum, maximum);
    }

    private static void PublishInitialOutputs(ReadOnlySpan<PhysicsChainCpuState> states, Span<PhysicsChainCpuOutput> outputs)
    {
        for (int particleIndex = 0; particleIndex < states.Length; ++particleIndex)
        {
            outputs[particleIndex].CurrentPosition = states[particleIndex].Position;
            outputs[particleIndex].PreviousPosition = states[particleIndex].PreviousPosition;
            outputs[particleIndex].IsColliding = states[particleIndex].IsColliding;
        }
    }

    private static uint NextGeneration(uint generation)
    {
        unchecked { ++generation; }
        return generation == 0u ? 1u : generation;
    }
}
