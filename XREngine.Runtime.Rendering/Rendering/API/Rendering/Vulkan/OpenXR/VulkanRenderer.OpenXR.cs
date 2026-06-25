using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Rendering;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private const int OpenXrEyeResourcePlannerStateCount = 2;

    private readonly record struct OpenXrDepthTarget(
        Image Image,
        DeviceMemory Memory,
        ImageView View,
        Format Format,
        ImageAspectFlags Aspect);

    private readonly record struct OpenXrSwapchainImageViewCacheEntry(ImageView View, Format Format);

    private Image[]? _openXrSingleSwapchainImages;
    private ImageView[]? _openXrSingleSwapchainImageViews;
    private bool[]? _openXrSingleSwapchainImageEverPresented;
    private readonly Dictionary<ulong, OpenXrSwapchainImageViewCacheEntry> _openXrSwapchainImageViews = new();
    private OpenXrDepthTarget _openXrCachedDepthTarget;
    private Extent2D _openXrCachedDepthExtent;
    private int _openXrExternalSwapchainRenderDepth;
    private int _openXrExternalSwapchainPrewarmDepth;
    private readonly ResourcePlannerRuntimeState[] _openXrResourcePlannerStates = new ResourcePlannerRuntimeState[OpenXrEyeResourcePlannerStateCount];
    private readonly bool[] _hasOpenXrResourcePlannerStates = new bool[OpenXrEyeResourcePlannerStateCount];

    public override bool IsRenderingExternalSwapchainTarget => _openXrExternalSwapchainRenderDepth > 0;
    public override bool AllowSynchronousResourceUploads
        => _openXrExternalSwapchainPrewarmDepth > 0 || base.AllowSynchronousResourceUploads;

    public override bool TryGetExternalSwapchainTargetRegion(out BoundingRectangle region)
    {
        if (_openXrExternalSwapchainRenderDepth > 0 &&
            swapChainExtent.Width > 0 &&
            swapChainExtent.Height > 0)
        {
            region = new BoundingRectangle(
                0,
                0,
                (int)Math.Min(swapChainExtent.Width, (uint)int.MaxValue),
                (int)Math.Min(swapChainExtent.Height, (uint)int.MaxValue));
            return true;
        }

        region = default;
        return false;
    }

    internal bool TryRenderOpenXrEyeSwapchain(
        Image image,
        Format format,
        Extent2D extent,
        int resourcePlannerStateIndex,
        Action emitFrameOps)
    {
        if (image.Handle == 0 || extent.Width == 0 || extent.Height == 0)
            return false;

        if (!UseDynamicRenderingRenderTargets)
        {
            Debug.VulkanWarningEvery(
                $"OpenXR.Vulkan.DynamicRenderingRequired.{GetHashCode()}",
                TimeSpan.FromSeconds(1),
                "[OpenXR] Vulkan OpenXR eye rendering requires dynamic rendering render targets.");
            return false;
        }

        ImageView openXrImageView = default;
        CommandBuffer commandBuffer = default;
        bool commandBufferAllocated = false;
        bool drainedFrameOps = false;

        Image[]? previousSwapChainImages = swapChainImages;
        ImageView[]? previousSwapChainImageViews = swapChainImageViews;
        Framebuffer[]? previousSwapChainFramebuffers = swapChainFramebuffers;
        bool[]? previousSwapchainImageEverPresented = _swapchainImageEverPresented;
        Format previousSwapChainImageFormat = swapChainImageFormat;
        Extent2D previousSwapChainExtent = swapChainExtent;
        Image previousDepthImage = _swapchainDepthImage;
        DeviceMemory previousDepthMemory = _swapchainDepthMemory;
        ImageView previousDepthView = _swapchainDepthView;
        Format previousDepthFormat = _swapchainDepthFormat;
        ImageAspectFlags previousDepthAspect = _swapchainDepthAspect;
        _openXrExternalSwapchainRenderDepth++;

        try
        {
            WaitForSubmittedFrameSlotsAndDrainRetiredResources();

            openXrImageView = GetOrCreateOpenXrSwapchainImageView(image, format);
            OpenXrDepthTarget depthTarget = GetOrCreateOpenXrDepthTarget(extent);

            _openXrSingleSwapchainImages ??= new Image[1];
            _openXrSingleSwapchainImageViews ??= new ImageView[1];
            _openXrSingleSwapchainImageEverPresented ??= new bool[1];
            _openXrSingleSwapchainImages[0] = image;
            _openXrSingleSwapchainImageViews[0] = openXrImageView;
            _openXrSingleSwapchainImageEverPresented[0] = false;

            swapChainImages = _openXrSingleSwapchainImages;
            swapChainImageViews = _openXrSingleSwapchainImageViews;
            swapChainFramebuffers = null;
            _swapchainImageEverPresented = _openXrSingleSwapchainImageEverPresented;
            swapChainImageFormat = format;
            swapChainExtent = extent;
            _swapchainDepthImage = depthTarget.Image;
            _swapchainDepthMemory = depthTarget.Memory;
            _swapchainDepthView = depthTarget.View;
            _swapchainDepthFormat = depthTarget.Format;
            _swapchainDepthAspect = depthTarget.Aspect;

            using (EnterOpenXrResourcePlannerScope(resourcePlannerStateIndex))
            {
                emitFrameOps();

                FrameOp[] ops = DrainFrameOps(out _);
                drainedFrameOps = true;
                ops = FilterDiagnosticSkippedFrameOps(ops);
                if (ops.Length == 0)
                {
                    Debug.VulkanWarningEvery(
                        $"OpenXR.Vulkan.NoEyeFrameOps.{GetHashCode()}",
                        TimeSpan.FromSeconds(1),
                        "[OpenXR] Vulkan eye rendering produced no frame operations.");
                    return false;
                }

                ops = VulkanRenderGraphCompiler.SortFrameOps(ops, CompiledRenderGraph);
                _ = PrepareResourcePlannerForFrameOps(ops);

                commandBuffer = AllocateCommandBuffer(CommandBufferLevel.Primary, "OpenXR eye primary command buffer");
                commandBufferAllocated = true;

                _isRecordingCommandBuffer = true;
                try
                {
                    _ = RecordCommandBuffer(
                        imageIndex: 0,
                        commandBuffer,
                        dynamicUiBatchTextSecondaryCommandBuffer: default,
                        ops,
                        dynamicUiBatchTextOpCount: 0,
                        commandChainSchedule: null,
                        preserveSwapchainForOverlay: false,
                        recordedSwapchainWriteCount: out _,
                        transitionSwapchainToPresent: false);
                }
                finally
                {
                    _isRecordingCommandBuffer = false;
                }

                bool submitted = SubmitAndWaitOpenXrCommandBuffer(commandBuffer, out bool commandBufferCompleted);
                if (!commandBufferCompleted)
                    commandBufferAllocated = false;
                if (submitted)
                    ForceFlushCompletedNonImageRetiredResources();

                return submitted;
            }
        }
        catch (Exception ex)
        {
            if (!drainedFrameOps)
                _ = DrainFrameOps(out _);

            Debug.VulkanWarningEvery(
                $"OpenXR.Vulkan.RenderEyeFailed.{GetHashCode()}",
                TimeSpan.FromSeconds(1),
                "[OpenXR] Vulkan eye render failed: {0}",
                ex.Message);
            return false;
        }
        finally
        {
            _openXrExternalSwapchainRenderDepth--;

            swapChainImages = previousSwapChainImages;
            swapChainImageViews = previousSwapChainImageViews;
            swapChainFramebuffers = previousSwapChainFramebuffers;
            _swapchainImageEverPresented = previousSwapchainImageEverPresented;
            swapChainImageFormat = previousSwapChainImageFormat;
            swapChainExtent = previousSwapChainExtent;
            _swapchainDepthImage = previousDepthImage;
            _swapchainDepthMemory = previousDepthMemory;
            _swapchainDepthView = previousDepthView;
            _swapchainDepthFormat = previousDepthFormat;
            _swapchainDepthAspect = previousDepthAspect;

            if (commandBufferAllocated && commandBuffer.Handle != 0)
                Api!.FreeCommandBuffers(device, commandPool, 1, ref commandBuffer);
        }
    }

    internal bool TryCopyOpenXrEyeSwapchainImageToTexture(
        Image sourceImage,
        Format sourceFormat,
        Extent2D sourceExtent,
        XRTexture2D? destinationTexture,
        string destinationLabel)
    {
        if (sourceImage.Handle == 0 || sourceExtent.Width == 0 || sourceExtent.Height == 0 || destinationTexture is null)
            return false;

        try
        {
            if (GetOrCreateAPIRenderObject(destinationTexture, generateNow: true) is not IVkImageDescriptorSource destinationSource)
                return false;

            if (!destinationSource.TryEnsureDescriptorReadyForUse(
                    $"OpenXR Vulkan eye mirror copy ({destinationLabel})",
                    AllowSynchronousResourceUploads))
            {
                Debug.VulkanWarningEvery(
                    $"OpenXR.Vulkan.Mirror.DestinationNotReady.{GetHashCode()}.{destinationLabel}",
                    TimeSpan.FromSeconds(2),
                    "[OpenXR] Vulkan eye mirror target '{0}' is not descriptor-ready.",
                    destinationLabel);
                return false;
            }

            Image destinationImage = destinationSource.DescriptorImage;
            if (destinationImage.Handle == 0)
                return false;

            Extent2D destinationExtent = ResolveOpenXrMirrorDestinationExtent(destinationTexture, destinationSource);
            if (destinationExtent.Width == 0 || destinationExtent.Height == 0)
                return false;

            ImageLayout destinationOldLayout = ResolveOpenXrMirrorDestinationLayout(destinationSource);

            using CommandScope scope = NewCommandScope();
            CommandBuffer commandBuffer = scope.CommandBuffer;

            TransitionOpenXrMirrorImage(
                commandBuffer,
                sourceImage,
                sourceFormat,
                ImageLayout.ColorAttachmentOptimal,
                ImageLayout.TransferSrcOptimal,
                ImageAspectFlags.ColorBit);

            TransitionOpenXrMirrorImage(
                commandBuffer,
                destinationImage,
                destinationSource.DescriptorFormat,
                destinationOldLayout,
                ImageLayout.TransferDstOptimal,
                NormalizeOpenXrMirrorAspect(destinationSource.DescriptorFormat, destinationSource.DescriptorAspect));

            ImageBlit blit = new()
            {
                SrcSubresource = new ImageSubresourceLayers
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    MipLevel = 0,
                    BaseArrayLayer = 0,
                    LayerCount = 1
                },
                DstSubresource = new ImageSubresourceLayers
                {
                    AspectMask = NormalizeOpenXrMirrorAspect(destinationSource.DescriptorFormat, destinationSource.DescriptorAspect),
                    MipLevel = 0,
                    BaseArrayLayer = 0,
                    LayerCount = 1
                }
            };
            blit.SrcOffsets.Element0 = new Offset3D { X = 0, Y = 0, Z = 0 };
            blit.SrcOffsets.Element1 = new Offset3D
            {
                X = checked((int)Math.Min(sourceExtent.Width, (uint)int.MaxValue)),
                Y = checked((int)Math.Min(sourceExtent.Height, (uint)int.MaxValue)),
                Z = 1
            };
            blit.DstOffsets.Element0 = new Offset3D { X = 0, Y = 0, Z = 0 };
            blit.DstOffsets.Element1 = new Offset3D
            {
                X = checked((int)Math.Min(destinationExtent.Width, (uint)int.MaxValue)),
                Y = checked((int)Math.Min(destinationExtent.Height, (uint)int.MaxValue)),
                Z = 1
            };

            Api!.CmdBlitImage(
                commandBuffer,
                sourceImage,
                ImageLayout.TransferSrcOptimal,
                destinationImage,
                ImageLayout.TransferDstOptimal,
                1,
                ref blit,
                Filter.Linear);

            TransitionOpenXrMirrorImage(
                commandBuffer,
                sourceImage,
                sourceFormat,
                ImageLayout.TransferSrcOptimal,
                ImageLayout.ColorAttachmentOptimal,
                ImageAspectFlags.ColorBit);

            TransitionOpenXrMirrorImage(
                commandBuffer,
                destinationImage,
                destinationSource.DescriptorFormat,
                ImageLayout.TransferDstOptimal,
                ImageLayout.ShaderReadOnlyOptimal,
                NormalizeOpenXrMirrorAspect(destinationSource.DescriptorFormat, destinationSource.DescriptorAspect));

            if (destinationSource is IVkFrameBufferAttachmentSource attachmentSource)
                attachmentSource.UpdateAttachmentTrackedLayout(ImageLayout.ShaderReadOnlyOptimal, 0, 0);

            return true;
        }
        catch (Exception ex)
        {
            Debug.VulkanWarningEvery(
                $"OpenXR.Vulkan.Mirror.CopyFailed.{GetHashCode()}.{destinationLabel}",
                TimeSpan.FromSeconds(2),
                "[OpenXR] Vulkan eye mirror copy to '{0}' failed: {1}",
                destinationLabel,
                ex.Message);
            return false;
        }
    }

    internal void PrewarmOpenXrEyeSwapchainResources(
        Format format,
        Extent2D extent,
        int resourcePlannerStateIndex,
        Action emitFrameOps)
    {
        if (extent.Width == 0 || extent.Height == 0)
            return;

        Image[]? previousSwapChainImages = swapChainImages;
        ImageView[]? previousSwapChainImageViews = swapChainImageViews;
        Framebuffer[]? previousSwapChainFramebuffers = swapChainFramebuffers;
        bool[]? previousSwapchainImageEverPresented = _swapchainImageEverPresented;
        Format previousSwapChainImageFormat = swapChainImageFormat;
        Extent2D previousSwapChainExtent = swapChainExtent;
        Image previousDepthImage = _swapchainDepthImage;
        DeviceMemory previousDepthMemory = _swapchainDepthMemory;
        ImageView previousDepthView = _swapchainDepthView;
        Format previousDepthFormat = _swapchainDepthFormat;
        ImageAspectFlags previousDepthAspect = _swapchainDepthAspect;

        _openXrExternalSwapchainRenderDepth++;
        _openXrExternalSwapchainPrewarmDepth++;

        try
        {
            WaitForSubmittedFrameSlotsAndDrainRetiredResources();

            OpenXrDepthTarget depthTarget = GetOrCreateOpenXrDepthTarget(extent);

            _openXrSingleSwapchainImages ??= new Image[1];
            _openXrSingleSwapchainImageViews ??= new ImageView[1];
            _openXrSingleSwapchainImageEverPresented ??= new bool[1];
            _openXrSingleSwapchainImages[0] = default;
            _openXrSingleSwapchainImageViews[0] = default;
            _openXrSingleSwapchainImageEverPresented[0] = false;

            swapChainImages = _openXrSingleSwapchainImages;
            swapChainImageViews = _openXrSingleSwapchainImageViews;
            swapChainFramebuffers = null;
            _swapchainImageEverPresented = _openXrSingleSwapchainImageEverPresented;
            swapChainImageFormat = format;
            swapChainExtent = extent;
            _swapchainDepthImage = depthTarget.Image;
            _swapchainDepthMemory = depthTarget.Memory;
            _swapchainDepthView = depthTarget.View;
            _swapchainDepthFormat = depthTarget.Format;
            _swapchainDepthAspect = depthTarget.Aspect;

            using (EnterOpenXrResourcePlannerScope(resourcePlannerStateIndex))
            {
                emitFrameOps();

                FrameOp[] ops = DrainFrameOps(out _);
                ops = FilterDiagnosticSkippedFrameOps(ops);
                if (ops.Length == 0)
                    return;

                ops = VulkanRenderGraphCompiler.SortFrameOps(ops, CompiledRenderGraph);
                _ = PrepareResourcePlannerForFrameOps(ops);
                PrewarmOpenXrFrameOpResources(ops);
            }
        }
        catch (Exception ex)
        {
            _ = DrainFrameOps(out _);
            Debug.VulkanWarningEvery(
                $"OpenXR.Vulkan.PrewarmEyeFailed.{GetHashCode()}",
                TimeSpan.FromSeconds(1),
                "[OpenXR] Vulkan eye resource prewarm failed: {0}",
                ex.Message);
        }
        finally
        {
            _openXrExternalSwapchainPrewarmDepth--;
            _openXrExternalSwapchainRenderDepth--;

            swapChainImages = previousSwapChainImages;
            swapChainImageViews = previousSwapChainImageViews;
            swapChainFramebuffers = previousSwapChainFramebuffers;
            _swapchainImageEverPresented = previousSwapchainImageEverPresented;
            swapChainImageFormat = previousSwapChainImageFormat;
            swapChainExtent = previousSwapChainExtent;
            _swapchainDepthImage = previousDepthImage;
            _swapchainDepthMemory = previousDepthMemory;
            _swapchainDepthView = previousDepthView;
            _swapchainDepthFormat = previousDepthFormat;
            _swapchainDepthAspect = previousDepthAspect;
        }
    }

    private void PrewarmOpenXrFrameOpResources(FrameOp[] ops)
    {
        if (ops.Length == 0)
            return;

        Dictionary<VkMeshRenderer, int> meshDrawSlotsByRenderer = _refreshMeshDrawSlotsByRendererScratch;
        meshDrawSlotsByRenderer.Clear();
        meshDrawSlotsByRenderer.EnsureCapacity(_refreshMeshDrawSlotCapacityHint);

        static int GetMeshDrawUniformSlot(Dictionary<VkMeshRenderer, int> slots, VkMeshRenderer renderer)
        {
            slots.TryGetValue(renderer, out int slot);
            slots[renderer] = slot + 1;
            return slot;
        }

        for (int i = 0; i < ops.Length; i++)
        {
            if (ops[i] is not MeshDrawOp drawOp)
                continue;

            int drawUniformSlot = GetMeshDrawUniformSlot(meshDrawSlotsByRenderer, drawOp.Draw.Renderer);
            if (drawOp.Draw.Renderer.TryPrewarmFrameDataForRecording(drawOp.Draw, drawUniformSlot, out string reason))
                continue;

            Debug.VulkanWarningEvery(
                $"OpenXR.Vulkan.PrewarmDrawResourcesFailed.{GetHashCode()}.{drawOp.Draw.Renderer.GetHashCode()}.{drawUniformSlot}",
                TimeSpan.FromSeconds(1),
                "[OpenXR] Vulkan eye prewarm could not prepare draw resources for mesh='{0}' material='{1}' slot={2}: {3}",
                drawOp.Draw.Renderer.MeshRenderer.Mesh?.Name ?? "<unnamed mesh>",
                (drawOp.Draw.MaterialOverride ?? drawOp.Draw.Renderer.MeshRenderer.Material)?.Name ?? "<unnamed material>",
                drawUniformSlot,
                reason);
        }

        _refreshMeshDrawSlotCapacityHint = Math.Max(1, meshDrawSlotsByRenderer.Count);
    }

    private OpenXrResourcePlannerScope EnterOpenXrResourcePlannerScope(int stateIndex)
        => new(this, stateIndex);

    private static int NormalizeOpenXrResourcePlannerStateIndex(int stateIndex)
        => (uint)stateIndex < OpenXrEyeResourcePlannerStateCount ? stateIndex : 0;

    private readonly struct OpenXrResourcePlannerScope : IDisposable
    {
        private readonly VulkanRenderer _renderer;
        private readonly ResourcePlannerRuntimeState _previousState;
        private readonly int _stateIndex;

        public OpenXrResourcePlannerScope(VulkanRenderer renderer, int stateIndex)
        {
            _renderer = renderer;
            _stateIndex = NormalizeOpenXrResourcePlannerStateIndex(stateIndex);
            _previousState = renderer.CaptureResourcePlannerRuntimeState();
            ResourcePlannerRuntimeState openXrState = renderer._hasOpenXrResourcePlannerStates[_stateIndex]
                ? renderer._openXrResourcePlannerStates[_stateIndex]
                : ResourcePlannerRuntimeState.CreateEmpty();
            renderer.RestoreResourcePlannerRuntimeState(openXrState);
        }

        public void Dispose()
        {
            _renderer._openXrResourcePlannerStates[_stateIndex] = _renderer.CaptureResourcePlannerRuntimeState();
            _renderer._hasOpenXrResourcePlannerStates[_stateIndex] = true;
            _renderer.RestoreResourcePlannerRuntimeState(_previousState);
        }
    }

    internal bool TryClearOpenXrSwapchainImage(Image image, Extent2D extent, ColorF4 color)
    {
        if (image.Handle == 0 || extent.Width == 0 || extent.Height == 0)
            return false;

        try
        {
            using CommandScope scope = NewCommandScope();
            CommandBuffer commandBuffer = scope.CommandBuffer;

            ImageSubresourceRange range = new()
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1
            };

            ImageMemoryBarrier toTransfer = new()
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = 0,
                DstAccessMask = AccessFlags.TransferWriteBit,
                OldLayout = ImageLayout.Undefined,
                NewLayout = ImageLayout.TransferDstOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = image,
                SubresourceRange = range
            };

            CmdPipelineBarrierTracked(
                commandBuffer,
                PipelineStageFlags.TopOfPipeBit,
                PipelineStageFlags.TransferBit,
                0,
                0,
                null,
                0,
                null,
                1,
                &toTransfer);

            ClearColorValue clearColor = new(color.R, color.G, color.B, color.A);
            Api!.CmdClearColorImage(commandBuffer, image, ImageLayout.TransferDstOptimal, ref clearColor, 1, ref range);

            ImageMemoryBarrier toColorAttachment = new()
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = AccessFlags.TransferWriteBit,
                DstAccessMask = AccessFlags.ColorAttachmentReadBit,
                OldLayout = ImageLayout.TransferDstOptimal,
                NewLayout = ImageLayout.ColorAttachmentOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = image,
                SubresourceRange = range
            };

            CmdPipelineBarrierTracked(
                commandBuffer,
                PipelineStageFlags.TransferBit,
                PipelineStageFlags.ColorAttachmentOutputBit,
                0,
                0,
                null,
                0,
                null,
                1,
                &toColorAttachment);

            return true;
        }
        catch (Exception ex)
        {
            Debug.VulkanWarningEvery(
                $"OpenXR.Vulkan.ClearFailed.{GetHashCode()}",
                TimeSpan.FromSeconds(1),
                "[OpenXR] Vulkan swapchain diagnostic clear failed: {0}",
                ex.Message);
            return false;
        }
    }

    private static Extent2D ResolveOpenXrMirrorDestinationExtent(
        XRTexture2D destinationTexture,
        IVkImageDescriptorSource destinationSource)
    {
        if (destinationSource is IVkFrameBufferAttachmentSource attachmentSource &&
            attachmentSource.TryGetAttachmentExtent(0, 0, out Extent2D attachmentExtent) &&
            attachmentExtent.Width > 0 &&
            attachmentExtent.Height > 0)
        {
            return attachmentExtent;
        }

        return new Extent2D(
            Math.Max(destinationTexture.Width, 1u),
            Math.Max(destinationTexture.Height, 1u));
    }

    private static ImageLayout ResolveOpenXrMirrorDestinationLayout(IVkImageDescriptorSource destinationSource)
    {
        ImageLayout layout = ImageLayout.Undefined;
        if (destinationSource is IVkFrameBufferAttachmentSource attachmentSource)
            layout = attachmentSource.GetAttachmentTrackedLayout(0, 0);

        if (layout == ImageLayout.Undefined)
            layout = destinationSource.TrackedImageLayout;

        return layout;
    }

    private static ImageAspectFlags NormalizeOpenXrMirrorAspect(Format format, ImageAspectFlags aspect)
    {
        if (!IsDepthStencilFormat(format))
            return ImageAspectFlags.ColorBit;

        ImageAspectFlags normalized = aspect & (ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit);
        return normalized == ImageAspectFlags.None ? ImageAspectFlags.DepthBit : normalized;
    }

    private void TransitionOpenXrMirrorImage(
        CommandBuffer commandBuffer,
        Image image,
        Format format,
        ImageLayout oldLayout,
        ImageLayout newLayout,
        ImageAspectFlags aspectMask)
    {
        if (oldLayout == newLayout)
            return;

        OpenXrMirrorBarrierAccess(oldLayout, out AccessFlags srcAccess, out PipelineStageFlags srcStage);
        OpenXrMirrorBarrierAccess(newLayout, out AccessFlags dstAccess, out PipelineStageFlags dstStage);

        ImageMemoryBarrier barrier = new()
        {
            SType = StructureType.ImageMemoryBarrier,
            SrcAccessMask = srcAccess,
            DstAccessMask = dstAccess,
            OldLayout = oldLayout,
            NewLayout = newLayout,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = NormalizeOpenXrMirrorAspect(format, aspectMask),
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1
            }
        };

        CmdPipelineBarrierTracked(
            commandBuffer,
            srcStage,
            dstStage,
            DependencyFlags.None,
            0,
            null,
            0,
            null,
            1,
            &barrier);
    }

    private static void OpenXrMirrorBarrierAccess(
        ImageLayout layout,
        out AccessFlags access,
        out PipelineStageFlags stage)
    {
        switch (layout)
        {
            case ImageLayout.Undefined:
                access = 0;
                stage = PipelineStageFlags.TopOfPipeBit;
                break;
            case ImageLayout.ColorAttachmentOptimal:
                access = AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit;
                stage = PipelineStageFlags.ColorAttachmentOutputBit;
                break;
            case ImageLayout.TransferSrcOptimal:
                access = AccessFlags.TransferReadBit;
                stage = PipelineStageFlags.TransferBit;
                break;
            case ImageLayout.TransferDstOptimal:
                access = AccessFlags.TransferWriteBit;
                stage = PipelineStageFlags.TransferBit;
                break;
            case ImageLayout.ShaderReadOnlyOptimal:
                access = AccessFlags.ShaderReadBit;
                stage = PipelineStageFlags.FragmentShaderBit;
                break;
            case ImageLayout.General:
                access = AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit;
                stage = PipelineStageFlags.AllCommandsBit;
                break;
            default:
                access = AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit;
                stage = PipelineStageFlags.AllCommandsBit;
                break;
        }
    }

    private void WaitForSubmittedFrameSlotsAndDrainRetiredResources()
    {
        if (_frameSlotTimelineValues is null)
            return;

        for (int i = 0; i < _frameSlotTimelineValues.Length; i++)
        {
            ulong value = _frameSlotTimelineValues[i];
            if (value != 0)
                WaitForTimelineValue(_graphicsTimelineSemaphore, value);
        }

        ForceFlushCompletedNonImageRetiredResources();
    }

    private ImageView GetOrCreateOpenXrSwapchainImageView(Image image, Format format)
    {
        ulong key = image.Handle;
        if (_openXrSwapchainImageViews.TryGetValue(key, out OpenXrSwapchainImageViewCacheEntry cached))
        {
            if (cached.Format == format && cached.View.Handle != 0)
                return cached.View;

            if (cached.View.Handle != 0)
                Api!.DestroyImageView(device, cached.View, null);
            _openXrSwapchainImageViews.Remove(key);
        }

        ImageView imageView = CreateOpenXrSwapchainImageView(image, format);
        _openXrSwapchainImageViews[key] = new OpenXrSwapchainImageViewCacheEntry(imageView, format);
        return imageView;
    }

    private ImageView CreateOpenXrSwapchainImageView(Image image, Format format)
    {
        ImageViewCreateInfo viewInfo = new()
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = image,
            ViewType = ImageViewType.Type2D,
            Format = format,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1,
            }
        };

        if (Api!.CreateImageView(device, ref viewInfo, null, out ImageView imageView) != Result.Success)
            throw new InvalidOperationException("Failed to create OpenXR Vulkan swapchain image view.");

        return imageView;
    }

    private OpenXrDepthTarget GetOrCreateOpenXrDepthTarget(Extent2D extent)
    {
        if (_openXrCachedDepthTarget.Image.Handle != 0 &&
            _openXrCachedDepthExtent.Width == extent.Width &&
            _openXrCachedDepthExtent.Height == extent.Height)
        {
            return _openXrCachedDepthTarget;
        }

        DestroyOpenXrDepthTarget(_openXrCachedDepthTarget);
        _openXrCachedDepthTarget = CreateOpenXrDepthTarget(extent);
        _openXrCachedDepthExtent = extent;
        return _openXrCachedDepthTarget;
    }

    private OpenXrDepthTarget CreateOpenXrDepthTarget(Extent2D extent)
    {
        Format depthFormat = FindDepthFormat();
        ImageAspectFlags depthAspect = IsDepthStencilFormat(depthFormat)
            ? ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit
            : ImageAspectFlags.DepthBit;

        ImageCreateInfo imageInfo = new()
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Extent = new Extent3D(extent.Width, extent.Height, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Format = depthFormat,
            Tiling = ImageTiling.Optimal,
            InitialLayout = ImageLayout.Undefined,
            Usage = ImageUsageFlags.DepthStencilAttachmentBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit,
            Samples = SampleCountFlags.Count1Bit,
            SharingMode = SharingMode.Exclusive,
        };

        if (Api!.CreateImage(device, ref imageInfo, null, out Image depthImage) != Result.Success)
            throw new InvalidOperationException("Failed to create OpenXR Vulkan depth image.");

        VulkanMemoryAllocation allocation = AllocateImageMemoryWithFallback(depthImage, MemoryPropertyFlags.DeviceLocalBit);
        _imageAllocations[depthImage.Handle] = allocation;

        if (Api!.BindImageMemory(device, depthImage, allocation.Memory, allocation.Offset) != Result.Success)
        {
            _imageAllocations.TryRemove(depthImage.Handle, out _);
            FreeMemoryAllocation(allocation);
            Api!.DestroyImage(device, depthImage, null);
            throw new InvalidOperationException("Failed to bind OpenXR Vulkan depth image memory.");
        }

        ImageViewCreateInfo viewInfo = new()
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = depthImage,
            ViewType = ImageViewType.Type2D,
            Format = depthFormat,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = depthAspect,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1,
            }
        };

        if (Api!.CreateImageView(device, ref viewInfo, null, out ImageView depthView) != Result.Success)
        {
            _imageAllocations.TryRemove(depthImage.Handle, out VulkanMemoryAllocation removed);
            FreeMemoryAllocation(removed);
            Api!.DestroyImage(device, depthImage, null);
            throw new InvalidOperationException("Failed to create OpenXR Vulkan depth image view.");
        }

        return new OpenXrDepthTarget(depthImage, allocation.Memory, depthView, depthFormat, depthAspect);
    }

    private void DestroyOpenXrDepthTarget(OpenXrDepthTarget target)
    {
        if (target.View.Handle != 0)
            Api!.DestroyImageView(device, target.View, null);

        if (target.Image.Handle == 0)
            return;

        Api!.DestroyImage(device, target.Image, null);
        if (_imageAllocations.TryRemove(target.Image.Handle, out VulkanMemoryAllocation allocation))
            FreeMemoryAllocation(allocation);
        else if (target.Memory.Handle != 0)
            Api!.FreeMemory(device, target.Memory, null);
    }

    private void DestroyOpenXrRenderingResources()
    {
        DestroyOpenXrResourcePlannerState();

        foreach (OpenXrSwapchainImageViewCacheEntry entry in _openXrSwapchainImageViews.Values)
        {
            if (entry.View.Handle != 0)
                Api!.DestroyImageView(device, entry.View, null);
        }
        _openXrSwapchainImageViews.Clear();

        DestroyOpenXrDepthTarget(_openXrCachedDepthTarget);
        _openXrCachedDepthTarget = default;
        _openXrCachedDepthExtent = default;

        if (_openXrSingleSwapchainImages is not null)
            _openXrSingleSwapchainImages[0] = default;
        if (_openXrSingleSwapchainImageViews is not null)
            _openXrSingleSwapchainImageViews[0] = default;
        if (_openXrSingleSwapchainImageEverPresented is not null)
            _openXrSingleSwapchainImageEverPresented[0] = false;
    }

    private void DestroyOpenXrResourcePlannerState()
    {
        bool hasAnyState = false;
        for (int i = 0; i < _hasOpenXrResourcePlannerStates.Length; i++)
            hasAnyState |= _hasOpenXrResourcePlannerStates[i];

        if (!hasAnyState)
            return;

        ResourcePlannerRuntimeState previousState = CaptureResourcePlannerRuntimeState();
        WaitForAllInFlightWork();
        for (int i = 0; i < _openXrResourcePlannerStates.Length; i++)
        {
            if (!_hasOpenXrResourcePlannerStates[i])
                continue;

            RestoreResourcePlannerRuntimeState(_openXrResourcePlannerStates[i]);
            ReleaseDescriptorReferencesForPhysicalResourceDestruction($"OpenXrResourcePlannerStateDestroy.eye{i}");
            DrainAllRetiredDescriptorPools();
            _resourceAllocator.DestroyPhysicalImages(this);
            _resourceAllocator.DestroyPhysicalBuffers(this);
            _openXrResourcePlannerStates[i] = default;
            _hasOpenXrResourcePlannerStates[i] = false;
        }

        RestoreResourcePlannerRuntimeState(previousState);
    }

    private bool SubmitAndWaitOpenXrCommandBuffer(CommandBuffer commandBuffer, out bool commandBufferCompleted)
    {
        commandBufferCompleted = false;

        FenceCreateInfo fenceCreateInfo = new()
        {
            SType = StructureType.FenceCreateInfo,
            Flags = 0,
        };

        if (Api!.CreateFence(device, ref fenceCreateInfo, null, out Fence fence) != Result.Success)
            throw new InvalidOperationException("Failed to create OpenXR Vulkan submit fence.");

        try
        {
            SubmitInfo submitInfo = new()
            {
                SType = StructureType.SubmitInfo,
                CommandBufferCount = 1,
                PCommandBuffers = &commandBuffer,
            };

            Result submitResult;
            lock (_oneTimeSubmitLock)
                submitResult = SubmitToQueueTracked(graphicsQueue, ref submitInfo, fence);

            if (submitResult != Result.Success)
            {
                if (submitResult == Result.ErrorDeviceLost)
                    MarkDeviceLost("OpenXR Vulkan eye submit returned ErrorDeviceLost");

                Debug.VulkanWarning($"[OpenXR] Vulkan eye QueueSubmit failed: {submitResult}");
                return false;
            }

            Result waitResult = Api!.WaitForFences(device, 1, &fence, true, ulong.MaxValue);
            if (waitResult != Result.Success)
            {
                if (waitResult == Result.ErrorDeviceLost)
                    MarkDeviceLost("OpenXR Vulkan eye fence wait returned ErrorDeviceLost");

                Debug.VulkanWarning($"[OpenXR] Vulkan eye fence wait failed: {waitResult}");
                return false;
            }

            commandBufferCompleted = true;
            return true;
        }
        finally
        {
            if (commandBufferCompleted)
                Api!.DestroyFence(device, fence, null);
        }
    }
}
