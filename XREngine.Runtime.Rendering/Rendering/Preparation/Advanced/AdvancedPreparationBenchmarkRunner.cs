using System.Diagnostics;
using XREngine.Rendering.Commands;

namespace XREngine.Rendering;

/// <summary>
/// Repeatable CPU-side planning benchmark for the required 1/8/32/128
/// skeletal-instance matrix. GPU timing is supplied by the dispatch backend in
/// live captures.
/// </summary>
public sealed class AdvancedPreparationBenchmarkRunner
{
    private readonly AdvancedDeformationJobStream _stream;
    private readonly AdvancedDeformationDispatchPlanner _planner;

    public AdvancedPreparationBenchmarkRunner(int maximumInstances = 128)
    {
        if (maximumInstances <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumInstances));
        _stream = new AdvancedDeformationJobStream(maximumInstances);
        _planner = new AdvancedDeformationDispatchPlanner(
            maximumInstances,
            maximumFamilies: 4);
    }

    public AdvancedPreparationBenchmarkSample Run(
        uint skeletalInstanceCount,
        EAdvancedPreparationBenchmarkScenario scenario,
        uint verticesPerInstance = 50_000u)
    {
        if (skeletalInstanceCount == 0u ||
            skeletalInstanceCount > (uint)_stream.Capacity)
        {
            throw new ArgumentOutOfRangeException(nameof(skeletalInstanceCount));
        }

        _stream.BeginFrame();
        AddJobs(skeletalInstanceCount, scenario, verticesPerInstance);
        _stream.FinalizeJobs(new AdvancedDeformationBudget(
            MaximumJobs: checked((uint)_stream.Capacity),
            MaximumVertices: ulong.MaxValue,
            MaximumOutputBytes: ulong.MaxValue,
            OverflowBehavior:
                EAdvancedDeformationOverflowBehavior.KeepPreviousAndInvalidateVelocity));
        _planner.Build(_stream.Jobs);

        long allocationBefore = GC.GetAllocatedBytesForCurrentThread();
        long timestamp = Stopwatch.GetTimestamp();
        _stream.BeginFrame();
        AddJobs(skeletalInstanceCount, scenario, verticesPerInstance);
        AdvancedDeformationAdmissionResult admission =
            _stream.FinalizeJobs(new AdvancedDeformationBudget(
                MaximumJobs: checked((uint)_stream.Capacity),
                MaximumVertices: ulong.MaxValue,
                MaximumOutputBytes: ulong.MaxValue,
                OverflowBehavior:
                    EAdvancedDeformationOverflowBehavior.KeepPreviousAndInvalidateVelocity));
        _planner.Build(_stream.Jobs);
        long elapsed = Stopwatch.GetTimestamp() - timestamp;
        long allocated =
            GC.GetAllocatedBytesForCurrentThread() - allocationBefore;

        return new AdvancedPreparationBenchmarkSample(
            skeletalInstanceCount,
            scenario,
            admission.AdmittedJobCount,
            checked((uint)_planner.Batches.Length),
            admission.AdmittedVertexCount,
            elapsed,
            allocated);
    }

    private void AddJobs(
        uint instanceCount,
        EAdvancedPreparationBenchmarkScenario scenario,
        uint verticesPerInstance)
    {
        bool mandatory =
            scenario is EAdvancedPreparationBenchmarkScenario.Still or
                EAdvancedPreparationBenchmarkScenario.Moving or
                EAdvancedPreparationBenchmarkScenario.Shadowed;
        float contribution = scenario switch
        {
            EAdvancedPreparationBenchmarkScenario.Moving => 1.0f,
            EAdvancedPreparationBenchmarkScenario.Still => 0.75f,
            EAdvancedPreparationBenchmarkScenario.Shadowed => 0.5f,
            _ => 0.01f,
        };

        for (uint instance = 0u; instance < instanceCount; instance++)
        {
            AdvancedGpuHandle mesh = new(instance + 1u, 1u);
            AdvancedGpuHandle pose = new(instance + 1u, 1u);
            AdvancedDeformationJobRecord job = new()
            {
                Mesh = mesh,
                SharedPose = pose,
                CurrentVertexOffset = instance * verticesPerInstance,
                PreviousVertexOffset = instance * verticesPerInstance,
                VertexCount = verticesPerInstance,
                BoneCount = 128u,
                MeshGeneration = 1u,
                PoseGeneration =
                    scenario == EAdvancedPreparationBenchmarkScenario.Moving
                        ? 2u
                        : 1u,
                PaletteGeneration = 1u,
                TopologyGeneration = 1u,
                VertexLayoutId = 0x1001UL,
                Features =
                    EAdvancedDeformationFeatureFlags.Skinning |
                    EAdvancedDeformationFeatureFlags.Normals |
                    EAdvancedDeformationFeatureFlags.Tangents |
                    EAdvancedDeformationFeatureFlags.Velocity,
                Precision = EAdvancedDeformationPrecision.Packed,
                Order = EAdvancedDeformationOrder.BlendshapeThenSkinning,
                OutputStride = 64u,
            };
            AdvancedDeformationJobKey key = new(
                mesh,
                pose,
                job.MeshGeneration,
                job.PoseGeneration,
                job.PaletteGeneration,
                job.TopologyGeneration,
                job.VertexLayoutId,
                job.Features,
                job.Precision);
            _stream.TryAdd(
                new AdvancedDeformationCandidate(
                    job,
                    key,
                    contribution,
                    Mandatory: mandatory,
                    Visible: mandatory),
                out _);
        }
    }
}
