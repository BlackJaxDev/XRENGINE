using System.Collections.Concurrent;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Command pool ownership for the desktop worker in WorkerRecord mode.
/// <para>
/// Vulkan requires external synchronization of a command pool while a thread
/// allocates, records, resets or frees any command buffer from it. In WorkerRecord
/// mode a background render lane records the desktop primary while the render
/// thread keeps running eye frames and command maintenance. The worker never takes
/// <see cref="VulkanCommandPoolAuthority.Gate"/> while it records, so that gate does
/// not keep the two threads apart.
/// </para>
/// <para>
/// The rule is ownership, not a lock: a pool that the desktop worker can use
/// belongs to the worker from the batch submit until the batch join. Only after the
/// join does it belong to the render thread again. While a batch is in flight,
/// other threads must not free, reset or allocate from such a pool. Retired command
/// buffers and invalidated resets that hit a worker pool stay queued, and the join
/// runs them. This keeps retired command buffers from piling up when the worker
/// runs every frame.
/// </para>
/// <para>
/// The worker pools are the lane arena pools of the desktop lane (registered at
/// submit), and every pool that the worker thread allocates a command buffer from
/// during its recording (command-chain pools and the worker thread's own pool). A
/// registered pool stays registered until it is destroyed, because the next batch
/// can use it again and its ownership must be known before that batch starts.
/// </para>
/// </summary>
internal sealed partial class VulkanCommandRuntime
{
    /// <summary>
    /// Nesting depth of desktop worker recording on the current thread. A non-zero
    /// value means that this thread is the worker that owns the registered pools.
    /// </summary>
    [ThreadStatic]
    private static int t_desktopWorkerRecordingDepth;

    /// <summary>
    /// Handles of the command pools that the desktop worker can use. Reads come from
    /// any thread without a lock. Adds happen only when a pool is first seen.
    /// </summary>
    private readonly ConcurrentDictionary<ulong, byte> _desktopWorkerPools = new();

    /// <summary>One while a desktop worker batch owns the registered pools.</summary>
    private int _desktopWorkerBatchInFlight;

    /// <summary>
    /// Non-zero when a retired command buffer was kept queued for worker ownership
    /// since the last join. The join then frees the queued buffers.
    /// </summary>
    private int _desktopWorkerDeferredRetiredCommandBuffers;

    /// <summary>
    /// Non-zero when an invalidated command buffer reset was skipped for worker
    /// ownership since the last join. The join then runs the resets.
    /// </summary>
    private int _desktopWorkerDeferredInvalidatedResets;

    /// <summary>
    /// Reused storage for the retired command buffers that the join frees. The
    /// render thread is the only user, so the storage needs no lock.
    /// </summary>
    private readonly List<(int FrameSlot, RetiredCommandBuffer Entry)> _desktopWorkerReadyRetiredScratch = new(64);

    /// <summary>True while a desktop worker batch owns its command pools.</summary>
    internal bool IsDesktopWorkerBatchInFlight
        => Volatile.Read(ref _desktopWorkerBatchInFlight) != 0;

    /// <summary>
    /// Gives the desktop lane's arena pools to the worker. Call this on the render
    /// thread before the batch is submitted, so that no maintenance write can start
    /// on a pool after the worker begins to record.
    /// </summary>
    internal void BeginDesktopWorkerPoolOwnership(VulkanLaneCommandFamilyArena laneArena)
    {
        ArgumentNullException.ThrowIfNull(laneArena);
        RegisterDesktopWorkerPool(laneArena.TransientPool.Handle);
        RegisterDesktopWorkerPool(laneArena.RetainedPool.Handle);
        if (Interlocked.Exchange(ref _desktopWorkerBatchInFlight, 1) != 0)
            throw new InvalidOperationException("A desktop worker batch already owns the desktop worker command pools.");
    }

    /// <summary>
    /// Gives the worker pools back to the render thread after the batch join. Then
    /// it frees the retired command buffers and runs the invalidated resets that
    /// maintenance kept queued while the worker owned the pools. Calling it when no
    /// batch is in flight does nothing.
    /// </summary>
    internal void EndDesktopWorkerPoolOwnership()
    {
        if (Interlocked.Exchange(ref _desktopWorkerBatchInFlight, 0) == 0)
            return;

        FreeRetiredCommandBuffersDeferredForDesktopWorker();
        if (Interlocked.Exchange(ref _desktopWorkerDeferredInvalidatedResets, 0) != 0)
            ResetInvalidatedCommandBuffers(maxItems: 64);
    }

    /// <summary>
    /// Marks the current thread as the desktop worker for the duration of the
    /// returned scope. Pools that this thread allocates from become worker pools,
    /// and this thread can write them while the batch is in flight.
    /// </summary>
    internal static DesktopWorkerRecordingThreadScope EnterDesktopWorkerRecordingThread()
        => DesktopWorkerRecordingThreadScope.Enter();

    /// <summary>
    /// Returns true when the current thread must not write <paramref name="poolHandle"/>
    /// now, because an in-flight desktop worker batch owns it.
    /// </summary>
    private bool MustDeferCommandPoolWriteForDesktopWorker(ulong poolHandle)
        => poolHandle != 0 &&
           t_desktopWorkerRecordingDepth == 0 &&
           Volatile.Read(ref _desktopWorkerBatchInFlight) != 0 &&
           _desktopWorkerPools.ContainsKey(poolHandle);

    /// <summary>
    /// Returns true when the current thread must not write the pool that allocated
    /// the command buffer <paramref name="commandBufferHandle"/>, because an
    /// in-flight desktop worker batch owns that pool. The caller holds the lock of
    /// <paramref name="tracker"/>.
    /// </summary>
    private bool MustDeferCommandBufferWriteForDesktopWorkerNoLock(
        VulkanResourceLifetimeTracker tracker,
        ulong commandBufferHandle)
    {
        // Check the cheap flag first, so that other modes skip the lookup.
        if (Volatile.Read(ref _desktopWorkerBatchInFlight) == 0)
            return false;

        return tracker.CommandBufferLifetimes.TryGetValue(
                   commandBufferHandle,
                   out VulkanCommandBufferLifetimeRecord? lifetime) &&
               MustDeferCommandPoolWriteForDesktopWorker(lifetime.AllocatingCommandPool.Handle);
    }

    /// <summary>
    /// Registers <paramref name="poolHandle"/> as a worker pool when the current
    /// thread records for the desktop worker. Command buffer allocation calls this
    /// before the native allocation.
    /// </summary>
    private void RegisterDesktopWorkerPoolIfWorkerThread(ulong poolHandle)
    {
        if (t_desktopWorkerRecordingDepth != 0)
            RegisterDesktopWorkerPool(poolHandle);
    }

    private void RegisterDesktopWorkerPool(ulong poolHandle)
    {
        // ContainsKey does not lock or allocate, so the steady state costs nothing.
        if (poolHandle != 0 && !_desktopWorkerPools.ContainsKey(poolHandle))
            _desktopWorkerPools.TryAdd(poolHandle, 0);
    }

    /// <summary>
    /// Removes a destroyed pool, so that a new pool with a reused handle is not
    /// treated as a worker pool.
    /// </summary>
    private void UnregisterDesktopWorkerPool(ulong poolHandle)
    {
        if (poolHandle != 0)
            _desktopWorkerPools.TryRemove(poolHandle, out _);
    }

    /// <summary>
    /// Records that maintenance kept a ready retired command buffer queued because
    /// the desktop worker owns its pool. The next join frees it.
    /// </summary>
    private void NoteRetiredCommandBufferDeferredForDesktopWorker()
    {
        Volatile.Write(ref _desktopWorkerDeferredRetiredCommandBuffers, 1);
        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanRetiredCommandBufferWorkerOwnedDeferral();
    }

    /// <summary>
    /// Frees the retired command buffers in worker pools that are ready for
    /// destruction. It runs on the render thread right after the join, when the
    /// render thread owns the pools again. Entries that are not ready stay queued
    /// for the normal drain.
    /// </summary>
    private void FreeRetiredCommandBuffersDeferredForDesktopWorker()
    {
        if (Interlocked.Exchange(ref _desktopWorkerDeferredRetiredCommandBuffers, 0) == 0)
            return;

        VulkanResourceRuntime resourceRuntime = ResourceRuntime;
        List<RetiredCommandBuffer>[] lists = resourceRuntime.Lifetime.Retirement.CommandBuffers;
        List<(int FrameSlot, RetiredCommandBuffer Entry)> ready = _desktopWorkerReadyRetiredScratch;
        ready.Clear();
        using (VulkanFrameLockScope.Enter(
                   resourceRuntime.Lifetime.Retirement.SyncRoot,
                   EVulkanFrameWaitReason.ResourceLifetimeLock))
        {
            for (int frameSlot = 0; frameSlot < lists.Length; frameSlot++)
            {
                List<RetiredCommandBuffer> list = lists[frameSlot];
                for (int index = list.Count - 1; index >= 0; index--)
                {
                    RetiredCommandBuffer candidate = list[index];
                    if (!_desktopWorkerPools.ContainsKey(candidate.CommandPool.Handle))
                        continue;
                    if (!IsCommandBufferRetirementReady(resourceRuntime, candidate.CommandBuffer, candidate.Ticket))
                        continue;

                    ready.Add((frameSlot, candidate));
                    list.RemoveAt(index);
                }
            }

            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanPendingRetiredCommandBufferCount(
                VulkanResourceRetirementQueue.CountPendingNoLock(lists));
        }

        for (int index = 0; index < ready.Count; index++)
        {
            (int frameSlot, RetiredCommandBuffer entry) = ready[index];
            FreeReadyRetiredCommandBuffer(Api, DeviceContext.Device, resourceRuntime, frameSlot, in entry);
        }

        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanRetiredResourceDrain(commandBuffers: ready.Count);
        ready.Clear();
    }

    /// <summary>
    /// Scope that marks the current thread as the desktop worker. The depth counter
    /// allows nested scopes on the same thread. A default instance does nothing.
    /// </summary>
    internal readonly ref struct DesktopWorkerRecordingThreadScope
    {
        private readonly bool _entered;

        private DesktopWorkerRecordingThreadScope(bool entered)
            => _entered = entered;

        internal static DesktopWorkerRecordingThreadScope Enter()
        {
            t_desktopWorkerRecordingDepth++;
            return new DesktopWorkerRecordingThreadScope(entered: true);
        }

        public void Dispose()
        {
            if (_entered)
                t_desktopWorkerRecordingDepth--;
        }
    }
}
