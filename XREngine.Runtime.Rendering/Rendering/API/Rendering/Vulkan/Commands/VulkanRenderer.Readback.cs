using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using ImageMagick;
using Silk.NET.Vulkan;
using XREngine.Data;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Vulkan
{
    public unsafe partial class VulkanRenderer
    {
        // =========== Readback Task Management ===========

        private readonly object _pendingReadbacksLock = new();
        private readonly List<System.Threading.Tasks.Task> _pendingReadbackTasks = [];

        private void RegisterReadbackTask(System.Threading.Tasks.Task task)
        {
            lock (_pendingReadbacksLock)
                _pendingReadbackTasks.Add(task);

            _ = task.ContinueWith(
                completed =>
                {
                    lock (_pendingReadbacksLock)
                        _pendingReadbackTasks.Remove(completed);
                },
                System.Threading.CancellationToken.None,
                System.Threading.Tasks.TaskContinuationOptions.ExecuteSynchronously,
                System.Threading.Tasks.TaskScheduler.Default);
        }

        private void WaitForPendingReadbackTasks(TimeSpan timeout)
        {
            System.Threading.Tasks.Task[] pending;
            lock (_pendingReadbacksLock)
            {
                if (_pendingReadbackTasks.Count == 0)
                    return;

                pending = [.. _pendingReadbackTasks];
            }

            try
            {
                System.Threading.Tasks.Task.WaitAll(pending, timeout);
            }
            catch
            {
                // Best-effort shutdown path: lingering readbacks should not abort renderer teardown.
            }
        }

        internal bool TryReadBufferBytesForDiagnostics(XRDataBuffer? sourceBuffer, uint sourceByteOffset, Span<byte> destination, out string reason)
        {
            reason = "<missing>";

            if (sourceBuffer is null)
                return false;

            if (destination.Length == 0)
            {
                reason = "<empty>";
                return true;
            }

            ulong byteOffset = sourceByteOffset;
            ulong byteCount = (ulong)destination.Length;
            ulong sourceLength = sourceBuffer.Length;
            if (byteOffset >= sourceLength || byteCount > sourceLength - byteOffset)
            {
                reason = $"<out-of-range:{byteOffset}+{byteCount}/{sourceLength}>";
                return false;
            }

            if (!TryGetAPIRenderObject(sourceBuffer, out AbstractRenderAPIObject? apiObject) ||
                apiObject is not VkDataBuffer vkBuffer ||
                !vkBuffer.IsGenerated ||
                vkBuffer.BufferHandle is not { } sourceHandle ||
                sourceHandle.Handle == 0)
            {
                reason = "<no-generated-vulkan-buffer>";
                return false;
            }

            if (!vkBuffer.LastUsageFlags.HasFlag(BufferUsageFlags.TransferSrcBit))
            {
                reason = $"<missing-transfer-src:{vkBuffer.LastUsageFlags}>";
                return false;
            }

            var (stagingBuffer, stagingMemory) = CreateReadbackBuffer(byteCount);
            try
            {
                using (var scope = NewCommandScope())
                {
                    BufferMemoryBarrier sourceBarrier = new()
                    {
                        SType = StructureType.BufferMemoryBarrier,
                        SrcAccessMask =
                            AccessFlags.ShaderWriteBit |
                            AccessFlags.TransferWriteBit |
                            AccessFlags.MemoryWriteBit,
                        DstAccessMask = AccessFlags.TransferReadBit,
                        SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                        DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                        Buffer = sourceHandle,
                        Offset = byteOffset,
                        Size = byteCount,
                    };

                    CmdPipelineBarrierTracked(
                        scope.CommandBuffer,
                        PipelineStageFlags.AllCommandsBit,
                        PipelineStageFlags.TransferBit,
                        0,
                        0,
                        null,
                        1,
                        &sourceBarrier,
                        0,
                        null);

                    BufferCopy copy = new()
                    {
                        SrcOffset = byteOffset,
                        DstOffset = 0,
                        Size = byteCount,
                    };

                    CmdCopyBufferTracked(scope.CommandBuffer, sourceHandle, stagingBuffer, 1, &copy);
                }

                if (!TryMapReadbackMemory(stagingBuffer, stagingMemory, 0, byteCount, out void* mappedPtr))
                {
                    reason = "<map-failed>";
                    return false;
                }

                try
                {
                    new Span<byte>(mappedPtr, destination.Length).CopyTo(destination);
                    reason = "gpu";
                    return true;
                }
                finally
                {
                    UnmapBufferMemory(stagingBuffer, stagingMemory);
                }
            }
            catch (Exception ex)
            {
                reason = $"<{ex.GetType().Name}>";
                Debug.VulkanWarningEvery(
                    $"Vulkan.Readback.BufferDiagnostics.{RuntimeHelpers.GetHashCode(sourceBuffer)}.{sourceByteOffset}.{destination.Length}",
                    TimeSpan.FromSeconds(2),
                    "[VulkanCounters] failed diagnostic buffer readback buffer='{0}' offset={1} length={2}: {3}: {4}",
                    sourceBuffer.AttributeName ?? sourceBuffer.Target.ToString(),
                    sourceByteOffset,
                    destination.Length,
                    ex.GetType().Name,
                    ex.Message);
                return false;
            }
            finally
            {
                DestroyBuffer(stagingBuffer, stagingMemory);
            }
        }

        // =========== Luminance Readback ===========

        public override void CalcDotLuminanceAsync(XRTexture2D texture, Action<bool, float> callback, Vector3 luminance, bool genMipmapsNow = true)
        {
            // Compute luminance by reading back the smallest mipmap level (ideally 1x1).
            if (texture is null)
            {
                callback?.Invoke(false, 0f);
                return;
            }

            var vkTex = GenericToAPI<VkTexture2D>(texture);
            if (vkTex is null || !vkTex.IsGenerated)
            {
                callback?.Invoke(false, 0f);
                return;
            }

            if (genMipmapsNow && !vkTex.UsesAllocatorImage)
                texture.GenerateMipmapsGPU();
            else if (genMipmapsNow)
                LogPlannerMipReadbackFallback(texture.Name, isArray: false);

            // Synchronous path: read smallest mip and compute luminance
            if (CalcDotLuminance(texture, luminance, out float dotLuminance, false))
                callback?.Invoke(true, dotLuminance);
            else
                callback?.Invoke(false, 0f);
        }
        public override void CalcDotLuminanceAsync(XRTexture2DArray texture, Action<bool, float> callback, Vector3 luminance, bool genMipmapsNow = true)
        {
            // Compute luminance by reading back the smallest mipmap level from all layers.
            if (texture is null)
            {
                callback?.Invoke(false, 0f);
                return;
            }

            var vkTex = GenericToAPI<VkTexture2DArray>(texture);
            if (vkTex is null || !vkTex.IsGenerated)
            {
                callback?.Invoke(false, 0f);
                return;
            }

            if (genMipmapsNow && !vkTex.UsesAllocatorImage)
                texture.GenerateMipmapsGPU();
            else if (genMipmapsNow)
                LogPlannerMipReadbackFallback(texture.Name, isArray: true);

            // Synchronous path: read smallest mip and compute luminance
            if (CalcDotLuminance(texture, luminance, out float dotLuminance, false))
                callback?.Invoke(true, dotLuminance);
            else
                callback?.Invoke(false, 0f);
        }
        public override void CalcDotLuminanceFrontAsyncCompute(BoundingRectangle region, bool withTransparency, Vector3 luminance, Action<bool, float> callback)
        {
            // Vulkan doesn't have a separate compute path for this; delegate to the standard async implementation
            CalcDotLuminanceFrontAsync(region, withTransparency, luminance, callback);
        }
        public override void CalcDotLuminanceFrontAsync(BoundingRectangle region, bool withTransparency, Vector3 luminance, Action<bool, float> callback)
        {
            _ = withTransparency; // Vulkan path reads the opaque presented color source.

            if (!TryReadLastWindowPresentColorRegionRgba8(region, out byte[] rgba, out int width, out int height))
            {
                WarnUnsupportedPostPresentSwapchainReadback(nameof(CalcDotLuminanceFrontAsync));
                callback?.Invoke(false, 0f);
                return;
            }

            int pixelCount = width * height;
            if (pixelCount <= 0)
            {
                callback?.Invoke(false, 0f);
                return;
            }

            float accum = 0f;
            for (int i = 0; i < pixelCount; i++)
            {
                int index = i * 4;
                byte r = rgba[index + 0];
                byte g = rgba[index + 1];
                byte b = rgba[index + 2];
                accum += (r * luminance.X + g * luminance.Y + b * luminance.Z) / 255f;
            }

            callback?.Invoke(true, accum / pixelCount);
        }

        // =========== Depth Readback ===========

        public override float GetDepth(int x, int y)
        {
            XRFrameBuffer? boundReadFrameBuffer = ActiveBoundReadFrameBuffer;
            if (boundReadFrameBuffer is not null)
            {
                x = Math.Clamp(x, 0, Math.Max((int)boundReadFrameBuffer.Width - 1, 0));
                y = Math.Clamp(y, 0, Math.Max((int)boundReadFrameBuffer.Height - 1, 0));

                if (TryResolveBlitImage(
                        boundReadFrameBuffer,
                        _lastPresentedImageIndex,
                        GetReadBufferMode(),
                        wantColor: false,
                        wantDepth: true,
                        wantStencil: false,
                        out BlitImageInfo depthSource,
                        isSource: true) &&
                    TryReadDepthPixel(depthSource, x, y, out float fboDepth))
                {
                    return fboDepth;
                }

                Debug.VulkanWarningEvery(
                    "Vulkan.Readback.DepthBoundFboFailed",
                    TimeSpan.FromSeconds(1),
                    "[Vulkan] GetDepth fallback to swapchain: unable to resolve/read bound read framebuffer '{0}'.",
                    boundReadFrameBuffer.Name ?? "<unnamed>");
            }

            return TryReadSwapchainDepthPixel(x, y, out float depth)
                ? depth
                : 1.0f;
        }

        private bool TryReadSwapchainDepthPixel(int x, int y, out float depth)
        {
            depth = 1.0f;

            if (_swapchainDepthImage.Handle == 0)
                return false;

            // Clamp coordinates to valid range
            x = Math.Clamp(x, 0, Math.Max((int)swapChainExtent.Width - 1, 0));
            y = Math.Clamp(y, 0, Math.Max((int)swapChainExtent.Height - 1, 0));

            // Determine byte size based on depth format
            uint pixelSize = GetDepthFormatPixelSize(_swapchainDepthFormat);
            if (pixelSize == 0)
                return false;

            ulong bufferSize = pixelSize;

            var (stagingBuffer, stagingMemory) = CreateReadbackBuffer(bufferSize);

            try
            {
                using var scope = NewCommandScope();

                // Transition depth image to transfer source
                ImageMemoryBarrier toTransferBarrier = new()
                {
                    SType = StructureType.ImageMemoryBarrier,
                    OldLayout = ImageLayout.DepthStencilAttachmentOptimal,
                    NewLayout = ImageLayout.TransferSrcOptimal,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = _swapchainDepthImage,
                    SubresourceRange = new ImageSubresourceRange
                    {
                        AspectMask = ImageAspectFlags.DepthBit,
                        BaseMipLevel = 0,
                        LevelCount = 1,
                        BaseArrayLayer = 0,
                        LayerCount = 1,
                    },
                    SrcAccessMask = AccessFlags.DepthStencilAttachmentWriteBit,
                    DstAccessMask = AccessFlags.TransferReadBit,
                };

                CmdPipelineBarrierTracked(
                    scope.CommandBuffer,
                    PipelineStageFlags.LateFragmentTestsBit,
                    PipelineStageFlags.TransferBit,
                    0, 0, null, 0, null, 1, &toTransferBarrier);

                BufferImageCopy copy = new()
                {
                    BufferOffset = 0,
                    BufferRowLength = 0,
                    BufferImageHeight = 0,
                    ImageSubresource = new ImageSubresourceLayers
                    {
                        AspectMask = ImageAspectFlags.DepthBit,
                        MipLevel = 0,
                        BaseArrayLayer = 0,
                        LayerCount = 1,
                    },
                    ImageOffset = new Offset3D { X = x, Y = y, Z = 0 },
                    ImageExtent = new Extent3D { Width = 1, Height = 1, Depth = 1 }
                };

                CmdCopyImageToBufferTracked(
                    scope.CommandBuffer,
                    _swapchainDepthImage,
                    ImageLayout.TransferSrcOptimal,
                    stagingBuffer,
                    1,
                    &copy);

                // Transition depth image back to attachment optimal
                ImageMemoryBarrier toAttachmentBarrier = toTransferBarrier with
                {
                    OldLayout = ImageLayout.TransferSrcOptimal,
                    NewLayout = ImageLayout.DepthStencilAttachmentOptimal,
                    SrcAccessMask = AccessFlags.TransferReadBit,
                    DstAccessMask = AccessFlags.DepthStencilAttachmentWriteBit | AccessFlags.DepthStencilAttachmentReadBit,
                };

                CmdPipelineBarrierTracked(
                    scope.CommandBuffer,
                    PipelineStageFlags.TransferBit,
                    PipelineStageFlags.EarlyFragmentTestsBit,
                    0, 0, null, 0, null, 1, &toAttachmentBarrier);
            }
            catch
            {
                DestroyBuffer(stagingBuffer, stagingMemory);
                return false;
            }

            // Map and read depth value
            if (!TryMapReadbackMemory(stagingBuffer, stagingMemory, 0, bufferSize, out void* mappedPtr))
            {
                DestroyBuffer(stagingBuffer, stagingMemory);
                return false;
            }

            depth = ReadDepthValue(mappedPtr, _swapchainDepthFormat);

            UnmapBufferMemory(stagingBuffer, stagingMemory);
            DestroyBuffer(stagingBuffer, stagingMemory);

            return true;
        }

        public override void GetDepthAsync(XRFrameBuffer fbo, int x, int y, Action<float> depthCallback)
        {
            // Prefer reading from the provided FBO depth attachment when available.
            if (fbo is not null)
            {
                x = Math.Clamp(x, 0, Math.Max((int)fbo.Width - 1, 0));
                y = Math.Clamp(y, 0, Math.Max((int)fbo.Height - 1, 0));

                if (TryResolveBlitImage(
                        fbo,
                        _lastPresentedImageIndex,
                        GetReadBufferMode(),
                        wantColor: false,
                        wantDepth: true,
                        wantStencil: false,
                        out BlitImageInfo depthSource,
                        isSource: true) &&
                    TryReadDepthPixel(depthSource, x, y, out float depth))
                {
                    depthCallback?.Invoke(depth);
                    return;
                }
            }

            // Async fallback: read from swapchain depth image via fence + background poll.

            if (_swapchainDepthImage.Handle == 0)
            {
                depthCallback?.Invoke(1.0f);
                return;
            }

            // Clamp coordinates to valid range
            x = Math.Clamp(x, 0, (int)swapChainExtent.Width - 1);
            y = Math.Clamp(y, 0, (int)swapChainExtent.Height - 1);

            // Determine byte size based on depth format
            uint pixelSize = GetDepthFormatPixelSize(_swapchainDepthFormat);
            if (pixelSize == 0)
            {
                depthCallback?.Invoke(1.0f);
                return;
            }

            ulong bufferSize = pixelSize;

            // Create staging buffer
            var (stagingBuffer, stagingMemory) = CreateReadbackBuffer(bufferSize);

            // Allocate command buffer
            CommandBufferAllocateInfo allocateInfo = new()
            {
                SType = StructureType.CommandBufferAllocateInfo,
                Level = CommandBufferLevel.Primary,
                CommandPool = commandPool,
                CommandBufferCount = 1,
            };

            if (AllocateVulkanCommandBuffersTracked(ref allocateInfo, out CommandBuffer commandBuffer, "Readback.Depth") != Result.Success)
            {
                DestroyBuffer(stagingBuffer, stagingMemory);
                depthCallback?.Invoke(1.0f);
                return;
            }

            // Create a fence for this operation
            FenceCreateInfo fenceInfo = new()
            {
                SType = StructureType.FenceCreateInfo,
                Flags = 0, // Start unsignaled
            };

            if (Api!.CreateFence(device, ref fenceInfo, null, out Fence fence) != Result.Success)
            {
                FreeVulkanCommandBufferTracked(commandPool, ref commandBuffer, "Readback.RecordFailure");
                DestroyBuffer(stagingBuffer, stagingMemory);
                depthCallback?.Invoke(1.0f);
                return;
            }

            // Record commands
            CommandBufferBeginInfo beginInfo = new()
            {
                SType = StructureType.CommandBufferBeginInfo,
                Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
            };

            if (Api!.BeginCommandBuffer(commandBuffer, ref beginInfo) != Result.Success)
            {
                Api.DestroyFence(device, fence, null);
                FreeVulkanCommandBufferTracked(commandPool, ref commandBuffer, "Readback.BeginFailure");
                DestroyBuffer(stagingBuffer, stagingMemory);
                depthCallback?.Invoke(1.0f);
                return;
            }
            ResetCommandBufferBindState(commandBuffer);

            // Transition depth image to transfer source
            ImageMemoryBarrier toTransferBarrier = new()
            {
                SType = StructureType.ImageMemoryBarrier,
                OldLayout = ImageLayout.DepthStencilAttachmentOptimal,
                NewLayout = ImageLayout.TransferSrcOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = _swapchainDepthImage,
                SubresourceRange = new ImageSubresourceRange
                {
                    AspectMask = ImageAspectFlags.DepthBit,
                    BaseMipLevel = 0,
                    LevelCount = 1,
                    BaseArrayLayer = 0,
                    LayerCount = 1,
                },
                SrcAccessMask = AccessFlags.DepthStencilAttachmentWriteBit,
                DstAccessMask = AccessFlags.TransferReadBit,
            };

            CmdPipelineBarrierTracked(
                commandBuffer,
                PipelineStageFlags.LateFragmentTestsBit,
                PipelineStageFlags.TransferBit,
                0, 0, null, 0, null, 1, &toTransferBarrier);

            BufferImageCopy copy = new()
            {
                BufferOffset = 0,
                BufferRowLength = 0,
                BufferImageHeight = 0,
                ImageSubresource = new ImageSubresourceLayers
                {
                    AspectMask = ImageAspectFlags.DepthBit,
                    MipLevel = 0,
                    BaseArrayLayer = 0,
                    LayerCount = 1,
                },
                ImageOffset = new Offset3D { X = x, Y = y, Z = 0 },
                ImageExtent = new Extent3D { Width = 1, Height = 1, Depth = 1 }
            };

            CmdCopyImageToBufferTracked(
                commandBuffer,
                _swapchainDepthImage,
                ImageLayout.TransferSrcOptimal,
                stagingBuffer,
                1,
                &copy);

            // Transition depth image back to attachment optimal
            ImageMemoryBarrier toAttachmentBarrier = toTransferBarrier with
            {
                OldLayout = ImageLayout.TransferSrcOptimal,
                NewLayout = ImageLayout.DepthStencilAttachmentOptimal,
                SrcAccessMask = AccessFlags.TransferReadBit,
                DstAccessMask = AccessFlags.DepthStencilAttachmentWriteBit | AccessFlags.DepthStencilAttachmentReadBit,
            };

            CmdPipelineBarrierTracked(
                commandBuffer,
                PipelineStageFlags.TransferBit,
                PipelineStageFlags.EarlyFragmentTestsBit,
                0, 0, null, 0, null, 1, &toAttachmentBarrier);

            Api!.EndCommandBuffer(commandBuffer);

            // Submit with fence
            SubmitInfo submitInfo = new()
            {
                SType = StructureType.SubmitInfo,
                CommandBufferCount = 1,
                PCommandBuffers = &commandBuffer,
            };

            Result depthSubmitResult;
            lock (_oneTimeSubmitLock)
            {
                depthSubmitResult = SubmitToQueueTracked(graphicsQueue, ref submitInfo, fence);
            }

            if (depthSubmitResult != Result.Success)
            {
                if (depthSubmitResult == Result.ErrorDeviceLost)
                    MarkDeviceLost("Depth readback QueueSubmit returned ErrorDeviceLost");

                Api!.DestroyFence(device, fence, null);
                FreeVulkanCommandBufferTracked(commandPool, ref commandBuffer, "Readback.SubmitFailure");
                DestroyBuffer(stagingBuffer, stagingMemory);
                depthCallback?.Invoke(1.0f);
                return;
            }

            // Capture values for the async continuation
            var api = Api;
            var dev = device;
            var pool = commandPool;
            var depthFormat = _swapchainDepthFormat;
            var capturedCommandBuffer = commandBuffer; // Copy for lambda capture

            // Wait for the fence asynchronously on a background thread
            var readbackTask = System.Threading.Tasks.Task.Run(() =>
            {
                bool submissionCompleted = false;
                try
                {
                    // Poll the fence with a timeout to avoid blocking indefinitely
                    const ulong timeoutNs = 5_000_000_000; // 5 seconds
                    var result = api!.WaitForFences(dev, 1, ref fence, true, timeoutNs);

                    if (result != Result.Success)
                    {
                        if (result == Result.ErrorDeviceLost)
                            MarkDeviceLost("Depth readback WaitForFences returned ErrorDeviceLost");

                        depthCallback?.Invoke(1.0f);
                        return;
                    }

                    NotifyVulkanFenceCompleted(fence);
                    submissionCompleted = true;

                    // Map and read depth value
                    if (!TryMapReadbackMemory(stagingBuffer, stagingMemory, 0, bufferSize, out void* mappedPtr))
                    {
                        depthCallback?.Invoke(1.0f);
                        return;
                    }

                    float depth = ReadDepthValue(mappedPtr, depthFormat);
                    UnmapBufferMemory(stagingBuffer, stagingMemory);

                    depthCallback?.Invoke(depth);
                }
                finally
                {
                    if (!submissionCompleted)
                    {
                        Debug.VulkanWarning(
                            "[Vulkan.ResourceLifetime] Preserving timed-out depth-readback fence, command buffer, and staging buffer because GPU completion was not proven.");
                    }
                    else
                    {
                        // Cleanup resources only after the fence proves the submission completed.
                        api!.DestroyFence(dev, fence, null);

                        CommandBuffer cmdToFree = capturedCommandBuffer;
                        FreeVulkanCommandBufferTracked(pool, ref cmdToFree, "Readback.AsyncComplete");
                        DestroyBuffer(stagingBuffer, stagingMemory);
                    }
                }
            });

            RegisterReadbackTask(readbackTask);
        }

        /// <summary>
        /// Static helper to destroy a buffer without requiring instance access.
        /// Used for async cleanup on background threads.
        /// </summary>
        private static void DestroyBufferStatic(Vk api, Device device, Silk.NET.Vulkan.Buffer buffer, DeviceMemory memory)
        {
            api.DestroyBuffer(device, buffer, null);
            api.FreeMemory(device, memory, null);
        }

        // =========== Pixel Readback ===========

        private ImageLayout GetSwapchainReadbackLayout(uint readIndex)
        {
            bool wasPresented = _swapchainImageEverPresented is not null
                && readIndex < _swapchainImageEverPresented.Length
                && _swapchainImageEverPresented[readIndex];

            return wasPresented
                ? ImageLayout.PresentSrcKhr
                : ImageLayout.Undefined;
        }

        public override void GetPixelAsync(int x, int y, bool withTransparency, Action<ColorF4> colorCallback)
        {
            XRFrameBuffer? boundReadFrameBuffer = ActiveBoundReadFrameBuffer;
            if (boundReadFrameBuffer is not null)
            {
                x = Math.Clamp(x, 0, Math.Max((int)boundReadFrameBuffer.Width - 1, 0));
                y = Math.Clamp(y, 0, Math.Max((int)boundReadFrameBuffer.Height - 1, 0));

                if (TryResolveBlitImage(
                        boundReadFrameBuffer,
                        _lastPresentedImageIndex,
                        ActiveReadBufferMode,
                        wantColor: true,
                        wantDepth: false,
                        wantStencil: false,
                        out BlitImageInfo colorSource,
                        isSource: true) &&
                    TryReadColorPixel(colorSource, x, y, out ColorF4 color))
                {
                    colorCallback?.Invoke(color);
                    return;
                }

                Debug.VulkanWarningEvery(
                    "Vulkan.Readback.PixelBoundFboFailed",
                    TimeSpan.FromSeconds(1),
                    "[Vulkan] GetPixelAsync fallback to swapchain: unable to resolve/read bound read framebuffer '{0}'.",
                    boundReadFrameBuffer.Name ?? "<unnamed>");
            }

            if (!TryReadLastWindowPresentColorPixel(x, y, out ColorF4 fallbackColor))
            {
                WarnUnsupportedPostPresentSwapchainReadback(nameof(GetPixelAsync));
                colorCallback?.Invoke(ColorF4.Transparent);
                return;
            }

            if (!withTransparency)
                fallbackColor.A = 1.0f;

            colorCallback?.Invoke(fallbackColor);
        }

        // =========== Screenshot Readback ===========

        // Vulkan readback copies image rows into the order expected by Magick/PNG export.
        // Do not reuse FramebufferTextureYDirection here; that is shader sampling policy.
        public override bool ScreenshotRequiresVerticalFlip => false;

        public override void GetScreenshotAsync(BoundingRectangle region, bool withTransparency, Action<MagickImage, int> imageCallback)
        {
            if (TryQueueScreenshotReadback(
                    region,
                    withTransparency,
                    result => imageCallback?.Invoke(result.Image!, result.PixelCount),
                    out string? failure))
            {
                return;
            }

            Debug.VulkanWarning("[Vulkan] GetScreenshotAsync could not queue a nonblocking readback: {0}", failure ?? "unknown failure");
            imageCallback?.Invoke(null!, 0);
        }

        private bool TryReadLastWindowPresentColorRegionRgba8(BoundingRectangle region, out byte[] rgbaPixels, out int width, out int height)
        {
            rgbaPixels = [];
            width = 0;
            height = 0;
            using IDisposable? plannerScope = _lastWindowPresentFrameOpContext is { } context
                ? EnterFrameOpResourcePlannerReadbackScope(in context)
                : null;

            if (_lastWindowPresentFrameBuffer is not null)
            {
                ClampReadbackRegion(region, _lastWindowPresentFrameBuffer.Width, _lastWindowPresentFrameBuffer.Height, out int fboX, out int fboY, out int fboW, out int fboH);
                if (TryResolveBlitImage(
                        _lastWindowPresentFrameBuffer,
                        _lastPresentedImageIndex,
                        EReadBufferMode.ColorAttachment0,
                        wantColor: true,
                        wantDepth: false,
                        wantStencil: false,
                        out BlitImageInfo colorSource,
                        isSource: true) &&
                    TryReadColorRegionRgba8(colorSource, fboX, fboY, fboW, fboH, out rgbaPixels))
                {
                    width = fboW;
                    height = fboH;
                    return true;
                }
            }

            if (_lastWindowPresentColorTexture is not IFrameBufferAttachement textureAttachment)
                return false;

            ClampReadbackRegion(region, textureAttachment.Width, textureAttachment.Height, out int texX, out int texY, out int texW, out int texH);
            if (!TryResolveTextureBlitImage(
                    _lastWindowPresentColorTexture,
                    mipLevel: 0,
                    layerIndex: 0,
                    ImageAspectFlags.ColorBit,
                    ImageLayout.ShaderReadOnlyOptimal,
                    PipelineStageFlags.FragmentShaderBit,
                    AccessFlags.ShaderReadBit,
                    out BlitImageInfo textureSource) ||
                !TryReadColorRegionRgba8(textureSource, texX, texY, texW, texH, out rgbaPixels))
            {
                return false;
            }

            width = texW;
            height = texH;
            return true;
        }

        private bool TryReadLastWindowPresentColorPixel(int x, int y, out ColorF4 color)
        {
            color = ColorF4.Transparent;
            using IDisposable? plannerScope = _lastWindowPresentFrameOpContext is { } context
                ? EnterFrameOpResourcePlannerReadbackScope(in context)
                : null;

            if (_lastWindowPresentFrameBuffer is not null)
            {
                x = Math.Clamp(x, 0, Math.Max((int)_lastWindowPresentFrameBuffer.Width - 1, 0));
                y = Math.Clamp(y, 0, Math.Max((int)_lastWindowPresentFrameBuffer.Height - 1, 0));
                if (TryResolveBlitImage(
                        _lastWindowPresentFrameBuffer,
                        _lastPresentedImageIndex,
                        EReadBufferMode.ColorAttachment0,
                        wantColor: true,
                        wantDepth: false,
                        wantStencil: false,
                        out BlitImageInfo colorSource,
                        isSource: true) &&
                    TryReadColorPixel(colorSource, x, y, out color))
                {
                    return true;
                }
            }

            if (_lastWindowPresentColorTexture is not IFrameBufferAttachement textureAttachment)
                return false;

            x = Math.Clamp(x, 0, Math.Max((int)textureAttachment.Width - 1, 0));
            y = Math.Clamp(y, 0, Math.Max((int)textureAttachment.Height - 1, 0));
            return TryResolveTextureBlitImage(
                    _lastWindowPresentColorTexture,
                    mipLevel: 0,
                    layerIndex: 0,
                    ImageAspectFlags.ColorBit,
                    ImageLayout.ShaderReadOnlyOptimal,
                    PipelineStageFlags.FragmentShaderBit,
                    AccessFlags.ShaderReadBit,
                    out BlitImageInfo textureSource) &&
                TryReadColorPixel(textureSource, x, y, out color);
        }

        private static void WarnUnsupportedPostPresentSwapchainReadback(string operation)
            => Debug.VulkanWarningEvery(
                $"Vulkan.Readback.{operation}.PostPresentSwapchainUnsupported",
                TimeSpan.FromSeconds(2),
                "[Vulkan] {0} skipped post-present swapchain readback: presentable images cannot be used after vkQueuePresentKHR without a fresh acquire. Capture from a tracked render target instead.",
                operation);

        private static void ForceOpaqueAlpha(byte[] rgbaPixels)
        {
            for (int i = 3; i < rgbaPixels.Length; i += 4)
                rgbaPixels[i] = 255;
        }

        // =========== Dot Luminance Computation ===========

        public override bool CalcDotLuminance(XRTexture2DArray texture, Vector3 luminance, out float dotLuminance, bool genMipmapsNow)
        {
            dotLuminance = 0f;

            var vkTex = GenericToAPI<VkTexture2DArray>(texture);
            if (vkTex is null || !vkTex.IsGenerated)
                return false;

            if (genMipmapsNow && !vkTex.UsesAllocatorImage)
                texture.GenerateMipmapsGPU();
            else if (genMipmapsNow)
                LogPlannerMipReadbackFallback(texture.Name, isArray: true);

            int layerCount = (int)texture.Depth;
            if (layerCount <= 0)
                return false;

            int mipLevel = vkTex.UsesAllocatorImage
                ? 0
                : XRTexture.GetSmallestMipmapLevel(texture.Width, texture.Height, texture.SmallestAllowedMipmapLevel);

            // Read one RGBA pixel per layer from the smallest mip
            ulong pixelSize = 16; // sizeof(Vector4) = 4 floats
            ulong bufferSize = pixelSize * (ulong)layerCount;

            var (stagingBuffer, stagingMemory) = CreateReadbackBuffer(bufferSize);

            try
            {
                using var scope = NewCommandScope();

                // Transition image to transfer source
                vkTex.TransitionImageLayout(ImageLayout.ShaderReadOnlyOptimal, ImageLayout.TransferSrcOptimal);

                BufferImageCopy copy = new()
                {
                    BufferOffset = 0,
                    BufferRowLength = 0,
                    BufferImageHeight = 0,
                    ImageSubresource = new ImageSubresourceLayers
                    {
                        AspectMask = ImageAspectFlags.ColorBit,
                        MipLevel = (uint)mipLevel,
                        BaseArrayLayer = 0,
                        LayerCount = (uint)layerCount,
                    },
                    ImageOffset = new Offset3D { X = 0, Y = 0, Z = 0 },
                    ImageExtent = new Extent3D { Width = 1, Height = 1, Depth = 1 }
                };

                CmdCopyImageToBufferTracked(
                    scope.CommandBuffer,
                    vkTex.Image,
                    ImageLayout.TransferSrcOptimal,
                    stagingBuffer,
                    1,
                    &copy);

                vkTex.TransitionImageLayout(ImageLayout.TransferSrcOptimal, ImageLayout.ShaderReadOnlyOptimal);
            }
            catch
            {
                DestroyBuffer(stagingBuffer, stagingMemory);
                return false;
            }

            // Map and compute average luminance
            if (!TryMapReadbackMemory(stagingBuffer, stagingMemory, 0, bufferSize, out void* mappedPtr))
            {
                DestroyBuffer(stagingBuffer, stagingMemory);
                return false;
            }

            try
            {
                Vector4* samples = (Vector4*)mappedPtr;
                Vector3 accum = Vector3.Zero;
                for (int i = 0; i < layerCount; i++)
                {
                    Vector4 sample = samples[i];
                    if (float.IsNaN(sample.X) || float.IsNaN(sample.Y) || float.IsNaN(sample.Z))
                        return false;
                    accum += new Vector3(sample.X, sample.Y, sample.Z);
                }

                Vector3 average = accum / layerCount;
                dotLuminance = Vector3.Dot(average, luminance);
                return true;
            }
            finally
            {
                UnmapBufferMemory(stagingBuffer, stagingMemory);
                DestroyBuffer(stagingBuffer, stagingMemory);
            }
        }
        public override bool CalcDotLuminance(XRTexture2D texture, Vector3 luminance, out float dotLuminance, bool genMipmapsNow)
        {
            dotLuminance = 0f;

            var vkTex = GenericToAPI<VkTexture2D>(texture);
            if (vkTex is null || !vkTex.IsGenerated)
                return false;

            if (genMipmapsNow && !vkTex.UsesAllocatorImage)
                texture.GenerateMipmapsGPU();
            else if (genMipmapsNow)
                LogPlannerMipReadbackFallback(texture.Name, isArray: false);

            int mipLevel = vkTex.UsesAllocatorImage
                ? 0
                : XRTexture.GetSmallestMipmapLevel(texture.Width, texture.Height, texture.SmallestAllowedMipmapLevel);

            // Read a single RGBA pixel from the smallest mip
            ulong pixelSize = 16; // sizeof(Vector4) = 4 floats
            ulong bufferSize = pixelSize;

            var (stagingBuffer, stagingMemory) = CreateReadbackBuffer(bufferSize);

            try
            {
                using var scope = NewCommandScope();

                // Transition image to transfer source
                vkTex.TransitionImageLayout(ImageLayout.ShaderReadOnlyOptimal, ImageLayout.TransferSrcOptimal);

                BufferImageCopy copy = new()
                {
                    BufferOffset = 0,
                    BufferRowLength = 0,
                    BufferImageHeight = 0,
                    ImageSubresource = new ImageSubresourceLayers
                    {
                        AspectMask = ImageAspectFlags.ColorBit,
                        MipLevel = (uint)mipLevel,
                        BaseArrayLayer = 0,
                        LayerCount = 1,
                    },
                    ImageOffset = new Offset3D { X = 0, Y = 0, Z = 0 },
                    ImageExtent = new Extent3D { Width = 1, Height = 1, Depth = 1 }
                };

                CmdCopyImageToBufferTracked(
                    scope.CommandBuffer,
                    vkTex.Image,
                    ImageLayout.TransferSrcOptimal,
                    stagingBuffer,
                    1,
                    &copy);

                vkTex.TransitionImageLayout(ImageLayout.TransferSrcOptimal, ImageLayout.ShaderReadOnlyOptimal);
            }
            catch
            {
                DestroyBuffer(stagingBuffer, stagingMemory);
                return false;
            }

            // Map and read the pixel
            if (!TryMapReadbackMemory(stagingBuffer, stagingMemory, 0, bufferSize, out void* mappedPtr))
            {
                DestroyBuffer(stagingBuffer, stagingMemory);
                return false;
            }

            try
            {
                Vector4 sample = *(Vector4*)mappedPtr;
                if (float.IsNaN(sample.X) || float.IsNaN(sample.Y) || float.IsNaN(sample.Z))
                    return false;

                Vector3 rgb = new(sample.X, sample.Y, sample.Z);
                dotLuminance = Vector3.Dot(rgb, luminance);
                return true;
            }
            finally
            {
                UnmapBufferMemory(stagingBuffer, stagingMemory);
                DestroyBuffer(stagingBuffer, stagingMemory);
            }
        }

        private static void LogPlannerMipReadbackFallback(string? textureName, bool isArray)
            => Debug.VulkanWarningEvery(
                isArray
                    ? "Vulkan.LuminanceReadback.PlannerMip0Fallback2DArray"
                    : "Vulkan.LuminanceReadback.PlannerMip0Fallback2D",
                TimeSpan.FromSeconds(2),
                "[Vulkan] Luminance readback is sampling mip 0 for planner-backed {0}source texture '{1}' because render-graph mip generation is not available yet.",
                isArray ? "array " : string.Empty,
                textureName ?? "<unnamed>");

        // =========== Texture Mip Readback ===========

        public override bool TryReadTextureMipRgbaFloat(
            XRTexture texture,
            int mipLevel,
            int layerIndex,
            out float[]? rgbaFloats,
            out int width,
            out int height,
            out string failure)
            => TryReadTextureMipRgbaFloat(
                texture,
                mipLevel,
                layerIndex,
                ImageLayout.ShaderReadOnlyOptimal,
                PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.ComputeShaderBit,
                AccessFlags.MemoryReadBit,
                useExpectedLayoutWhenUntracked: false,
                out rgbaFloats,
                out width,
                out height,
                out failure);

        private bool TryReadTextureMipRgbaFloat(
            XRTexture texture,
            int mipLevel,
            int layerIndex,
            ImageLayout expectedSourceLayout,
            PipelineStageFlags expectedSourceStage,
            AccessFlags expectedSourceAccess,
            bool useExpectedLayoutWhenUntracked,
            out float[]? rgbaFloats,
            out int width,
            out int height,
            out string failure)
        {
            rgbaFloats = null;
            width = 0;
            height = 0;
            failure = string.Empty;

            if (!RuntimeEngine.IsRenderThread)
            {
                failure = "Readback unavailable off render thread";
                return false;
            }

            if (!TryResolveTextureMipReadbackSize(texture, out int baseWidth, out int baseHeight, out int layerCount, out bool multisample))
            {
                failure = "Unsupported texture type";
                return false;
            }

            if (multisample)
            {
                failure = "Multisample textures do not support mip readback";
                return false;
            }

            width = Math.Max(1, baseWidth >> Math.Max(0, mipLevel));
            height = Math.Max(1, baseHeight >> Math.Max(0, mipLevel));

            int clampedLayer = Math.Clamp(layerIndex, 0, Math.Max(0, layerCount - 1));

            if (!TryResolveTextureBlitImage(
                    texture,
                    Math.Max(0, mipLevel),
                    clampedLayer,
                    ImageAspectFlags.ColorBit,
                    expectedSourceLayout,
                    expectedSourceStage,
                    expectedSourceAccess,
                    out BlitImageInfo source))
            {
                failure = "Texture not uploaded";
                return false;
            }

            if (useExpectedLayoutWhenUntracked && source.PreferredLayout == ImageLayout.Undefined)
            {
                source = source.WithResolvedState(
                    source.Image,
                    expectedSourceLayout,
                    source.Extent);
            }

            if (IsDepthOrStencilFormat(source.Format))
            {
                if (!TryResolveTextureBlitImage(
                        texture,
                        Math.Max(0, mipLevel),
                        clampedLayer,
                        ImageAspectFlags.DepthBit,
                        ImageLayout.DepthStencilReadOnlyOptimal,
                        PipelineStageFlags.EarlyFragmentTestsBit |
                        PipelineStageFlags.LateFragmentTestsBit |
                        PipelineStageFlags.FragmentShaderBit |
                        PipelineStageFlags.ComputeShaderBit,
                        AccessFlags.DepthStencilAttachmentReadBit |
                        AccessFlags.ShaderReadBit |
                        AccessFlags.MemoryReadBit,
                        out source))
                {
                    failure = "Depth texture not uploaded";
                    return false;
                }

                if (!TryReadDepthRegionRgbaFloat(source, 0, 0, width, height, out rgbaFloats))
                {
                    failure = "Depth texture readback failed";
                    return false;
                }

                return true;
            }

            if (!TryReadColorRegionRgbaFloat(source, 0, 0, width, height, out rgbaFloats))
            {
                failure = "Texture readback failed";
                return false;
            }

            return true;
        }

        private static bool TryResolveTextureMipReadbackSize(
            XRTexture texture,
            out int width,
            out int height,
            out int layerCount,
            out bool multisample)
        {
            width = 0;
            height = 0;
            layerCount = 1;
            multisample = false;

            switch (texture)
            {
                case XRTexture2D tex2D:
                    width = checked((int)tex2D.Width);
                    height = checked((int)tex2D.Height);
                    multisample = tex2D.MultiSample;
                    return true;
                case XRTexture2DArray tex2DArray:
                    width = checked((int)tex2DArray.Width);
                    height = checked((int)tex2DArray.Height);
                    layerCount = checked((int)Math.Max(tex2DArray.Depth, 1u));
                    multisample = tex2DArray.MultiSample;
                    return true;
                case XRTexture2DView tex2DView:
                    width = checked((int)tex2DView.Width);
                    height = checked((int)tex2DView.Height);
                    multisample = tex2DView.Multisample;
                    return true;
                case XRTexture2DArrayView tex2DArrayView:
                    width = checked((int)tex2DArrayView.Width);
                    height = checked((int)tex2DArrayView.Height);
                    layerCount = checked((int)Math.Max(tex2DArrayView.NumLayers, 1u));
                    multisample = tex2DArrayView.Multisample;
                    return true;
                default:
                    return false;
            }
        }

        public override bool TryReadTexturePixelRgbaFloat(
            XRTexture texture,
            int mipLevel,
            int layerIndex,
            out Vector4 rgba,
            out string failure)
        {
            rgba = Vector4.Zero;
            if (!TryReadTextureMipRgbaFloat(texture, mipLevel, layerIndex, out float[]? rgbaFloats, out _, out _, out failure)
                || rgbaFloats is null
                || rgbaFloats.Length < 4)
            {
                failure = string.IsNullOrWhiteSpace(failure) ? "Texture readback failed" : failure;
                return false;
            }

            rgba = new Vector4(rgbaFloats[0], rgbaFloats[1], rgbaFloats[2], rgbaFloats[3]);
            return true;
        }

        internal bool TryCaptureTextureLayerToPng(
            XRTexture texture,
            int mipLevel,
            int layerIndex,
            ImageLayout expectedSourceLayout,
            PipelineStageFlags expectedSourceStage,
            AccessFlags expectedSourceAccess,
            string outputPath,
            out int width,
            out int height,
            out RenderedOutputCaptureMetrics? metrics,
            out string failure)
        {
            metrics = null;
            if (!TryReadTextureMipRgbaFloat(
                    texture,
                    mipLevel,
                    layerIndex,
                    expectedSourceLayout,
                    expectedSourceStage,
                    expectedSourceAccess,
                    useExpectedLayoutWhenUntracked: true,
                    out float[]? rgbaFloats,
                    out width,
                    out height,
                    out failure) ||
                rgbaFloats is null)
            {
                return false;
            }

            byte[] rgba8 = new byte[rgbaFloats.Length];
            for (int i = 0; i < rgbaFloats.Length; i++)
            {
                float value = Math.Clamp(rgbaFloats[i], 0.0f, 1.0f);
                rgba8[i] = (byte)MathF.Round(value * byte.MaxValue);
            }

            string fullPath = Path.GetFullPath(outputPath);
            string? directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            try
            {
                using var image = new MagickImage(rgba8, new MagickReadSettings
                {
                    Width = checked((uint)width),
                    Height = checked((uint)height),
                    Format = MagickFormat.Rgba,
                    Depth = 8,
                });
                image.Write(fullPath);
                metrics = StereoRenderedOutputMetrics.MeasureCapture(
                    rgbaFloats,
                    width,
                    height);
                File.WriteAllText(
                    fullPath + ".metrics.json",
                    System.Text.Json.JsonSerializer.Serialize(
                        metrics,
                        new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                failure = string.Empty;
                return true;
            }
            catch (Exception ex)
            {
                failure = $"PNG write failed: {ex.Message}";
                return false;
            }
        }
    }
}
