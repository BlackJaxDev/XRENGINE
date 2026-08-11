namespace XREngine.Rendering.Vulkan;

/// <summary>Owns bounded command-chain caches and reusable scheduling scratch.</summary>
internal sealed class VulkanCommandChainState
{
    private long _artifactMutationGeneration;
    private ulong _scheduleGeneration;

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

    /// <summary>
    /// Monotonic authority clock for every native secondary-artifact mutation.
    /// A cached schedule or primary that captured this value can validate the
    /// complete command-chain set with one comparison instead of walking every
    /// chain and recorded dependency on every frame.
    /// </summary>
    internal long SnapshotArtifactMutationGeneration()
        => System.Threading.Volatile.Read(ref _artifactMutationGeneration);

    internal void NotifyArtifactMutation()
        => System.Threading.Interlocked.Increment(ref _artifactMutationGeneration);

    /// <summary>
    /// Advances the command-chain cache recency clock. The clock lives beside
    /// the cache artifacts it orders instead of behind a second scheduler.
    /// </summary>
    internal ulong NextScheduleGeneration()
        => _scheduleGeneration = VulkanGeneration.NextNonZero(_scheduleGeneration);
}
