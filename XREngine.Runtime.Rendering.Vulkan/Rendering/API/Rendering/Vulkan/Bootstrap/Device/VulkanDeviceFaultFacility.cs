namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Owns the native device-fault capability state and KHR command delegates for one logical device.
/// </summary>
internal sealed partial class VulkanDeviceFaultFacility
{
    private long _deviceLossFalloutCount;
    private string? _deviceLostReason;

    /// <summary>Gets the terminal logical-device loss reason, when one was recorded.</summary>
    internal string? DeviceLostReason => Volatile.Read(ref _deviceLostReason);

    /// <summary>Gets the number of device-loss observations after the first transition.</summary>
    internal long DeviceLossFalloutCount => Interlocked.Read(ref _deviceLossFalloutCount);

    internal void RecordDeviceLossFallout()
        => Interlocked.Increment(ref _deviceLossFalloutCount);

    internal void CompleteDeviceLoss(string reason)
        => Volatile.Write(ref _deviceLostReason, reason);

    /// <summary>
    /// Gets whether the enabled KHR device-fault feature is supported by the device.
    /// </summary>
    internal bool SupportsKhrDeviceFault { get; private set; }

    /// <summary>
    /// Gets whether KHR vendor binary fault data is enabled.
    /// </summary>
    internal bool SupportsKhrDeviceFaultVendorBinary { get; private set; }

    /// <summary>
    /// Gets whether masked KHR device-fault reports are enabled.
    /// </summary>
    internal bool SupportsKhrDeviceFaultReportMasked { get; private set; }

    /// <summary>
    /// Gets whether KHR device loss on masked reports is enabled.
    /// </summary>
    internal bool SupportsKhrDeviceFaultDeviceLostOnMasked { get; private set; }

    /// <summary>
    /// Gets the maximum report count advertised by the KHR device-fault properties.
    /// </summary>
    internal uint KhrDeviceFaultMaxReportCount { get; private set; }

    /// <summary>
    /// Gets whether the enabled EXT device-fault feature is supported by the device.
    /// </summary>
    internal bool SupportsExtDeviceFault { get; private set; }

    /// <summary>
    /// Gets whether EXT vendor binary fault data is enabled.
    /// </summary>
    internal bool SupportsExtDeviceFaultVendorBinary { get; private set; }

    /// <summary>
    /// Gets whether fault reporting is currently using the KHR command path.
    /// </summary>
    internal bool IsUsingKhrDeviceFault { get; private set; }

    /// <summary>
    /// Gets the loaded KHR fault-report command, if available.
    /// </summary>
    internal VkGetDeviceFaultReportsKhrDelegate? GetDeviceFaultReportsKhr { get; private set; }

    /// <summary>
    /// Gets the loaded KHR fault-debug command, if available.
    /// </summary>
    internal VkGetDeviceFaultDebugInfoKhrDelegate? GetDeviceFaultDebugInfoKhr { get; private set; }

    /// <summary>
    /// Publishes the KHR device-fault feature policy selected during logical-device creation.
    /// </summary>
    internal void PublishKhrSupport(
        bool supportsDeviceFault,
        bool supportsVendorBinary,
        bool supportsReportMasked,
        bool supportsDeviceLostOnMasked,
        uint maxReportCount)
    {
        SupportsKhrDeviceFault = supportsDeviceFault;
        SupportsKhrDeviceFaultVendorBinary = supportsVendorBinary;
        SupportsKhrDeviceFaultReportMasked = supportsReportMasked;
        SupportsKhrDeviceFaultDeviceLostOnMasked = supportsDeviceLostOnMasked;
        KhrDeviceFaultMaxReportCount = maxReportCount;
    }

    /// <summary>
    /// Publishes the EXT device-fault feature policy selected during logical-device creation.
    /// </summary>
    internal void PublishExtSupport(bool supportsDeviceFault, bool supportsVendorBinary)
    {
        SupportsExtDeviceFault = supportsDeviceFault;
        SupportsExtDeviceFaultVendorBinary = supportsVendorBinary;
    }

    /// <summary>
    /// Publishes a complete KHR command table and activates the KHR reporting path only when both commands are available.
    /// </summary>
    internal void PublishKhrCommandTable(
        VkGetDeviceFaultReportsKhrDelegate? getDeviceFaultReports,
        VkGetDeviceFaultDebugInfoKhrDelegate? getDeviceFaultDebugInfo)
    {
        GetDeviceFaultReportsKhr = getDeviceFaultReports;
        GetDeviceFaultDebugInfoKhr = getDeviceFaultDebugInfo;
        IsUsingKhrDeviceFault =
            SupportsKhrDeviceFault &&
            getDeviceFaultReports is not null &&
            getDeviceFaultDebugInfo is not null;
    }

    /// <summary>
    /// Clears KHR commands and makes the EXT path, when present, the only eligible reporting path.
    /// </summary>
    internal void ResetKhrCommandTable()
    {
        GetDeviceFaultReportsKhr = null;
        GetDeviceFaultDebugInfoKhr = null;
        IsUsingKhrDeviceFault = false;
    }

    /// <summary>
    /// Clears all per-device device-fault state before the owning logical device is discarded.
    /// </summary>
    internal void Reset()
    {
        Interlocked.Exchange(ref _deviceLossFalloutCount, 0);
        Volatile.Write(ref _deviceLostReason, null);
        SupportsKhrDeviceFault = false;
        SupportsKhrDeviceFaultVendorBinary = false;
        SupportsKhrDeviceFaultReportMasked = false;
        SupportsKhrDeviceFaultDeviceLostOnMasked = false;
        KhrDeviceFaultMaxReportCount = 0;
        SupportsExtDeviceFault = false;
        SupportsExtDeviceFaultVendorBinary = false;
        ResetKhrCommandTable();
    }
}
