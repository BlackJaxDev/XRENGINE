using System;
using System.Diagnostics;
using System.Numerics;
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

                CmdPipelineBarrierTracked(
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
            if (!TryMapReadbackMemory(stagingBuffer, stagingMemory, 0, bufferSize, out void* mappedPtr))
            {
                DestroyBuffer(stagingBuffer, stagingMemory);
                return 1.0f;
            }

            float depth = ReadDepthValue(mappedPtr, _swapchainDepthFormat);

            UnmapBufferMemory(stagingBuffer, stagingMemory);
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
            var (stagingBuffer, stagingMemory) = CreateReadbackBuffer(bufferSize);

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
                    // Cleanup resources
                    api!.DestroyFence(dev, fence, null);

                    CommandBuffer cmdToFree = capturedCommandBuffer;
                    api!.FreeCommandBuffers(dev, pool, 1, ref cmdToFree);
                    DestroyBuffer(stagingBuffer, stagingMemory);
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

        public override bool ScreenshotRequiresVerticalFlip
            => RenderClipSpacePolicy.FramebufferTextureYDirection(RuntimeGraphicsApiKind.Vulkan) == ERenderClipSpaceYDirection.YDown;

        public override void GetScreenshotAsync(BoundingRectangle region, bool withTransparency, Action<MagickImage, int> imageCallback)
        {
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
                    if (!withTransparency)
                        ForceOpaqueAlpha(rgbaPixels);

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

            if (!TryReadLastWindowPresentColorRegionRgba8(region, out byte[] pixels, out int w, out int h))
            {
                WarnUnsupportedPostPresentSwapchainReadback(nameof(GetScreenshotAsync));
                imageCallback?.Invoke(null!, 0);
                return;
            }

            try
            {
                if (!withTransparency)
                    ForceOpaqueAlpha(pixels);

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
        }

        private bool TryReadLastWindowPresentColorRegionRgba8(BoundingRectangle region, out byte[] rgbaPixels, out int width, out int height)
        {
            rgbaPixels = [];
            width = 0;
            height = 0;

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
                    PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.ComputeShaderBit,
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
