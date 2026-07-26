using System.Collections.ObjectModel;
using System.Text;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    /// <summary>
    /// Immutable structural render-graph generation. Its compatibility identity deliberately
    /// excludes acquired external image handles and frame-local data.
    /// </summary>
    internal sealed class VulkanRenderGraphPlan
    {
        private static long _nextGeneration;
        private readonly ReadOnlyCollection<RenderGraphPlanPass> _passes;
        private readonly ReadOnlyCollection<RenderGraphPlanEdge> _edges;
        private readonly ReadOnlyCollection<RenderGraphPlanResourceLifetime> _lifetimes;
        private readonly ReadOnlyCollection<RenderGraphPlanSubmission> _submissions;
        private readonly ReadOnlyCollection<RenderGraphPlanOutputContract> _outputs;

        internal VulkanRenderGraphPlan(
            IReadOnlyList<RenderPassMetadata> orderedPasses,
            IReadOnlyList<VulkanCompiledPassBatch> batches,
            RenderGraphSynchronizationInfo synchronization)
        {
            var passToSubmission = new Dictionary<int, int>(orderedPasses.Count);
            for (int batchIndex = 0; batchIndex < batches.Count; batchIndex++)
            {
                foreach (int passIndex in batches[batchIndex].PassIndices)
                    passToSubmission[passIndex] = batchIndex;
            }

            var passes = new List<RenderGraphPlanPass>(orderedPasses.Count);
            var lifetimeByResource = new Dictionary<string, (int First, int Last)>(StringComparer.OrdinalIgnoreCase);
            var versionByResource = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var outputByResource = new Dictionary<string, RenderGraphPlanOutputContract>(StringComparer.OrdinalIgnoreCase);
            var identity = new VulkanStableHash64(1u);
            for (int passOrder = 0; passOrder < orderedPasses.Count; passOrder++)
            {
                RenderPassMetadata pass = orderedPasses[passOrder];
                string attachmentSignature = BuildAttachmentSignature(pass);
                var resources = new List<RenderGraphPlanResourceUse>(pass.ResourceUsages.Count);
                for (int usageIndex = 0; usageIndex < pass.ResourceUsages.Count; usageIndex++)
                {
                    RenderPassResourceUsage usage = pass.ResourceUsages[usageIndex];
                    int logicalVersion = ResolveLogicalVersion(usage, versionByResource);
                    RenderGraphSyncState syncState = RenderGraphSynchronizationPlanner.ResolveState(usage, pass.Stage);
                    resources.Add(new RenderGraphPlanResourceUse(
                        usage.ResourceName,
                        usage.ResourceType,
                        usage.Access,
                        syncState.StageMask,
                        syncState.AccessMask,
                        syncState.Layout,
                        usage.SubresourceRange,
                        logicalVersion,
                        usage.IsImported,
                        usage.ImportedInitialState));

                    string lifetimeKey = $"{usage.ResourceName}@v{logicalVersion}|m{usage.SubresourceRange.BaseMipLevel}:{usage.SubresourceRange.MipLevelCount}|l{usage.SubresourceRange.BaseArrayLayer}:{usage.SubresourceRange.ArrayLayerCount}";
                    if (lifetimeByResource.TryGetValue(lifetimeKey, out (int First, int Last) lifetime))
                        lifetimeByResource[lifetimeKey] = (lifetime.First, passOrder);
                    else
                        lifetimeByResource.Add(lifetimeKey, (passOrder, passOrder));

                    identity.Add(usage.ResourceName);
                    identity.Add((int)usage.ResourceType);
                    identity.Add((int)usage.Access);
                    identity.Add((int)usage.LoadOp);
                    identity.Add((int)usage.StoreOp);
                    identity.Add(usage.ResolveSourceColorIndex.HasValue);
                    if (usage.ResolveSourceColorIndex is uint resolveSourceColorIndex)
                        identity.Add(resolveSourceColorIndex);
                    identity.Add((int)syncState.StageMask);
                    identity.Add((int)syncState.AccessMask);
                    identity.Add(syncState.Layout.HasValue ? (int)syncState.Layout.Value + 1 : 0);
                    identity.Add(logicalVersion);
                    identity.Add(usage.SubresourceRange.BaseMipLevel);
                    identity.Add(usage.SubresourceRange.MipLevelCount);
                    identity.Add(usage.SubresourceRange.BaseArrayLayer);
                    identity.Add(usage.SubresourceRange.ArrayLayerCount);
                    identity.Add(usage.IsImported);
                    identity.Add(usage.ImportedInitialState.HasValue);
                    if (usage.ImportedInitialState is { } importedInitialState)
                        AddSyncState(ref identity, importedInitialState);

                    outputByResource[lifetimeKey] = new RenderGraphPlanOutputContract(
                        usage.ResourceName,
                        logicalVersion,
                        usage.SubresourceRange,
                        pass.PassIndex,
                        syncState,
                        usage.IsImported);
                }

                passes.Add(new RenderGraphPlanPass(
                    pass.PassIndex,
                    passOrder,
                    pass.Name,
                    pass.Stage,
                    pass.RequiresPipelineReady,
                    attachmentSignature,
                    resources.AsReadOnly()));
                identity.Add(pass.PassIndex);
                identity.Add(pass.Name);
                identity.Add((int)pass.Stage);
                identity.Add(pass.RequiresPipelineReady);
                identity.Add(attachmentSignature);
                identity.Add(pass.DescriptorSchemas.Count);
                foreach (string descriptorSchema in pass.DescriptorSchemas)
                    identity.Add(descriptorSchema);
            }

            var edges = new List<RenderGraphPlanEdge>(synchronization.Edges.Count);
            foreach (RenderGraphSynchronizationEdge edge in synchronization.Edges)
            {
                edges.Add(new RenderGraphPlanEdge(
                    edge.ProducerPassIndex,
                    edge.ConsumerPassIndex,
                    edge.ResourceName,
                    edge.ResourceType,
                    edge.ResourceVersion,
                    edge.SubresourceRange,
                    edge.ProducerState,
                    edge.ConsumerState,
                    edge.DependencyOnly));
                identity.Add(edge.ProducerPassIndex);
                identity.Add(edge.ConsumerPassIndex);
                identity.Add(edge.ResourceName);
                identity.Add(edge.ResourceVersion);
                identity.Add((int)edge.ResourceType);
                identity.Add(edge.SubresourceRange.BaseMipLevel);
                identity.Add(edge.SubresourceRange.MipLevelCount);
                identity.Add(edge.SubresourceRange.BaseArrayLayer);
                identity.Add(edge.SubresourceRange.ArrayLayerCount);
                AddSyncState(ref identity, edge.ProducerState);
                AddSyncState(ref identity, edge.ConsumerState);
                identity.Add(edge.DependencyOnly);
            }

            var submissions = new List<RenderGraphPlanSubmission>(batches.Count);
            for (int batchIndex = 0; batchIndex < batches.Count; batchIndex++)
            {
                VulkanCompiledPassBatch batch = batches[batchIndex];
                var waits = new HashSet<int>();
                foreach (RenderGraphSynchronizationEdge edge in synchronization.Edges)
                {
                    if (!passToSubmission.TryGetValue(edge.ProducerPassIndex, out int producerSubmission) ||
                        !passToSubmission.TryGetValue(edge.ConsumerPassIndex, out int consumerSubmission) ||
                        consumerSubmission != batchIndex || producerSubmission == consumerSubmission ||
                        batches[producerSubmission].Stage == batch.Stage)
                    {
                        continue;
                    }

                    waits.Add(producerSubmission);
                }

                int[] passIndices = [.. batch.PassIndices];
                int[] waitIndices = [.. waits.OrderBy(static value => value)];
                submissions.Add(new RenderGraphPlanSubmission(
                    batch.BatchIndex,
                    batch.Stage,
                    batch.AttachmentSignature,
                    Array.AsReadOnly(passIndices),
                    Array.AsReadOnly(waitIndices),
                    SignalIndex: batch.BatchIndex + 1));
                identity.Add(batch.BatchIndex);
                identity.Add((int)batch.Stage);
                identity.Add(batch.AttachmentSignature);
                identity.Add(passIndices.Length);
                foreach (int passIndex in passIndices)
                    identity.Add(passIndex);
                identity.Add(waitIndices.Length);
                foreach (int waitIndex in waitIndices)
                    identity.Add(waitIndex);
                identity.Add(batch.BatchIndex + 1);
            }

            _passes = passes.AsReadOnly();
            _edges = edges.AsReadOnly();
            _lifetimes = lifetimeByResource
                .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new RenderGraphPlanResourceLifetime(
                    pair.Key,
                    pair.Value.First,
                    pair.Value.Last,
                    ResolveSubmissionIndex(orderedPasses[pair.Value.First].PassIndex, passToSubmission),
                    ResolveSubmissionIndex(orderedPasses[pair.Value.Last].PassIndex, passToSubmission)))
                .ToList()
                .AsReadOnly();
            _submissions = submissions.AsReadOnly();
            _outputs = outputByResource.Values
                .OrderBy(static output => output.ResourceName, StringComparer.Ordinal)
                .ThenBy(static output => output.LogicalVersion)
                .ToList()
                .AsReadOnly();
            identity.Add(_outputs.Count);
            foreach (RenderGraphPlanOutputContract output in _outputs)
            {
                identity.Add(output.ResourceName);
                identity.Add(output.LogicalVersion);
                identity.Add(output.SubresourceRange.BaseMipLevel);
                identity.Add(output.SubresourceRange.MipLevelCount);
                identity.Add(output.SubresourceRange.BaseArrayLayer);
                identity.Add(output.SubresourceRange.ArrayLayerCount);
                identity.Add(output.LastUsePassIndex);
                AddSyncState(ref identity, output.FinalState);
                identity.Add(output.Imported);
            }
            CompatibilityIdentity = identity.Value;
            Generation = unchecked((ulong)Interlocked.Increment(ref _nextGeneration));
            SubmissionCount = _submissions.Count;
        }

        public ulong Generation { get; }
        public ulong CompatibilityIdentity { get; }
        public ReadOnlyCollection<RenderGraphPlanPass> Passes => _passes;
        public ReadOnlyCollection<RenderGraphPlanEdge> Edges => _edges;
        public ReadOnlyCollection<RenderGraphPlanResourceLifetime> Lifetimes => _lifetimes;
        public ReadOnlyCollection<RenderGraphPlanSubmission> Submissions => _submissions;
        public ReadOnlyCollection<RenderGraphPlanOutputContract> Outputs => _outputs;
        public int SubmissionCount { get; }
        /// <summary>Physical aliasing remains fail-closed until asynchronous interval proof exists.</summary>
        public bool PhysicalAliasingEnabled => false;

        /// <summary>Emits a deterministic structural dump suitable for diagnostics and tests.</summary>
        public string Dump()
        {
            var builder = new StringBuilder(256 + (_passes.Count * 96) + (_edges.Count * 96));
            builder.Append("VulkanRenderGraphPlan generation=").Append(Generation)
                .Append(" identity=0x").Append(CompatibilityIdentity.ToString("X16")).AppendLine();
            foreach (RenderGraphPlanPass pass in _passes)
            {
                builder.Append("pass order=").Append(pass.Order).Append(" id=").Append(pass.PassIndex)
                    .Append(" queue=").Append(pass.Stage).Append(" name=").Append(pass.Name)
                    .Append(" attachments=").Append(pass.AttachmentSignature).AppendLine();
                foreach (RenderGraphPlanResourceUse resource in pass.Resources)
                {
                    builder.Append("  resource ").Append(resource.Name).Append(" v").Append(resource.LogicalVersion)
                        .Append(' ').Append(resource.Access).Append(" range=").Append(resource.SubresourceRange)
                        .Append(" sync=").Append(resource.StageMask).Append('/').Append(resource.AccessMask)
                        .Append(" layout=").Append(resource.Layout?.ToString() ?? "none")
                        .Append(resource.Imported ? " imported" : string.Empty).AppendLine();
                }
            }

            foreach (RenderGraphPlanEdge edge in _edges)
            {
                builder.Append("edge ").Append(edge.ProducerPassIndex).Append(" -> ").Append(edge.ConsumerPassIndex)
                    .Append(" resource=").Append(edge.ResourceName).Append(" v").Append(edge.ResourceVersion)
                    .Append(" barrier=").Append(edge.ProducerState).Append(" => ").Append(edge.ConsumerState)
                    .Append(edge.DependencyOnly ? " explicit" : " derived").AppendLine();
            }

            foreach (RenderGraphPlanResourceLifetime lifetime in _lifetimes)
            {
                builder.Append("lifetime resource=").Append(lifetime.ResourceKey)
                    .Append(" first=").Append(lifetime.FirstPassOrder)
                    .Append(" last=").Append(lifetime.LastPassOrder).AppendLine();
            }

            foreach (RenderGraphPlanSubmission submission in _submissions)
            {
                builder.Append("submission index=").Append(submission.SubmissionIndex)
                    .Append(" queue=").Append(submission.Queue)
                    .Append(" passes=").AppendJoin(',', submission.PassIndices)
                    .Append(" waits=").AppendJoin(',', submission.WaitSubmissionIndices)
                    .Append(" signal=").Append(submission.SignalIndex).AppendLine();
            }

            foreach (RenderGraphPlanOutputContract output in _outputs)
            {
                builder.Append("output resource=").Append(output.ResourceName).Append(" v").Append(output.LogicalVersion)
                    .Append(" lastUse=").Append(output.LastUsePassIndex)
                    .Append(" state=").Append(output.FinalState).AppendLine();
            }

            builder.Append("submissions=").Append(SubmissionCount)
                .Append(" physicalAliasing=").Append(PhysicalAliasingEnabled).AppendLine();

            return builder.ToString();
        }

        private static string BuildAttachmentSignature(RenderPassMetadata pass)
        {
            var builder = new StringBuilder();
            foreach (RenderPassResourceUsage usage in pass.ResourceUsages
                .Where(static usage => usage.IsAttachment)
                .OrderBy(static usage => usage.ResourceName, StringComparer.Ordinal))
            {
                if (builder.Length > 0)
                    builder.Append('|');
                builder.Append(usage.ResourceType).Append(':').Append(usage.ResourceName)
                    .Append(':').Append(usage.LoadOp).Append(':').Append(usage.StoreOp)
                    .Append(":resolve=").Append(usage.ResolveSourceColorIndex?.ToString() ?? "none");
            }

            return builder.Length == 0 ? "none" : builder.ToString();
        }

        private static int ResolveLogicalVersion(
            RenderPassResourceUsage usage,
            Dictionary<string, int> versionByResource)
        {
            if (usage.LogicalVersion >= 0)
            {
                versionByResource[usage.ResourceName] = Math.Max(
                    usage.LogicalVersion,
                    versionByResource.GetValueOrDefault(usage.ResourceName, -1));
                return usage.LogicalVersion;
            }

            int current = versionByResource.GetValueOrDefault(usage.ResourceName, -1);
            if (usage.Access is ERenderGraphAccess.Write or ERenderGraphAccess.ReadWrite)
            {
                current++;
                versionByResource[usage.ResourceName] = current;
            }

            return current;
        }

        private static int ResolveSubmissionIndex(int passIndex, IReadOnlyDictionary<int, int> passToSubmission)
            => passToSubmission.TryGetValue(passIndex, out int submissionIndex) ? submissionIndex : -1;

        private static void AddSyncState(ref VulkanStableHash64 identity, in RenderGraphSyncState state)
        {
            identity.Add((int)state.StageMask);
            identity.Add((int)state.AccessMask);
            identity.Add(state.Layout.HasValue ? (int)state.Layout.Value + 1 : 0);
        }
    }

}
