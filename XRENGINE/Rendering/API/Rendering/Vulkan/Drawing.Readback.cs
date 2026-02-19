using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
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

            if (genMipmapsNow)
                texture.GenerateMipmapsGPU();

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

            if (genMipmapsNow)
                texture.GenerateMipmapsGPU();

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
            // Read back the last presented swapchain image region on the GPU, then compute dot-luminance on CPU.
            // This is synchronous on a one-time command buffer but runs off the render thread.
            if (swapChainImages is null || swapChainImages.Length == 0)
            {
                callback?.Invoke(false, 0f);
                return;
            }

            _ = withTransparency; // Vulkan path always reads opaque swapchain output today.

            var extent = swapChainExtent;
            int x = Math.Max(0, region.X);
            int y = Math.Max(0, region.Y);
            int w = Math.Clamp(region.Width, 1, Math.Max(1, (int)extent.Width - x));
            int h = Math.Clamp(region.Height, 1, Math.Max(1, (int)extent.Height - y));

            ulong pixelStride = 4; // Assuming 8-bit RGBA swapchain formats (B8G8R8A8/R8G8B8A8).
            ulong bufferSize = (ulong)(w * h) * pixelStride;

            // Create a host-visible staging buffer to receive the image copy.
            var (stagingBuffer, stagingMemory) = CreateBuffer(
                bufferSize,
                BufferUsageFlags.TransferDstBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                null);

            try
            {
                using var scope = NewCommandScope();

                uint readIndex = _lastPresentedImageIndex % (uint)swapChainImages.Length;
                var image = swapChainImages[readIndex];

                // After swapchain recreation, new images start in UNDEFINED layout.
                // Only assume PresentSrcKhr if the image was actually presented.
                bool wasPresented = _swapchainImageEverPresented is not null
                    && readIndex < _swapchainImageEverPresented.Length
                    && _swapchainImageEverPresented[readIndex];

                ImageLayout srcLayout = wasPresented
                    ? ImageLayout.PresentSrcKhr
                    : ImageLayout.Undefined;

                // Transition to transfer src, copy, then transition back to present.
                TransitionSwapchainImage(scope.CommandBuffer, image, srcLayout, ImageLayout.TransferSrcOptimal);

                BufferImageCopy copy = new()
                {
                    BufferOffset = 0,
                    BufferRowLength = 0,
                    BufferImageHeight = 0,
                    ImageSubresource = new ImageSubresourceLayers
                    {
                        AspectMask = ImageAspectFlags.ColorBit,
                        MipLevel = 0,
                        BaseArrayLayer = 0,
                        LayerCount = 1,
                    },
                    ImageOffset = new Offset3D { X = x, Y = y, Z = 0 },
                    ImageExtent = new Extent3D { Width = (uint)w, Height = (uint)h, Depth = 1 }
                };

                Api!.CmdCopyImageToBuffer(
                    scope.CommandBuffer,
                    image,
                    ImageLayout.TransferSrcOptimal,
                    stagingBuffer,
                    1,
                    &copy);

                TransitionSwapchainImage(scope.CommandBuffer, image, ImageLayout.TransferSrcOptimal, ImageLayout.PresentSrcKhr);
            }
            catch
            {
                DestroyBuffer(stagingBuffer, stagingMemory);
                callback?.Invoke(false, 0f);
                return;
            }

            // Map and compute luminance on CPU.
            void* mappedPtr;
            if (Api!.MapMemory(device, stagingMemory, 0, bufferSize, 0, &mappedPtr) != Result.Success)
            {
                DestroyBuffer(stagingBuffer, stagingMemory);
                callback?.Invoke(false, 0f);
                return;
            }

            try
            {
                float accum = 0f;
                uint pixelCount = (uint)(w * h);
                byte* p = (byte*)mappedPtr;
                for (uint i = 0; i < pixelCount; i++)
                {
                    // Swapchain formats are BGRA; treat alpha as opaque for luminance.
                    byte b = p[i * pixelStride + 0];
                    byte g = p[i * pixelStride + 1];
                    byte r = p[i * pixelStride + 2];
                    accum += (r * luminance.X + g * luminance.Y + b * luminance.Z) / 255f;
                }

                float average = pixelCount > 0 ? accum / pixelCount : 0f;
                callback?.Invoke(true, average);
            }
            finally
            {
                Api!.UnmapMemory(device, stagingMemory);
                DestroyBuffer(stagingBuffer, stagingMemory);
            }
        }

        // =========== Depth Readback ===========

        public override float GetDepth(int x, int y)
        {
            // Synchronous depth readback from the swapchain depth buffer.
            // Note: This is slow and should be avoided in performance-critical code.
            if (_swapchainDepthImage.Handle == 0)
                return 1.0f;

            // Clamp coordinates to valid range
            x = Math.Clamp(x, 0, (int)swapChainExtent.Width - 1);
            y = Math.Clamp(y, 0, (int)swapChainExtent.Height - 1);

            // Determine byte size based on depth format
            uint pixelSize = GetDepthFormatPixelSize(_swapchainDepthFormat);
            if (pixelSize == 0)
                return 1.0f;

            ulong bufferSize = pixelSize;

            var (stagingBuffer, stagingMemory) = CreateBuffer(
                bufferSize,
                BufferUsageFlags.TransferDstBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                null);

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

                Api!.CmdPipelineBarrier(
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

                Api!.CmdCopyImageToBuffer(
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

                Api!.CmdPipelineBarrier(
                    scope.CommandBuffer,
                    PipelineStageFlags.TransferBit,
                    PipelineStageFlags.EarlyFragmentTestsBit,
                    0, 0, null, 0, null, 1, &toAttachmentBarrier);
            }
            catch
            {
                DestroyBuffer(stagingBuffer, stagingMemory);
                return 1.0f;
            }

            // Map and read depth value
            void* mappedPtr;
            if (Api!.MapMemory(device, stagingMemory, 0, bufferSize, 0, &mappedPtr) != Result.Success)
            {
                DestroyBuffer(stagingBuffer, stagingMemory);
                return 1.0f;
            }

            float depth = ReadDepthValue(mappedPtr, _swapchainDepthFormat);

            Api!.UnmapMemory(device, stagingMemory);
            DestroyBuffer(stagingBuffer, stagingMemory);

            return depth;
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
            var (stagingBuffer, stagingMemory) = CreateBuffer(
                bufferSize,
                BufferUsageFlags.TransferDstBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                null);

            // Allocate command buffer
            CommandBufferAllocateInfo allocateInfo = new()
            {
                SType = StructureType.CommandBufferAllocateInfo,
                Level = CommandBufferLevel.Primary,
                CommandPool = commandPool,
                CommandBufferCount = 1,
            };

            if (Api!.AllocateCommandBuffers(device, ref allocateInfo, out CommandBuffer commandBuffer) != Result.Success)
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
                Api!.FreeCommandBuffers(device, commandPool, 1, ref commandBuffer);
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

            Api!.BeginCommandBuffer(commandBuffer, ref beginInfo);

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

            Api!.CmdPipelineBarrier(
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

            Api!.CmdCopyImageToBuffer(
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

            Api!.CmdPipelineBarrier(
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
                depthSubmitResult = Api!.QueueSubmit(graphicsQueue, 1, ref submitInfo, fence);

            if (depthSubmitResult != Result.Success)
            {
                Api!.DestroyFence(device, fence, null);
                Api!.FreeCommandBuffers(device, commandPool, 1, ref commandBuffer);
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
                try
                {
                    // Poll the fence with a timeout to avoid blocking indefinitely
                    const ulong timeoutNs = 5_000_000_000; // 5 seconds
                    var result = api!.WaitForFences(dev, 1, ref fence, true, timeoutNs);

                    if (result != Result.Success)
                    {
                        depthCallback?.Invoke(1.0f);
                        return;
                    }

                    // Map and read depth value
                    void* mappedPtr;
                    if (api!.MapMemory(dev, stagingMemory, 0, bufferSize, 0, &mappedPtr) != Result.Success)
                    {
                        depthCallback?.Invoke(1.0f);
                        return;
                    }

                    float depth = ReadDepthValue(mappedPtr, depthFormat);
                    api!.UnmapMemory(dev, stagingMemory);

                    depthCallback?.Invoke(depth);
                }
                finally
                {
                    // Cleanup resources
                    api!.DestroyFence(dev, fence, null);

                    CommandBuffer cmdToFree = capturedCommandBuffer;
                    api!.FreeCommandBuffers(dev, pool, 1, ref cmdToFree);
                    DestroyBufferStatic(api, dev, stagingBuffer, stagingMemory);
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

        public override void GetPixelAsync(int x, int y, bool withTransparency, Action<ColorF4> colorCallback)
        {
            if (_boundReadFrameBuffer is not null)
            {
                x = Math.Clamp(x, 0, Math.Max((int)_boundReadFrameBuffer.Width - 1, 0));
                y = Math.Clamp(y, 0, Math.Max((int)_boundReadFrameBuffer.Height - 1, 0));

                if (TryResolveBlitImage(
                        _boundReadFrameBuffer,
                        _lastPresentedImageIndex,
                        _readBufferMode,
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
                    _boundReadFrameBuffer.Name ?? "<unnamed>");
            }

            // Read a single pixel from the last presented swapchain image.
            if (swapChainImages is null || swapChainImages.Length == 0)
            {
                colorCallback?.Invoke(ColorF4.Transparent);
                return;
            }

            _ = withTransparency; // Vulkan swapchain is always opaque

            // Clamp coordinates
            x = Math.Clamp(x, 0, (int)swapChainExtent.Width - 1);
            y = Math.Clamp(y, 0, (int)swapChainExtent.Height - 1);

            ulong pixelStride = 4; // BGRA8
            ulong bufferSize = pixelStride;

            var (stagingBuffer, stagingMemory) = CreateBuffer(
                bufferSize,
                BufferUsageFlags.TransferDstBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                null);

            try
            {
                using var scope = NewCommandScope();

                var image = swapChainImages[_lastPresentedImageIndex % (uint)swapChainImages.Length];

                TransitionSwapchainImage(scope.CommandBuffer, image, ImageLayout.PresentSrcKhr, ImageLayout.TransferSrcOptimal);

                BufferImageCopy copy = new()
                {
                    BufferOffset = 0,
                    BufferRowLength = 0,
                    BufferImageHeight = 0,
                    ImageSubresource = new ImageSubresourceLayers
                    {
                        AspectMask = ImageAspectFlags.ColorBit,
                        MipLevel = 0,
                        BaseArrayLayer = 0,
                        LayerCount = 1,
                    },
                    ImageOffset = new Offset3D { X = x, Y = y, Z = 0 },
                    ImageExtent = new Extent3D { Width = 1, Height = 1, Depth = 1 }
                };

                Api!.CmdCopyImageToBuffer(
                    scope.CommandBuffer,
                    image,
                    ImageLayout.TransferSrcOptimal,
                    stagingBuffer,
                    1,
                    &copy);

                TransitionSwapchainImage(scope.CommandBuffer, image, ImageLayout.TransferSrcOptimal, ImageLayout.PresentSrcKhr);
            }
            catch
            {
                DestroyBuffer(stagingBuffer, stagingMemory);
                colorCallback?.Invoke(ColorF4.Transparent);
                return;
            }

            // Map and read pixel
            void* mappedPtr;
            if (Api!.MapMemory(device, stagingMemory, 0, bufferSize, 0, &mappedPtr) != Result.Success)
            {
                DestroyBuffer(stagingBuffer, stagingMemory);
                colorCallback?.Invoke(ColorF4.Transparent);
                return;
            }

            byte* p = (byte*)mappedPtr;
            // BGRA format
            float b = p[0] / 255f;
            float g = p[1] / 255f;
            float r = p[2] / 255f;
            float a = p[3] / 255f;

            Api!.UnmapMemory(device, stagingMemory);
            DestroyBuffer(stagingBuffer, stagingMemory);

            colorCallback?.Invoke(new ColorF4(r, g, b, a));
        }

        // =========== Screenshot Readback ===========

        public override void GetScreenshotAsync(BoundingRectangle region, bool withTransparency, Action<MagickImage, int> imageCallback)
        {
            _ = withTransparency; // Vulkan path always reads resolved color output.

            if (_boundReadFrameBuffer is not null)
            {
                ClampReadbackRegion(region, _boundReadFrameBuffer.Width, _boundReadFrameBuffer.Height, out int fboX, out int fboY, out int fboW, out int fboH);

                if (TryResolveBlitImage(
                        _boundReadFrameBuffer,
                        _lastPresentedImageIndex,
                        _readBufferMode,
                        wantColor: true,
                        wantDepth: false,
                        wantStencil: false,
                        out BlitImageInfo colorSource,
                        isSource: true) &&
                    TryReadColorRegionRgba8(colorSource, fboX, fboY, fboW, fboH, out byte[] rgbaPixels))
                {
                    try
                    {
                        var magickImage = new MagickImage(rgbaPixels, new MagickReadSettings
                        {
                            Width = (uint)fboW,
                            Height = (uint)fboH,
                            Format = MagickFormat.Rgba,
                            Depth = 8
                        });

                        imageCallback?.Invoke(magickImage, fboW * fboH);
                        return;
                    }
                    catch (Exception ex)
                    {
                        Debug.VulkanWarning($"GetScreenshotAsync failed to create image from read FBO: {ex.Message}");
                        imageCallback?.Invoke(null!, 0);
                        return;
                    }
                }

                Debug.VulkanWarningEvery(
                    "Vulkan.Readback.ScreenshotBoundFboFailed",
                    TimeSpan.FromSeconds(1),
                    "[Vulkan] GetScreenshotAsync fallback to swapchain: unable to resolve/read bound read framebuffer '{0}'.",
                    _boundReadFrameBuffer.Name ?? "<unnamed>");
            }

            // Fallback: capture from the last presented swapchain image.
            if (swapChainImages is null || swapChainImages.Length == 0)
            {
                imageCallback?.Invoke(null!, 0);
                return;
            }

            ClampReadbackRegion(region, swapChainExtent.Width, swapChainExtent.Height, out int x, out int y, out int w, out int h);

            ulong pixelStride = 4; // BGRA8
            ulong bufferSize = (ulong)(w * h) * pixelStride;

            var (stagingBuffer, stagingMemory) = CreateBuffer(
                bufferSize,
                BufferUsageFlags.TransferDstBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                null);

            try
            {
                using var scope = NewCommandScope();

                var image = swapChainImages[_lastPresentedImageIndex % (uint)swapChainImages.Length];

                TransitionSwapchainImage(scope.CommandBuffer, image, ImageLayout.PresentSrcKhr, ImageLayout.TransferSrcOptimal);

                BufferImageCopy copy = new()
                {
                    BufferOffset = 0,
                    BufferRowLength = 0,
                    BufferImageHeight = 0,
                    ImageSubresource = new ImageSubresourceLayers
                    {
                        AspectMask = ImageAspectFlags.ColorBit,
                        MipLevel = 0,
                        BaseArrayLayer = 0,
                        LayerCount = 1,
                    },
                    ImageOffset = new Offset3D { X = x, Y = y, Z = 0 },
                    ImageExtent = new Extent3D { Width = (uint)w, Height = (uint)h, Depth = 1 }
                };

                Api!.CmdCopyImageToBuffer(
                    scope.CommandBuffer,
                    image,
                    ImageLayout.TransferSrcOptimal,
                    stagingBuffer,
                    1,
                    &copy);

                TransitionSwapchainImage(scope.CommandBuffer, image, ImageLayout.TransferSrcOptimal, ImageLayout.PresentSrcKhr);
            }
            catch
            {
                DestroyBuffer(stagingBuffer, stagingMemory);
                imageCallback?.Invoke(null!, 0);
                return;
            }

            void* mappedPtr;
            if (Api!.MapMemory(device, stagingMemory, 0, bufferSize, 0, &mappedPtr) != Result.Success)
            {
                DestroyBuffer(stagingBuffer, stagingMemory);
                imageCallback?.Invoke(null!, 0);
                return;
            }

            try
            {
                byte[] pixels = new byte[(int)bufferSize];
                Marshal.Copy((IntPtr)mappedPtr, pixels, 0, (int)bufferSize);

                // Swapchain path reads BGRA8, while Magick expects RGBA.
                for (int i = 0; i < pixels.Length; i += 4)
                    (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);

                var magickImage = new MagickImage(pixels, new MagickReadSettings
                {
                    Width = (uint)w,
                    Height = (uint)h,
                    Format = MagickFormat.Rgba,
                    Depth = 8
                });

                imageCallback?.Invoke(magickImage, w * h);
            }
            catch (Exception ex)
            {
                Debug.VulkanWarning($"GetScreenshotAsync failed to create image: {ex.Message}");
                imageCallback?.Invoke(null!, 0);
            }
            finally
            {
                Api!.UnmapMemory(device, stagingMemory);
                DestroyBuffer(stagingBuffer, stagingMemory);
            }
        }

        // =========== Dot Luminance Computation ===========

        public override bool CalcDotLuminance(XRTexture2DArray texture, Vector3 luminance, out float dotLuminance, bool genMipmapsNow)
        {
            dotLuminance = 0f;

            var vkTex = GenericToAPI<VkTexture2DArray>(texture);
            if (vkTex is null || !vkTex.IsGenerated)
                return false;

            if (genMipmapsNow)
                texture.GenerateMipmapsGPU();

            int layerCount = (int)texture.Depth;
            if (layerCount <= 0)
                return false;

            int mipLevel = XRTexture.GetSmallestMipmapLevel(texture.Width, texture.Height, texture.SmallestAllowedMipmapLevel);

            // Read one RGBA pixel per layer from the smallest mip
            ulong pixelSize = 16; // sizeof(Vector4) = 4 floats
            ulong bufferSize = pixelSize * (ulong)layerCount;

            var (stagingBuffer, stagingMemory) = CreateBuffer(
                bufferSize,
                BufferUsageFlags.TransferDstBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                null);

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

                Api!.CmdCopyImageToBuffer(
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
            void* mappedPtr;
            if (Api!.MapMemory(device, stagingMemory, 0, bufferSize, 0, &mappedPtr) != Result.Success)
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
                Api!.UnmapMemory(device, stagingMemory);
                DestroyBuffer(stagingBuffer, stagingMemory);
            }
        }
        public override bool CalcDotLuminance(XRTexture2D texture, Vector3 luminance, out float dotLuminance, bool genMipmapsNow)
        {
            dotLuminance = 0f;

            var vkTex = GenericToAPI<VkTexture2D>(texture);
            if (vkTex is null || !vkTex.IsGenerated)
                return false;

            if (genMipmapsNow)
                texture.GenerateMipmapsGPU();

            int mipLevel = XRTexture.GetSmallestMipmapLevel(texture.Width, texture.Height, texture.SmallestAllowedMipmapLevel);

            // Read a single RGBA pixel from the smallest mip
            ulong pixelSize = 16; // sizeof(Vector4) = 4 floats
            ulong bufferSize = pixelSize;

            var (stagingBuffer, stagingMemory) = CreateBuffer(
                bufferSize,
                BufferUsageFlags.TransferDstBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                null);

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

                Api!.CmdCopyImageToBuffer(
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
            void* mappedPtr;
            if (Api!.MapMemory(device, stagingMemory, 0, bufferSize, 0, &mappedPtr) != Result.Success)
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
                Api!.UnmapMemory(device, stagingMemory);
                DestroyBuffer(stagingBuffer, stagingMemory);
            }
        }

        // =========== Texture Mip Readback ===========

        public override bool TryReadTextureMipRgbaFloat(
            XRTexture texture,
            int mipLevel,
            int layerIndex,
            out float[]? rgbaFloats,
            out int width,
            out int height,
            out string failure)
        {
            rgbaFloats = null;
            width = 0;
            height = 0;
            failure = string.Empty;

            if (!Engine.IsRenderThread)
            {
                failure = "Readback unavailable off render thread";
                return false;
            }

            if (texture is XRTexture2D tex2D && tex2D.MultiSample)
            {
                failure = "Multisample textures do not support mip readback";
                return false;
            }

            if (texture is XRTexture2DArray tex2DArray && tex2DArray.MultiSample)
            {
                failure = "Multisample textures do not support mip readback";
                return false;
            }

            int baseWidth;
            int baseHeight;
            switch (texture)
            {
                case XRTexture2D t2d:
                    baseWidth = (int)t2d.Width;
                    baseHeight = (int)t2d.Height;
                    break;
                case XRTexture2DArray t2da:
                    baseWidth = (int)t2da.Width;
                    baseHeight = (int)t2da.Height;
                    break;
                default:
                    failure = "Unsupported texture type";
                    return false;
            }

            width = Math.Max(1, baseWidth >> Math.Max(0, mipLevel));
            height = Math.Max(1, baseHeight >> Math.Max(0, mipLevel));

            int clampedLayer = texture is XRTexture2DArray array
                ? Math.Clamp(layerIndex, 0, Math.Max(0, (int)array.Depth - 1))
                : 0;

            if (!TryResolveTextureBlitImage(
                    texture,
                    Math.Max(0, mipLevel),
                    clampedLayer,
                    ImageAspectFlags.ColorBit,
                    ImageLayout.ShaderReadOnlyOptimal,
                    PipelineStageFlags.AllCommandsBit,
                    AccessFlags.MemoryReadBit,
                    out BlitImageInfo source))
            {
                failure = "Texture not uploaded";
                return false;
            }

            if (!TryReadColorRegionRgbaFloat(source, 0, 0, width, height, out rgbaFloats))
            {
                failure = "Texture readback failed";
                return false;
            }

            return true;
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
    }
}
