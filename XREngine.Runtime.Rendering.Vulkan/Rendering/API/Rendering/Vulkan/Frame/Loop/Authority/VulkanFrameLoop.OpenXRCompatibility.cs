using Silk.NET.Vulkan;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanFrameLoop
{
    // OpenXR preparation consumes the same published wrapper generation as the
    // desktop loop. It never reaches through the renderer facade.
    private AbstractRenderAPIObject? GetOrCreateAPIRenderObject(
        GenericRenderObject renderObject,
        bool generateNow = false)
        => _resourceRuntime.BackendObjectContext?
            .GetOrCreateAPIRenderObject(renderObject, generateNow);

    private bool TryGetAPIRenderObject(
        GenericRenderObject renderObject,
        out AbstractRenderAPIObject? apiObject)
    {
        apiObject = _resourceRuntime.BackendObjects.Get(renderObject);
        return apiObject is not null;
    }

    private int DescriptorFrameSlotFrameCount
        => _commandRuntime.DescriptorFrameSlotFrameCount;

    private bool DescriptorTraceEnabled
        => _commandRuntime.IsOpenXrTraceEnabled;

    private bool HasObservedDesktopFrameTick => HasObservedTick;

    private DesktopFrameActivitySnapshot CaptureDesktopFrameActivity()
        => CaptureActivity();

    private ref long _lastCommandBufferDirtyTimestamp
        => ref _commandRuntime.CommandBuffers.LastDirtyTimestamp;

    private void DeviceWaitIdle()
    {
        ReaderWriterLockSlim admissionGate = _deviceContext.QueueAdmissionGate;
        bool ownsAdmission = !admissionGate.IsWriteLockHeld;
        if (ownsAdmission)
            admissionGate.EnterWriteLock();
        try
        {
            if (!_deviceContext.IsOperational)
                return;

            Result result = _deviceContext.Api.DeviceWaitIdle(_deviceContext.Device);
            if (result == Result.Success)
                _commandRuntime.CompleteTrackedDevice();
            else if (result == Result.ErrorDeviceLost)
                MarkDeviceLost("DeviceWaitIdle returned ErrorDeviceLost", "vkDeviceWaitIdle", result);
            else if (result != Result.Success)
                throw new InvalidOperationException($"vkDeviceWaitIdle failed: {result}.");
        }
        finally
        {
            if (ownsAdmission)
                admissionGate.ExitWriteLock();
        }
    }

    private IVulkanMemoryAllocator MemoryAllocator
        => _resourceRuntime.Allocations.Buffers.MemoryAllocator
           ?? throw new InvalidOperationException("The Vulkan memory allocator is not initialized.");
}
