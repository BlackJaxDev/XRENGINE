using Silk.NET.Core.Native;
using Silk.NET.Core;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using XREngine.Data.Rendering;
using XREngine.Rendering.DLSS;
using XREngine.Rendering.XeSS;
using VkSemaphore = Silk.NET.Vulkan.Semaphore;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Represents a Vulkan-based sidecar for handling upscaling bridge operations, including resource management and queue handling.
/// </summary>
internal sealed unsafe partial class VulkanUpscaleBridgeSidecar : IDisposable
{
    private const int FramesInFlight = 2;
    private const uint DuplicateSameAccess = 0x00000002;
    private static int _nextStreamlineViewportId;

    private readonly Vk _api;
    private readonly VulkanUpscaleBridgeProbe.VulkanUpscaleBridgeSelectedDevice _selectedDevice;
    private readonly KhrExternalMemoryWin32 _externalMemoryWin32;
    private readonly KhrExternalSemaphoreWin32 _externalSemaphoreWin32;
    private readonly VulkanUpscaleBridgeFrameResources _frameResources;
    private readonly uint _streamlineViewportId;
    private readonly uint _streamlineGraphicsQueueIndex;
    private readonly uint _streamlineComputeQueueIndex;
    private readonly uint _streamlineOpticalFlowQueueIndex;
    private readonly VulkanUpscaleBridgeVulkanContext _nativeContext;
    private readonly object _graphicsQueueOperationGate = new();
    private readonly VulkanDeviceStateMachine _deviceState = new();
    private readonly VulkanFrameTelemetry _telemetry = new();
    private VulkanUpscaleBridgeDeviceLossRecord? _firstDeviceLoss;
    private bool _disposed;
    private Instance _instance;
    private Device _device;
    private Queue _graphicsQueue;
    private CommandPool _commandPool;
    private VulkanUpscaleBridgeFrameSlot[] _ownedSlots = [];
    private readonly List<VulkanUpscaleBridgeFrameSlot[]> _retainedRetiredSlotGenerations = [];
    // This counter belongs solely to the sidecar device. It intentionally does not
    // participate in the main renderer's lifetime tracker or generation domain.
    private long _nextResourceAllocationGeneration;
    private NvidiaDlssManager.Native.BridgeSession? _dlssSession;
    private IntelXessManager.Native.BridgeSession? _xessSession;

    /// <summary>
    /// Initializes a new instance of the <see cref="VulkanUpscaleBridgeSidecar"/> class with the specified OpenGL vendor, renderer, and frame resources.
    /// </summary>
    /// <param name="openGlVendor">The vendor string of the OpenGL implementation.</param>
    /// <param name="openGlRenderer">The renderer string of the OpenGL implementation.</param>
    /// <param name="frameResources">The frame resources required for the upscaling bridge sidecar.</param>
    /// <exception cref="InvalidOperationException">Thrown if the Vulkan instance or device cannot be created successfully.</exception>
    public VulkanUpscaleBridgeSidecar(string? openGlVendor, string? openGlRenderer, in VulkanUpscaleBridgeFrameResources frameResources)
    {
        _frameResources = frameResources;
        _streamlineViewportId = unchecked((uint)Interlocked.Increment(ref _nextStreamlineViewportId));
        _api = Vk.GetApi();
        ResolveVendorRequirements(
            in frameResources,
            out EVulkanUpscaleBridgeVendor? requirementsVendor,
            out string[] additionalInstanceExtensions,
            out uint minApiVersion,
            out string[] additionalDeviceExtensions,
            out IntPtr additionalDeviceFeatureChain,
            out string[] additionalDeviceFeatures12,
            out string[] additionalDeviceFeatures13,
            out NvidiaDlssManager.Native.StreamlineQueueRequirements dlssQueueRequirements);

        _instance = CreateInstance(additionalInstanceExtensions, minApiVersion);
        _selectedDevice = SelectDevice(_api, _instance, openGlVendor, openGlRenderer);
        _device = CreateDevice(
            _selectedDevice,
            additionalDeviceExtensions,
            additionalDeviceFeatureChain,
            additionalDeviceFeatures12,
            additionalDeviceFeatures13,
            dlssQueueRequirements,
            requirementsVendor == EVulkanUpscaleBridgeVendor.Xess,
            out global::System.UInt32 streamlineGraphicsQueueIndex,
            out global::System.UInt32 streamlineComputeQueueIndex,
            out global::System.UInt32 streamlineOpticalFlowQueueIndex);
        _streamlineGraphicsQueueIndex = streamlineGraphicsQueueIndex;
        _streamlineComputeQueueIndex = streamlineComputeQueueIndex;
        _streamlineOpticalFlowQueueIndex = streamlineOpticalFlowQueueIndex;
        _graphicsQueue = GetGraphicsQueue(_selectedDevice.GraphicsQueueFamilyIndex);
        _commandPool = CreateCommandPool(_selectedDevice.GraphicsQueueFamilyIndex);
        _nativeContext = new VulkanUpscaleBridgeVulkanContext(
            _api,
            _instance,
            _selectedDevice.Device,
            _device,
            _selectedDevice.GraphicsQueueFamilyIndex,
            _streamlineGraphicsQueueIndex,
            _streamlineComputeQueueIndex,
            _streamlineOpticalFlowQueueIndex);

        if (!_api.TryGetDeviceExtension(_instance, _device, out _externalMemoryWin32))
            throw new InvalidOperationException("Failed to load VK_KHR_external_memory_win32 for the bridge sidecar.");
        if (!_api.TryGetDeviceExtension(_instance, _device, out _externalSemaphoreWin32))
            throw new InvalidOperationException("Failed to load VK_KHR_external_semaphore_win32 for the bridge sidecar.");
    }

    public string DeviceName => _selectedDevice.DeviceName;
    public uint VendorId => _selectedDevice.VendorId;
    public uint DeviceId => _selectedDevice.DeviceId;
    public int FrameSlotCount => FramesInFlight;
    public Queue GraphicsQueue => _graphicsQueue;
    public uint GraphicsQueueFamilyIndex => _selectedDevice.GraphicsQueueFamilyIndex;
    public uint GraphicsQueueIndex => 0;
    internal VulkanUpscaleBridgeVulkanContext NativeContext => _nativeContext;

    /// <summary>
    /// Waits for the specified frame slot to become available for use.
    /// </summary>
    /// <param name="slot">The frame slot to wait for.</param>
    /// <exception cref="ObjectDisposedException">Thrown if the bridge sidecar has been disposed.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the Vulkan upscale bridge device is not operational or if waiting for the frame slot fails.</exception>
    public void WaitForFrameSlotAvailability(VulkanUpscaleBridgeFrameSlot slot)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(VulkanUpscaleBridgeSidecar));

        ThrowIfDeviceOperationNotAdmitted("VulkanUpscaleBridge.WaitForFrameSlotAvailability");

        WaitForFrameSlotCompletion(slot, "availability");
    }

    /// <summary>
    /// Creates an array of frame slots for the specified viewport using the provided renderer and frame resources.
    /// </summary>
    /// <param name="renderer">The OpenGL renderer to use for creating the frame slots.</param>
    /// <param name="frameResources">The frame resources containing the internal width and height for the frame slots.</param>
    /// <param name="viewportTag">A tag identifying the viewport for which the frame slots are being created.</param>
    /// <returns>An array of Vulkan upscale bridge frame slots.</returns>
    public VulkanUpscaleBridgeFrameSlot[] CreateFrameSlots(IOpenGlVendorUpscaleBackendCapability renderer, VulkanUpscaleBridgeFrameResources frameResources, string viewportTag)
    {
        ThrowIfDeviceOperationNotAdmitted("VulkanUpscaleBridge.CreateFrameSlots");
        VulkanUpscaleBridgeFrameSlot[] slots = PrepareFrameSlots(renderer, frameResources, viewportTag);
        Volatile.Write(ref _ownedSlots, slots);
        return slots;
    }

    /// <summary>
    /// Prepares a complete, published slot generation without changing the
    /// generation currently owned by the sidecar. The caller owns the commit.
    /// </summary>
    private VulkanUpscaleBridgeFrameSlot[] PrepareFrameSlots(
        IOpenGlVendorUpscaleBackendCapability renderer,
        VulkanUpscaleBridgeFrameResources frameResources,
        string viewportTag)
    {
        ThrowIfDeviceOperationNotAdmitted("VulkanUpscaleBridge.PrepareFrameSlots");
        VulkanUpscaleBridgeFrameSlot[] slots = new VulkanUpscaleBridgeFrameSlot[FramesInFlight];
        ulong allocationGeneration = AllocateSidecarResourceGeneration();
        try
        {
            for (int slotIndex = 0; slotIndex < slots.Length; slotIndex++)
                slots[slotIndex] = CreateFrameSlot(renderer, frameResources, $"{viewportTag}.Slot{slotIndex}", slotIndex, allocationGeneration);

            PublishFrameSlots(slots, allocationGeneration);
            return slots;
        }
        catch
        {
            DestroyFrameSlots(slots);
            throw;
        }
    }

    /// <summary>
    /// Recreates the frame slots for the specified viewport, destroying any existing slots and creating new ones based on the provided frame resources.
    /// </summary>
    /// <param name="renderer">The OpenGL renderer to use for recreating the frame slots.</param>
    /// <param name="frameResources">The frame resources containing the internal width and height for the frame slots.</param>
    /// <param name="viewportTag">A tag identifying the viewport for which the frame slots are being recreated.</param>
    /// <returns>An array of Vulkan upscale bridge frame slots.</returns>
    /// <exception cref="ObjectDisposedException">Thrown if the Vulkan upscale bridge sidecar has been disposed.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the Vulkan upscale bridge sidecar device is unavailable.</exception>
    public VulkanUpscaleBridgeFrameSlot[] RecreateFrameSlots(IOpenGlVendorUpscaleBackendCapability renderer, VulkanUpscaleBridgeFrameResources frameResources, string viewportTag)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(VulkanUpscaleBridgeSidecar));

        if (_device.Handle == 0)
            throw new InvalidOperationException("The Vulkan upscale bridge sidecar device is unavailable.");

        using VulkanQueueOperationLease lease = VulkanQueueOperationLease.TryEnter(
            _graphicsQueueOperationGate,
            _deviceState,
            _telemetry);
        if (!lease.Acquired)
            throw new InvalidOperationException($"The Vulkan upscale bridge device is {_deviceState.State}.");

        // Prepare the full replacement first. Any failure here leaves the currently
        // published generation and its vendor sessions untouched.
        VulkanUpscaleBridgeFrameSlot[] replacement = PrepareFrameSlots(renderer, frameResources, viewportTag);
        VulkanUpscaleBridgeFrameSlot[] previous = Interlocked.Exchange(ref _ownedSlots, replacement);

        try
        {
            // Every sidecar queue submission is associated with exactly one
            // frame-slot fence. Retire the former generation only after these
            // bounded owners complete; do not idle unrelated work.
            for (int slotIndex = 0; slotIndex < previous.Length; slotIndex++)
                WaitForFrameSlotCompletion(previous[slotIndex], "frame-resource recreate retirement");

            ResetVendorSessionsForFrameResourceRecreate();
            DestroyFrameSlots(previous);
            return replacement;
        }
        catch
        {
            // Once published, the replacement must remain authoritative. A wait
            // failure indicates the sidecar device is no longer usable, so retain
            // both generations for device-loss/shutdown cleanup rather than
            // destroying resources whose completion state is unknown.
            _retainedRetiredSlotGenerations.Add(previous);
            throw;
        }
    }

    private void WaitForFrameSlotCompletion(VulkanUpscaleBridgeFrameSlot slot, string reason)
    {
        ThrowIfDeviceOperationNotAdmitted("vkWaitForFences.VulkanUpscaleBridge");
        Fence submitFence = slot.SubmitFence;
        Result waitResult = _api.WaitForFences(_device, 1, in submitFence, true, ulong.MaxValue);
        ObserveDeviceResult(waitResult, "vkWaitForFences.VulkanUpscaleBridge", slot);
        if (waitResult != Result.Success)
        {
            throw new InvalidOperationException(
                $"Failed to wait for bridge slot {slot.SlotIndex} {reason} ({waitResult}).");
        }
    }

    /// <summary>
    /// Submits a no-op handoff for the specified Vulkan upscale bridge frame slot.
    /// </summary>
    /// <param name="slot">The Vulkan upscale bridge frame slot for which to submit a no-op handoff.</param>
    /// <exception cref="ObjectDisposedException">Thrown if the Vulkan upscale bridge sidecar has been disposed.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the Vulkan upscale bridge sidecar device is unavailable or if command buffer submission fails.</exception>
    public void SubmitNoOpHandoff(VulkanUpscaleBridgeFrameSlot slot)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(VulkanUpscaleBridgeSidecar));

        ThrowIfDeviceOperationNotAdmitted("VulkanUpscaleBridge.SubmitNoOpHandoff");
        Fence submitFence = slot.SubmitFence;
        _api.ResetFences(_device, 1, in submitFence);
        ResetCommandBufferTracked(slot.CommandBuffer);

        CommandBufferBeginInfo beginInfo = new()
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };

        ThrowIfDeviceOperationNotAdmitted("vkBeginCommandBuffer.VulkanUpscaleBridge.NoOpHandoff");
        Result beginResult = _api.BeginCommandBuffer(slot.CommandBuffer, in beginInfo);
        ObserveDeviceResult(beginResult, "vkBeginCommandBuffer.VulkanUpscaleBridge.NoOpHandoff", slot);
        if (beginResult != Result.Success)
            throw new InvalidOperationException($"Failed to begin bridge handoff command buffer for slot {slot.SlotIndex}.");

        Result endResult = _api.EndCommandBuffer(slot.CommandBuffer);
        ObserveDeviceResult(endResult, "vkEndCommandBuffer.VulkanUpscaleBridge.NoOpHandoff", slot);
        if (endResult != Result.Success)
            throw new InvalidOperationException($"Failed to end bridge handoff command buffer for slot {slot.SlotIndex}.");

        VkSemaphore waitSemaphore = slot.ReadySemaphore.VulkanSemaphore;
        VkSemaphore signalSemaphore = slot.CompleteSemaphore.VulkanSemaphore;
        PipelineStageFlags waitStage = PipelineStageFlags.AllCommandsBit;
        CommandBuffer commandBuffer = slot.CommandBuffer;

        SubmitInfo submitInfo = new()
        {
            SType = StructureType.SubmitInfo,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = &waitSemaphore,
            PWaitDstStageMask = &waitStage,
            CommandBufferCount = 1,
            PCommandBuffers = &commandBuffer,
            SignalSemaphoreCount = 1,
            PSignalSemaphores = &signalSemaphore,
        };

        Result submitResult = SubmitToGraphicsQueue(ref submitInfo, slot);
        if (submitResult != Result.Success)
            throw new InvalidOperationException($"Failed to submit bridge handoff for slot {slot.SlotIndex} ({submitResult}).");
    }

    /// <summary>
    /// Submits a passthrough blit for the specified Vulkan upscale bridge frame slot.
    /// </summary>
    /// <param name="slot">The Vulkan upscale bridge frame slot for which to submit a passthrough blit.</param>
    /// <exception cref="ObjectDisposedException">Thrown if the Vulkan upscale bridge sidecar has been disposed.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the Vulkan upscale bridge sidecar device is unavailable or if command buffer submission fails.</exception>
    public void SubmitPassthroughBlit(VulkanUpscaleBridgeFrameSlot slot)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(VulkanUpscaleBridgeSidecar));

        ThrowIfDeviceOperationNotAdmitted("VulkanUpscaleBridge.SubmitPassthroughBlit");
        Fence submitFence = slot.SubmitFence;
        _api.ResetFences(_device, 1, in submitFence);
        ResetCommandBufferTracked(slot.CommandBuffer);

        CommandBufferBeginInfo beginInfo = new()
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };

        ThrowIfDeviceOperationNotAdmitted("vkBeginCommandBuffer.VulkanUpscaleBridge.PassthroughBlit");
        Result beginResult = _api.BeginCommandBuffer(slot.CommandBuffer, in beginInfo);
        ObserveDeviceResult(beginResult, "vkBeginCommandBuffer.VulkanUpscaleBridge.PassthroughBlit", slot);
        if (beginResult != Result.Success)
            throw new InvalidOperationException($"Failed to begin bridge passthrough command buffer for slot {slot.SlotIndex}.");

        _nativeContext.TransitionImageLayout(
            slot.CommandBuffer,
            slot.SourceColor,
            ImageLayout.TransferSrcOptimal,
            PipelineStageFlags.TransferBit,
            AccessFlags.TransferReadBit);
        _nativeContext.TransitionImageLayout(
            slot.CommandBuffer,
            slot.OutputColor,
            ImageLayout.TransferDstOptimal,
            PipelineStageFlags.TransferBit,
            AccessFlags.TransferWriteBit);

        ImageBlit region = new()
        {
            SrcSubresource = new ImageSubresourceLayers
            {
                AspectMask = ImageAspectFlags.ColorBit,
                MipLevel = 0,
                BaseArrayLayer = 0,
                LayerCount = 1,
            },
            DstSubresource = new ImageSubresourceLayers
            {
                AspectMask = ImageAspectFlags.ColorBit,
                MipLevel = 0,
                BaseArrayLayer = 0,
                LayerCount = 1,
            },
        };
        region.SrcOffsets[0] = new Offset3D(0, 0, 0);
        region.SrcOffsets[1] = new Offset3D((int)slot.SourceColorTexture.Width, (int)slot.SourceColorTexture.Height, 1);
        region.DstOffsets[0] = new Offset3D(0, 0, 0);
        region.DstOffsets[1] = new Offset3D((int)slot.OutputColorTexture.Width, (int)slot.OutputColorTexture.Height, 1);

        _api.CmdBlitImage(
            slot.CommandBuffer,
            slot.SourceColor.VulkanImage,
            ImageLayout.TransferSrcOptimal,
            slot.OutputColor.VulkanImage,
            ImageLayout.TransferDstOptimal,
            1,
            &region,
            Filter.Linear);

        _nativeContext.TransitionImageLayout(
            slot.CommandBuffer,
            slot.SourceColor,
            ImageLayout.General,
            PipelineStageFlags.AllCommandsBit,
            AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit);
        _nativeContext.TransitionImageLayout(
            slot.CommandBuffer,
            slot.OutputColor,
            ImageLayout.General,
            PipelineStageFlags.AllCommandsBit,
            AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit);

        Result endResult = _api.EndCommandBuffer(slot.CommandBuffer);
        ObserveDeviceResult(endResult, "vkEndCommandBuffer.VulkanUpscaleBridge.PassthroughBlit", slot);
        if (endResult != Result.Success)
            throw new InvalidOperationException($"Failed to end bridge passthrough command buffer for slot {slot.SlotIndex}.");

        VkSemaphore waitSemaphore = slot.ReadySemaphore.VulkanSemaphore;
        VkSemaphore signalSemaphore = slot.CompleteSemaphore.VulkanSemaphore;
        PipelineStageFlags waitStage = PipelineStageFlags.TransferBit;
        CommandBuffer commandBuffer = slot.CommandBuffer;

        SubmitInfo submitInfo = new()
        {
            SType = StructureType.SubmitInfo,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = &waitSemaphore,
            PWaitDstStageMask = &waitStage,
            CommandBufferCount = 1,
            PCommandBuffers = &commandBuffer,
            SignalSemaphoreCount = 1,
            PSignalSemaphores = &signalSemaphore,
        };

        Result submitResult = SubmitToGraphicsQueue(ref submitInfo, slot);
        if (submitResult != Result.Success)
            throw new InvalidOperationException($"Failed to submit bridge passthrough blit for slot {slot.SlotIndex} ({submitResult}).");
    }

    /// <summary>
    /// Submits a vendor-specific upscale operation for the specified Vulkan upscale bridge frame slot.
    /// </summary>
    /// <param name="slot">The Vulkan upscale bridge frame slot for which to submit the vendor-specific upscale operation.</param>
    /// <param name="parameters">The dispatch parameters for the vendor-specific upscale operation.</param>
    /// <param name="failureReason">Outputs the reason for failure if the submission fails.</param>
    /// <returns>True if the submission was successful; otherwise, false.</returns>
    /// <exception cref="ObjectDisposedException">Thrown if the Vulkan upscale bridge sidecar has been disposed.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the Vulkan upscale bridge sidecar device is unavailable or if command buffer submission fails.</exception>
    public bool SubmitVendorUpscale(
        VulkanUpscaleBridgeFrameSlot slot,
        in VulkanUpscaleBridgeDispatchParameters parameters,
        out string failureReason)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(VulkanUpscaleBridgeSidecar));

        failureReason = string.Empty;
        if (!TryAdmitDeviceOperation("VulkanUpscaleBridge.SubmitVendorUpscale", out failureReason))
            return false;

        Fence submitFence = slot.SubmitFence;
        _api.ResetFences(_device, 1, in submitFence);
        ResetCommandBufferTracked(slot.CommandBuffer);

        CommandBufferBeginInfo beginInfo = new()
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };

        ThrowIfDeviceOperationNotAdmitted("vkBeginCommandBuffer.VulkanUpscaleBridge.VendorUpscale");
        Result beginResult = _api.BeginCommandBuffer(slot.CommandBuffer, in beginInfo);
        ObserveDeviceResult(beginResult, "vkBeginCommandBuffer.VulkanUpscaleBridge.VendorUpscale", slot, parameters.Vendor);
        if (beginResult != Result.Success)
        {
            failureReason = $"Failed to begin bridge vendor command buffer for slot {slot.SlotIndex}.";
            return false;
        }

        bool recorded = parameters.Vendor switch
        {
            EVulkanUpscaleBridgeVendor.Dlss => EnsureDlssSession(out failureReason) && _dlssSession!.Record(slot, in parameters, out failureReason),
            EVulkanUpscaleBridgeVendor.Xess => EnsureXessSession(in parameters, out failureReason) && _xessSession!.Record(slot, in parameters, out failureReason),
            _ => false,
        };

        if (!recorded)
        {
            _api.EndCommandBuffer(slot.CommandBuffer);
            ResetVendorSession(parameters.Vendor);
            failureReason = string.IsNullOrWhiteSpace(failureReason)
                ? $"Failed to record {parameters.Vendor} bridge commands."
                : failureReason;
            return false;
        }

        Result endResult = _api.EndCommandBuffer(slot.CommandBuffer);
        ObserveDeviceResult(endResult, "vkEndCommandBuffer.VulkanUpscaleBridge.VendorUpscale", slot, parameters.Vendor);
        if (endResult != Result.Success)
        {
            ResetVendorSession(parameters.Vendor);
            failureReason = $"Failed to end bridge vendor command buffer for slot {slot.SlotIndex}.";
            return false;
        }

        VkSemaphore waitSemaphore = slot.ReadySemaphore.VulkanSemaphore;
        VkSemaphore signalSemaphore = slot.CompleteSemaphore.VulkanSemaphore;
        PipelineStageFlags waitStage = PipelineStageFlags.AllCommandsBit;
        CommandBuffer commandBuffer = slot.CommandBuffer;

        SubmitInfo submitInfo = new()
        {
            SType = StructureType.SubmitInfo,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = &waitSemaphore,
            PWaitDstStageMask = &waitStage,
            CommandBufferCount = 1,
            PCommandBuffers = &commandBuffer,
            SignalSemaphoreCount = 1,
            PSignalSemaphores = &signalSemaphore,
        };

        Result submitResult = SubmitToGraphicsQueue(ref submitInfo, slot, parameters.Vendor);
        if (submitResult != Result.Success)
        {
            ResetVendorSession(parameters.Vendor);
            failureReason = $"Failed to submit bridge {parameters.Vendor} dispatch for slot {slot.SlotIndex} ({submitResult}).";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Submits the specified command buffer to the graphics queue with the given fence.
    /// </summary>
    /// <param name="submitInfo">The submit information describing the command buffer submission.</param>
    /// <param name="fence">The fence to signal upon completion of the submission.</param>
    /// <returns>The result of the queue submission operation.</returns>
    private Result SubmitToGraphicsQueue(
        ref SubmitInfo submitInfo,
        VulkanUpscaleBridgeFrameSlot slot,
        EVulkanUpscaleBridgeVendor? dispatchVendor = null)
    {
        if (!TryAdmitDeviceOperation("vkQueueSubmit.VulkanUpscaleBridge", out _))
            return Result.ErrorDeviceLost;
        using VulkanQueueOperationLease lease = VulkanQueueOperationLease.TryEnter(
            _graphicsQueueOperationGate,
            _deviceState,
            _telemetry);
        if (!lease.Acquired)
            return Result.ErrorDeviceLost;

        Result result = _api.QueueSubmit(_graphicsQueue, 1, ref submitInfo, slot.SubmitFence);
        ObserveDeviceResult(result, "vkQueueSubmit.VulkanUpscaleBridge", slot, dispatchVendor);
        return result;
    }

    /// <summary>
    /// Observes the result of a Vulkan device operation and handles device loss if necessary.
    /// </summary>
    /// <param name="result">The result of the Vulkan device operation.</param>
    private bool TryAdmitDeviceOperation(string operation, out string failureReason)
    {
        if (!_disposed && _deviceState.IsOperational)
        {
            failureReason = string.Empty;
            return true;
        }

        failureReason = $"Cannot start Vulkan upscale bridge operation '{operation}' while device state is {_deviceState.State}.";
        return false;
    }

    private void ThrowIfDeviceOperationNotAdmitted(string operation)
    {
        if (!TryAdmitDeviceOperation(operation, out string failureReason))
            throw new InvalidOperationException(failureReason);
    }

    private void ObserveDeviceResult(
        Result result,
        string operation,
        VulkanUpscaleBridgeFrameSlot? slot = null,
        EVulkanUpscaleBridgeVendor? dispatchVendor = null,
        ulong allocationGeneration = 0,
        int slotIndex = -1,
        string? resourceName = null)
    {
        if (result != Result.ErrorDeviceLost || !_deviceState.TryBeginLossCollection())
            return;

        VulkanUpscaleBridgeDeviceLossRecord record = new(
            DateTimeOffset.UtcNow,
            operation,
            result,
            _selectedDevice.DeviceName,
            _selectedDevice.VendorId,
            _selectedDevice.DeviceId,
            slot?.SlotIndex ?? slotIndex,
            dispatchVendor,
            slot?.AllocationGeneration ?? allocationGeneration,
            slot?.PublicationGeneration ?? 0,
            resourceName);
        Interlocked.CompareExchange(ref _firstDeviceLoss, record, null);
        _deviceState.CompleteLossCollection();
        Debug.VulkanWarning(
            "[Vulkan.UpscaleBridge] First device loss: operation={0}, result={1}, device={2}, vendorId=0x{3:X4}, deviceId=0x{4:X4}, slot={5}, dispatchVendor={6}, allocationGeneration={7}, publicationGeneration={8}, resource={9}.",
            record.Operation,
            record.Result,
            record.DeviceName,
            record.VendorId,
            record.DeviceId,
            record.SlotIndex,
            record.DispatchVendor?.ToString() ?? "none",
            record.AllocationGeneration,
            record.PublicationGeneration,
            record.ResourceName ?? "none");
    }

    /// <summary>
    /// Ensures that a DLSS session is available, creating one if necessary.
    /// </summary>
    /// <param name="failureReason">Outputs the reason for failure if the session could not be ensured.</param>
    /// <returns>True if the DLSS session is available or was successfully created; otherwise, false.</returns>
    private bool EnsureDlssSession(out string failureReason)
    {
        failureReason = string.Empty;

        if (_dlssSession is not null)
            return true;

        if (!NvidiaDlssManager.Native.TryCreateBridgeSession(_nativeContext, _streamlineViewportId, out _dlssSession, out failureReason)
            || _dlssSession is null)
        {
            _dlssSession = null;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Ensures that an XESS session is available, creating one if necessary.
    /// </summary>
    /// <param name="parameters">The parameters required to create or ensure the XESS session.</param>
    /// <param name="failureReason">Outputs the reason for failure if the session could not be ensured.</param>
    /// <returns>True if the XESS session is available or was successfully created; otherwise, false.</returns>
    private bool EnsureXessSession(in VulkanUpscaleBridgeDispatchParameters parameters, out string failureReason)
    {
        failureReason = string.Empty;

        if (_xessSession is not null)
            return true;

        if (!IntelXessManager.Native.TryCreateBridgeSession(_nativeContext, in parameters, out _xessSession, out failureReason)
            || _xessSession is null)
        {
            _xessSession = null;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Resets the session associated with the specified vendor, disposing of any existing session.
    /// </summary>
    /// <param name="vendor">The vendor whose session should be reset.</param>
    private void ResetVendorSession(EVulkanUpscaleBridgeVendor vendor)
    {
        switch (vendor)
        {
            case EVulkanUpscaleBridgeVendor.Dlss:
                _dlssSession?.Dispose();
                _dlssSession = null;
                break;
            case EVulkanUpscaleBridgeVendor.Xess:
                _xessSession?.Dispose();
                _xessSession = null;
                break;
        }
    }

    /// <summary>
    /// Resets the sessions for all vendors in preparation for recreating frame resources.
    /// </summary>
    private void ResetVendorSessionsForFrameResourceRecreate()
    {
        _dlssSession?.ResetResources();

        _xessSession?.Dispose();
        _xessSession = null;
    }

    /// <summary>
    /// Destroys all frame slots owned by this instance, releasing their Vulkan resources and disposing of them.
    /// </summary>
    private void DestroyOwnedFrameSlots()
    {
        DestroyFrameSlots(_ownedSlots);
        _ownedSlots = [];
        for (int index = _retainedRetiredSlotGenerations.Count - 1; index >= 0; index--)
            DestroyFrameSlots(_retainedRetiredSlotGenerations[index]);
        _retainedRetiredSlotGenerations.Clear();
    }

    private VulkanUpscaleBridgeFrameSlot CreateFrameSlot(
        IOpenGlVendorUpscaleBackendCapability renderer,
        VulkanUpscaleBridgeFrameResources frameResources,
        string slotTag,
        int slotIndex,
        ulong allocationGeneration)
    {
        CommandBuffer commandBuffer = default;
        Fence submitFence = default;
        VulkanUpscaleBridgeSharedImage? sourceColor = null;
        VulkanUpscaleBridgeSharedImage? sourceDepth = null;
        VulkanUpscaleBridgeSharedImage? sourceMotion = null;
        VulkanUpscaleBridgeSharedImage? exposure = null;
        VulkanUpscaleBridgeSharedImage? outputColor = null;
        VulkanUpscaleBridgeSharedSemaphore? readySemaphore = null;
        VulkanUpscaleBridgeSharedSemaphore? completeSemaphore = null;

        try
        {
            commandBuffer = AllocateCommandBuffer(slotIndex, allocationGeneration);
            submitFence = CreateFence(slotIndex, allocationGeneration);
            sourceColor = CreateSharedImage(renderer, slotTag, slotIndex, EVulkanUpscaleBridgeSurfaceKind.SourceColor, (uint)frameResources.InternalWidth, (uint)frameResources.InternalHeight, EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat, ESizedInternalFormat.Rgba16f, EFrameBufferAttachment.ColorAttachment0, ImageUsageFlags.TransferDstBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.SampledBit | ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.StorageBit, ImageAspectFlags.ColorBit, ImageAspectFlags.ColorBit, linearFilter: true, allocationGeneration);
            sourceDepth = CreateSharedImage(renderer, slotTag, slotIndex, EVulkanUpscaleBridgeSurfaceKind.SourceDepth, (uint)frameResources.InternalWidth, (uint)frameResources.InternalHeight, EPixelInternalFormat.Depth24Stencil8, EPixelFormat.DepthStencil, EPixelType.UnsignedInt248, ESizedInternalFormat.Depth24Stencil8, EFrameBufferAttachment.DepthStencilAttachment, ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit | ImageUsageFlags.DepthStencilAttachmentBit, ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit, ImageAspectFlags.DepthBit, linearFilter: false, allocationGeneration);
            sourceMotion = CreateSharedImage(renderer, slotTag, slotIndex, EVulkanUpscaleBridgeSurfaceKind.SourceMotion, (uint)frameResources.InternalWidth, (uint)frameResources.InternalHeight, EPixelInternalFormat.RG16f, EPixelFormat.Rg, EPixelType.HalfFloat, ESizedInternalFormat.Rg16f, EFrameBufferAttachment.ColorAttachment0, ImageUsageFlags.TransferDstBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.SampledBit | ImageUsageFlags.ColorAttachmentBit, ImageAspectFlags.ColorBit, ImageAspectFlags.ColorBit, linearFilter: false, allocationGeneration);
            exposure = CreateSharedImage(renderer, slotTag, slotIndex, EVulkanUpscaleBridgeSurfaceKind.Exposure, 1u, 1u, EPixelInternalFormat.R32f, EPixelFormat.Red, EPixelType.Float, ESizedInternalFormat.R32f, EFrameBufferAttachment.ColorAttachment0, ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit | ImageUsageFlags.ColorAttachmentBit, ImageAspectFlags.ColorBit, ImageAspectFlags.ColorBit, linearFilter: false, allocationGeneration);
            bool outputHdr = frameResources.OutputHdr;
            outputColor = CreateSharedImage(renderer, slotTag, slotIndex, EVulkanUpscaleBridgeSurfaceKind.OutputColor, (uint)frameResources.DisplayWidth, (uint)frameResources.DisplayHeight, outputHdr ? EPixelInternalFormat.Rgba16f : EPixelInternalFormat.Rgba8, EPixelFormat.Rgba, outputHdr ? EPixelType.HalfFloat : EPixelType.UnsignedByte, outputHdr ? ESizedInternalFormat.Rgba16f : ESizedInternalFormat.Rgba8, EFrameBufferAttachment.ColorAttachment0, ImageUsageFlags.TransferDstBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.SampledBit | ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.StorageBit, ImageAspectFlags.ColorBit, ImageAspectFlags.ColorBit, linearFilter: true, allocationGeneration);
            readySemaphore = CreateSharedSemaphore(renderer, $"{slotTag}.Ready", slotIndex, allocationGeneration);
            completeSemaphore = CreateSharedSemaphore(renderer, $"{slotTag}.Complete", slotIndex, allocationGeneration);
            return new VulkanUpscaleBridgeFrameSlot(allocationGeneration, slotIndex, sourceColor, sourceDepth, sourceMotion, exposure, outputColor, readySemaphore, completeSemaphore, commandBuffer, submitFence);
        }
        catch
        {
            DestroyPartialFrameSlot(commandBuffer, submitFence, sourceColor, sourceDepth, sourceMotion, exposure, outputColor, readySemaphore, completeSemaphore);
            throw;
        }
    }

    private void DestroyPartialFrameSlot(CommandBuffer commandBuffer, Fence submitFence, params IDisposable?[] resources)
    {
        for (int index = resources.Length - 1; index >= 0; index--)
            resources[index]?.Dispose();

        for (int index = resources.Length - 1; index >= 0; index--)
        {
            switch (resources[index])
            {
                case VulkanUpscaleBridgeSharedImage image:
                    image.DestroyVulkanResources(_api, _device);
                    break;
                case VulkanUpscaleBridgeSharedSemaphore semaphore:
                    semaphore.DestroyVulkanResources(_api, _device);
                    break;
            }
        }

        if (submitFence.Handle != 0)
            _api.DestroyFence(_device, submitFence, null);
        if (commandBuffer.Handle != 0 && _commandPool.Handle != 0)
            _api.FreeCommandBuffers(_device, _commandPool, 1, &commandBuffer);
    }

    private void DestroyFrameSlots(VulkanUpscaleBridgeFrameSlot[] slots)
    {
        for (int index = slots.Length - 1; index >= 0; index--)
            slots[index]?.Dispose();
        for (int index = slots.Length - 1; index >= 0; index--)
            slots[index]?.DestroyVulkanResources(_api, _device, _commandPool);
    }

    private static void PublishFrameSlots(VulkanUpscaleBridgeFrameSlot[] slots, ulong publicationGeneration)
    {
        for (int index = 0; index < slots.Length; index++)
        {
            if (slots[index] is null)
                throw new InvalidOperationException("Cannot publish an incomplete Vulkan upscale bridge frame-slot transaction.");
            slots[index].Publish(publicationGeneration);
        }
    }

    private ulong AllocateSidecarResourceGeneration()
    {
        long generation = Interlocked.Increment(ref _nextResourceAllocationGeneration);
        if (generation <= 0)
            throw new InvalidOperationException("Vulkan upscale bridge resource allocation generation overflowed.");
        return unchecked((ulong)generation);
    }

    /// <summary>
    /// Disposes of the VulkanUpscaleBridgeSidecar instance, releasing all associated resources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        lock (_graphicsQueueOperationGate)
        {
            _deviceState.Dispose();
            if (_device.Handle != 0)
            {
                _api.DeviceWaitIdle(_device);
                _xessSession?.Dispose();
                _xessSession = null;
                _dlssSession?.Dispose();
                _dlssSession = null;
                DestroyOwnedFrameSlots();

                if (_commandPool.Handle != 0)
                    _api.DestroyCommandPool(_device, _commandPool, null);
                _api.DestroyDevice(_device, null);
                _device = default;
            }
        }

        if (_instance.Handle != 0)
        {
            _api.DestroyInstance(_instance, null);
            _instance = default;
        }

        _xessSession = null;
        _dlssSession = null;
    }

    /// <summary>
    /// Selects a suitable Vulkan device for the bridge sidecar based on the provided OpenGL vendor and renderer information.
    /// </summary>
    /// <param name="api">The Vulkan API instance.</param>
    /// <param name="instance">The Vulkan instance.</param>
    /// <param name="openGlVendor">The OpenGL vendor string.</param>
    /// <param name="openGlRenderer">The OpenGL renderer string.</param>
    /// <returns>The selected Vulkan device for the bridge sidecar.</returns>
    /// <exception cref="InvalidOperationException">Thrown if a suitable Vulkan device cannot be selected for the bridge sidecar.</exception>
    private static VulkanUpscaleBridgeProbe.VulkanUpscaleBridgeSelectedDevice SelectDevice(
        Vk api,
        Instance instance,
        string? openGlVendor,
        string? openGlRenderer)
    {
        if (!VulkanUpscaleBridgeProbe.TrySelectDevice(api, instance, openGlVendor, openGlRenderer, out var selectedDevice, out string? failureReason) ||
            selectedDevice is null)
        {
            throw new InvalidOperationException(failureReason ?? "Failed to select a Vulkan device for the bridge sidecar.");
        }

        return selectedDevice;
    }

    /// <summary>
    /// Resolves the vendor-specific requirements for the Vulkan upscale bridge based on the provided frame resources.
    /// </summary>
    /// <param name="frameResources">The frame resources for the Vulkan upscale bridge.</param>
    /// <param name="requirementsVendor">The resolved vendor requirements for the bridge.</param>
    /// <param name="instanceExtensions">The required Vulkan instance extensions.</param>
    /// <param name="minApiVersion">The minimum Vulkan API version required.</param>
    /// <param name="deviceExtensions">The required Vulkan device extensions.</param>
    /// <param name="deviceFeatureChain">The pointer to the device feature chain.</param>
    /// <param name="deviceFeatures12">The required Vulkan 1.2 device features.</param>
    /// <param name="deviceFeatures13">The required Vulkan 1.3 device features.</param>
    /// <param name="dlssQueueRequirements">The DLSS queue requirements.</param>
    /// <exception cref="InvalidOperationException">Thrown if the vendor requirements cannot be resolved.</exception>
    private static void ResolveVendorRequirements(
        in VulkanUpscaleBridgeFrameResources frameResources,
        out EVulkanUpscaleBridgeVendor? requirementsVendor,
        out string[] instanceExtensions,
        out uint minApiVersion,
        out string[] deviceExtensions,
        out IntPtr deviceFeatureChain,
        out string[] deviceFeatures12,
        out string[] deviceFeatures13,
        out NvidiaDlssManager.Native.StreamlineQueueRequirements dlssQueueRequirements)
    {
        requirementsVendor = null;
        instanceExtensions = [];
        minApiVersion = Vk.Version11;
        deviceExtensions = [];
        deviceFeatureChain = IntPtr.Zero;
        deviceFeatures12 = [];
        deviceFeatures13 = [];
        dlssQueueRequirements = default;

        bool preferDlss = RuntimeEngine.Rendering.VulkanUpscaleBridgeSnapshot.DlssFirst;
        string? dlssFailure = null;
        string? xessFailure = null;

        if (preferDlss)
        {
            if (frameResources.EnableDlss
                && TryResolveDlssRequirements(
                    out instanceExtensions,
                    out minApiVersion,
                    out deviceExtensions,
                    out deviceFeatures12,
                    out deviceFeatures13,
                    out dlssQueueRequirements,
                    out dlssFailure))
            {
                requirementsVendor = EVulkanUpscaleBridgeVendor.Dlss;
                return;
            }

            if (frameResources.EnableXess
                && TryResolveXessRequirements(
                    out instanceExtensions,
                    out minApiVersion,
                    out xessFailure))
            {
                requirementsVendor = EVulkanUpscaleBridgeVendor.Xess;
                return;
            }
        }
        else
        {
            if (frameResources.EnableXess
                && TryResolveXessRequirements(
                    out instanceExtensions,
                    out minApiVersion,
                    out xessFailure))
            {
                requirementsVendor = EVulkanUpscaleBridgeVendor.Xess;
                return;
            }

            if (frameResources.EnableDlss
                && TryResolveDlssRequirements(
                    out instanceExtensions,
                    out minApiVersion,
                    out deviceExtensions,
                    out deviceFeatures12,
                    out deviceFeatures13,
                    out dlssQueueRequirements,
                    out dlssFailure))
            {
                requirementsVendor = EVulkanUpscaleBridgeVendor.Dlss;
                return;
            }
        }

        if (frameResources.EnableDlss && !frameResources.EnableXess && !string.IsNullOrWhiteSpace(dlssFailure))
            throw new InvalidOperationException(dlssFailure);

        if (frameResources.EnableXess && !frameResources.EnableDlss && !string.IsNullOrWhiteSpace(xessFailure))
            throw new InvalidOperationException(xessFailure);

        if (frameResources.EnableDlss && frameResources.EnableXess)
        {
            throw new InvalidOperationException(preferDlss
                ? dlssFailure ?? xessFailure ?? "No vendor requirement query succeeded for the Vulkan upscale bridge sidecar."
                : xessFailure ?? dlssFailure ?? "No vendor requirement query succeeded for the Vulkan upscale bridge sidecar.");
        }
    }

    /// <summary>
    /// Tries to resolve the Vulkan requirements for DLSS (Deep Learning Super Sampling) and returns whether the resolution was successful.
    /// </summary>
    /// <param name="instanceExtensions">The required Vulkan instance extensions for DLSS.</param>
    /// <param name="minApiVersion">The minimum Vulkan API version required for DLSS.</param>
    /// <param name="deviceExtensions">The required Vulkan device extensions for DLSS.</param>
    /// <param name="deviceFeatures12">The required Vulkan 1.2 device features for DLSS.</param>
    /// <param name="deviceFeatures13">The required Vulkan 1.3 device features for DLSS.</param>
    /// <param name="queueRequirements">The DLSS queue requirements.</param>
    /// <param name="failureReason">The reason for failure if the requirements could not be resolved.</param>
    /// <returns>True if the Vulkan requirements for DLSS were successfully resolved; otherwise, false.</returns>
    private static bool TryResolveDlssRequirements(
        out string[] instanceExtensions,
        out uint minApiVersion,
        out string[] deviceExtensions,
        out string[] deviceFeatures12,
        out string[] deviceFeatures13,
        out NvidiaDlssManager.Native.StreamlineQueueRequirements queueRequirements,
        out string failureReason)
    {
        instanceExtensions = [];
        minApiVersion = Vk.Version11;
        deviceExtensions = [];
        deviceFeatures12 = [];
        deviceFeatures13 = [];
        queueRequirements = default;
        failureReason = string.Empty;

        if (!NvidiaDlssManager.Native.IsAvailable)
        {
            failureReason = NvidiaDlssManager.Native.LastError ?? "Streamline could not be loaded.";
            return false;
        }

        bool success = NvidiaDlssManager.Native.TryGetRequiredVulkanRequirements(
            out instanceExtensions,
            out deviceExtensions,
            out deviceFeatures12,
            out deviceFeatures13,
            out queueRequirements,
            out failureReason);
        if (!success)
            return false;

        minApiVersion = DetermineMinimumApiVersionForRequestedFeatureSets(deviceFeatures12, deviceFeatures13);
        return true;
    }

    private static uint DetermineMinimumApiVersionForRequestedFeatureSets(string[] featureNames12, string[] featureNames13)
    {
        if (featureNames13.Length > 0)
            return Vk.Version13;
        if (featureNames12.Length > 0)
            return Vk.Version12;

        return Vk.Version11;
    }

    private static bool TryResolveXessRequirements(
        out string[] instanceExtensions,
        out uint minApiVersion,
        out string failureReason)
    {
        instanceExtensions = [];
        minApiVersion = Vk.Version11;
        failureReason = string.Empty;

        if (!IntelXessManager.Native.IsAvailable)
        {
            failureReason = IntelXessManager.Native.LastError ?? "XeSS could not be loaded.";
            return false;
        }

        return IntelXessManager.Native.TryGetRequiredInstanceExtensions(out instanceExtensions, out minApiVersion, out failureReason);
    }

    private Instance CreateInstance(string[] additionalExtensions, uint minApiVersion)
    {
        byte* applicationName = null;
        byte* engineName = null;
        byte** enabledExtensions = null;
        try
        {
            applicationName = (byte*)Marshal.StringToHGlobalAnsi("XRENGINE VulkanUpscaleBridgeSidecar");
            engineName = (byte*)Marshal.StringToHGlobalAnsi("XRENGINE");

            string[] extensionsToEnable = additionalExtensions
                .Where(static extension => !string.IsNullOrWhiteSpace(extension))
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            if (extensionsToEnable.Length > 0)
                enabledExtensions = (byte**)SilkMarshal.StringArrayToPtr(extensionsToEnable);

            ApplicationInfo appInfo = new()
            {
                SType = StructureType.ApplicationInfo,
                PApplicationName = applicationName,
                ApplicationVersion = new Version32(1, 0, 0),
                PEngineName = engineName,
                EngineVersion = new Version32(1, 0, 0),
                ApiVersion = Math.Max(Vk.Version11, minApiVersion),
            };

            InstanceCreateInfo createInfo = new()
            {
                SType = StructureType.InstanceCreateInfo,
                PApplicationInfo = &appInfo,
                EnabledExtensionCount = (uint)extensionsToEnable.Length,
                PpEnabledExtensionNames = enabledExtensions,
            };

            if (_api.CreateInstance(ref createInfo, null, out Instance instance) != Result.Success)
                throw new InvalidOperationException("Failed to create the Vulkan upscale bridge sidecar instance.");

            return instance;
        }
        finally
        {
            if (enabledExtensions is not null)
                SilkMarshal.Free((nint)enabledExtensions);
            if (applicationName is not null)
                Marshal.FreeHGlobal((IntPtr)applicationName);
            if (engineName is not null)
                Marshal.FreeHGlobal((IntPtr)engineName);
        }
    }

    private Device CreateDevice(
        VulkanUpscaleBridgeProbe.VulkanUpscaleBridgeSelectedDevice selectedDevice,
        string[] additionalExtensions,
        IntPtr additionalFeatureChain,
        string[] additionalFeatureNames12,
        string[] additionalFeatureNames13,
        NvidiaDlssManager.Native.StreamlineQueueRequirements queueRequirements,
        bool allowDeferredXessRequirements,
        out uint streamlineGraphicsQueueIndex,
        out uint streamlineComputeQueueIndex,
        out uint streamlineOpticalFlowQueueIndex)
    {
        streamlineGraphicsQueueIndex = 0;
        streamlineComputeQueueIndex = 0;
        streamlineOpticalFlowQueueIndex = 0;

        if (additionalExtensions.Length == 0 && allowDeferredXessRequirements && IntelXessManager.Native.IsAvailable)
        {
            if (!IntelXessManager.Native.TryGetRequiredDeviceRequirements(_instance, selectedDevice.Device, out additionalExtensions, out additionalFeatureChain, out string failureReason)
                && !_frameResources.EnableDlss)
            {
                throw new InvalidOperationException(failureReason);
            }
        }

        uint queueCount = ResolveStreamlineQueueConfiguration(
            selectedDevice.Device,
            selectedDevice.GraphicsQueueFamilyIndex,
            queueRequirements,
            out streamlineGraphicsQueueIndex,
            out streamlineComputeQueueIndex,
            out streamlineOpticalFlowQueueIndex);

        using var priorityMemory = GlobalMemory.Allocate(checked((int)queueCount * sizeof(float)));
        float* queuePriorities = (float*)Unsafe.AsPointer(ref priorityMemory.GetPinnableReference());
        for (int queueIndex = 0; queueIndex < queueCount; queueIndex++)
            queuePriorities[queueIndex] = 1.0f;

        DeviceQueueCreateInfo queueCreateInfo = new()
        {
            SType = StructureType.DeviceQueueCreateInfo,
            QueueFamilyIndex = selectedDevice.GraphicsQueueFamilyIndex,
            QueueCount = queueCount,
            PQueuePriorities = queuePriorities,
        };

        string[] extensionsToEnable =
        [
            "VK_KHR_external_memory",
            "VK_KHR_external_semaphore",
            "VK_KHR_external_memory_win32",
            "VK_KHR_external_semaphore_win32",
        ];

        string[] mergedExtensions = extensionsToEnable
            .Concat(additionalExtensions.Where(static extension => !string.IsNullOrWhiteSpace(extension)))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        PhysicalDeviceVulkan12Features requestedFeatures12 = default;
        PhysicalDeviceVulkan13Features requestedFeatures13 = default;
        void* featureChain = additionalFeatureChain == IntPtr.Zero ? null : (void*)additionalFeatureChain;
        BuildRequestedVulkanFeatures(
            selectedDevice.Device,
            additionalFeatureNames12,
            additionalFeatureNames13,
            ref requestedFeatures12,
            ref requestedFeatures13);
        if (additionalFeatureNames13.Length > 0)
        {
            requestedFeatures13.PNext = featureChain;
            featureChain = &requestedFeatures13;
        }

        if (additionalFeatureNames12.Length > 0)
        {
            requestedFeatures12.PNext = featureChain;
            featureChain = &requestedFeatures12;
        }

        DeviceCreateInfo createInfo = new()
        {
            SType = StructureType.DeviceCreateInfo,
            PNext = featureChain,
            QueueCreateInfoCount = 1,
            PQueueCreateInfos = &queueCreateInfo,
            EnabledExtensionCount = (uint)mergedExtensions.Length,
            PpEnabledExtensionNames = (byte**)SilkMarshal.StringArrayToPtr(mergedExtensions),
        };

        try
        {
            if (_api.CreateDevice(selectedDevice.Device, in createInfo, null, out Device device) != Result.Success)
                throw new InvalidOperationException($"Failed to create the Vulkan bridge sidecar device for '{selectedDevice.DeviceName}'.");

            return device;
        }
        finally
        {
            SilkMarshal.Free((nint)createInfo.PpEnabledExtensionNames);
        }
    }

    private QueueFamilyProperties GetQueueFamilyProperties(PhysicalDevice device, uint queueFamilyIndex)
    {
        uint queueFamilyCount = 0;
        _api.GetPhysicalDeviceQueueFamilyProperties(device, ref queueFamilyCount, null);
        if (queueFamilyCount == 0 || queueFamilyIndex >= queueFamilyCount)
            throw new InvalidOperationException($"Vulkan queue family {queueFamilyIndex} is unavailable for the bridge sidecar.");

        var properties = new QueueFamilyProperties[queueFamilyCount];
        fixed (QueueFamilyProperties* propertiesPtr = properties)
        {
            _api.GetPhysicalDeviceQueueFamilyProperties(device, ref queueFamilyCount, propertiesPtr);
        }

        return properties[queueFamilyIndex];
    }

    private uint ResolveStreamlineQueueConfiguration(
        PhysicalDevice device,
        uint queueFamilyIndex,
        NvidiaDlssManager.Native.StreamlineQueueRequirements queueRequirements,
        out uint streamlineGraphicsQueueIndex,
        out uint streamlineComputeQueueIndex,
        out uint streamlineOpticalFlowQueueIndex)
    {
        streamlineGraphicsQueueIndex = 0;
        streamlineComputeQueueIndex = 0;
        streamlineOpticalFlowQueueIndex = 0;

        if (queueRequirements.OpticalFlowQueues > 0)
        {
            throw new InvalidOperationException(
                "Streamline requested Vulkan optical-flow queues, but the OpenGL bridge sidecar only provisions pure DLSS upscaling queues.");
        }

        QueueFamilyProperties queueFamilyProperties = GetQueueFamilyProperties(device, queueFamilyIndex);
        if (queueRequirements.ComputeQueues > 0 && (queueFamilyProperties.QueueFlags & QueueFlags.ComputeBit) == 0)
        {
            throw new InvalidOperationException(
                $"The selected Vulkan queue family {queueFamilyIndex} does not support compute work required by Streamline DLSS.");
        }

        uint queueCount = 1;
        if (queueRequirements.GraphicsQueues > 0)
        {
            streamlineGraphicsQueueIndex = queueCount;
            queueCount += queueRequirements.GraphicsQueues;
        }

        if (queueRequirements.ComputeQueues > 0)
        {
            streamlineComputeQueueIndex = queueCount;
            queueCount += queueRequirements.ComputeQueues;
        }
        else
        {
            streamlineComputeQueueIndex = streamlineGraphicsQueueIndex;
        }

        if (queueCount > queueFamilyProperties.QueueCount)
        {
            throw new InvalidOperationException(
                $"Streamline requested {queueCount - 1} extra Vulkan queues, but queue family {queueFamilyIndex} only exposes {queueFamilyProperties.QueueCount} total queues.");
        }

        return queueCount;
    }

    private void BuildRequestedVulkanFeatures(
        PhysicalDevice device,
        string[] featureNames12,
        string[] featureNames13,
        ref PhysicalDeviceVulkan12Features requestedFeatures12,
        ref PhysicalDeviceVulkan13Features requestedFeatures13)
    {
        if (featureNames12.Length == 0 && featureNames13.Length == 0)
            return;

        PhysicalDeviceVulkan12Features supportedFeatures12 = new()
        {
            SType = StructureType.PhysicalDeviceVulkan12Features,
        };
        PhysicalDeviceVulkan13Features supportedFeatures13 = new()
        {
            SType = StructureType.PhysicalDeviceVulkan13Features,
        };
        PhysicalDeviceFeatures2 supportedFeatures2 = new()
        {
            SType = StructureType.PhysicalDeviceFeatures2,
        };

        if (featureNames12.Length > 0)
        {
            supportedFeatures2.PNext = &supportedFeatures12;
            supportedFeatures12.PNext = featureNames13.Length > 0 ? &supportedFeatures13 : null;
        }
        else
        {
            supportedFeatures2.PNext = &supportedFeatures13;
        }

        _api.GetPhysicalDeviceFeatures2(device, &supportedFeatures2);

        if (featureNames13.Length > 0)
        {
            requestedFeatures13 = new PhysicalDeviceVulkan13Features
            {
                SType = StructureType.PhysicalDeviceVulkan13Features,
            };
            ValidateAndPopulateRequestedFeatures(ref requestedFeatures13, in supportedFeatures13, featureNames13, "Vulkan 1.3");
        }

        if (featureNames12.Length > 0)
        {
            requestedFeatures12 = new PhysicalDeviceVulkan12Features
            {
                SType = StructureType.PhysicalDeviceVulkan12Features,
            };
            ValidateAndPopulateRequestedFeatures(ref requestedFeatures12, in supportedFeatures12, featureNames12, "Vulkan 1.2");
        }
    }

    private static void ValidateAndPopulateRequestedFeatures<TFeatures>(
        ref TFeatures requestedFeatures,
        in TFeatures supportedFeatures,
        string[] featureNames,
        string featureGroup) where TFeatures : struct
    {
        List<string> unknownFeatures = [];
        List<string> unsupportedFeatures = [];

        foreach (string featureName in featureNames
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal))
        {
            if (!TryResolveFeatureField(typeof(TFeatures), featureName, out FieldInfo? field) || field is null)
            {
                unknownFeatures.Add(featureName);
                continue;
            }

            if (!TryReadFeatureField(in supportedFeatures, field, out bool supported) || !supported)
            {
                unsupportedFeatures.Add(featureName);
                continue;
            }

            WriteFeatureField(ref requestedFeatures, field, enabled: true);
        }

        if (unknownFeatures.Count > 0)
        {
            throw new InvalidOperationException(
                $"Streamline requested unknown {featureGroup} feature fields: {string.Join(", ", unknownFeatures)}.");
        }

        if (unsupportedFeatures.Count > 0)
        {
            throw new InvalidOperationException(
                $"The selected Vulkan device does not support Streamline-required {featureGroup} features: {string.Join(", ", unsupportedFeatures)}.");
        }
    }

    private static bool TryResolveFeatureField(Type featureType, string featureName, out FieldInfo? field)
    {
        string pascalCaseName = featureName.Length == 0
            ? string.Empty
            : char.ToUpperInvariant(featureName[0]) + featureName[1..];

        field = featureType.GetField(pascalCaseName, BindingFlags.Public | BindingFlags.Instance)
            ?? featureType.GetField(featureName, BindingFlags.Public | BindingFlags.Instance);
        return field is not null;
    }

    private static bool TryReadFeatureField<TFeatures>(in TFeatures features, FieldInfo field, out bool value) where TFeatures : struct
    {
        object boxed = features;
        return TryConvertFeatureFieldValue(field.GetValue(boxed), out value);
    }

    private static void WriteFeatureField<TFeatures>(ref TFeatures features, FieldInfo field, bool enabled) where TFeatures : struct
    {
        object boxed = features;
        field.SetValue(boxed, CreateFeatureFieldValue(field.FieldType, enabled));
        features = (TFeatures)boxed;
    }

    private static object CreateFeatureFieldValue(Type fieldType, bool enabled)
    {
        if (TryCreateDirectFeatureFieldValue(fieldType, enabled, out object? directValue))
            return directValue!;

        ConstructorInfo? constructor = fieldType.GetConstructor([typeof(bool)])
            ?? fieldType.GetConstructor([typeof(uint)])
            ?? fieldType.GetConstructor([typeof(int)]);
        if (constructor is not null)
        {
            Type parameterType = constructor.GetParameters()[0].ParameterType;
            object argument = parameterType == typeof(bool)
                ? enabled
                : parameterType == typeof(uint)
                    ? enabled ? 1u : 0u
                    : enabled ? 1 : 0;
            return constructor.Invoke([argument]);
        }

        object instance = Activator.CreateInstance(fieldType)
            ?? throw new InvalidOperationException($"Could not construct Vulkan feature field type '{fieldType.FullName}'.");
        if (!TryAssignFeatureValueMember(instance, enabled))
        {
            throw new InvalidOperationException(
                $"Unsupported Vulkan feature field type '{fieldType.FullName}' in Streamline requirement translation.");
        }

        return instance;
    }

    private static bool TryConvertFeatureFieldValue(object? rawValue, out bool value)
    {
        switch (rawValue)
        {
            case bool boolValue:
                value = boolValue;
                return true;
            case uint uintValue:
                value = uintValue != 0;
                return true;
            case int intValue:
                value = intValue != 0;
                return true;
            case byte byteValue:
                value = byteValue != 0;
                return true;
            case null:
                value = false;
                return false;
        }

        if (TryReadFeatureValueMember(rawValue, out value)
            || TryConvertFeatureFieldValueViaOperators(rawValue, typeof(bool), out value)
            || TryConvertFeatureFieldValueViaOperators(rawValue, typeof(uint), out value)
            || TryConvertFeatureFieldValueViaOperators(rawValue, typeof(int), out value)
            || TryConvertFeatureFieldValueViaOperators(rawValue, typeof(byte), out value))
            return true;

        value = false;
        return false;
    }

    private static bool TryCreateDirectFeatureFieldValue(Type fieldType, bool enabled, out object? value)
    {
        if (fieldType == typeof(bool))
        {
            value = enabled;
            return true;
        }

        if (fieldType == typeof(uint))
        {
            value = enabled ? 1u : 0u;
            return true;
        }

        if (fieldType == typeof(int))
        {
            value = enabled ? 1 : 0;
            return true;
        }

        if (fieldType == typeof(byte))
        {
            value = enabled ? (byte)1 : (byte)0;
            return true;
        }

        object[] candidates = [enabled, enabled ? 1u : 0u, enabled ? 1 : 0, enabled ? (byte)1 : (byte)0];
        foreach (object candidate in candidates)
        {
            if (TryInvokeUserDefinedConversion(candidate, fieldType, out value))
                return true;
        }

        value = null;
        return false;
    }

    /// <summary>
    /// Tries to assign a boolean value to the "Value" member of a feature value instance, creating the appropriate type if necessary.
    /// </summary>
    /// <param name="instance">The feature value instance whose "Value" member is to be assigned.</param>
    /// <param name="enabled">The boolean value to assign to the "Value" member.</param>
    /// <returns>True if the assignment succeeds; otherwise, false.</returns>
    private static bool TryAssignFeatureValueMember(object instance, bool enabled)
    {
        Type instanceType = instance.GetType();
        FieldInfo? valueField = instanceType.GetField("Value", BindingFlags.Public | BindingFlags.Instance);
        if (valueField is not null && TryCreateDirectFeatureFieldValue(valueField.FieldType, enabled, out object? fieldValue))
        {
            valueField.SetValue(instance, fieldValue);
            return true;
        }

        PropertyInfo? valueProperty = instanceType.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
        if (valueProperty is not null
            && valueProperty.CanWrite
            && TryCreateDirectFeatureFieldValue(valueProperty.PropertyType, enabled, out object? propertyValue))
        {
            valueProperty.SetValue(instance, propertyValue);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Tries to read the "Value" member of a raw feature value instance and convert it to a boolean.
    /// </summary>
    /// <param name="rawValue">The raw feature value instance from which to read the "Value" member.</param>
    /// <param name="value">The resulting boolean value if the conversion succeeds; otherwise, false.</param>
    /// <returns>True if the conversion succeeds; otherwise, false.</returns>
    private static bool TryReadFeatureValueMember(object rawValue, out bool value)
    {
        Type rawType = rawValue.GetType();
        FieldInfo? valueField = rawType.GetField("Value", BindingFlags.Public | BindingFlags.Instance);
        if (valueField is not null)
            return TryConvertFeatureFieldValue(valueField.GetValue(rawValue), out value);

        PropertyInfo? valueProperty = rawType.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
        if (valueProperty is not null && valueProperty.CanRead)
            return TryConvertFeatureFieldValue(valueProperty.GetValue(rawValue), out value);

        value = false;
        return false;
    }

    /// <summary>
    /// Tries to convert the raw feature field value to a boolean using an intermediate type and user-defined conversion operators.
    /// </summary>
    /// <param name="rawValue">The raw feature field value to be converted.</param>
    /// <param name="intermediateType">The intermediate type to which the raw value should be converted before converting to boolean.</param>
    /// <param name="value">The resulting boolean value if the conversion succeeds; otherwise, false.</param>
    /// <returns>True if the conversion succeeds; otherwise, false.</returns>
    private static bool TryConvertFeatureFieldValueViaOperators(object rawValue, Type intermediateType, out bool value)
    {
        if (TryInvokeUserDefinedConversion(rawValue, intermediateType, out object? convertedValue))
            return TryConvertFeatureFieldValue(convertedValue, out value);

        value = false;
        return false;
    }

    /// <summary>
    /// Tries to invoke a user-defined conversion operator to convert the source value to the target type.
    /// </summary>
    /// <param name="sourceValue">The value to be converted.</param>
    /// <param name="targetType">The type to which the value should be converted.</param>
    /// <param name="convertedValue">The converted value if the conversion succeeds; otherwise, null.</param>
    /// <returns>True if the conversion succeeds; otherwise, false.</returns>
    private static bool TryInvokeUserDefinedConversion(object sourceValue, Type targetType, out object? convertedValue)
    {
        Type sourceType = sourceValue.GetType();
        foreach (Type type in new[] { sourceType, targetType })
        {
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (method.ReturnType != targetType)
                    continue;
                if (method.Name != "op_Implicit" && method.Name != "op_Explicit")
                    continue;

                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length != 1 || !parameters[0].ParameterType.IsAssignableFrom(sourceType))
                    continue;

                convertedValue = method.Invoke(null, [sourceValue]);
                return convertedValue is not null || !targetType.IsValueType;
            }
        }

        convertedValue = null;
        return false;
    }

    /// <summary>
    /// Gets the graphics queue for the specified queue family index.
    /// </summary>
    /// <param name="queueFamilyIndex">The index of the queue family for which to get the graphics queue.</param>
    /// <returns>The graphics queue for the specified queue family index.</returns>
    private Queue GetGraphicsQueue(uint queueFamilyIndex)
    {
        _api.GetDeviceQueue(_device, queueFamilyIndex, 0, out Queue queue);
        return queue;
    }

    /// <summary>
    /// Creates a Vulkan command pool for the specified queue family index.
    /// </summary>
    /// <param name="queueFamilyIndex">The index of the queue family for which to create the command pool.</param>
    /// <returns>The created Vulkan command pool.</returns>
    /// <exception cref="InvalidOperationException">Thrown if the command pool cannot be created.</exception>
    private CommandPool CreateCommandPool(uint queueFamilyIndex)
    {
        ThrowIfDeviceOperationNotAdmitted("vkCreateCommandPool.VulkanUpscaleBridge");
        CommandPoolCreateInfo createInfo = new()
        {
            SType = StructureType.CommandPoolCreateInfo,
            QueueFamilyIndex = queueFamilyIndex,
            Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
        };

        Result result = _api.CreateCommandPool(_device, in createInfo, null, out CommandPool commandPool);
        ObserveDeviceResult(result, "vkCreateCommandPool.VulkanUpscaleBridge");
        if (result != Result.Success)
            throw new InvalidOperationException("Failed to create the Vulkan bridge sidecar command pool.");

        return commandPool;
    }

    /// <summary>
    /// Allocates a primary Vulkan command buffer from the command pool.
    /// </summary>
    /// <returns>the allocated primary Vulkan command buffer.</returns>
    /// <exception cref="InvalidOperationException">Thrown if the command buffer cannot be allocated.</exception>
    private CommandBuffer AllocateCommandBuffer(int slotIndex, ulong allocationGeneration)
    {
        ThrowIfDeviceOperationNotAdmitted("vkAllocateCommandBuffers.VulkanUpscaleBridge");
        CommandBufferAllocateInfo allocateInfo = new()
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = _commandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1,
        };

        Result result =
            _api.AllocateCommandBuffers(_device, in allocateInfo, out CommandBuffer commandBuffer);
        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanAllocateCommandBuffersCall(
            allocateInfo.CommandBufferCount,
            result == Result.Success);
        ObserveDeviceResult(
            result,
            "vkAllocateCommandBuffers.VulkanUpscaleBridge",
            allocationGeneration: allocationGeneration,
            slotIndex: slotIndex,
            resourceName: $"Slot{slotIndex}.CommandBuffer");
        if (result != Result.Success)
            throw new InvalidOperationException("Failed to allocate a Vulkan bridge handoff command buffer.");

        return commandBuffer;
    }

    private Result ResetCommandBufferTracked(CommandBuffer commandBuffer)
    {
        Result result = _api.ResetCommandBuffer(commandBuffer, 0);
        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanResetCommandBufferCall();
        return result;
    }

    /// <summary>
    /// Creates a Vulkan fence that is initially signaled.
    /// </summary>
    /// <returns>A Vulkan fence that is initially signaled.</returns>
    /// <exception cref="InvalidOperationException">Thrown if the fence cannot be created.</exception>
    private Fence CreateFence(int slotIndex, ulong allocationGeneration)
    {
        ThrowIfDeviceOperationNotAdmitted("vkCreateFence.VulkanUpscaleBridge");
        FenceCreateInfo createInfo = new()
        {
            SType = StructureType.FenceCreateInfo,
            Flags = FenceCreateFlags.SignaledBit,
        };

        Result result = _api.CreateFence(_device, in createInfo, null, out Fence fence);
        ObserveDeviceResult(
            result,
            "vkCreateFence.VulkanUpscaleBridge",
            allocationGeneration: allocationGeneration,
            slotIndex: slotIndex,
            resourceName: $"Slot{slotIndex}.SubmitFence");
        if (result != Result.Success)
            throw new InvalidOperationException("Failed to create a Vulkan bridge submit fence.");

        return fence;
    }

    /// <summary>
    /// Creates a shared Vulkan semaphore that can be used with both Vulkan and OpenGL.
    /// </summary>
    /// <param name="renderer">The OpenGL renderer instance.</param>
    /// <param name="name">The name of the shared semaphore.</param>
    /// <returns>A VulkanUpscaleBridgeSharedSemaphore representing the shared semaphore.</returns>
    /// <exception cref="InvalidOperationException">Thrown if the shared semaphore cannot be created or imported into OpenGL.</exception>
    private VulkanUpscaleBridgeSharedSemaphore CreateSharedSemaphore(
        IOpenGlVendorUpscaleBackendCapability renderer,
        string name,
        int slotIndex,
        ulong allocationGeneration)
    {
        ThrowIfDeviceOperationNotAdmitted("vkCreateSemaphore.VulkanUpscaleBridge");
        ExportSemaphoreCreateInfo exportInfo = new()
        {
            SType = StructureType.ExportSemaphoreCreateInfo,
            HandleTypes = ExternalSemaphoreHandleTypeFlags.OpaqueWin32Bit,
        };

        SemaphoreCreateInfo createInfo = new()
        {
            SType = StructureType.SemaphoreCreateInfo,
            PNext = &exportInfo,
        };

        VkSemaphore semaphore = default;
        uint glSemaphore = 0;
        IntPtr glHandle = IntPtr.Zero;
        try
        {
            ThrowIfDeviceOperationNotAdmitted("vkCreateSemaphore.VulkanUpscaleBridge");
            Result createResult = _api.CreateSemaphore(_device, in createInfo, null, out semaphore);
            ObserveDeviceResult(
                createResult,
                "vkCreateSemaphore.VulkanUpscaleBridge",
                allocationGeneration: allocationGeneration,
                slotIndex: slotIndex,
                resourceName: name);
            if (createResult != Result.Success)
                throw new InvalidOperationException($"Failed to create bridge semaphore '{name}'.");

        SemaphoreGetWin32HandleInfoKHR handleInfo = new()
        {
            SType = StructureType.SemaphoreGetWin32HandleInfoKhr,
            Semaphore = semaphore,
            HandleType = ExternalSemaphoreHandleTypeFlags.OpaqueWin32Bit,
        };

            IntPtr exportedHandle = IntPtr.Zero;
            ThrowIfDeviceOperationNotAdmitted("vkGetSemaphoreWin32HandleKHR.VulkanUpscaleBridge");
            Result exportResult = _externalSemaphoreWin32.GetSemaphoreWin32Handle(_device, &handleInfo, &exportedHandle);
            ObserveDeviceResult(
                exportResult,
                "vkGetSemaphoreWin32HandleKHR.VulkanUpscaleBridge",
                allocationGeneration: allocationGeneration,
                slotIndex: slotIndex,
                resourceName: name);
            if (exportResult != Result.Success || exportedHandle == IntPtr.Zero)
                throw new InvalidOperationException($"Failed to export bridge semaphore handle '{name}'.");

            glHandle = DuplicateHandleForImport(exportedHandle);
            glSemaphore = renderer.CreateImportedSemaphore(glHandle);
            if (glSemaphore == 0)
                throw new InvalidOperationException($"Failed to import bridge semaphore '{name}' into OpenGL.");

            // OpenGL consumes the duplicated handle on a successful import.
            glHandle = IntPtr.Zero;

            return new VulkanUpscaleBridgeSharedSemaphore(allocationGeneration, name, renderer, semaphore, glSemaphore);
        }
        catch
        {
            if (glSemaphore != 0)
                renderer.DeleteSemaphore(glSemaphore);
            if (glHandle != IntPtr.Zero)
                CloseHandle(glHandle);
            if (semaphore.Handle != 0)
                _api.DestroySemaphore(_device, semaphore, null);
            throw;
        }
    }

    /// <summary>
    /// Creates a shared Vulkan image that can be used with both Vulkan and OpenGL, and returns a handle to the shared image.
    /// </summary>
    /// <param name="renderer">The OpenGL renderer instance.</param>
    /// <param name="slotTag">A tag identifying the slot for the shared image.</param>
    /// <param name="kind">The kind of Vulkan upscale bridge surface.</param>
    /// <param name="width">The width of the shared image.</param>
    /// <param name="height">The height of the shared image.</param>
    /// <param name="internalFormat">The internal format of the image.</param>
    /// <param name="pixelFormat">The pixel format of the image.</param>
    /// <param name="pixelType">The pixel type of the image.</param>
    /// <param name="sizedInternalFormat">The sized internal format of the image.</param>
    /// <param name="attachment">The framebuffer attachment for the image.</param>
    /// <param name="usage">The usage flags for the Vulkan image.</param>
    /// <param name="aspectMask">The aspect mask for the Vulkan image.</param>
    /// <param name="viewAspectMask">The aspect mask for the Vulkan image view.</param>
    /// <param name="linearFilter">Indicates whether linear filtering should be used.</param>
    /// <returns>A handle to the shared Vulkan image.</returns>
    /// <exception cref="InvalidOperationException">Thrown if the shared image cannot be created.</exception>
    private VulkanUpscaleBridgeSharedImage CreateSharedImage(
        IOpenGlVendorUpscaleBackendCapability renderer,
        string slotTag,
        int slotIndex,
        EVulkanUpscaleBridgeSurfaceKind kind,
        uint width,
        uint height,
        EPixelInternalFormat internalFormat,
        EPixelFormat pixelFormat,
        EPixelType pixelType,
        ESizedInternalFormat sizedInternalFormat,
        EFrameBufferAttachment attachment,
        ImageUsageFlags usage,
        ImageAspectFlags aspectMask,
        ImageAspectFlags viewAspectMask,
        bool linearFilter,
        ulong allocationGeneration)
    {
        ThrowIfDeviceOperationNotAdmitted("vkCreateImage.VulkanUpscaleBridge");
        string name = $"VulkanUpscaleBridge.{slotTag}.{kind}";
        Format vkFormat = MapFormat(internalFormat);
        Image image = default;
        DeviceMemory memory = default;
        ImageView imageView = default;
        XRTexture2D? texture = null;
        XRFrameBuffer? frameBuffer = null;
        IntPtr glHandle = IntPtr.Zero;

        ExternalMemoryImageCreateInfo externalImageInfo = new()
        {
            SType = StructureType.ExternalMemoryImageCreateInfo,
            HandleTypes = ExternalMemoryHandleTypeFlags.OpaqueWin32Bit,
        };

        ImageCreateInfo imageInfo = new()
        {
            SType = StructureType.ImageCreateInfo,
            PNext = &externalImageInfo,
            ImageType = ImageType.Type2D,
            Format = vkFormat,
            Extent = new Extent3D(width, height, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = usage,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
        };

        try
        {
            ThrowIfDeviceOperationNotAdmitted("vkCreateImage.VulkanUpscaleBridge");
            Result createImageResult = _api.CreateImage(_device, in imageInfo, null, out image);
            ObserveDeviceResult(
                createImageResult,
                "vkCreateImage.VulkanUpscaleBridge",
                allocationGeneration: allocationGeneration,
                slotIndex: slotIndex,
                resourceName: name);
            if (createImageResult != Result.Success)
                throw new InvalidOperationException($"Failed to create bridge image '{name}'.");

        _api.GetImageMemoryRequirements(_device, image, out MemoryRequirements memoryRequirements);

        MemoryDedicatedAllocateInfo dedicatedInfo = new()
        {
            SType = StructureType.MemoryDedicatedAllocateInfo,
            Image = image,
        };

        ExportMemoryAllocateInfo exportInfo = new()
        {
            SType = StructureType.ExportMemoryAllocateInfo,
            PNext = &dedicatedInfo,
            HandleTypes = ExternalMemoryHandleTypeFlags.OpaqueWin32Bit,
        };

        MemoryAllocateInfo allocateInfo = new()
        {
            SType = StructureType.MemoryAllocateInfo,
            PNext = &exportInfo,
            AllocationSize = memoryRequirements.Size,
            MemoryTypeIndex = FindMemoryType(memoryRequirements.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit),
        };

        ThrowIfDeviceOperationNotAdmitted("vkAllocateMemory.VulkanUpscaleBridge");
        Result allocateResult = _api.AllocateMemory(_device, in allocateInfo, null, out memory);
        ObserveDeviceResult(
            allocateResult,
            "vkAllocateMemory.VulkanUpscaleBridge",
            allocationGeneration: allocationGeneration,
            slotIndex: slotIndex,
            resourceName: name);
        if (allocateResult != Result.Success)
            throw new InvalidOperationException($"Failed to allocate bridge memory for '{name}'.");

        ThrowIfDeviceOperationNotAdmitted("vkBindImageMemory.VulkanUpscaleBridge");
        Result bindResult = _api.BindImageMemory(_device, image, memory, 0);
        ObserveDeviceResult(
            bindResult,
            "vkBindImageMemory.VulkanUpscaleBridge",
            allocationGeneration: allocationGeneration,
            slotIndex: slotIndex,
            resourceName: name);
        if (bindResult != Result.Success)
            throw new InvalidOperationException($"Failed to bind bridge memory for '{name}'.");

        ImageViewCreateInfo viewInfo = new()
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = image,
            ViewType = ImageViewType.Type2D,
            Format = vkFormat,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = viewAspectMask,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1,
            },
        };

        ThrowIfDeviceOperationNotAdmitted("vkCreateImageView.VulkanUpscaleBridge");
        Result createViewResult = _api.CreateImageView(_device, in viewInfo, null, out imageView);
        ObserveDeviceResult(
            createViewResult,
            "vkCreateImageView.VulkanUpscaleBridge",
            allocationGeneration: allocationGeneration,
            slotIndex: slotIndex,
            resourceName: name);
        if (createViewResult != Result.Success)
            throw new InvalidOperationException($"Failed to create bridge image view for '{name}'.");

        MemoryGetWin32HandleInfoKHR handleInfo = new()
        {
            SType = StructureType.MemoryGetWin32HandleInfoKhr,
            Memory = memory,
            HandleType = ExternalMemoryHandleTypeFlags.OpaqueWin32Bit,
        };

        IntPtr exportedHandle = IntPtr.Zero;
        ThrowIfDeviceOperationNotAdmitted("vkGetMemoryWin32HandleKHR.VulkanUpscaleBridge");
        Result exportResult = _externalMemoryWin32.GetMemoryWin32Handle(_device, &handleInfo, &exportedHandle);
        ObserveDeviceResult(
            exportResult,
            "vkGetMemoryWin32HandleKHR.VulkanUpscaleBridge",
            allocationGeneration: allocationGeneration,
            slotIndex: slotIndex,
            resourceName: name);
        if (exportResult != Result.Success || exportedHandle == IntPtr.Zero)
            throw new InvalidOperationException($"Failed to export bridge memory handle for '{name}'.");

        glHandle = DuplicateHandleForImport(exportedHandle);

        texture = XRTexture2D.CreateFrameBufferTexture(width, height, internalFormat, pixelFormat, pixelType, attachment);
        texture.Name = name;
        texture.SamplerName = name;
        texture.Resizable = false;
        texture.SizedInternalFormat = sizedInternalFormat;
        texture.MinFilter = linearFilter ? ETexMinFilter.Linear : ETexMinFilter.Nearest;
        texture.MagFilter = linearFilter ? ETexMagFilter.Linear : ETexMagFilter.Nearest;
        texture.UWrap = ETexWrapMode.ClampToEdge;
        texture.VWrap = ETexWrapMode.ClampToEdge;
        texture.AutoGenerateMipmaps = false;
        texture.SetOpenGlExternalMemoryImport(glHandle, memoryRequirements.Size, name, mipLevels: 1);
        // The texture import consumes the duplicated handle when generation succeeds.

        if (!renderer.TryGenerateTexture(texture, out string textureFailure))
            throw new InvalidOperationException(textureFailure);
        glHandle = IntPtr.Zero;

        frameBuffer = new((texture, attachment, 0, -1))
        {
            Name = $"{name}.FBO",
        };
        if (!renderer.TryGenerateFrameBuffer(frameBuffer, out string frameBufferFailure))
            throw new InvalidOperationException(frameBufferFailure);

        return new VulkanUpscaleBridgeSharedImage(allocationGeneration, name, kind, image, memory, imageView, vkFormat, aspectMask, viewAspectMask, usage, texture, frameBuffer);
        }
        catch
        {
            frameBuffer?.Destroy(true);
            texture?.Destroy(true);
            if (glHandle != IntPtr.Zero)
                CloseHandle(glHandle);
            if (imageView.Handle != 0)
                _api.DestroyImageView(_device, imageView, null);
            if (image.Handle != 0)
                _api.DestroyImage(_device, image, null);
            if (memory.Handle != 0)
                _api.FreeMemory(_device, memory, null);
            throw;
        }
    }

    /// <summary>
    /// Finds a suitable Vulkan memory type index that satisfies the specified type bits and required memory properties.
    /// </summary>
    /// <param name="typeBits">A bitmask representing the allowed memory types for the Vulkan resource.</param>
    /// <param name="requiredProperties">The required memory property flags for the Vulkan memory type.</param>
    /// <returns>The index of a suitable Vulkan memory type that satisfies the specified requirements.</returns>
    /// <exception cref="InvalidOperationException">Thrown if no suitable Vulkan memory type could be found that satisfies the specified requirements.</exception>
    private uint FindMemoryType(uint typeBits, MemoryPropertyFlags requiredProperties)
    {
        _api.GetPhysicalDeviceMemoryProperties(_selectedDevice.Device, out PhysicalDeviceMemoryProperties memoryProperties);
        for (uint index = 0; index < memoryProperties.MemoryTypeCount; index++)
        {
            if ((typeBits & (1u << (int)index)) == 0)
                continue;

            if ((memoryProperties.MemoryTypes[(int)index].PropertyFlags & requiredProperties) != requiredProperties)
                continue;

            return index;
        }

        throw new InvalidOperationException($"Failed to resolve a Vulkan memory type for bridge resources on '{_selectedDevice.DeviceName}'.");
    }

    /// <summary>
    /// Maps a given OpenGL internal pixel format to the corresponding Vulkan format.
    /// </summary>
    /// <param name="internalFormat">The OpenGL internal pixel format to be mapped.</param>
    /// <returns>The corresponding Vulkan format.</returns>
    /// <exception cref="NotSupportedException">Thrown if the OpenGL internal pixel format does not have a corresponding Vulkan format mapping.</exception>
    private static Format MapFormat(EPixelInternalFormat internalFormat)
    {
        return internalFormat switch
        {
            EPixelInternalFormat.Rgba16f => Format.R16G16B16A16Sfloat,
            EPixelInternalFormat.Rgba8 => Format.R8G8B8A8Unorm,
            EPixelInternalFormat.RG16f => Format.R16G16Sfloat,
            EPixelInternalFormat.R32f => Format.R32Sfloat,
            EPixelInternalFormat.Depth24Stencil8 => Format.D24UnormS8Uint,
            _ => throw new NotSupportedException($"Vulkan upscale bridge does not yet map GL format '{internalFormat}'."),
        };
    }

    /// <summary>
    /// Duplicates a Win32 handle for import into another API, such as OpenGL. 
    /// Closes the original handle after duplication.
    /// </summary>
    /// <param name="sourceHandle">The original Win32 handle to duplicate.</param>
    /// <returns>The duplicated Win32 handle suitable for import into another API.</returns>
    /// <exception cref="InvalidOperationException"></exception>
    private static IntPtr DuplicateHandleForImport(IntPtr sourceHandle)
    {
        IntPtr currentProcess = GetCurrentProcess();
        if (!DuplicateHandle(currentProcess, sourceHandle, currentProcess, out IntPtr duplicatedHandle, 0, false, DuplicateSameAccess))
        {
            CloseHandle(sourceHandle);
            throw new InvalidOperationException("Failed to duplicate a Vulkan bridge Win32 handle for OpenGL import.");
        }

        CloseHandle(sourceHandle);
        return duplicatedHandle;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DuplicateHandle(
        IntPtr hSourceProcessHandle,
        IntPtr hSourceHandle,
        IntPtr hTargetProcessHandle,
        out IntPtr lpTargetHandle,
        uint dwDesiredAccess,
        bool bInheritHandle,
        uint dwOptions);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
}
