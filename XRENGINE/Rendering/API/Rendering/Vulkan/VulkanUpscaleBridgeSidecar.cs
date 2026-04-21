using Silk.NET.Core.Native;
using Silk.NET.Core;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using XREngine.Data.Rendering;
using XREngine.Rendering.DLSS;
using XREngine.Rendering.OpenGL;
using XREngine.Rendering.XeSS;
using VkSemaphore = Silk.NET.Vulkan.Semaphore;

namespace XREngine.Rendering.Vulkan;

internal enum EVulkanUpscaleBridgeSurfaceKind
{
    SourceColor,
    SourceDepth,
    SourceMotion,
    Exposure,
    OutputColor,
}

internal sealed unsafe class VulkanUpscaleBridgeSharedSemaphore(
    string name,
    OpenGLRenderer renderer,
    VkSemaphore vkSemaphore,
    uint glSemaphore) : IDisposable
{
    private readonly OpenGLRenderer _renderer = renderer;
    private bool _disposed;

    public string Name { get; } = name;
    public VkSemaphore VulkanSemaphore { get; } = vkSemaphore;
    public uint GlSemaphore { get; } = glSemaphore;

    internal void DestroyVulkanResources(Vk api, Device device)
    {
        if (VulkanSemaphore.Handle != 0)
            api.DestroySemaphore(device, VulkanSemaphore, null);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _renderer.DeleteSemaphore(GlSemaphore);
    }
}

internal sealed unsafe class VulkanUpscaleBridgeSharedImage(
    string name,
    EVulkanUpscaleBridgeSurfaceKind kind,
    Image vkImage,
    DeviceMemory vkMemory,
    ImageView vkImageView,
    Format vkFormat,
    ImageAspectFlags aspectMask,
    ImageAspectFlags viewAspectMask,
    ImageUsageFlags usage,
    XRTexture2D texture,
    XRFrameBuffer frameBuffer) : IDisposable
{
    private bool _disposed;

    public string Name { get; } = name;
    public EVulkanUpscaleBridgeSurfaceKind Kind { get; } = kind;
    public Image VulkanImage { get; } = vkImage;
    public DeviceMemory VulkanMemory { get; } = vkMemory;
    public ImageView VulkanImageView { get; } = vkImageView;
    public Format VulkanFormat { get; } = vkFormat;
    public ImageAspectFlags AspectMask { get; } = aspectMask;
    public ImageAspectFlags ViewAspectMask { get; } = viewAspectMask;
    public ImageUsageFlags Usage { get; } = usage;
    public XRTexture2D Texture { get; } = texture;
    public XRFrameBuffer FrameBuffer { get; } = frameBuffer;
    public ImageLayout CurrentLayout { get; set; }

    internal void DestroyVulkanResources(Vk api, Device device)
    {
        if (VulkanImageView.Handle != 0)
            api.DestroyImageView(device, VulkanImageView, null);
        if (VulkanImage.Handle != 0)
            api.DestroyImage(device, VulkanImage, null);
        if (VulkanMemory.Handle != 0)
            api.FreeMemory(device, VulkanMemory, null);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        FrameBuffer.Destroy(true);
        Texture.Destroy(true);
    }
}

internal sealed unsafe class VulkanUpscaleBridgeFrameSlot(
    int slotIndex,
    VulkanUpscaleBridgeSharedImage sourceColor,
    VulkanUpscaleBridgeSharedImage sourceDepth,
    VulkanUpscaleBridgeSharedImage sourceMotion,
    VulkanUpscaleBridgeSharedImage exposure,
    VulkanUpscaleBridgeSharedImage outputColor,
    VulkanUpscaleBridgeSharedSemaphore readySemaphore,
    VulkanUpscaleBridgeSharedSemaphore completeSemaphore,
    CommandBuffer commandBuffer,
    Fence submitFence) : IDisposable
{
    private bool _disposed;

    public int SlotIndex { get; } = slotIndex;
    public VulkanUpscaleBridgeSharedImage SourceColor { get; } = sourceColor;
    public VulkanUpscaleBridgeSharedImage SourceDepth { get; } = sourceDepth;
    public VulkanUpscaleBridgeSharedImage SourceMotion { get; } = sourceMotion;
    public VulkanUpscaleBridgeSharedImage Exposure { get; } = exposure;
    public VulkanUpscaleBridgeSharedImage OutputColor { get; } = outputColor;
    public VulkanUpscaleBridgeSharedSemaphore ReadySemaphore { get; } = readySemaphore;
    public VulkanUpscaleBridgeSharedSemaphore CompleteSemaphore { get; } = completeSemaphore;
    public CommandBuffer CommandBuffer { get; } = commandBuffer;
    public Fence SubmitFence { get; } = submitFence;

    public XRTexture2D SourceColorTexture => SourceColor.Texture;
    public XRTexture2D SourceDepthTexture => SourceDepth.Texture;
    public XRTexture2D SourceMotionTexture => SourceMotion.Texture;
    public XRTexture2D ExposureTexture => Exposure.Texture;
    public XRTexture2D OutputColorTexture => OutputColor.Texture;

    public XRFrameBuffer SourceColorFrameBuffer => SourceColor.FrameBuffer;
    public XRFrameBuffer SourceDepthFrameBuffer => SourceDepth.FrameBuffer;
    public XRFrameBuffer SourceMotionFrameBuffer => SourceMotion.FrameBuffer;
    public XRFrameBuffer ExposureFrameBuffer => Exposure.FrameBuffer;
    public XRFrameBuffer OutputColorFrameBuffer => OutputColor.FrameBuffer;

    public uint GlReadySemaphore => ReadySemaphore.GlSemaphore;
    public uint GlCompleteSemaphore => CompleteSemaphore.GlSemaphore;

    internal void DestroyVulkanResources(Vk api, Device device)
    {
        if (SubmitFence.Handle != 0)
            api.DestroyFence(device, SubmitFence, null);

        CompleteSemaphore.DestroyVulkanResources(api, device);
        ReadySemaphore.DestroyVulkanResources(api, device);
        OutputColor.DestroyVulkanResources(api, device);
        Exposure.DestroyVulkanResources(api, device);
        SourceMotion.DestroyVulkanResources(api, device);
        SourceDepth.DestroyVulkanResources(api, device);
        SourceColor.DestroyVulkanResources(api, device);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        CompleteSemaphore.Dispose();
        ReadySemaphore.Dispose();
        OutputColor.Dispose();
        Exposure.Dispose();
        SourceMotion.Dispose();
        SourceDepth.Dispose();
        SourceColor.Dispose();
    }
}

internal sealed unsafe class VulkanUpscaleBridgeSidecar : IDisposable
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
    private bool _disposed;
    private Instance _instance;
    private Device _device;
    private Queue _graphicsQueue;
    private CommandPool _commandPool;
    private VulkanUpscaleBridgeFrameSlot[] _ownedSlots = [];
    private NvidiaDlssManager.Native.BridgeSession? _dlssSession;
    private IntelXessManager.Native.BridgeSession? _xessSession;

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

        uint streamlineGraphicsQueueIndex;
        uint streamlineComputeQueueIndex;
        uint streamlineOpticalFlowQueueIndex;
        _device = CreateDevice(
            _selectedDevice,
            additionalDeviceExtensions,
            additionalDeviceFeatureChain,
            additionalDeviceFeatures12,
            additionalDeviceFeatures13,
            dlssQueueRequirements,
            requirementsVendor == EVulkanUpscaleBridgeVendor.Xess,
            out streamlineGraphicsQueueIndex,
            out streamlineComputeQueueIndex,
            out streamlineOpticalFlowQueueIndex);
        _streamlineGraphicsQueueIndex = streamlineGraphicsQueueIndex;
        _streamlineComputeQueueIndex = streamlineComputeQueueIndex;
        _streamlineOpticalFlowQueueIndex = streamlineOpticalFlowQueueIndex;
        _graphicsQueue = GetGraphicsQueue(_selectedDevice.GraphicsQueueFamilyIndex);
        _commandPool = CreateCommandPool(_selectedDevice.GraphicsQueueFamilyIndex);

        if (!_api.TryGetDeviceExtension(_instance, _device, out _externalMemoryWin32))
            throw new InvalidOperationException("Failed to load VK_KHR_external_memory_win32 for the bridge sidecar.");
        if (!_api.TryGetDeviceExtension(_instance, _device, out _externalSemaphoreWin32))
            throw new InvalidOperationException("Failed to load VK_KHR_external_semaphore_win32 for the bridge sidecar.");
    }

    public string DeviceName => _selectedDevice.DeviceName;
    public uint VendorId => _selectedDevice.VendorId;
    public uint DeviceId => _selectedDevice.DeviceId;
    public int FrameSlotCount => FramesInFlight;
    public Instance Instance => _instance;
    public PhysicalDevice PhysicalDevice => _selectedDevice.Device;
    public Device Device => _device;
    public Queue GraphicsQueue => _graphicsQueue;
    public uint GraphicsQueueFamilyIndex => _selectedDevice.GraphicsQueueFamilyIndex;
    public uint GraphicsQueueIndex => 0;
    public uint StreamlineGraphicsQueueIndex => _streamlineGraphicsQueueIndex;
    public uint StreamlineComputeQueueIndex => _streamlineComputeQueueIndex;
    public uint StreamlineOpticalFlowQueueIndex => _streamlineOpticalFlowQueueIndex;

    public void WaitForFrameSlotAvailability(VulkanUpscaleBridgeFrameSlot slot)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(VulkanUpscaleBridgeSidecar));

        Fence submitFence = slot.SubmitFence;
        Result waitResult = _api.WaitForFences(_device, 1, in submitFence, true, ulong.MaxValue);
        if (waitResult != Result.Success)
            throw new InvalidOperationException($"Failed to wait for bridge slot {slot.SlotIndex} availability ({waitResult}).");
    }

    public VulkanUpscaleBridgeFrameSlot[] CreateFrameSlots(OpenGLRenderer renderer, VulkanUpscaleBridgeFrameResources frameResources, string viewportTag)
    {
        VulkanUpscaleBridgeFrameSlot[] slots = new VulkanUpscaleBridgeFrameSlot[FramesInFlight];
        for (int slotIndex = 0; slotIndex < slots.Length; slotIndex++)
        {
            string slotTag = $"{viewportTag}.Slot{slotIndex}";
            CommandBuffer commandBuffer = AllocateCommandBuffer();
            Fence submitFence = CreateFence();

            VulkanUpscaleBridgeSharedImage sourceColor = CreateSharedImage(
                renderer,
                slotTag,
                EVulkanUpscaleBridgeSurfaceKind.SourceColor,
                (uint)frameResources.InternalWidth,
                (uint)frameResources.InternalHeight,
                EPixelInternalFormat.Rgba16f,
                EPixelFormat.Rgba,
                EPixelType.HalfFloat,
                ESizedInternalFormat.Rgba16f,
                EFrameBufferAttachment.ColorAttachment0,
                ImageUsageFlags.TransferDstBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.SampledBit | ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.StorageBit,
                ImageAspectFlags.ColorBit,
                ImageAspectFlags.ColorBit,
                linearFilter: true);

            VulkanUpscaleBridgeSharedImage sourceDepth = CreateSharedImage(
                renderer,
                slotTag,
                EVulkanUpscaleBridgeSurfaceKind.SourceDepth,
                (uint)frameResources.InternalWidth,
                (uint)frameResources.InternalHeight,
                EPixelInternalFormat.Depth24Stencil8,
                EPixelFormat.DepthStencil,
                EPixelType.UnsignedInt248,
                ESizedInternalFormat.Depth24Stencil8,
                EFrameBufferAttachment.DepthStencilAttachment,
                ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit | ImageUsageFlags.DepthStencilAttachmentBit,
                ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit,
                ImageAspectFlags.DepthBit,
                linearFilter: false);

            VulkanUpscaleBridgeSharedImage sourceMotion = CreateSharedImage(
                renderer,
                slotTag,
                EVulkanUpscaleBridgeSurfaceKind.SourceMotion,
                (uint)frameResources.InternalWidth,
                (uint)frameResources.InternalHeight,
                EPixelInternalFormat.RG16f,
                EPixelFormat.Rg,
                EPixelType.HalfFloat,
                ESizedInternalFormat.Rg16f,
                EFrameBufferAttachment.ColorAttachment0,
                ImageUsageFlags.TransferDstBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.SampledBit | ImageUsageFlags.ColorAttachmentBit,
                ImageAspectFlags.ColorBit,
                ImageAspectFlags.ColorBit,
                linearFilter: false);

            VulkanUpscaleBridgeSharedImage exposure = CreateSharedImage(
                renderer,
                slotTag,
                EVulkanUpscaleBridgeSurfaceKind.Exposure,
                1u,
                1u,
                EPixelInternalFormat.R32f,
                EPixelFormat.Red,
                EPixelType.Float,
                ESizedInternalFormat.R32f,
                EFrameBufferAttachment.ColorAttachment0,
                ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit | ImageUsageFlags.ColorAttachmentBit,
                ImageAspectFlags.ColorBit,
                ImageAspectFlags.ColorBit,
                linearFilter: false);

            bool outputHdr = frameResources.OutputHdr;
            VulkanUpscaleBridgeSharedImage outputColor = CreateSharedImage(
                renderer,
                slotTag,
                EVulkanUpscaleBridgeSurfaceKind.OutputColor,
                (uint)frameResources.DisplayWidth,
                (uint)frameResources.DisplayHeight,
                outputHdr ? EPixelInternalFormat.Rgba16f : EPixelInternalFormat.Rgba8,
                EPixelFormat.Rgba,
                outputHdr ? EPixelType.HalfFloat : EPixelType.UnsignedByte,
                outputHdr ? ESizedInternalFormat.Rgba16f : ESizedInternalFormat.Rgba8,
                EFrameBufferAttachment.ColorAttachment0,
                ImageUsageFlags.TransferDstBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.SampledBit | ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.StorageBit,
                ImageAspectFlags.ColorBit,
                ImageAspectFlags.ColorBit,
                linearFilter: true);

            VulkanUpscaleBridgeSharedSemaphore readySemaphore = CreateSharedSemaphore(renderer, $"{slotTag}.Ready");
            VulkanUpscaleBridgeSharedSemaphore completeSemaphore = CreateSharedSemaphore(renderer, $"{slotTag}.Complete");

            slots[slotIndex] = new VulkanUpscaleBridgeFrameSlot(
                slotIndex,
                sourceColor,
                sourceDepth,
                sourceMotion,
                exposure,
                outputColor,
                readySemaphore,
                completeSemaphore,
                commandBuffer,
                submitFence);
        }

            _ownedSlots = slots;
        return slots;
    }

    public void SubmitNoOpHandoff(VulkanUpscaleBridgeFrameSlot slot)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(VulkanUpscaleBridgeSidecar));

        Fence submitFence = slot.SubmitFence;
        _api.ResetFences(_device, 1, in submitFence);
        _api.ResetCommandBuffer(slot.CommandBuffer, 0);

        CommandBufferBeginInfo beginInfo = new()
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };

        if (_api.BeginCommandBuffer(slot.CommandBuffer, in beginInfo) != Result.Success)
            throw new InvalidOperationException($"Failed to begin bridge handoff command buffer for slot {slot.SlotIndex}.");

        if (_api.EndCommandBuffer(slot.CommandBuffer) != Result.Success)
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

        if (_api.QueueSubmit(_graphicsQueue, 1, &submitInfo, slot.SubmitFence) != Result.Success)
            throw new InvalidOperationException($"Failed to submit bridge handoff for slot {slot.SlotIndex}.");
    }

    public void SubmitPassthroughBlit(VulkanUpscaleBridgeFrameSlot slot)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(VulkanUpscaleBridgeSidecar));

        Fence submitFence = slot.SubmitFence;
        _api.ResetFences(_device, 1, in submitFence);
        _api.ResetCommandBuffer(slot.CommandBuffer, 0);

        CommandBufferBeginInfo beginInfo = new()
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };

        if (_api.BeginCommandBuffer(slot.CommandBuffer, in beginInfo) != Result.Success)
            throw new InvalidOperationException($"Failed to begin bridge passthrough command buffer for slot {slot.SlotIndex}.");

        TransitionImageLayout(
            slot.CommandBuffer,
            slot.SourceColor,
            ImageLayout.TransferSrcOptimal,
            PipelineStageFlags.TransferBit,
            AccessFlags.TransferReadBit);
        TransitionImageLayout(
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

        TransitionImageLayout(
            slot.CommandBuffer,
            slot.SourceColor,
            ImageLayout.General,
            PipelineStageFlags.AllCommandsBit,
            AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit);
        TransitionImageLayout(
            slot.CommandBuffer,
            slot.OutputColor,
            ImageLayout.General,
            PipelineStageFlags.AllCommandsBit,
            AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit);

        if (_api.EndCommandBuffer(slot.CommandBuffer) != Result.Success)
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

        if (_api.QueueSubmit(_graphicsQueue, 1, &submitInfo, slot.SubmitFence) != Result.Success)
            throw new InvalidOperationException($"Failed to submit bridge passthrough blit for slot {slot.SlotIndex}.");
    }

    public bool SubmitVendorUpscale(
        VulkanUpscaleBridgeFrameSlot slot,
        in VulkanUpscaleBridgeDispatchParameters parameters,
        out string failureReason)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(VulkanUpscaleBridgeSidecar));

        failureReason = string.Empty;

        Fence submitFence = slot.SubmitFence;
        _api.ResetFences(_device, 1, in submitFence);
        _api.ResetCommandBuffer(slot.CommandBuffer, 0);

        CommandBufferBeginInfo beginInfo = new()
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };

        if (_api.BeginCommandBuffer(slot.CommandBuffer, in beginInfo) != Result.Success)
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

        if (_api.EndCommandBuffer(slot.CommandBuffer) != Result.Success)
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

        if (_api.QueueSubmit(_graphicsQueue, 1, &submitInfo, slot.SubmitFence) != Result.Success)
        {
            ResetVendorSession(parameters.Vendor);
            failureReason = $"Failed to submit bridge {parameters.Vendor} dispatch for slot {slot.SlotIndex}.";
            return false;
        }

        return true;
    }

    public void RecordTransitionImageLayout(
        CommandBuffer commandBuffer,
        VulkanUpscaleBridgeSharedImage image,
        ImageLayout newLayout,
        PipelineStageFlags dstStage,
        AccessFlags dstAccessMask)
        => TransitionImageLayout(commandBuffer, image, newLayout, dstStage, dstAccessMask);

    private bool EnsureDlssSession(out string failureReason)
    {
        failureReason = string.Empty;

        if (_dlssSession is not null)
            return true;

        if (!NvidiaDlssManager.Native.TryCreateBridgeSession(this, _streamlineViewportId, out _dlssSession, out failureReason)
            || _dlssSession is null)
        {
            _dlssSession = null;
            return false;
        }

        return true;
    }

    private bool EnsureXessSession(in VulkanUpscaleBridgeDispatchParameters parameters, out string failureReason)
    {
        failureReason = string.Empty;

        if (_xessSession is not null)
            return true;

        if (!IntelXessManager.Native.TryCreateBridgeSession(this, in parameters, out _xessSession, out failureReason)
            || _xessSession is null)
        {
            _xessSession = null;
            return false;
        }

        return true;
    }

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

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (_device.Handle != 0)
        {
            _api.DeviceWaitIdle(_device);
            _xessSession?.Dispose();
            _xessSession = null;
            _dlssSession?.Dispose();
            _dlssSession = null;
            for (int i = _ownedSlots.Length - 1; i >= 0; i--)
                _ownedSlots[i].DestroyVulkanResources(_api, _device);
            _ownedSlots = [];

            if (_commandPool.Handle != 0)
                _api.DestroyCommandPool(_device, _commandPool, null);
            _api.DestroyDevice(_device, null);
            _device = default;
        }

        if (_instance.Handle != 0)
        {
            _api.DestroyInstance(_instance, null);
            _instance = default;
        }

        _xessSession = null;
        _dlssSession = null;
    }

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

        bool preferDlss = Engine.Rendering.VulkanUpscaleBridgeSnapshot.DlssFirst;
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

        float* queuePriorities = stackalloc float[(int)queueCount];
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

    private static bool TryConvertFeatureFieldValueViaOperators(object rawValue, Type intermediateType, out bool value)
    {
        if (TryInvokeUserDefinedConversion(rawValue, intermediateType, out object? convertedValue))
            return TryConvertFeatureFieldValue(convertedValue, out value);

        value = false;
        return false;
    }

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

    private Queue GetGraphicsQueue(uint queueFamilyIndex)
    {
        _api.GetDeviceQueue(_device, queueFamilyIndex, 0, out Queue queue);
        return queue;
    }

    private CommandPool CreateCommandPool(uint queueFamilyIndex)
    {
        CommandPoolCreateInfo createInfo = new()
        {
            SType = StructureType.CommandPoolCreateInfo,
            QueueFamilyIndex = queueFamilyIndex,
            Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
        };

        if (_api.CreateCommandPool(_device, in createInfo, null, out CommandPool commandPool) != Result.Success)
            throw new InvalidOperationException("Failed to create the Vulkan bridge sidecar command pool.");

        return commandPool;
    }

    private CommandBuffer AllocateCommandBuffer()
    {
        CommandBufferAllocateInfo allocateInfo = new()
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = _commandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1,
        };

        CommandBuffer commandBuffer = default;
        if (_api.AllocateCommandBuffers(_device, in allocateInfo, out commandBuffer) != Result.Success)
            throw new InvalidOperationException("Failed to allocate a Vulkan bridge handoff command buffer.");

        return commandBuffer;
    }

    private Fence CreateFence()
    {
        FenceCreateInfo createInfo = new()
        {
            SType = StructureType.FenceCreateInfo,
            Flags = FenceCreateFlags.SignaledBit,
        };

        if (_api.CreateFence(_device, in createInfo, null, out Fence fence) != Result.Success)
            throw new InvalidOperationException("Failed to create a Vulkan bridge submit fence.");

        return fence;
    }

    private VulkanUpscaleBridgeSharedSemaphore CreateSharedSemaphore(OpenGLRenderer renderer, string name)
    {
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

        if (_api.CreateSemaphore(_device, in createInfo, null, out VkSemaphore semaphore) != Result.Success)
            throw new InvalidOperationException($"Failed to create bridge semaphore '{name}'.");

        SemaphoreGetWin32HandleInfoKHR handleInfo = new()
        {
            SType = StructureType.SemaphoreGetWin32HandleInfoKhr,
            Semaphore = semaphore,
            HandleType = ExternalSemaphoreHandleTypeFlags.OpaqueWin32Bit,
        };

        IntPtr exportedHandle = IntPtr.Zero;
        if (_externalSemaphoreWin32.GetSemaphoreWin32Handle(_device, &handleInfo, &exportedHandle) != Result.Success || exportedHandle == IntPtr.Zero)
            throw new InvalidOperationException($"Failed to export bridge semaphore handle '{name}'.");

        IntPtr glHandle = DuplicateHandleForImport(exportedHandle);
        uint glSemaphore = renderer.CreateImportedSemaphore((void*)glHandle);
        if (glSemaphore == 0)
            throw new InvalidOperationException($"Failed to import bridge semaphore '{name}' into OpenGL.");

        return new VulkanUpscaleBridgeSharedSemaphore(name, renderer, semaphore, glSemaphore);
    }

    private VulkanUpscaleBridgeSharedImage CreateSharedImage(
        OpenGLRenderer renderer,
        string slotTag,
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
        bool linearFilter)
    {
        string name = $"VulkanUpscaleBridge.{slotTag}.{kind}";
        Format vkFormat = MapFormat(internalFormat);

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

        if (_api.CreateImage(_device, in imageInfo, null, out Image image) != Result.Success)
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

        if (_api.AllocateMemory(_device, in allocateInfo, null, out DeviceMemory memory) != Result.Success)
            throw new InvalidOperationException($"Failed to allocate bridge memory for '{name}'.");

        if (_api.BindImageMemory(_device, image, memory, 0) != Result.Success)
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

        if (_api.CreateImageView(_device, in viewInfo, null, out ImageView imageView) != Result.Success)
            throw new InvalidOperationException($"Failed to create bridge image view for '{name}'.");

        MemoryGetWin32HandleInfoKHR handleInfo = new()
        {
            SType = StructureType.MemoryGetWin32HandleInfoKhr,
            Memory = memory,
            HandleType = ExternalMemoryHandleTypeFlags.OpaqueWin32Bit,
        };

        IntPtr exportedHandle = IntPtr.Zero;
        if (_externalMemoryWin32.GetMemoryWin32Handle(_device, &handleInfo, &exportedHandle) != Result.Success || exportedHandle == IntPtr.Zero)
            throw new InvalidOperationException($"Failed to export bridge memory handle for '{name}'.");

        IntPtr glHandle = DuplicateHandleForImport(exportedHandle);

        XRTexture2D texture = XRTexture2D.CreateFrameBufferTexture(width, height, internalFormat, pixelFormat, pixelType, attachment);
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

        if (renderer.GenericToAPI<GLTexture2D>(texture) is not GLTexture2D glTexture)
            throw new InvalidOperationException($"Failed to create the OpenGL wrapper for bridge texture '{name}'.");
        glTexture.Generate();

        XRFrameBuffer frameBuffer = new((texture, attachment, 0, -1))
        {
            Name = $"{name}.FBO",
        };
        if (renderer.GenericToAPI<GLFrameBuffer>(frameBuffer) is not GLFrameBuffer glFrameBuffer)
            throw new InvalidOperationException($"Failed to create the OpenGL wrapper for bridge framebuffer '{name}'.");
        glFrameBuffer.Generate();

        return new VulkanUpscaleBridgeSharedImage(name, kind, image, memory, imageView, vkFormat, aspectMask, viewAspectMask, usage, texture, frameBuffer);
    }

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

    private void TransitionImageLayout(
        CommandBuffer commandBuffer,
        VulkanUpscaleBridgeSharedImage image,
        ImageLayout newLayout,
        PipelineStageFlags dstStage,
        AccessFlags dstAccessMask)
    {
        ImageLayout oldLayout = image.CurrentLayout;
        if (oldLayout == newLayout)
            return;

        PipelineStageFlags srcStage = ResolvePipelineStage(oldLayout);
        AccessFlags srcAccessMask = ResolveAccessMask(oldLayout);

        ImageMemoryBarrier barrier = new()
        {
            SType = StructureType.ImageMemoryBarrier,
            SrcAccessMask = srcAccessMask,
            DstAccessMask = dstAccessMask,
            OldLayout = oldLayout,
            NewLayout = newLayout,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image.VulkanImage,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = image.AspectMask,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1,
            },
        };

        ImageMemoryBarrier* barrierPtr = stackalloc ImageMemoryBarrier[1];
        barrierPtr[0] = barrier;

        _api.CmdPipelineBarrier(
            commandBuffer,
            srcStage,
            dstStage,
            DependencyFlags.None,
            0,
            null,
            0,
            null,
            1,
            barrierPtr);

        image.CurrentLayout = newLayout;
    }

    private static PipelineStageFlags ResolvePipelineStage(ImageLayout layout)
        => layout switch
        {
            ImageLayout.Undefined => PipelineStageFlags.TopOfPipeBit,
            ImageLayout.TransferSrcOptimal or ImageLayout.TransferDstOptimal => PipelineStageFlags.TransferBit,
            ImageLayout.ColorAttachmentOptimal => PipelineStageFlags.ColorAttachmentOutputBit,
            ImageLayout.DepthStencilAttachmentOptimal or ImageLayout.DepthAttachmentOptimal => PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit,
            ImageLayout.ShaderReadOnlyOptimal => PipelineStageFlags.FragmentShaderBit,
            _ => PipelineStageFlags.AllCommandsBit,
        };

    private static AccessFlags ResolveAccessMask(ImageLayout layout)
        => layout switch
        {
            ImageLayout.Undefined => 0,
            ImageLayout.TransferSrcOptimal => AccessFlags.TransferReadBit,
            ImageLayout.TransferDstOptimal => AccessFlags.TransferWriteBit,
            ImageLayout.ColorAttachmentOptimal => AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit,
            ImageLayout.DepthStencilAttachmentOptimal or ImageLayout.DepthAttachmentOptimal
                => AccessFlags.DepthStencilAttachmentReadBit | AccessFlags.DepthStencilAttachmentWriteBit,
            ImageLayout.DepthStencilReadOnlyOptimal or ImageLayout.DepthReadOnlyOptimal
                => AccessFlags.DepthStencilAttachmentReadBit,
            ImageLayout.ShaderReadOnlyOptimal => AccessFlags.ShaderReadBit,
            _ => AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit,
        };

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
