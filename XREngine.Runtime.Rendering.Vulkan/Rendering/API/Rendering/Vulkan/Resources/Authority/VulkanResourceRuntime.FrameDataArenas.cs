namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanResourceRuntime
{
    private const int ScreenshotReadbackFrameDataSlotCount = 8;
    private const int DepthReadbackFrameDataSlotCount = 8;
    private const int GpuStatsReadbackFrameDataSlotCount = 32;
    private const ulong FrameDataArenaInitialChunkCapacity = 256UL * 1024UL;
    private const ulong ReadbackFrameDataArenaInitialChunkCapacity = 1024UL * 1024UL;
    private const ulong GpuStatsFrameDataArenaInitialChunkCapacity = 4UL * 1024UL;
    private const ulong SynchronousFrameDataArenaInitialChunkCapacity = 1024UL * 1024UL;
    private readonly object _synchronousFrameDataArenaGate = new();

    /// <summary>Canonical mapped storage for frame-varying upload and GPU data streams.</summary>
    internal VulkanFrameDataArena? FrameDataArena { get; private set; }

    /// <summary>Independent mapped arena for fence-owned asynchronous readbacks.</summary>
    internal VulkanFrameDataArena? ReadbackFrameDataArena { get; private set; }

    /// <summary>Independent small-slot arena for asynchronous GPU statistics readbacks.</summary>
    internal VulkanFrameDataArena? GpuStatsFrameDataArena { get; private set; }

    /// <summary>Single-slot scratch arena whose callers prove completion with a synchronous fence wait.</summary>
    internal VulkanFrameDataArena? SynchronousFrameDataArena { get; private set; }

    /// <summary>
    /// Acquires the synchronous scratch slot exclusively until its submit/wait/read operation
    /// completes. Callers must publish accepted queue ownership before awaiting the fence.
    /// </summary>
    internal bool TryAcquireSynchronousFrameDataArenaLease(out VulkanSynchronousFrameDataArenaLease lease)
    {
        lease = default;
        if (!Monitor.TryEnter(_synchronousFrameDataArenaGate))
            return false;

        VulkanFrameDataArena? arena = SynchronousFrameDataArena;
        if (arena is null || !arena.IsActive ||
            !arena.TryResetFrameSlot(0, arena.Generation, submissionCompletionProven: false))
        {
            Monitor.Exit(_synchronousFrameDataArenaGate);
            return false;
        }

        lease = new VulkanSynchronousFrameDataArenaLease(this, arena);
        return true;
    }

    internal void ReleaseSynchronousFrameDataArenaLease()
        => Monitor.Exit(_synchronousFrameDataArenaGate);

    internal void InitializeFrameDataArenas(
        VulkanDeviceContext deviceContext,
        int desktopFrameSlotCount)
    {
        if (FrameDataArena is not null || ReadbackFrameDataArena is not null ||
            GpuStatsFrameDataArena is not null || SynchronousFrameDataArena is not null)
            throw new InvalidOperationException("Vulkan frame-data arenas are already initialized.");

        int frameSlotCount = Math.Max(desktopFrameSlotCount, Descriptors.FrameSlotCount);
        if (frameSlotCount <= 0)
            return;

        VulkanMappedFrameArenaBackend backend = new(
            deviceContext.Api,
            deviceContext.PhysicalDevice,
            deviceContext.Device,
            deviceContext,
            this,
            Allocations.Buffers,
            deviceContext.NonCoherentAtomSize);
        VulkanFrameDataArena frameData = new(backend, FrameDataArenaInitialChunkCapacity);
        VulkanFrameDataArena readback = new(backend, ReadbackFrameDataArenaInitialChunkCapacity);
        VulkanFrameDataArena gpuStats = new(backend, GpuStatsFrameDataArenaInitialChunkCapacity);
        VulkanFrameDataArena synchronous = new(backend, SynchronousFrameDataArenaInitialChunkCapacity);
        try
        {
            frameData.Initialize(frameSlotCount);
            readback.Initialize(ScreenshotReadbackFrameDataSlotCount + DepthReadbackFrameDataSlotCount);
            gpuStats.Initialize(GpuStatsReadbackFrameDataSlotCount);
            synchronous.Initialize(1);
            FrameDataArena = frameData;
            ReadbackFrameDataArena = readback;
            GpuStatsFrameDataArena = gpuStats;
            SynchronousFrameDataArena = synchronous;
        }
        catch
        {
            frameData.Destroy();
            readback.Destroy();
            gpuStats.Destroy();
            synchronous.Destroy();
            throw;
        }
    }

    internal void DestroyFrameDataArenas()
    {
        VulkanFrameDataArena? readback = ReadbackFrameDataArena;
        VulkanFrameDataArena? gpuStats = GpuStatsFrameDataArena;
        VulkanFrameDataArena? frameData = FrameDataArena;
        VulkanFrameDataArena? synchronous = SynchronousFrameDataArena;
        SynchronousFrameDataArena = null;
        GpuStatsFrameDataArena = null;
        ReadbackFrameDataArena = null;
        FrameDataArena = null;
        readback?.Destroy();
        gpuStats?.Destroy();
        synchronous?.Destroy();
        frameData?.Destroy();
    }

    /// <summary>
    /// Publishes the fixed set of mapped-memory owners at a frame boundary. This reads only
    /// owner-maintained counters; it never walks allocation registries or dirty-range storage.
    /// </summary>
    internal void PublishMappedMemoryTelemetry()
    {
        VulkanMappedMemoryCounters mappedMemory = Buffers.SnapshotMappedMemoryCounters();
        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanMappedMemoryGauges(
            mappedMemory.Reservations,
            mappedMemory.ReservedBytes,
            mappedMemory.FlushExpansionBytes,
            mappedMemory.InvalidateExpansionBytes,
            mappedMemory.Failures);

        VulkanFrameDataArena.MappingTelemetry frameData = FrameDataArena?.GetMappingTelemetry() ?? default;
        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanFrameDataArenaMappingGauges(
            frameData.AllocatedBytes,
            frameData.AllocationCount,
            frameData.DirtyBytes,
            frameData.DirtyRangeCount,
            frameData.FlushExpansionBytes,
            frameData.InvalidateExpansionBytes);

        MappedFrameArena?.PublishTelemetry();
    }

    internal void RegisterMappedFrameArenaChunkLifetime(
        Silk.NET.Vulkan.Buffer buffer,
        string owner)
        => RegisterResource(
            Silk.NET.Vulkan.ObjectType.Buffer,
            buffer.Handle,
            owner);

    internal void CompleteMappedFrameArenaChunkLifetime(
        Silk.NET.Vulkan.Buffer buffer)
        => CompleteSimpleResourceDestruction(
            Silk.NET.Vulkan.ObjectType.Buffer,
            buffer.Handle);
}
