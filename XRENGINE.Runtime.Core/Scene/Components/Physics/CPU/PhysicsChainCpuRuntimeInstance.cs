using System.Numerics;

namespace XREngine.Components;

/// <summary>
/// World-owned storage for one registered CPU chain. Arrays are allocated only
/// at structural registration/retemplate boundaries and remain stable while
/// the instance is live.
/// </summary>
internal sealed class PhysicsChainCpuRuntimeInstance
{
    public const int DefaultCandidateCapacity = 64;

    public PhysicsChainCpuRuntimeInstance(
        PhysicsChainTemplate template,
        in PhysicsChainCpuInput input,
        PhysicsChainCpuTreeInput[] treeInputs,
        PhysicsChainCpuParticleInput[] particleInputs,
        PhysicsChainCpuCollider[] colliders,
        PhysicsChainCpuSharedColliderSet? sharedColliderSet,
        float[] influenceRadii,
        PhysicsChainCpuConsumerFlags consumerFlags,
        PhysicsChainCpuMirrorPolicy mirrorPolicy,
        PhysicsChainCpuState[] states,
        PhysicsChainCpuOutput[] outputs)
    {
        Template = template;
        Input = input;
        TreeInputs = treeInputs;
        ParticleInputs = particleInputs;
        Colliders = colliders;
        SharedColliderSet = sharedColliderSet;
        InfluenceRadii = influenceRadii;
        ConsumerFlags = consumerFlags;
        MirrorPolicy = mirrorPolicy;
        States = states;
        Outputs = outputs;
        int candidateCapacity = sharedColliderSet is null
            ? 0
            : Math.Min(sharedColliderSet.ColliderCount, DefaultCandidateCapacity);
        CandidateColliders = candidateCapacity == 0 ? [] : new PhysicsChainCpuCollider[candidateCapacity];
        CandidateIndices = sharedColliderSet is not null && sharedColliderSet.ColliderCount > 4
            ? new int[candidateCapacity]
            : [];
        TraversalStack = sharedColliderSet is not null && sharedColliderSet.ColliderCount > 4
            ? new int[sharedColliderSet.RequiredTraversalStackLength]
            : [];

        bool needsPalette = (consumerFlags & (PhysicsChainCpuConsumerFlags.Palette | PhysicsChainCpuConsumerFlags.TransformMirror)) != 0;
        CurrentPalette = needsPalette ? new Matrix4x4[states.Length] : [];
        PreviousPalette = (consumerFlags & PhysicsChainCpuConsumerFlags.Palette) != 0
            ? new Matrix4x4[states.Length]
            : [];
        TransformMirror = (consumerFlags & PhysicsChainCpuConsumerFlags.TransformMirror) != 0
            ? new Matrix4x4[states.Length]
            : [];
    }

    public PhysicsChainTemplate Template { get; }
    public PhysicsChainCpuInput Input;
    public PhysicsChainCpuTreeInput[] TreeInputs { get; }
    public PhysicsChainCpuParticleInput[] ParticleInputs { get; }
    public PhysicsChainCpuCollider[] Colliders { get; }
    public PhysicsChainCpuSharedColliderSet? SharedColliderSet { get; }
    public PhysicsChainCpuCollider[] CandidateColliders { get; }
    public int[] CandidateIndices { get; }
    public int[] TraversalStack { get; }
    public int EffectiveColliderCount => SharedColliderSet?.ColliderCount ?? Colliders.Length;
    public PhysicsChainCpuMirrorPolicy MirrorPolicy { get; }
    public PhysicsChainCpuOutputPolicy OutputPolicy = PhysicsChainCpuOutputPolicy.EverySimulationStep;
    public float[] InfluenceRadii { get; }
    public PhysicsChainCpuConsumerFlags ConsumerFlags { get; }
    public PhysicsChainCpuState[] States { get; }
    public PhysicsChainCpuOutput[] Outputs { get; }
    public Matrix4x4[] CurrentPalette { get; }
    public Matrix4x4[] PreviousPalette { get; }
    public Matrix4x4[] TransformMirror { get; }
    public PhysicsChainCpuBounds Bounds = PhysicsChainCpuBounds.Invalid;
    public long SimulationFrame;
    public uint OutputGeneration = 1u;
    public PhysicsChainCpuKernelFamily LastKernelFamily = PhysicsChainCpuKernelFamily.ScalarLinear;
    public long LastTransformMirrorFrame = -1L;
    public int TransformMirrorAgeFrames;
    public long TransformMirrorCostTicks;
}
