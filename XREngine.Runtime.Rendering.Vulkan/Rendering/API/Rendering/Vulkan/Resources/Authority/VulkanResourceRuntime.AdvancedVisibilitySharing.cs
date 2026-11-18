using System.Diagnostics;
using System.Threading;
using VkBufferHandle = Silk.NET.Vulkan.Buffer;
using Debug = XREngine.Debug;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Lets one Advanced visibility bank use the final visibility of another bank in the
/// same frame. The OpenXR quad-view inset pair uses it: the wide pair publishes its
/// persistent visibility state after late visibility, and the inset copies that state
/// into its own persistent buffer and draws only what the wide pair drew.
/// </summary>
internal sealed partial class VulkanResourceRuntime
{
    /// <summary>
    /// The persistent visibility state that one bank published after its late visibility.
    /// It holds one 32-byte record per candidate and view, in the bank's view order.
    /// </summary>
    internal readonly record struct AdvancedVisibilitySharedSource(
        VkBufferHandle PersistentStateBuffer,
        ulong PersistentStateTopologyGeneration,
        uint PayloadCapacity,
        uint ViewCount,
        ulong RenderFrameId);

    private const int AdvancedVisibilitySharingSourceCapacity = VulkanAdvancedVisibilityOutputCapacity.Maximum;
    private readonly AdvancedVisibilitySharedSource[] _advancedVisibilitySharedSources =
        new AdvancedVisibilitySharedSource[AdvancedVisibilitySharingSourceCapacity];
    private ulong _activeSharedVisibilityConsumerReservationId;
    private ulong _activeSharedVisibilityProducerReservationId;
    private long _sharedVisibilityConsumedCount;
    private long _sharedVisibilityFallbackCount;
    private long _sharedVisibilityLastReportTimestamp;
    private string _sharedVisibilityLastFallbackReason = "none";

    /// <summary>
    /// Records that a bank finished late visibility for a render frame, so a consumer that
    /// records later in the same frame can copy its persistent state.
    /// </summary>
    internal void PublishAdvancedVisibilitySharedSource(
        ulong reservationId,
        in AdvancedVisibilitySharedSource source)
    {
        if (reservationId == 0u || reservationId > (ulong)AdvancedVisibilitySharingSourceCapacity)
            return;
        lock (_advancedVisibilitySharedSources)
            _advancedVisibilitySharedSources[(int)reservationId - 1] = source;
    }

    /// <summary>
    /// Selects the producer bank for the visibility preparation recorded next for the
    /// consumer bank. Pass zero for both to clear it. Render-thread state: the OpenXR
    /// stereo record sets it around the inset pair's synchronous recording.
    /// </summary>
    internal void SetActiveAdvancedVisibilitySharing(ulong consumerReservationId, ulong producerReservationId)
    {
        Volatile.Write(ref _activeSharedVisibilityConsumerReservationId, consumerReservationId);
        Volatile.Write(ref _activeSharedVisibilityProducerReservationId, producerReservationId);
    }

    /// <summary>
    /// Returns the producer's published state when the consumer bank should use shared
    /// visibility in this render frame. A consumer bank with no active link returns false
    /// with no fallback count; a link whose source does not match counts a fallback.
    /// </summary>
    internal bool TryGetActiveAdvancedVisibilitySharedSource(
        ulong consumerReservationId,
        uint consumerPayloadCapacity,
        uint consumerViewCount,
        ulong persistentStateByteLength,
        ulong renderFrameId,
        out AdvancedVisibilitySharedSource source)
    {
        source = default;
        if (consumerReservationId == 0u ||
            Volatile.Read(ref _activeSharedVisibilityConsumerReservationId) != consumerReservationId)
            return false;

        ulong producerId = Volatile.Read(ref _activeSharedVisibilityProducerReservationId);
        string? failure = null;
        if (producerId == 0u || producerId > (ulong)AdvancedVisibilitySharingSourceCapacity)
            failure = "no producer bank";
        else
        {
            lock (_advancedVisibilitySharedSources)
                source = _advancedVisibilitySharedSources[(int)producerId - 1];
            if (source.PersistentStateBuffer.Handle == 0)
                failure = "the producer has not published";
            else if (source.RenderFrameId != renderFrameId)
                failure = "the producer published another render frame";
            else if (source.PayloadCapacity != consumerPayloadCapacity || source.ViewCount != consumerViewCount)
                failure = "the producer has another candidate count or view count";
            else if ((ulong)consumerPayloadCapacity * consumerViewCount * 2u * 32u > persistentStateByteLength)
                failure = "the persistent state buffer cannot hold both record sets";
        }

        if (failure is null)
            Interlocked.Increment(ref _sharedVisibilityConsumedCount);
        else
        {
            Interlocked.Increment(ref _sharedVisibilityFallbackCount);
            _sharedVisibilityLastFallbackReason = failure;
            source = default;
        }

        ReportAdvancedVisibilitySharing();
        return failure is null;
    }

    /// <summary>
    /// Writes the consumed and fallback counts to <c>openxr-pair-failures.log</c> about every
    /// five seconds when <c>XRE_VULKAN_RECORDING_DIAG=1</c> is set.
    /// </summary>
    private void ReportAdvancedVisibilitySharing()
    {
        if (!VulkanMeshRenderingConventions.CommandRecordingDiagnosticsEnabled)
            return;
        long now = Stopwatch.GetTimestamp();
        if (_sharedVisibilityLastReportTimestamp != 0 &&
            now - _sharedVisibilityLastReportTimestamp < Stopwatch.Frequency * 5)
            return;
        _sharedVisibilityLastReportTimestamp = now;
        Debug.WriteAuxiliaryLog("openxr-pair-failures.log",
            $"{System.DateTime.Now:HH:mm:ss.fff} SharedVisibility consumed={Interlocked.Read(ref _sharedVisibilityConsumedCount)} " +
            $"fallback={Interlocked.Read(ref _sharedVisibilityFallbackCount)} lastFallback={_sharedVisibilityLastFallbackReason}");
    }
}
