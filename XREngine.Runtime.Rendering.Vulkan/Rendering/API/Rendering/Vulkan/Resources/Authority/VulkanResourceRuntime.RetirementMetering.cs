namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanResourceRuntime
{
    private VulkanRetirementMeter.BudgetBypassScope? _forcedRetirementBudgetBypass;
    /// <summary>Shared destruction budget for all slot drains in one production frame.</summary>
    internal VulkanRetirementMeter RetirementMeter { get; } = new();
    internal VulkanRetirementDrainScratch RetirementDrainScratch { get; } = new();

    internal void BindRetirementMetering()
        => Descriptors.ConfigureRetirementMeter(RetirementMeter);

    /// <summary>Resets shared retirement accounting at the production-frame boundary.</summary>
    internal void BeginRetirementMeteringFrame(long frameSerial)
    {
        RetirementMeter.BeginFrame(frameSerial);
        lock (Lifetime.Tracker.SyncRoot)
            Lifetime.Tracker.ReapObservedSubmissionsNoLock();
    }

    /// <summary>Explicit shutdown-only budget bypass; it does not relax completion proof requirements.</summary>
    internal VulkanRetirementMeter.BudgetBypassScope EnterForcedRetirementBudgetBypass()
        => RetirementMeter.EnterForcedBudgetBypass();

    /// <summary>Returns an allocation-free diagnostic view valid for the current production frame.</summary>
    internal VulkanRetirementMeterSnapshot GetRetirementMeterSnapshot()
    {
        RefreshRetirementBacklog();
        return RetirementMeter.GetSnapshot();
    }

    private void RefreshRetirementBacklog()
    {
        lock (Lifetime.Retirement.SyncRoot)
        {
            RetirementMeter.ReportBacklog(EVulkanRetirementWorkClass.Buffer,
                VulkanResourceRetirementQueue.CountPendingNoLock(Lifetime.Retirement.Buffers), 0);
            RetirementMeter.ReportBacklog(EVulkanRetirementWorkClass.Framebuffer,
                VulkanResourceRetirementQueue.CountPendingNoLock(Lifetime.Retirement.Framebuffers), 0);
            RetirementMeter.ReportBacklog(EVulkanRetirementWorkClass.Descriptor,
                VulkanResourceRetirementQueue.CountPendingNoLock(Lifetime.Retirement.DescriptorSets) +
                VulkanResourceRetirementQueue.CountPendingNoLock(Lifetime.Retirement.DescriptorPools), 0);
            RetirementMeter.ReportBacklog(EVulkanRetirementWorkClass.Pipeline,
                VulkanResourceRetirementQueue.CountPendingNoLock(Lifetime.Retirement.Pipelines), 0);
            RetirementMeter.ReportBacklog(EVulkanRetirementWorkClass.PipelineLayout,
                VulkanResourceRetirementQueue.CountPendingNoLock(Lifetime.Retirement.PipelineLayouts) +
                VulkanResourceRetirementQueue.CountPendingNoLock(Lifetime.Retirement.DescriptorSetLayouts), 0);
            RetirementMeter.ReportBacklog(EVulkanRetirementWorkClass.QueryPool,
                VulkanResourceRetirementQueue.CountPendingNoLock(Lifetime.Retirement.QueryPools), 0);
            RetirementMeter.ReportBacklog(EVulkanRetirementWorkClass.ImageView,
                VulkanResourceRetirementQueue.CountPendingNoLock(Lifetime.Retirement.BufferViews) +
                VulkanResourceRetirementQueue.CountPendingNoLock(Lifetime.Retirement.Images), 0);
            RetirementMeter.ReportBacklog(EVulkanRetirementWorkClass.Image,
                VulkanResourceRetirementQueue.CountPendingNoLock(Lifetime.Retirement.Images), 0);
            RetirementMeter.ReportBacklog(EVulkanRetirementWorkClass.Sampler,
                VulkanResourceRetirementQueue.CountPendingNoLock(Lifetime.Retirement.Images), 0);
            RetirementMeter.ReportBacklog(EVulkanRetirementWorkClass.CommandArtifact,
                VulkanResourceRetirementQueue.CountPendingNoLock(Lifetime.Retirement.CommandBuffers) +
                VulkanResourceRetirementQueue.CountPendingNoLock(Lifetime.Retirement.CommandPools), 0);
            RetirementMeter.ReportBacklog(EVulkanRetirementWorkClass.Callback,
                Descriptors.GetReleasedMaterialDescriptorClosureCount(), 0);

            RecordOldest(EVulkanRetirementWorkClass.Buffer, Lifetime.Retirement.Buffers, static entry => entry.Ticket);
            RecordOldest(EVulkanRetirementWorkClass.Framebuffer, Lifetime.Retirement.Framebuffers, static entry => entry.Ticket);
            RecordOldest(EVulkanRetirementWorkClass.Descriptor, Lifetime.Retirement.DescriptorSets, static entry => entry.Ticket);
            RecordOldest(EVulkanRetirementWorkClass.Descriptor, Lifetime.Retirement.DescriptorPools, static entry => entry.Ticket);
            RecordOldest(EVulkanRetirementWorkClass.Pipeline, Lifetime.Retirement.Pipelines, static entry => entry.Ticket);
            RecordOldest(EVulkanRetirementWorkClass.PipelineLayout, Lifetime.Retirement.PipelineLayouts, static entry => entry.Ticket);
            RecordOldest(EVulkanRetirementWorkClass.PipelineLayout, Lifetime.Retirement.DescriptorSetLayouts, static entry => entry.Ticket);
            RecordOldest(EVulkanRetirementWorkClass.QueryPool, Lifetime.Retirement.QueryPools, static entry => entry.Ticket);
            RecordOldest(EVulkanRetirementWorkClass.ImageView, Lifetime.Retirement.BufferViews, static entry => entry.Ticket);
            RecordOldest(EVulkanRetirementWorkClass.Image, Lifetime.Retirement.Images, static entry => entry.Ticket);
            RecordOldest(EVulkanRetirementWorkClass.ImageView, Lifetime.Retirement.Images, static entry => entry.Ticket);
            RecordOldest(EVulkanRetirementWorkClass.Sampler, Lifetime.Retirement.Images, static entry => entry.Ticket);
            RecordOldest(EVulkanRetirementWorkClass.CommandArtifact, Lifetime.Retirement.CommandBuffers, static entry => entry.Ticket);
            RecordOldest(EVulkanRetirementWorkClass.CommandArtifact, Lifetime.Retirement.CommandPools, static entry => entry.Ticket);
        }
    }

    private void RecordOldest<TEntry>(
        EVulkanRetirementWorkClass workClass,
        List<TEntry>[] entries,
        Func<TEntry, VulkanRetirementTicket> ticketSelector)
        => RetirementMeter.RecordOldestPendingTimestamp(
            workClass,
            VulkanResourceRetirementQueue.GetOldestEnqueuedTimestampNoLock(entries, ticketSelector));

    private void QuarantineRetirementFailure(
        EVulkanRetirementWorkClass workClass,
        ulong handle,
        Exception exception)
    {
        lock (Lifetime.Retirement.SyncRoot)
            Lifetime.Retirement.QuarantinedFailures.Add(new(workClass, handle, exception));
    }

    private void CompleteRetiredBufferDeduplication(int frameSlot, in RetiredBuffer retired)
    {
        lock (Lifetime.Retirement.SyncRoot)
        {
            if (retired.Buffer.Handle != 0)
                VulkanResourceRetirementQueue.ReleaseUniqueNoLock(
                    frameSlot, retired.Buffer.Handle, Lifetime.Retirement.BufferHandles,
                    Lifetime.Retirement.AllBufferHandles);
            if (retired.Memory.Handle != 0)
                VulkanResourceRetirementQueue.ReleaseUniqueNoLock(
                    frameSlot, retired.Memory.Handle, Lifetime.Retirement.MemoryHandles,
                    Lifetime.Retirement.AllMemoryHandles);
        }
    }
}
