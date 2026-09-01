namespace XREngine.Rendering.Vulkan;

public sealed partial class VulkanRenderer
{
    /// <summary>
    /// Copies current retirement counters for diagnostic clients. This allocates
    /// only on request and never polls Vulkan or changes completion proof.
    /// Frame-local values may advance while an asynchronous client reads them.
    /// </summary>
    public VulkanRetirementDiagnostic CaptureRetirementDiagnostics()
    {
        VulkanRetirementMeterSnapshot snapshot = _resourceRuntime.GetRetirementMeterSnapshot();
        int count = (int)EVulkanRetirementWorkClass.Callback + 1;
        VulkanRetirementClassDiagnostic[] classes = new VulkanRetirementClassDiagnostic[count];
        for (int index = 0; index < count; index++)
        {
            EVulkanRetirementWorkClass workClass = (EVulkanRetirementWorkClass)index;
            classes[index] = new(
                workClass.ToString(), snapshot.GetOrdinaryCap(workClass),
                snapshot.GetHighWaterMark(workClass), snapshot.GetAdmitted(workClass),
                snapshot.GetCompleted(workClass), snapshot.GetDeferred(workClass),
                snapshot.GetBacklog(workClass), snapshot.GetOldestPendingAgeMilliseconds(workClass),
                snapshot.IsUncapped(workClass), snapshot.GetUncappedActivationCount(workClass));
        }

        int quarantined;
        lock (_resourceRuntime.Lifetime.Retirement.SyncRoot)
            quarantined = _resourceRuntime.Lifetime.Retirement.QuarantinedFailures.Count;
        VulkanDesktopOutputState desktop = _outputRuntime.Desktop;
        VulkanWsiPresentCompletion? presentation = desktop.PresentCompletion;
        return new(snapshot.FrameSerial, snapshot.GetElapsedMilliseconds(),
            snapshot.GetDrainDurationSampleCount(), snapshot.GetDrainDurationOverflowCount(),
            snapshot.GetMaximumPublishedDrainDurationMilliseconds(), snapshot.GetDrainDurationP50Milliseconds(),
            snapshot.GetDrainDurationP95Milliseconds(), snapshot.GetDrainDurationP99Milliseconds(), classes, quarantined,
            desktop.Maintenance1Enabled, desktop.Generation,
            presentation?.SubmittedCount ?? 0, presentation?.CompletedCount ?? 0,
            presentation?.CapacityDeferrals ?? 0, presentation?.HasUnprovenLegacyPresent ?? false,
            _frameLoop.DeviceWaitIdleCalls);
    }
}
