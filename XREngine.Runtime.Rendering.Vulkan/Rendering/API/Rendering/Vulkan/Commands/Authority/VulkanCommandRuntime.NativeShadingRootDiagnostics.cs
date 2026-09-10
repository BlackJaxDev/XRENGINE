using System.Threading;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanCommandRuntime
{
    private long _recordedImmediateNativeShadingDispatches;
    private long _recordedAddressNativeShadingDispatches;
    private long _immediateNativeShadingPushBytes;
    private long _addressNativeShadingPushBytes;
    private long _nativeShadingParameterUploadBytes;
    private long _nativeShadingParameterBlocks;

    /// <summary>
    /// Records one successful native opaque shading operation. The operation may
    /// issue many dispatches, so each counter is updated once rather than once
    /// per dispatch.
    /// </summary>
    internal void RecordNativeShadingRootUse(bool addressRoot, int dispatchCount)
    {
        if (dispatchCount <= 0)
            return;

        long dispatches = dispatchCount;
        if (addressRoot)
        {
            Interlocked.Add(ref _recordedAddressNativeShadingDispatches, dispatches);
            Interlocked.Add(ref _addressNativeShadingPushBytes, checked(dispatches * 16L));
            Interlocked.Add(ref _nativeShadingParameterUploadBytes, 64L);
            Interlocked.Increment(ref _nativeShadingParameterBlocks);
            return;
        }

        Interlocked.Add(ref _recordedImmediateNativeShadingDispatches, dispatches);
        Interlocked.Add(ref _immediateNativeShadingPushBytes, checked(dispatches * 64L));
    }

    internal VulkanNativeShadingRootDiagnosticSnapshot CaptureNativeShadingRootDiagnostics()
        => new(
            VulkanNativeShadingRootPolicy.Requested,
            Interlocked.Read(ref _recordedImmediateNativeShadingDispatches),
            Interlocked.Read(ref _recordedAddressNativeShadingDispatches),
            Interlocked.Read(ref _immediateNativeShadingPushBytes),
            Interlocked.Read(ref _addressNativeShadingPushBytes),
            Interlocked.Read(ref _nativeShadingParameterUploadBytes),
            Interlocked.Read(ref _nativeShadingParameterBlocks));
}
