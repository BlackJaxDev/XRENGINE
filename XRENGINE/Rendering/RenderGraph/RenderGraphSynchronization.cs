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
    RenderPassResourceType ResourceType,
    RenderGraphSyncState ProducerState,
    RenderGraphSyncState ConsumerState,
    bool DependencyOnly);

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
        var lastUsageByResource = new Dictionary<string, (int PassIndex, RenderPassResourceType Type, RenderGraphSyncState State)>(StringComparer.OrdinalIgnoreCase);

        foreach (RenderPassMetadata pass in orderedPasses)
        {
            foreach (RenderPassResourceUsage usage in pass.ResourceUsages)
            {
                if (string.IsNullOrWhiteSpace(usage.ResourceName))
                    continue;

                RenderGraphSyncState consumerState = ResolveState(usage, pass.Stage);
                if (lastUsageByResource.TryGetValue(usage.ResourceName, out var producer))
                {
                    if (producer.PassIndex != pass.PassIndex)
                    {
                        edges.Add(new RenderGraphSynchronizationEdge(
                            producer.PassIndex,
                            pass.PassIndex,
                            usage.ResourceName,
                            usage.ResourceType,
                            producer.State,
                            consumerState,
                            DependencyOnly: false));
                    }
                }

                lastUsageByResource[usage.ResourceName] = (pass.PassIndex, usage.ResourceType, consumerState);
            }
        }

        // Preserve explicit dependencies even when no resource edge was inferred.
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
                    RenderPassResourceType.TransferDestination,
                    producerState,
                    consumerState,
                    DependencyOnly: true));
            }
        }

        return new RenderGraphSynchronizationInfo(edges);
    }

    public static IReadOnlyList<RenderPassMetadata> TopologicallySort(IReadOnlyCollection<RenderPassMetadata> passMetadata)
    {
        Dictionary<int, RenderPassMetadata> lookup = passMetadata.ToDictionary(p => p.PassIndex);
        Dictionary<int, int> inDegree = lookup.Keys.ToDictionary(k => k, _ => 0);
        Dictionary<int, List<int>> edges = lookup.Keys.ToDictionary(k => k, _ => new List<int>());

        foreach (RenderPassMetadata pass in lookup.Values)
        {
            foreach (int dependency in pass.ExplicitDependencies)
            {
                if (!lookup.ContainsKey(dependency))
                    continue;

                edges[dependency].Add(pass.PassIndex);
                inDegree[pass.PassIndex] = inDegree[pass.PassIndex] + 1;
            }
        }

        SortedSet<int> ready = new(inDegree.Where(kvp => kvp.Value == 0).Select(kvp => kvp.Key));
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

        HashSet<int> included = ordered.Select(p => p.PassIndex).ToHashSet();
        foreach (RenderPassMetadata pass in lookup.Values.OrderBy(p => p.PassIndex))
        {
            if (!included.Contains(pass.PassIndex))
                ordered.Add(pass);
        }

        return ordered;
    }

    private static RenderGraphSyncState ResolveDependencyState(IReadOnlyCollection<RenderPassMetadata> passMetadata, int passIndex)
    {
        RenderPassMetadata? pass = passMetadata.FirstOrDefault(p => p.PassIndex == passIndex);
        RenderGraphPassStage stage = pass?.Stage ?? RenderGraphPassStage.Graphics;
        return new RenderGraphSyncState(ResolveStage(RenderPassResourceType.TransferDestination, stage), RenderGraphAccessMask.MemoryRead | RenderGraphAccessMask.MemoryWrite, null);
    }

    private static RenderGraphSyncState ResolveState(RenderPassResourceUsage usage, RenderGraphPassStage passStage)
        => new(
            ResolveStage(usage.ResourceType, passStage),
            ResolveAccess(usage.ResourceType, usage.Access),
            ResolveLayout(usage.ResourceType));

    private static RenderGraphStageMask ResolveStage(RenderPassResourceType type, RenderGraphPassStage passStage)
    {
        return type switch
        {
            RenderPassResourceType.ColorAttachment or RenderPassResourceType.ResolveAttachment => RenderGraphStageMask.ColorAttachmentOutput,
            RenderPassResourceType.DepthAttachment or RenderPassResourceType.StencilAttachment => RenderGraphStageMask.EarlyFragmentTests | RenderGraphStageMask.LateFragmentTests,
            RenderPassResourceType.TransferSource or RenderPassResourceType.TransferDestination => RenderGraphStageMask.Transfer,
            RenderPassResourceType.VertexBuffer or RenderPassResourceType.IndexBuffer => RenderGraphStageMask.VertexInput,
            RenderPassResourceType.IndirectBuffer => RenderGraphStageMask.DrawIndirect,
            RenderPassResourceType.UniformBuffer or RenderPassResourceType.SampledTexture => passStage switch
            {
                RenderGraphPassStage.Compute => RenderGraphStageMask.ComputeShader,
                RenderGraphPassStage.Transfer => RenderGraphStageMask.Transfer,
                _ => RenderGraphStageMask.VertexShader | RenderGraphStageMask.FragmentShader
            },
            RenderPassResourceType.StorageBuffer or RenderPassResourceType.StorageTexture => passStage switch
            {
                RenderGraphPassStage.Compute => RenderGraphStageMask.ComputeShader,
                RenderGraphPassStage.Transfer => RenderGraphStageMask.Transfer,
                _ => RenderGraphStageMask.VertexShader | RenderGraphStageMask.FragmentShader
            },
            _ => passStage switch
            {
                RenderGraphPassStage.Compute => RenderGraphStageMask.ComputeShader,
                RenderGraphPassStage.Transfer => RenderGraphStageMask.Transfer,
                _ => RenderGraphStageMask.AllGraphics
            }
        };
    }

    private static RenderGraphAccessMask ResolveAccess(RenderPassResourceType type, RenderGraphAccess accessIntent)
    {
        bool reads = accessIntent is RenderGraphAccess.Read or RenderGraphAccess.ReadWrite;
        bool writes = accessIntent is RenderGraphAccess.Write or RenderGraphAccess.ReadWrite;

        RenderGraphAccessMask mask = RenderGraphAccessMask.None;
        switch (type)
        {
            case RenderPassResourceType.ColorAttachment:
            case RenderPassResourceType.ResolveAttachment:
                if (reads)
                    mask |= RenderGraphAccessMask.ColorAttachmentRead;
                if (writes)
                    mask |= RenderGraphAccessMask.ColorAttachmentWrite;
                break;
            case RenderPassResourceType.DepthAttachment:
            case RenderPassResourceType.StencilAttachment:
                if (reads)
                    mask |= RenderGraphAccessMask.DepthStencilRead;
                if (writes)
                    mask |= RenderGraphAccessMask.DepthStencilWrite;
                break;
            case RenderPassResourceType.UniformBuffer:
            case RenderPassResourceType.SampledTexture:
                mask |= RenderGraphAccessMask.ShaderRead;
                if (type == RenderPassResourceType.UniformBuffer)
                    mask |= RenderGraphAccessMask.UniformRead;
                break;
            case RenderPassResourceType.StorageBuffer:
            case RenderPassResourceType.StorageTexture:
                if (reads)
                    mask |= RenderGraphAccessMask.ShaderRead;
                if (writes)
                    mask |= RenderGraphAccessMask.ShaderWrite;
                break;
            case RenderPassResourceType.VertexBuffer:
                mask |= RenderGraphAccessMask.VertexAttributeRead;
                break;
            case RenderPassResourceType.IndexBuffer:
                mask |= RenderGraphAccessMask.IndexRead;
                break;
            case RenderPassResourceType.IndirectBuffer:
                mask |= RenderGraphAccessMask.IndirectCommandRead;
                break;
            case RenderPassResourceType.TransferSource:
                mask |= RenderGraphAccessMask.TransferRead;
                break;
            case RenderPassResourceType.TransferDestination:
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

    private static RenderGraphImageLayout? ResolveLayout(RenderPassResourceType type)
    {
        return type switch
        {
            RenderPassResourceType.ColorAttachment or RenderPassResourceType.ResolveAttachment => RenderGraphImageLayout.ColorAttachment,
            RenderPassResourceType.DepthAttachment or RenderPassResourceType.StencilAttachment => RenderGraphImageLayout.DepthStencilAttachment,
            RenderPassResourceType.SampledTexture => RenderGraphImageLayout.ShaderReadOnly,
            RenderPassResourceType.StorageTexture => RenderGraphImageLayout.General,
            RenderPassResourceType.TransferSource => RenderGraphImageLayout.TransferSource,
            RenderPassResourceType.TransferDestination => RenderGraphImageLayout.TransferDestination,
            _ => null
        };
    }
}
