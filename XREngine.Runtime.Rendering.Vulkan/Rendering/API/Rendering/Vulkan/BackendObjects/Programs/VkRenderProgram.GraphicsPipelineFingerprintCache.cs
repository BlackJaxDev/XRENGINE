using System.Threading;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Vulkan;

internal unsafe partial class VkRenderProgram
{
    /// <summary>Inputs and result of the last graphics pipeline fingerprint. Immutable, so workers can share it.</summary>
    private sealed class GraphicsPipelineFingerprintEntry(
        ulong linkGeneration,
        ulong interfaceGeneration,
        ulong descriptorLayoutFingerprint,
        int heapBackend,
        uint heapPushByteCount,
        string?[] stageIdentities,
        ulong value)
    {
        public ulong LinkGeneration { get; } = linkGeneration;
        public ulong InterfaceGeneration { get; } = interfaceGeneration;
        public ulong DescriptorLayoutFingerprint { get; } = descriptorLayoutFingerprint;
        public int HeapBackend { get; } = heapBackend;
        public uint HeapPushByteCount { get; } = heapPushByteCount;
        public string?[] StageIdentities { get; } = stageIdentities;
        public ulong Value { get; } = value;
    }

    private GraphicsPipelineFingerprintEntry? _graphicsPipelineFingerprintEntry;

    /// <summary>
    /// Returns <see cref="ComputeGraphicsPipelineFingerprint"/> without hashing when no
    /// input changed. Each draw builds a pipeline key, and the full hash reads the
    /// artifact identity strings and all descriptor bindings. A stage identity compares
    /// by reference, so a new string with the same text only causes a new hash.
    /// </summary>
    internal ulong GetGraphicsPipelineFingerprint()
    {
        GraphicsPipelineFingerprintEntry? entry = Volatile.Read(ref _graphicsPipelineFingerprintEntry);
        ulong linkGeneration = LinkGeneration;
        ulong interfaceGeneration = InterfaceGeneration;
        ulong descriptorLayoutFingerprint = DescriptorLayoutFingerprint;
        int heapBackend = (int)BackendContext.Resources.Descriptors.Heap.ActiveBackend;
        uint heapPushByteCount = _descriptorHeapLayout?.PushByteCount ?? 0u;
        if (entry is not null &&
            entry.LinkGeneration == linkGeneration &&
            entry.InterfaceGeneration == interfaceGeneration &&
            entry.DescriptorLayoutFingerprint == descriptorLayoutFingerprint &&
            entry.HeapBackend == heapBackend &&
            entry.HeapPushByteCount == heapPushByteCount &&
            StageIdentitiesMatch(entry.StageIdentities))
            return entry.Value;

        string?[] stageIdentities = new string?[VulkanProgramUtilities.StageOrder.Length];
        for (int stageIndex = 0; stageIndex < stageIdentities.Length; stageIndex++)
            stageIdentities[stageIndex] = ResolveStageIdentity(VulkanProgramUtilities.StageOrder[stageIndex]);

        ulong value = ComputeGraphicsPipelineFingerprint();
        Volatile.Write(ref _graphicsPipelineFingerprintEntry, new GraphicsPipelineFingerprintEntry(
            linkGeneration,
            interfaceGeneration,
            descriptorLayoutFingerprint,
            heapBackend,
            heapPushByteCount,
            stageIdentities,
            value));
        return value;
    }

    private bool StageIdentitiesMatch(string?[] stageIdentities)
    {
        for (int stageIndex = 0; stageIndex < stageIdentities.Length; stageIndex++)
            if (!ReferenceEquals(stageIdentities[stageIndex], ResolveStageIdentity(VulkanProgramUtilities.StageOrder[stageIndex])))
                return false;
        return true;
    }

    private string? ResolveStageIdentity(EProgramStageMask stage)
        => _stageLookup.TryGetValue(stage, out VkShader? shader)
            ? shader.LastArtifact?.Identity ?? shader.CompileStatus.ArtifactIdentity ?? shader.StageDebugLabel
            : null;
}
