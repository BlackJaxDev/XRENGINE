using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace XREngine.Rendering.RenderGraph;

[Flags]
public enum RenderGraphStageMask
{
    None = 0,
    TopOfPipe = 1 << 0,
    VertexInput = 1 << 1,
    VertexShader = 1 << 2,
    FragmentShader = 1 << 3,
    EarlyFragmentTests = 1 << 4,
    LateFragmentTests = 1 << 5,
    ColorAttachmentOutput = 1 << 6,
    ComputeShader = 1 << 7,
    Transfer = 1 << 8,
    DrawIndirect = 1 << 9,
    Host = 1 << 10,
    AllGraphics = 1 << 11,
    AllCommands = 1 << 12
}

[Flags]
public enum RenderGraphAccessMask
{
    None = 0,
    MemoryRead = 1 << 0,
    MemoryWrite = 1 << 1,
    ShaderRead = 1 << 2,
    ShaderWrite = 1 << 3,
    UniformRead = 1 << 4,
    ColorAttachmentRead = 1 << 5,
    ColorAttachmentWrite = 1 << 6,
    DepthStencilRead = 1 << 7,
    DepthStencilWrite = 1 << 8,
    VertexAttributeRead = 1 << 9,
    IndexRead = 1 << 10,
    IndirectCommandRead = 1 << 11,
    TransferRead = 1 << 12,
    TransferWrite = 1 << 13
}

public enum RenderGraphImageLayout
{
    Undefined,
    ColorAttachment,
    DepthStencilAttachment,
    RenderingLocalRead,
    ShaderReadOnly,
    General,
    TransferSource,
    TransferDestination,
    Present
}

public readonly record struct RenderGraphSyncState(
    RenderGraphStageMask StageMask,
    RenderGraphAccessMask AccessMask,
    RenderGraphImageLayout? Layout);

public sealed record RenderGraphSynchronizationEdge(
    int ProducerPassIndex,
    int ConsumerPassIndex,
    string ResourceName,
    ERenderPassResourceType ResourceType,
    RenderGraphSubresourceRange SubresourceRange,
    RenderGraphSyncState ProducerState,
    RenderGraphSyncState ConsumerState,
    bool DependencyOnly,
    int ResourceVersion = -1);

public sealed class RenderGraphSynchronizationInfo
{
    private readonly List<RenderGraphSynchronizationEdge> _edges;
    private readonly Dictionary<int, List<RenderGraphSynchronizationEdge>> _edgesByConsumer;

    internal RenderGraphSynchronizationInfo(IEnumerable<RenderGraphSynchronizationEdge> edges)
    {
        _edges = [.. edges];
        _edgesByConsumer = new Dictionary<int, List<RenderGraphSynchronizationEdge>>();
        foreach (RenderGraphSynchronizationEdge edge in _edges)
        {
            if (!_edgesByConsumer.TryGetValue(edge.ConsumerPassIndex, out List<RenderGraphSynchronizationEdge>? list))
            {
                list = [];
                _edgesByConsumer.Add(edge.ConsumerPassIndex, list);
            }

            list.Add(edge);
        }
    }

    public static RenderGraphSynchronizationInfo Empty { get; } = new([]);

    public ReadOnlyCollection<RenderGraphSynchronizationEdge> Edges
        => _edges.AsReadOnly();

    public IReadOnlyList<RenderGraphSynchronizationEdge> GetEdgesForConsumer(int passIndex)
        => _edgesByConsumer.TryGetValue(passIndex, out List<RenderGraphSynchronizationEdge>? list)
            ? list
            : Array.Empty<RenderGraphSynchronizationEdge>();
}

public static class RenderGraphSynchronizationPlanner
{
    public static RenderGraphSynchronizationInfo Build(IReadOnlyCollection<RenderPassMetadata>? passMetadata)
    {
        if (passMetadata is null || passMetadata.Count == 0)
            return RenderGraphSynchronizationInfo.Empty;

        IReadOnlyList<RenderPassMetadata> orderedPasses = TopologicallySort(passMetadata);
        var edges = new List<RenderGraphSynchronizationEdge>();
        var priorUsagesByResource = new Dictionary<string, List<TrackedResourceUsage>>(StringComparer.OrdinalIgnoreCase);
        var versionByResource = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (RenderPassMetadata pass in orderedPasses)
        {
            foreach (RenderPassResourceUsage usage in pass.ResourceUsages)
            {
                if (string.IsNullOrWhiteSpace(usage.ResourceName))
                    continue;

                RenderGraphSyncState consumerState = ResolveState(usage, pass.Stage);
                int resourceVersion = ResolveResourceVersion(usage, versionByResource);
                if (!priorUsagesByResource.TryGetValue(usage.ResourceName, out List<TrackedResourceUsage>? priorUsages))
                {
                    priorUsages = [];
                    priorUsagesByResource.Add(usage.ResourceName, priorUsages);
                }

                bool currentWrites = Writes(usage.Access);
                for (int priorIndex = 0; priorIndex < priorUsages.Count; priorIndex++)
                {
                    TrackedResourceUsage prior = priorUsages[priorIndex];
                    if (prior.PassIndex == pass.PassIndex ||
                        !prior.SubresourceRange.Overlaps(usage.SubresourceRange) ||
                        (!prior.Writes && !currentWrites))
                    {
                        continue;
                    }

                    edges.Add(new RenderGraphSynchronizationEdge(
                        prior.PassIndex,
                        pass.PassIndex,
                        usage.ResourceName,
                        usage.ResourceType,
                        Intersect(prior.SubresourceRange, usage.SubresourceRange),
                        prior.State,
                        consumerState,
                        DependencyOnly: false,
                        resourceVersion));
                }

                priorUsages.Add(new TrackedResourceUsage(
                    pass.PassIndex,
                    usage.SubresourceRange,
                    consumerState,
                    currentWrites));
            }
        }

        foreach (RenderPassMetadata pass in orderedPasses)
        {
            foreach (int dependency in pass.ExplicitDependencies)
            {
                bool alreadyRepresented = edges.Any(e =>
                    e.ProducerPassIndex == dependency &&
                    e.ConsumerPassIndex == pass.PassIndex);

                if (alreadyRepresented)
                    continue;

                RenderGraphSyncState producerState = ResolveDependencyState(passMetadata, dependency);
                RenderGraphSyncState consumerState = ResolveDependencyState(passMetadata, pass.PassIndex);

                edges.Add(new RenderGraphSynchronizationEdge(
                    dependency,
                    pass.PassIndex,
                    string.Empty,
                    ERenderPassResourceType.TransferDestination,
                    RenderGraphSubresourceRange.Full,
                    producerState,
                    consumerState,
                    DependencyOnly: true));
            }
        }

        return new RenderGraphSynchronizationInfo(edges);
    }

    private static int ResolveResourceVersion(
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

    public static IReadOnlyList<RenderPassMetadata> TopologicallySort(IReadOnlyCollection<RenderPassMetadata> passMetadata)
    {
        Dictionary<int, RenderPassMetadata> lookup = passMetadata.ToDictionary(p => p.PassIndex);
        Dictionary<int, int> inDegree = lookup.Keys.ToDictionary(k => k, _ => 0);
        Dictionary<int, List<int>> edges = lookup.Keys.ToDictionary(k => k, _ => new List<int>());

        foreach (RenderPassMetadata pass in lookup.Values)
        {
            foreach (RenderPassResourceUsage usage in pass.ResourceUsages)
            {
                if (!usage.SubresourceRange.IsValid)
                    throw new InvalidOperationException($"Render graph pass {pass.PassIndex} ('{pass.Name}') declares an invalid subresource range for '{usage.ResourceName}': {usage.SubresourceRange}.");
            }

            foreach (int dependency in pass.ExplicitDependencies)
            {
                if (!lookup.ContainsKey(dependency))
                    throw new InvalidOperationException($"Render graph pass {pass.PassIndex} ('{pass.Name}') depends on missing pass {dependency}.");

                AddDependencyEdge(dependency, pass.PassIndex, edges, inDegree);
            }
        }

        AddVersionedResourceEdges(lookup, edges, inDegree);

        SortedSet<int> ready = new(
            inDegree.Where(kvp => kvp.Value == 0).Select(kvp => kvp.Key),
            Comparer<int>.Create((left, right) => ComparePassDeclarationOrder(lookup, left, right)));
        List<RenderPassMetadata> ordered = new(lookup.Count);

        while (ready.Count > 0)
        {
            int passIndex = ready.Min;
            ready.Remove(passIndex);
            ordered.Add(lookup[passIndex]);

            foreach (int consumer in edges[passIndex])
            {
                int next = inDegree[consumer] - 1;
                inDegree[consumer] = next;
                if (next == 0)
                    ready.Add(consumer);
            }
        }

        if (ordered.Count == lookup.Count)
            return ordered;

        string cycle = DescribeCycle(lookup, edges, inDegree);
        throw new InvalidOperationException($"Render graph contains a dependency cycle: {cycle}.");
    }

    private static void AddVersionedResourceEdges(
        IReadOnlyDictionary<int, RenderPassMetadata> lookup,
        Dictionary<int, List<int>> edges,
        Dictionary<int, int> inDegree)
    {
        var producersByVersion = new Dictionary<string, List<(int PassIndex, RenderPassResourceUsage Usage)>>(StringComparer.OrdinalIgnoreCase);
        var consumers = new List<(int PassIndex, RenderPassResourceUsage Usage, string Key)>();

        foreach (RenderPassMetadata pass in lookup.Values)
        {
            foreach (RenderPassResourceUsage usage in pass.ResourceUsages)
            {
                if (usage.LogicalVersion < 0 || string.IsNullOrWhiteSpace(usage.ResourceName))
                    continue;

                string key = $"{usage.ResourceName}@v{usage.LogicalVersion}";
                bool writes = usage.Access is ERenderGraphAccess.Write or ERenderGraphAccess.ReadWrite;
                bool reads = usage.Access is ERenderGraphAccess.Read or ERenderGraphAccess.ReadWrite;

                if (writes)
                {
                    if (!producersByVersion.TryGetValue(key, out List<(int PassIndex, RenderPassResourceUsage Usage)>? producers))
                    {
                        producers = [];
                        producersByVersion.Add(key, producers);
                    }

                    for (int producerIndex = 0; producerIndex < producers.Count; producerIndex++)
                    {
                        if (producers[producerIndex].Usage.SubresourceRange.Overlaps(usage.SubresourceRange))
                            throw new InvalidOperationException($"Logical resource '{usage.ResourceName}' version {usage.LogicalVersion} has overlapping producers in passes {producers[producerIndex].PassIndex} and {pass.PassIndex}.");
                    }

                    producers.Add((pass.PassIndex, usage));
                }

                if (reads)
                    consumers.Add((pass.PassIndex, usage, key));
            }
        }

        foreach ((int consumer, RenderPassResourceUsage usage, string key) in consumers)
        {
            bool matchedProducer = false;
            if (producersByVersion.TryGetValue(key, out List<(int PassIndex, RenderPassResourceUsage Usage)>? producers))
            {
                for (int producerIndex = 0; producerIndex < producers.Count; producerIndex++)
                {
                    (int producer, RenderPassResourceUsage producerUsage) = producers[producerIndex];
                    if (!producerUsage.SubresourceRange.Overlaps(usage.SubresourceRange))
                        continue;

                    matchedProducer = true;
                    if (producer != consumer)
                        AddDependencyEdge(producer, consumer, edges, inDegree);
                }
            }

            if (!matchedProducer && (!usage.IsImported || usage.ImportedInitialState is null))
                throw new InvalidOperationException($"Logical resource '{usage.ResourceName}' version {usage.LogicalVersion} is read before it is produced or imported with a valid initial state.");
        }
    }

    private static void AddDependencyEdge(
        int producer,
        int consumer,
        Dictionary<int, List<int>> edges,
        Dictionary<int, int> inDegree)
    {
        if (edges[producer].Contains(consumer))
            return;

        edges[producer].Add(consumer);
        inDegree[consumer] = inDegree[consumer] + 1;
    }

    private static string DescribeCycle(
        IReadOnlyDictionary<int, RenderPassMetadata> lookup,
        IReadOnlyDictionary<int, List<int>> edges,
        IReadOnlyDictionary<int, int> inDegree)
    {
        HashSet<int> remaining = inDegree.Where(static pair => pair.Value > 0).Select(static pair => pair.Key).ToHashSet();
        int start = remaining.Min();
        var path = new List<int>();
        var pathIndex = new Dictionary<int, int>();
        int current = start;

        while (true)
        {
            if (pathIndex.TryGetValue(current, out int cycleStart))
                return string.Join(" -> ", path.Skip(cycleStart).Append(current).Select(index => $"{index} ('{lookup[index].Name}')"));

            pathIndex[current] = path.Count;
            path.Add(current);
            int next = edges[current].FirstOrDefault(remaining.Contains);
            if (!remaining.Contains(next))
                return string.Join(" -> ", remaining.OrderBy(static index => index).Select(index => $"{index} ('{lookup[index].Name}')"));

            current = next;
        }
    }

    private static int ComparePassDeclarationOrder(
        IReadOnlyDictionary<int, RenderPassMetadata> lookup,
        int left,
        int right)
    {
        if (left == right)
            return 0;

        int leftOrder = lookup[left].DeclarationOrder;
        int rightOrder = lookup[right].DeclarationOrder;
        int orderCompare = leftOrder.CompareTo(rightOrder);
        return orderCompare != 0
            ? orderCompare
            : left.CompareTo(right);
    }

    private static RenderGraphSyncState ResolveDependencyState(IReadOnlyCollection<RenderPassMetadata> passMetadata, int passIndex)
    {
        RenderPassMetadata? pass = passMetadata.FirstOrDefault(p => p.PassIndex == passIndex);
        ERenderGraphPassStage stage = pass?.Stage ?? ERenderGraphPassStage.Graphics;
        return new RenderGraphSyncState(ResolveStage(ERenderPassResourceType.TransferDestination, stage), RenderGraphAccessMask.MemoryRead | RenderGraphAccessMask.MemoryWrite, null);
    }

    internal static RenderGraphSyncState ResolveState(RenderPassResourceUsage usage, ERenderGraphPassStage passStage)
        => new(
            ResolveStage(usage.ResourceType, passStage),
            ResolveAccess(usage.ResourceType, usage.Access),
            ResolveLayout(usage.ResourceType));

    private static RenderGraphStageMask ResolveStage(ERenderPassResourceType type, ERenderGraphPassStage passStage)
    {
        return type switch
        {
            ERenderPassResourceType.ColorAttachment or ERenderPassResourceType.ResolveAttachment => RenderGraphStageMask.ColorAttachmentOutput,
            ERenderPassResourceType.DepthAttachment or ERenderPassResourceType.StencilAttachment => RenderGraphStageMask.EarlyFragmentTests | RenderGraphStageMask.LateFragmentTests,
            ERenderPassResourceType.TransferSource or ERenderPassResourceType.TransferDestination => RenderGraphStageMask.Transfer,
            ERenderPassResourceType.VertexBuffer or ERenderPassResourceType.IndexBuffer => RenderGraphStageMask.VertexInput,
            ERenderPassResourceType.IndirectBuffer => RenderGraphStageMask.DrawIndirect,
            ERenderPassResourceType.UniformBuffer or ERenderPassResourceType.SampledTexture => passStage switch
            {
                ERenderGraphPassStage.Compute => RenderGraphStageMask.ComputeShader,
                ERenderGraphPassStage.Transfer => RenderGraphStageMask.Transfer,
                _ => RenderGraphStageMask.VertexShader | RenderGraphStageMask.FragmentShader
            },
            ERenderPassResourceType.StorageBuffer or ERenderPassResourceType.StorageTexture => passStage switch
            {
                ERenderGraphPassStage.Compute => RenderGraphStageMask.ComputeShader,
                ERenderGraphPassStage.Transfer => RenderGraphStageMask.Transfer,
                _ => RenderGraphStageMask.VertexShader | RenderGraphStageMask.FragmentShader
            },
            _ => passStage switch
            {
                ERenderGraphPassStage.Compute => RenderGraphStageMask.ComputeShader,
                ERenderGraphPassStage.Transfer => RenderGraphStageMask.Transfer,
                _ => RenderGraphStageMask.AllGraphics
            }
        };
    }

    private static RenderGraphAccessMask ResolveAccess(ERenderPassResourceType type, ERenderGraphAccess accessIntent)
    {
        bool reads = accessIntent is ERenderGraphAccess.Read or ERenderGraphAccess.ReadWrite;
        bool writes = accessIntent is ERenderGraphAccess.Write or ERenderGraphAccess.ReadWrite;

        RenderGraphAccessMask mask = RenderGraphAccessMask.None;
        switch (type)
        {
            case ERenderPassResourceType.ColorAttachment:
            case ERenderPassResourceType.ResolveAttachment:
                if (reads)
                    mask |= RenderGraphAccessMask.ColorAttachmentRead;
                if (writes)
                    mask |= RenderGraphAccessMask.ColorAttachmentWrite;
                break;
            case ERenderPassResourceType.DepthAttachment:
            case ERenderPassResourceType.StencilAttachment:
                if (reads)
                    mask |= RenderGraphAccessMask.DepthStencilRead;
                if (writes)
                    mask |= RenderGraphAccessMask.DepthStencilWrite;
                break;
            case ERenderPassResourceType.UniformBuffer:
            case ERenderPassResourceType.SampledTexture:
                mask |= RenderGraphAccessMask.ShaderRead;
                if (type == ERenderPassResourceType.UniformBuffer)
                    mask |= RenderGraphAccessMask.UniformRead;
                break;
            case ERenderPassResourceType.StorageBuffer:
            case ERenderPassResourceType.StorageTexture:
                if (reads)
                    mask |= RenderGraphAccessMask.ShaderRead;
                if (writes)
                    mask |= RenderGraphAccessMask.ShaderWrite;
                break;
            case ERenderPassResourceType.VertexBuffer:
                mask |= RenderGraphAccessMask.VertexAttributeRead;
                break;
            case ERenderPassResourceType.IndexBuffer:
                mask |= RenderGraphAccessMask.IndexRead;
                break;
            case ERenderPassResourceType.IndirectBuffer:
                mask |= RenderGraphAccessMask.IndirectCommandRead;
                break;
            case ERenderPassResourceType.TransferSource:
                mask |= RenderGraphAccessMask.TransferRead;
                break;
            case ERenderPassResourceType.TransferDestination:
                mask |= RenderGraphAccessMask.TransferWrite;
                break;
            default:
                if (reads)
                    mask |= RenderGraphAccessMask.MemoryRead;
                if (writes)
                    mask |= RenderGraphAccessMask.MemoryWrite;
                break;
        }

        return mask == RenderGraphAccessMask.None ? RenderGraphAccessMask.MemoryRead : mask;
    }

    private static RenderGraphImageLayout? ResolveLayout(ERenderPassResourceType type)
    {
        return type switch
        {
            ERenderPassResourceType.ColorAttachment or ERenderPassResourceType.ResolveAttachment => RenderGraphImageLayout.ColorAttachment,
            ERenderPassResourceType.DepthAttachment or ERenderPassResourceType.StencilAttachment => RenderGraphImageLayout.DepthStencilAttachment,
            ERenderPassResourceType.SampledTexture => RenderGraphImageLayout.ShaderReadOnly,
            ERenderPassResourceType.StorageTexture => RenderGraphImageLayout.General,
            ERenderPassResourceType.TransferSource => RenderGraphImageLayout.TransferSource,
            ERenderPassResourceType.TransferDestination => RenderGraphImageLayout.TransferDestination,
            _ => null
        };
    }

    private static bool Writes(ERenderGraphAccess access)
        => access is ERenderGraphAccess.Write or ERenderGraphAccess.ReadWrite;

    private static RenderGraphSubresourceRange Intersect(
        in RenderGraphSubresourceRange left,
        in RenderGraphSubresourceRange right)
    {
        uint mipStart = Math.Max(left.BaseMipLevel, right.BaseMipLevel);
        uint layerStart = Math.Max(left.BaseArrayLayer, right.BaseArrayLayer);
        ulong mipEnd = Math.Min(End(left.BaseMipLevel, left.MipLevelCount), End(right.BaseMipLevel, right.MipLevelCount));
        ulong layerEnd = Math.Min(End(left.BaseArrayLayer, left.ArrayLayerCount), End(right.BaseArrayLayer, right.ArrayLayerCount));
        return new RenderGraphSubresourceRange(
            mipStart,
            Count(mipStart, mipEnd),
            layerStart,
            Count(layerStart, layerEnd));
    }

    private static ulong End(uint start, uint count)
        => count == RenderGraphSubresourceRange.Remaining ? ulong.MaxValue : (ulong)start + count;

    private static uint Count(uint start, ulong end)
        => end == ulong.MaxValue ? RenderGraphSubresourceRange.Remaining : checked((uint)(end - start));

    private readonly record struct TrackedResourceUsage(
        int PassIndex,
        RenderGraphSubresourceRange SubresourceRange,
        RenderGraphSyncState State,
        bool Writes);
}
