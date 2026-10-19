using System.Runtime.InteropServices;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanCommandRuntime
{
    private Action<ulong, VulkanMappedFrameAllocation>? _invalidateFrameDataCommand;
    private readonly Lock _frameDataInvalidationGate = new();
    private readonly List<ulong> _frameDataInvalidationHandles = new(16);
    private readonly HashSet<ulong> _frameDataInvalidationSet = [];

    private void InvalidateFrameDataCommand(ulong handle, VulkanMappedFrameAllocation allocation)
    {
        lock (_frameDataInvalidationGate)
        {
            _frameDataInvalidationHandles.Clear();
            _frameDataInvalidationSet.Clear();
            _frameDataInvalidationHandles.Add(handle);
            _frameDataInvalidationSet.Add(handle);
            if (_configuredResourceRuntime is not { } resources ||
                !resources.AppendRecordedCommandParents(
                    allocation, _frameDataInvalidationHandles, _frameDataInvalidationSet))
                return;
            InvalidateCachedCommandBuffers(
                CollectionsMarshal.AsSpan(_frameDataInvalidationHandles), "FrameDataOwnerRetired");
        }
    }
}
