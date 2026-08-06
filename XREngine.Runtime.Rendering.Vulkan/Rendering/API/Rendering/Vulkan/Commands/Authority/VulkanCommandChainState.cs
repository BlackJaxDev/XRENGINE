namespace XREngine.Rendering.Vulkan;

/// <summary>Owns bounded command-chain caches and reusable scheduling scratch.</summary>
internal sealed class VulkanCommandChainState
{
    internal Dictionary<CommandChainKey, CommandChain>[]? Caches;
    internal Dictionary<uint, Dictionary<CommandChainKey, CommandChain>>? ExternalCaches;
    internal List<RenderPacket> PacketScratch { get; } = [];
    internal List<RenderPacket> PacketPool { get; } = [];
    internal List<RenderPacketPayloadArena> PacketPayloadArenas { get; } = [];
    internal RenderPacketPayloadArena? ActivePacketPayloadArena;
    internal DrawPacket[] DrawPacketScratch { get; } = new DrawPacket[64];
    internal int PacketPoolCursor;
    internal List<RenderPassChainGroup> GroupScratch { get; } = [];
    internal List<CommandChainKey> GroupKeyScratch { get; } = [];
    internal Dictionary<ulong, int> StructuralOccurrenceScratch { get; } = [];
    internal HashSet<RenderViewKey> ViewKeyScratch { get; } = [];
    internal Dictionary<uint, CommandChainStabilityGuardState> StabilityGuardStates { get; } = [];
    internal int TraceDumped;
    internal long TraceLastDumpTimestamp;
}
