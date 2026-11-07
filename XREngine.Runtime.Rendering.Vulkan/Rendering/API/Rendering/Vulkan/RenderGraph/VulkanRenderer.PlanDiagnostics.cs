using System.Globalization;
using System.Runtime.CompilerServices;
using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.RenderGraph.Diagnostics;
using XREngine.Rendering.Vulkan.RenderGraph;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Publishes the render graph plan of the Vulkan renderer to tools. The renderer plans one pipeline instance at a time,
/// so the snapshot of each instance is kept until the renderer plans that instance again.
/// </summary>
public sealed partial class VulkanRenderer : IRenderGraphPlanDiagnosticsSource
{
    private readonly ConditionalWeakTable<XRRenderPipelineInstance, VulkanPlanDiagnosticsEntry> _planSnapshots = new();
    private readonly Lock _planSnapshotLock = new();
    private VulkanPlanDiagnosticsEntry? _lastPlanEntry;

    /// <inheritdoc />
    public override IRenderGraphPlanDiagnosticsSource? PlanDiagnostics => this;

    /// <inheritdoc />
    public RenderGraphPlanSnapshot? GetPlanSnapshot(XRRenderPipelineInstance? instance)
    {
        VulkanRenderGraphPlan plan = _framePlanner.CurrentPlan;
        lock (_planSnapshotLock)
        {
            VulkanPlanDiagnosticsEntry? cached = instance is null
                ? _lastPlanEntry
                : _planSnapshots.TryGetValue(instance, out VulkanPlanDiagnosticsEntry? entry) ? entry : null;
            if (cached is not null && ReferenceEquals(cached.Plan, plan))
                return cached.Snapshot;

            if (plan.CompiledGraph.OrderedPasses.Count == 0)
                return cached?.Snapshot;

            if (instance is not null && !PlanBelongsTo(plan, instance))
                return cached?.Snapshot;

            RenderGraphPlanSnapshot snapshot = BuildPlanSnapshot(plan);
            VulkanPlanDiagnosticsEntry updated = new(plan, snapshot);
            _lastPlanEntry = updated;
            if (instance is not null)
                _planSnapshots.AddOrUpdate(instance, updated);
            return snapshot;
        }
    }

    /// <summary>
    /// Returns true when the passes of <paramref name="plan"/> come from the pass metadata of <paramref name="instance"/>.
    /// The planner filters the metadata of the instance but keeps the same pass objects.
    /// </summary>
    private static bool PlanBelongsTo(VulkanRenderGraphPlan plan, XRRenderPipelineInstance instance)
    {
        IReadOnlyCollection<RenderPassMetadata>? source = instance.ActiveGeneration?.PassMetadata ?? instance.Pipeline?.PassMetadata;
        if (source is null || source.Count == 0)
            return false;

        RenderPassMetadata first = plan.CompiledGraph.OrderedPasses[0];
        foreach (RenderPassMetadata pass in source)
            if (ReferenceEquals(pass, first))
                return true;

        return false;
    }

    private RenderGraphPlanSnapshot BuildPlanSnapshot(VulkanRenderGraphPlan plan)
    {
        VulkanCompiledRenderGraph graph = plan.CompiledGraph;
        RenderGraphCompilation compilation = new(
            graph.OrderedPasses,
            graph.PassOrder,
            graph.Batches,
            graph.Synchronization,
            graph.ScreenSpaceUiPassOrder);

        List<string> notes =
        [
            "This is the newest plan that the Vulkan renderer published for this pipeline.",
        ];

        Dictionary<string, (int GroupIndex, bool Transient)> aliasByName = new(StringComparer.OrdinalIgnoreCase);
        List<PlanAliasGroupSnapshot> aliasGroups = CollectAliasGroups(plan, aliasByName, notes);

        return RenderGraphPlanSnapshotFactory.FromCompilation(
            EPlanSnapshotOrigin.Vulkan,
            plan.Revision,
            graph.OrderedPasses,
            compilation,
            executedPassIndices: null,
            submissions: CollectSubmissions(graph),
            barriers: CollectBarriers(plan.Barriers),
            lifetimes: CollectLifetimes(graph, aliasByName),
            aliasGroups: aliasGroups,
            notes: notes);
    }

    private static List<PlanSubmissionSnapshot> CollectSubmissions(VulkanCompiledRenderGraph graph)
    {
        Dictionary<int, int> batchByPass = [];
        for (int i = 0; i < graph.Batches.Count; i++)
        {
            RenderGraphPassBatch batch = graph.Batches[i];
            foreach (int passIndex in batch.PassIndices)
                batchByPass[passIndex] = batch.BatchIndex;
        }

        IReadOnlyList<RenderGraphPlanSubmission> source = graph.Plan.Submissions;
        List<PlanSubmissionSnapshot> submissions = new(source.Count);
        for (int i = 0; i < source.Count; i++)
        {
            RenderGraphPlanSubmission submission = source[i];
            List<int> batchIndices = [];
            for (int p = 0; p < submission.PassIndices.Count; p++)
            {
                if (batchByPass.TryGetValue(submission.PassIndices[p], out int batchIndex) && !batchIndices.Contains(batchIndex))
                    batchIndices.Add(batchIndex);
            }

            string label = submission.WaitSubmissionIndices.Count == 0
                ? submission.Queue.ToString()
                : string.Concat(submission.Queue.ToString(), "  waits on ", string.Join(", ", submission.WaitSubmissionIndices));
            submissions.Add(new PlanSubmissionSnapshot(submission.SubmissionIndex, label, batchIndices));
        }

        return submissions;
    }

    private static List<PlanBarrierSnapshot> CollectBarriers(VulkanBarrierPlan barriers)
    {
        List<PlanBarrierSnapshot> result = new(barriers.ImageBarriers.Length + barriers.BufferBarriers.Length + barriers.SwapchainBarriers.Length);
        foreach (VulkanFrozenImageBarrier barrier in barriers.ImageBarriers)
        {
            string name = NameOrId(barriers, barrier.ResourceId);
            string range = string.Concat(
                "mip ", barrier.Range.BaseMipLevel.ToString(CultureInfo.InvariantCulture),
                "+", barrier.Range.LevelCount.ToString(CultureInfo.InvariantCulture),
                "  layer ", barrier.Range.BaseArrayLayer.ToString(CultureInfo.InvariantCulture),
                "+", barrier.Range.LayerCount.ToString(CultureInfo.InvariantCulture),
                "  ", barrier.Next.AspectMask.ToString());
            result.Add(new PlanBarrierSnapshot(
                barrier.PassIndex,
                name,
                "Image",
                range,
                barrier.Previous.Layout.ToString(),
                barrier.Next.Layout.ToString(),
                barrier.Previous.StageMask.ToString(),
                barrier.Next.StageMask.ToString(),
                barrier.Previous.AccessMask.ToString(),
                barrier.Next.AccessMask.ToString(),
                string.Concat("format ", barrier.NativeFormat.ToString(), QueueText(barrier.SrcQueueFamilyIndex, barrier.DstQueueFamilyIndex))));
        }

        foreach (VulkanFrozenBufferBarrier barrier in barriers.BufferBarriers)
        {
            result.Add(new PlanBarrierSnapshot(
                barrier.PassIndex,
                barrier.LogicalResourceName,
                "Buffer",
                string.Concat("offset ", barrier.NativeOffset.ToString(CultureInfo.InvariantCulture), "  size ", barrier.NativeSize.ToString(CultureInfo.InvariantCulture)),
                string.Empty,
                string.Empty,
                barrier.Previous.StageMask.ToString(),
                barrier.Next.StageMask.ToString(),
                barrier.Previous.AccessMask.ToString(),
                barrier.Next.AccessMask.ToString(),
                QueueText(barrier.SrcQueueFamilyIndex, barrier.DstQueueFamilyIndex).TrimStart()));
        }

        foreach (VulkanFrozenSwapchainBarrier barrier in barriers.SwapchainBarriers)
        {
            result.Add(new PlanBarrierSnapshot(
                barrier.PassIndex,
                NameOrId(barriers, barrier.ResourceId),
                "Swapchain",
                barrier.ResourceType.ToString(),
                barrier.Previous.Layout.ToString(),
                barrier.Next.Layout.ToString(),
                barrier.Previous.StageMask.ToString(),
                barrier.Next.StageMask.ToString(),
                barrier.Previous.AccessMask.ToString(),
                barrier.Next.AccessMask.ToString(),
                QueueText(barrier.SrcQueueFamilyIndex, barrier.DstQueueFamilyIndex).TrimStart()));
        }

        return result;
    }

    private static string NameOrId(VulkanBarrierPlan barriers, VulkanResourceId id)
    {
        string name = barriers.GetDiagnosticResourceName(id);
        return name.Length > 0 ? name : string.Concat("resource ", id.Value.ToString(CultureInfo.InvariantCulture));
    }

    private static string QueueText(uint source, uint destination)
        => source == destination
            ? string.Empty
            : string.Concat("  queue ", source.ToString(CultureInfo.InvariantCulture), " to ", destination.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// Reads the physical image groups of the resource generation that owns <paramref name="plan"/>. A group with more
    /// than one logical resource shares one memory allocation.
    /// </summary>
    private List<PlanAliasGroupSnapshot> CollectAliasGroups(
        VulkanRenderGraphPlan plan,
        Dictionary<string, (int GroupIndex, bool Transient)> aliasByName,
        List<string> notes)
    {
        List<PlanAliasGroupSnapshot> groups = [];
        ResourcePlannerRuntimeGeneration generation;
        try
        {
            generation = _framePlanner.GetPublishedResourcePlannerGeneration();
        }
        catch (InvalidOperationException)
        {
            notes.Add("The resource generation is not published yet, so the snapshot has no alias groups.");
            return groups;
        }

        ref readonly ResourcePlannerRuntimeState state = ref generation.State;
        if (!ReferenceEquals(state.RenderGraphPlan, plan) || state.ResourceAllocator is not { IsRetired: false } allocator)
        {
            notes.Add("The published resource generation belongs to another plan, so the snapshot has no alias groups.");
            return groups;
        }

        notes.Add(string.Concat("Transient attachments: ", allocator.TransientAttachmentPlan.Describe()));
        try
        {
            foreach (VulkanPhysicalImageGroup group in allocator.EnumeratePhysicalGroups())
            {
                IReadOnlyList<VulkanImageAllocation> logical = group.LogicalResources;
                for (int i = 0; i < logical.Count; i++)
                    aliasByName[logical[i].Name] = (-1, group.AllowsAliasing);

                if (logical.Count < 2)
                    continue;

                int groupIndex = groups.Count;
                string[] names = new string[logical.Count];
                for (int i = 0; i < logical.Count; i++)
                {
                    names[i] = logical[i].Name;
                    aliasByName[names[i]] = (groupIndex, group.AllowsAliasing);
                }

                string description = string.Concat(
                    group.Format.ToString(), "  ",
                    group.ResolvedExtent.Width.ToString(CultureInfo.InvariantCulture), "x",
                    group.ResolvedExtent.Height.ToString(CultureInfo.InvariantCulture), "  ",
                    group.MemoryProperties.ToString());
                groups.Add(new PlanAliasGroupSnapshot(groupIndex, names, description));
            }
        }
        catch (InvalidOperationException)
        {
            // The render thread replaced the allocator groups during the read. The next plan revision reads them again.
            groups.Clear();
            aliasByName.Clear();
            notes.Add("The resource allocator changed during the read, so the snapshot has no alias groups.");
        }

        return groups;
    }

    private static List<PlanResourceLifetimeSnapshot> CollectLifetimes(
        VulkanCompiledRenderGraph graph,
        Dictionary<string, (int GroupIndex, bool Transient)> aliasByName)
    {
        IReadOnlyList<RenderGraphPlanResourceLifetime> source = graph.Plan.Lifetimes;
        List<PlanResourceLifetimeSnapshot> lifetimes = new(source.Count);
        for (int i = 0; i < source.Count; i++)
        {
            RenderGraphPlanResourceLifetime lifetime = source[i];
            (int groupIndex, bool transient) = aliasByName.TryGetValue(lifetime.ResourceKey, out var alias) ? alias : (-1, false);
            lifetimes.Add(new PlanResourceLifetimeSnapshot(lifetime.ResourceKey, lifetime.FirstPassOrder, lifetime.LastPassOrder, groupIndex, transient));
        }

        return lifetimes;
    }
}
