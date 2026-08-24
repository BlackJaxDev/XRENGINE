using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Vulkan.RenderGraph;

/// <summary>Numeric, allocation-free recording view of a compiled render graph.</summary>
internal sealed class VulkanCompiledRenderGraphExecution
{
    internal readonly record struct Pass(int PassIndex, int ResourceOffset, int ResourceCount, ERenderGraphPassStage Stage, bool RequiresPipelineReady);
    internal readonly record struct ResourceUse(VulkanResourceId ResourceId, ERenderPassResourceType Type, ERenderGraphAccess Access, RenderGraphStageMask Stages, RenderGraphAccessMask AccessMask, RenderGraphImageLayout Layout, uint BaseMip, uint MipCount, uint BaseLayer, uint LayerCount, int LogicalVersion, bool Imported);
    internal readonly record struct Edge(int ProducerPassIndex, int ConsumerPassIndex, VulkanResourceId ResourceId, ERenderPassResourceType Type, int Version, RenderGraphSyncState Producer, RenderGraphSyncState Consumer, bool DependencyOnly);
    internal readonly record struct Submission(int Index, int PassOffset, int PassCount, int WaitOffset, int WaitCount, ERenderGraphPassStage Queue, int SignalIndex);

    private readonly Pass[] _passes;
    private readonly ResourceUse[] _resources;
    private readonly Edge[] _edges;
    private readonly Submission[] _submissions;
    private readonly int[] _submissionPasses;
    private readonly int[] _submissionWaits;
    private readonly int _firstPassIndex;
    private readonly int[] _passOrderByPassIndex;

    internal ReadOnlySpan<Pass> Passes => _passes;
    internal ReadOnlySpan<ResourceUse> Resources => _resources;
    internal ReadOnlySpan<Edge> Edges => _edges;
    internal ReadOnlySpan<Submission> Submissions => _submissions;
    internal int EdgeCount => _edges.Length;

    internal bool TryGetPassOrder(int passIndex, out int order)
    {
        long slot = (long)passIndex - _firstPassIndex;
        if ((ulong)slot < (ulong)_passOrderByPassIndex.Length &&
            (order = _passOrderByPassIndex[(int)slot]) >= 0)
            return true;

        order = -1;
        return false;
    }

    internal VulkanCompiledRenderGraphExecution(
        IReadOnlyList<RenderGraphPlanPass> passes, IReadOnlyList<RenderGraphPlanEdge> edges,
        IReadOnlyList<RenderGraphPlanSubmission> submissions,
        VulkanRenderGraphResourceIds resourceIds)
        : this(ColdCompiler.Compile(passes, edges, submissions, resourceIds)) { }

    private VulkanCompiledRenderGraphExecution(ColdCompiler.Result result)
    {
        _passes = result.Passes;
        _resources = result.Resources;
        _edges = result.Edges;
        _submissions = result.Submissions;
        _submissionPasses = result.SubmissionPasses;
        _submissionWaits = result.SubmissionWaits;
        _firstPassIndex = result.FirstPassIndex;
        _passOrderByPassIndex = result.PassOrderByPassIndex;
    }

    /// <summary>Cold compiler only.  The published execution object retains no names or registries.</summary>
    private static class ColdCompiler
    {
        internal readonly record struct Result(Pass[] Passes, ResourceUse[] Resources, Edge[] Edges, Submission[] Submissions, int[] SubmissionPasses, int[] SubmissionWaits, int FirstPassIndex, int[] PassOrderByPassIndex);

        internal static Result Compile(
            IReadOnlyList<RenderGraphPlanPass> passes,
            IReadOnlyList<RenderGraphPlanEdge> edges,
            IReadOnlyList<RenderGraphPlanSubmission> submissions,
            VulkanRenderGraphResourceIds resourceIds)
        {
            int resourceCount = 0;
            for (int index = 0; index < passes.Count; index++)
                resourceCount += passes[index].Resources.Count;
            Pass[] compiledPasses = new Pass[passes.Count];
            ResourceUse[] resources = new ResourceUse[resourceCount];
            int resourceCursor = 0;
            for (int index = 0; index < passes.Count; index++)
            {
                RenderGraphPlanPass pass = passes[index];
                int offset = resourceCursor;
                for (int resourceIndex = 0; resourceIndex < pass.Resources.Count; resourceIndex++)
                {
                    RenderGraphPlanResourceUse use = pass.Resources[resourceIndex];
                    resources[resourceCursor++] = new ResourceUse(resourceIds.GetOrAdd(use.Name), use.ResourceType, use.Access, use.StageMask, use.AccessMask, use.Layout ?? RenderGraphImageLayout.Undefined, use.SubresourceRange.BaseMipLevel, use.SubresourceRange.MipLevelCount, use.SubresourceRange.BaseArrayLayer, use.SubresourceRange.ArrayLayerCount, use.LogicalVersion, use.Imported);
                }
                compiledPasses[index] = new Pass(pass.PassIndex, offset, resourceCursor - offset, pass.Stage, pass.RequiresPipelineReady);
            }

            Edge[] compiledEdges = new Edge[edges.Count];
            for (int index = 0; index < edges.Count; index++)
            {
                RenderGraphPlanEdge edge = edges[index];
                VulkanResourceId resourceId = edge.DependencyOnly
                    ? VulkanResourceId.Invalid
                    : resourceIds.GetOrAdd(edge.ResourceName);
                compiledEdges[index] = new Edge(edge.ProducerPassIndex, edge.ConsumerPassIndex, resourceId, edge.ResourceType, edge.ResourceVersion, edge.ProducerState, edge.ConsumerState, edge.DependencyOnly);
            }

            int passCount = 0;
            int waitCount = 0;
            for (int index = 0; index < submissions.Count; index++)
            {
                passCount += submissions[index].PassIndices.Count;
                waitCount += submissions[index].WaitSubmissionIndices.Count;
            }
            Submission[] compiledSubmissions = new Submission[submissions.Count];
            int[] submissionPasses = new int[passCount];
            int[] waits = new int[waitCount];
            int passCursor = 0;
            int waitCursor = 0;
            for (int index = 0; index < submissions.Count; index++)
            {
                RenderGraphPlanSubmission submission = submissions[index];
                int passOffset = passCursor;
                for (int item = 0; item < submission.PassIndices.Count; item++)
                    submissionPasses[passCursor++] = submission.PassIndices[item];
                int waitOffset = waitCursor;
                for (int item = 0; item < submission.WaitSubmissionIndices.Count; item++)
                    waits[waitCursor++] = submission.WaitSubmissionIndices[item];
                compiledSubmissions[index] = new Submission(submission.SubmissionIndex, passOffset, passCursor - passOffset, waitOffset, waitCursor - waitOffset, submission.Queue, submission.SignalIndex);
            }

            int minPassIndex = int.MaxValue;
            int maxPassIndex = int.MinValue;
            for (int index = 0; index < compiledPasses.Length; index++)
            {
                minPassIndex = Math.Min(minPassIndex, compiledPasses[index].PassIndex);
                maxPassIndex = Math.Max(maxPassIndex, compiledPasses[index].PassIndex);
            }
            int[] passOrderByPassIndex = compiledPasses.Length == 0
                ? []
                : new int[checked(maxPassIndex - minPassIndex + 1)];
            Array.Fill(passOrderByPassIndex, -1);
            for (int index = 0; index < compiledPasses.Length; index++)
                passOrderByPassIndex[compiledPasses[index].PassIndex - minPassIndex] = index;

            return new Result(compiledPasses, resources, compiledEdges, compiledSubmissions, submissionPasses, waits, minPassIndex, passOrderByPassIndex);
        }
    }

    internal ReadOnlySpan<int> GetSubmissionPasses(in Submission submission) => _submissionPasses.AsSpan(submission.PassOffset, submission.PassCount);
    internal ReadOnlySpan<int> GetSubmissionWaits(in Submission submission) => _submissionWaits.AsSpan(submission.WaitOffset, submission.WaitCount);
}
