using System;
using ImageMagick;
using Extensions;
using Silk.NET.Vulkan;
using System.Numerics;
using System.Linq;
using System.Runtime.InteropServices;
using XREngine;
using XREngine.Data;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Vulkan
{
    public unsafe partial class VulkanRenderer
    {
        private float _materialUniformSecondsLive;

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

                var image = swapChainImages[_lastPresentedImageIndex % (uint)swapChainImages.Length];

                // Transition to transfer src, copy, then transition back to present.
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
            System.Threading.Tasks.Task.Run(() =>
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
        public override void MemoryBarrier(EMemoryBarrierMask mask)
        {
            if (mask == EMemoryBarrierMask.None)
                return;

            _state.RegisterMemoryBarrier(mask);
            MarkCommandBuffersDirty();
        }
        public override void ColorMask(bool red, bool green, bool blue, bool alpha)
        {
            _state.SetColorMask(red, green, blue, alpha);
            MarkCommandBuffersDirty();
        }

        public override void Blit(
            XRFrameBuffer? inFBO,
            XRFrameBuffer? outFBO,
            int inX, int inY, uint inW, uint inH,
            int outX, int outY, uint outW, uint outH,
            EReadBufferMode readBufferMode,
            bool colorBit, bool depthBit, bool stencilBit,
            bool linearFilter)
        {
            if (!colorBit && !depthBit && !stencilBit)
                return;

            if (inFBO is null && outFBO is null)
                return;

            if (inW == 0 || inH == 0 || outW == 0 || outH == 0)
                return;

            if (inFBO is not null)
            {
                EnsureFrameBufferRegistered(inFBO);
                EnsureFrameBufferAttachmentsRegistered(inFBO);
            }

            if (outFBO is not null)
            {
                EnsureFrameBufferRegistered(outFBO);
                EnsureFrameBufferAttachmentsRegistered(outFBO);
            }

            int passIndex = Engine.Rendering.State.CurrentRenderGraphPassIndex;
            EnqueueFrameOp(new BlitOp(
                EnsureValidPassIndex(passIndex, "Blit"),
                inFBO,
                outFBO,
                inX,
                inY,
                inW,
                inH,
                outX,
                outY,
                outW,
                outH,
                readBufferMode,
                colorBit,
                depthBit,
                stencilBit,
                linearFilter,
                CaptureFrameOpContext()));
        }
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
        public override void ClearColor(ColorF4 color)
        {
            _state.SetClearColor(color);
            MarkCommandBuffersDirty();
        }
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
        protected override AbstractRenderAPIObject CreateAPIRenderObject(GenericRenderObject renderObject)
            => renderObject switch
            {
                //Meshes
                XRMaterial data => new VkMaterial(this, data),
                XRMeshRenderer.BaseVersion data => new VkMeshRenderer(this, data),
                XRRenderProgramPipeline data => new VkRenderProgramPipeline(this, data),
                XRRenderProgram data => new VkRenderProgram(this, data),
                XRDataBuffer data => new VkDataBuffer(this, data),
                XRSampler s => new VkSampler(this, s),
                XRShader s => new VkShader(this, s),

                //FBOs
                XRRenderBuffer data => new VkRenderBuffer(this, data),
                XRFrameBuffer data => new VkFrameBuffer(this, data),

                //Texture 1D
                XRTexture1D data => new VkTexture1D(this, data),
                XRTexture1DArray data => new VkTexture1DArray(this, data),
                XRTextureViewBase data => new VkTextureView(this, data),

                //Texture 2D
                XRTexture2D data => new VkTexture2D(this, data),
                XRTexture2DArray data => new VkTexture2DArray(this, data),
                XRTextureRectangle data => new VkTextureRectangle(this, data),

                //Texture 3D
                XRTexture3D data => new VkTexture3D(this, data),

                //Texture Cube
                XRTextureCube data => new VkTextureCube(this, data),
                XRTextureCubeArray data => new VkTextureCubeArray(this, data),

                //Texture Buffer
                XRTextureBuffer data => new VkTextureBuffer(this, data),

                //Feedback
                XRRenderQuery data => new VkRenderQuery(this, data),
                XRTransformFeedback data => new VkTransformFeedback(this, data),

                _ => throw new InvalidOperationException($"Render object type {renderObject.GetType()} is not supported.")
            };
        public override void CropRenderArea(BoundingRectangle region)
        {
            _state.SetScissor(region);
            MarkCommandBuffersDirty();
        }
        public override void SetRenderArea(BoundingRectangle region)
        {
            _state.SetViewport(region);
            MarkCommandBuffersDirty();
        }

        private const int MAX_FRAMES_IN_FLIGHT = 2;

        /// <summary>
        /// Per-frame-slot retirement queue for buffer handles that cannot be destroyed
        /// immediately because a command buffer recorded during the same frame may still
        /// reference them.  Drained after <c>WaitForFences</c> signals that the slot's
        /// GPU work has completed.
        /// </summary>
        private readonly List<(Silk.NET.Vulkan.Buffer Buffer, DeviceMemory Memory)>[] _retiredBuffers =
            [new(), new()]; // length == MAX_FRAMES_IN_FLIGHT

        /// <summary>
        /// Queues a buffer+memory pair for deferred destruction.  The pair will be
        /// destroyed the next time this frame slot is reused (after the fence wait
        /// guarantees the GPU is done with it).
        /// </summary>
        internal void RetireBuffer(Silk.NET.Vulkan.Buffer buffer, DeviceMemory memory)
            => _retiredBuffers[currentFrame].Add((buffer, memory));

        /// <summary>
        /// Destroys all buffers that were retired during the last use of the current
        /// frame slot.  Called immediately after <c>WaitForFences</c>.
        /// </summary>
        private void DrainRetiredBuffers()
        {
            var list = _retiredBuffers[currentFrame];
            if (list.Count == 0)
                return;

            foreach (var (buf, mem) in list)
            {
                if (buf.Handle != 0)
                    Api!.DestroyBuffer(device, buf, null);
                if (mem.Handle != 0)
                    Api!.FreeMemory(device, mem, null);
            }
            list.Clear();
        }

        private int currentFrame = 0;
        private ulong _vkDebugFrameCounter = 0;

        protected override void WindowRenderCallback(double delta)
        {
            _vkDebugFrameCounter++;

            // The deferred rendering model means frame ops change every frame (they are drained
            // and consumed during recording). We MUST re-record the command buffer each frame to
            // pick up the latest ops. Without this, a frame where no EnqueueFrameOp was called
            // (e.g., pipeline threw before any op) would leave the dirty flag false and resubmit
            // a stale command buffer that references old ops. This also ensures DrainFrameOps()
            // always runs, preventing stale ops from accumulating in the queue.
            MarkCommandBuffersDirty();

            // Some platforms/drivers do not reliably emit out-of-date/suboptimal or resize callbacks
            // on every size transition. Proactively compare the live framebuffer size to the current
            // swapchain extent and trigger a rebuild when they diverge.
            var liveFramebufferSize = Window!.FramebufferSize;
            var liveWindowSize = Window.Size;
            uint liveSurfaceWidth = (uint)Math.Max(Math.Max(liveFramebufferSize.X, liveWindowSize.X), 0);
            uint liveSurfaceHeight = (uint)Math.Max(Math.Max(liveFramebufferSize.Y, liveWindowSize.Y), 0);

            if (liveSurfaceWidth > 0 && liveSurfaceHeight > 0 &&
                (liveSurfaceWidth != swapChainExtent.Width || liveSurfaceHeight != swapChainExtent.Height))
            {
                _frameBufferInvalidated = true;

                Debug.VulkanEvery(
                    $"Vulkan.Frame.{GetHashCode()}.SizeMismatch",
                    TimeSpan.FromSeconds(1),
                    "[Vulkan] Detected surface/swapchain size mismatch: WindowFB={0}x{1} Window={2}x{3} LiveSurface={4}x{5} Swapchain={6}x{7}. Scheduling swapchain recreate.",
                    liveFramebufferSize.X,
                    liveFramebufferSize.Y,
                    liveWindowSize.X,
                    liveWindowSize.Y,
                    liveSurfaceWidth,
                    liveSurfaceHeight,
                    swapChainExtent.Width,
                    swapChainExtent.Height);
            }

            // If the window resized (or other framebuffer-dependent state changed), rebuild swapchain resources
            // before we acquire/record/submit. Waiting until after present can cause visible stretching/borders.
            if (_frameBufferInvalidated)
            {
                _frameBufferInvalidated = false;
                RecreateSwapChain();
            }

            // 1. Wait for the previous frame to finish
            Api!.WaitForFences(device, 1, ref inFlightFences![currentFrame], true, ulong.MaxValue);

            // Now that the GPU has finished all work for this frame slot, destroy
            // buffers that were retired during its previous recording.
            DrainRetiredBuffers();

            // Helpful when tracking down DPI / resize issues.
            Debug.VulkanEvery(
                $"Vulkan.Frame.{GetHashCode()}.Sizes",
                TimeSpan.FromSeconds(1),
                "[Vulkan] Frame={0} WindowFB={1}x{2} Swapchain={3}x{4}",
                _vkDebugFrameCounter,
                Window.FramebufferSize.X,
                Window.FramebufferSize.Y,
                swapChainExtent.Width,
                swapChainExtent.Height);

            // 2. Acquire the next image from the swap chain
            uint imageIndex = 0;
            var result = khrSwapChain!.AcquireNextImage(device, swapChain, ulong.MaxValue, imageAvailableSemaphores![currentFrame], default, ref imageIndex);

            Debug.VulkanEvery(
                $"Vulkan.Frame.{GetHashCode()}.Acquire",
                TimeSpan.FromSeconds(1),
                "[Vulkan] Frame={0} InFlightSlot={1} AcquiredImage={2} LastPresented={3}",
                _vkDebugFrameCounter,
                currentFrame,
                imageIndex,
                _lastPresentedImageIndex);

            if (result == Result.ErrorOutOfDateKhr)
            {
                RecreateSwapChain();
                return;
            }
            else if (result == Result.SuboptimalKhr)
            {
                RecreateSwapChain();
                return;
            }
            else if (result != Result.Success && result != Result.SuboptimalKhr)
                throw new Exception("Failed to acquire swap chain image.");

            // 3. Check if a previous frame is using this image (i.e. there is its fence to wait on)
            if (imagesInFlight![imageIndex].Handle != default)
                Api!.WaitForFences(device, 1, ref imagesInFlight[imageIndex], true, ulong.MaxValue);
            
            // Mark the image as now being in use by this frame
            imagesInFlight[imageIndex] = inFlightFences[currentFrame];

            // 4. Record the command buffer
            // Note: This currently records a default pass (Clear + ImGui). 
            // Full integration with the engine's render queue happens via frame operations enqueued during the frame.
            EnsureCommandBufferRecorded(imageIndex);

            // 5. Submit the command buffer
            SubmitInfo submitInfo = new()
            {
                SType = StructureType.SubmitInfo,
            };

            var waitSemaphores = stackalloc[] { imageAvailableSemaphores[currentFrame] };
            var waitStages = stackalloc[] { PipelineStageFlags.ColorAttachmentOutputBit };
            var buffer = _commandBuffers![imageIndex];

            submitInfo = submitInfo with
            {
                WaitSemaphoreCount = 1,
                PWaitSemaphores = waitSemaphores,
                PWaitDstStageMask = waitStages,
                CommandBufferCount = 1,
                PCommandBuffers = &buffer
            };

            var signalSemaphores = stackalloc[] { renderFinishedSemaphores![currentFrame] };
            submitInfo = submitInfo with
            {
                SignalSemaphoreCount = 1,
                PSignalSemaphores = signalSemaphores,
            };

            Api!.ResetFences(device, 1, ref inFlightFences[currentFrame]);

            Result frameSubmitResult;
            lock (_oneTimeSubmitLock)
                frameSubmitResult = Api!.QueueSubmit(graphicsQueue, 1, ref submitInfo, inFlightFences[currentFrame]);

            if (frameSubmitResult != Result.Success)
            {
                // Submit failed (usually because a referenced resource was destroyed during recording).
                // Log the error, skip present, and advance the frame so fences stay in sync.
                Debug.LogWarning(
                    $"[Vulkan] Frame submit failed (Result={frameSubmitResult}) on frame {_vkDebugFrameCounter}, image {imageIndex}. Skipping present.");
                currentFrame = (currentFrame + 1) % MAX_FRAMES_IN_FLIGHT;
                return;
            }

            // Trim idle staging buffers so the pool does not grow unbounded.
            _stagingManager.Trim(this);

            Debug.VulkanEvery(
                $"Vulkan.Frame.{GetHashCode()}.Submit",
                TimeSpan.FromSeconds(1),
                "[Vulkan] Frame={0} SubmittedImage={1}",
                _vkDebugFrameCounter,
                imageIndex);

            // 6. Present the image
            var swapChains = stackalloc[] { swapChain };
            PresentInfoKHR presentInfo = new()
            {
                SType = StructureType.PresentInfoKhr,
                WaitSemaphoreCount = 1,
                PWaitSemaphores = signalSemaphores,
                SwapchainCount = 1,
                PSwapchains = swapChains,
                PImageIndices = &imageIndex
            };

            lock (_oneTimeSubmitLock)
                result = khrSwapChain.QueuePresent(presentQueue, ref presentInfo);
            _lastPresentedImageIndex = imageIndex;

            Debug.VulkanEvery(
                $"Vulkan.Frame.{GetHashCode()}.Present",
                TimeSpan.FromSeconds(1),
                "[Vulkan] Frame={0} PresentedImage={1} Result={2}",
                _vkDebugFrameCounter,
                imageIndex,
                result);

            _frameBufferInvalidated |=
                result == Result.ErrorOutOfDateKhr ||
                result == Result.SuboptimalKhr;

            if (_frameBufferInvalidated)
            {
                _frameBufferInvalidated = false;
                RecreateSwapChain();
            }
            else if (result != Result.Success)
                throw new Exception("Failed to present swap chain image.");

            currentFrame = (currentFrame + 1) % MAX_FRAMES_IN_FLIGHT;
        }

        // =========== Indirect + Pipeline Abstraction stubs for Vulkan ===========
        public override void BindVAOForRenderer(XRMeshRenderer.BaseVersion? version)
        {
            if (version is null)
            {
                _boundMeshRendererForIndirect = null;
                _boundIndexType = IndexType.Uint32;
                _boundIndexCount = 0;
                return;
            }

            var vkMesh = GenericToAPI<VkMeshRenderer>(version);
            if (vkMesh is null)
            {
                _boundMeshRendererForIndirect = null;
                _boundIndexType = IndexType.Uint32;
                _boundIndexCount = 0;
                return;
            }

            vkMesh.Generate();
            _boundMeshRendererForIndirect = vkMesh;

            if (vkMesh.TryGetPrimaryIndexBinding(out _, out IndexType indexType, out uint indexCount))
            {
                _boundIndexType = indexType;
                _boundIndexCount = indexCount;
            }
            else
            {
                _boundIndexType = IndexType.Uint32;
                _boundIndexCount = 0;
            }
        }

        public override bool ValidateIndexedVAO(XRMeshRenderer.BaseVersion? version)
        {
            return TryGetIndexBufferInfo(version, out _, out _);
        }

        public override bool TryGetIndexBufferInfo(XRMeshRenderer.BaseVersion? version, out IndexSize indexElementSize, out uint indexCount)
        {
            indexElementSize = IndexSize.FourBytes;
            indexCount = 0;

            var vkMesh = version is not null ? GenericToAPI<VkMeshRenderer>(version) : _boundMeshRendererForIndirect;
            if (vkMesh is null)
                return false;

            bool updateBoundState = version is null || _boundMeshRendererForIndirect == vkMesh;
            vkMesh.Generate();
            if (!vkMesh.TryGetPrimaryIndexBufferInfo(out indexElementSize, out indexCount))
            {
                if (updateBoundState)
                {
                    _boundIndexType = IndexType.Uint32;
                    _boundIndexCount = 0;
                }

                return false;
            }

            if (updateBoundState)
            {
                _boundIndexType = ToVkIndexType(indexElementSize);
                _boundIndexCount = indexCount;
            }

            return true;
        }

        public override bool TrySyncMeshRendererIndexBuffer(XRMeshRenderer meshRenderer, XRDataBuffer indexBuffer, IndexSize elementSize)
        {
            if (meshRenderer is null || indexBuffer is null)
                return false;

            var version = meshRenderer.GetDefaultVersion();
            var vkMesh = GenericToAPI<VkMeshRenderer>(version);
            if (vkMesh is null)
                return false;

            var vkIndexBuffer = GenericToAPI<VkDataBuffer>(indexBuffer);
            if (vkIndexBuffer is null)
                return false;

            vkMesh.Generate();
            vkIndexBuffer.Generate();
            vkMesh.SetTriangleIndexBuffer(vkIndexBuffer, elementSize);

            if (_boundMeshRendererForIndirect == vkMesh &&
                vkMesh.TryGetPrimaryIndexBinding(out _, out IndexType boundType, out uint boundCount))
            {
                _boundIndexType = boundType;
                _boundIndexCount = boundCount;
            }

            MarkCommandBuffersDirty();
            return true;
        }

        // =========== Indirect Draw State ===========
        private VkDataBuffer? _boundIndirectBuffer;
        private VkDataBuffer? _boundParameterBuffer;
        private VkMeshRenderer? _boundMeshRendererForIndirect;
        private IndexType _boundIndexType = IndexType.Uint32;
        private uint _boundIndexCount;

        public override void BindDrawIndirectBuffer(XRDataBuffer buffer)
        {
            var vkBuffer = GenericToAPI<VkDataBuffer>(buffer);
            _boundIndirectBuffer = vkBuffer;
            MarkCommandBuffersDirty();
        }

        public override void UnbindDrawIndirectBuffer()
        {
            _boundIndirectBuffer = null;
            MarkCommandBuffersDirty();
        }

        public override void BindParameterBuffer(XRDataBuffer buffer)
        {
            var vkBuffer = GenericToAPI<VkDataBuffer>(buffer);
            _boundParameterBuffer = vkBuffer;
            MarkCommandBuffersDirty();
        }

        public override void UnbindParameterBuffer()
        {
            _boundParameterBuffer = null;
            MarkCommandBuffersDirty();
        }

        public override void MultiDrawElementsIndirect(uint drawCount, uint stride)
        {
            MultiDrawElementsIndirectWithOffset(drawCount, stride, 0);
        }

        public override void MultiDrawElementsIndirectWithOffset(uint drawCount, uint stride, nuint byteOffset)
        {
            if (_boundIndirectBuffer?.BufferHandle is null)
            {
                Debug.VulkanWarning("MultiDrawElementsIndirectWithOffset: No indirect buffer bound.");
                return;
            }

            if (_boundMeshRendererForIndirect is null || _boundIndexCount == 0)
            {
                Debug.VulkanWarning("MultiDrawElementsIndirectWithOffset: No indexed mesh renderer bound.");
                return;
            }

            int passIndex = Engine.Rendering.State.CurrentRenderGraphPassIndex;
            EnqueueFrameOp(new IndirectDrawOp(
                EnsureValidPassIndex(passIndex, "IndirectDraw"),
                _boundIndirectBuffer,
                _boundParameterBuffer,
                drawCount,
                stride,
                byteOffset,
                UseCount: false,
                CaptureFrameOpContext()));

            Engine.Rendering.Stats.IncrementMultiDrawCalls();
            Engine.Rendering.Stats.IncrementDrawCalls((int)drawCount);
        }

        public override void MultiDrawElementsIndirectCount(uint maxDrawCount, uint stride, nuint byteOffset)
        {
            if (!_supportsDrawIndirectCount)
            {
                Debug.VulkanWarning("MultiDrawElementsIndirectCount called but VK_KHR_draw_indirect_count is not supported. Falling back to regular indirect draw.");
                MultiDrawElementsIndirectWithOffset(maxDrawCount, stride, byteOffset);
                return;
            }

            if (_boundIndirectBuffer?.BufferHandle is null)
            {
                Debug.VulkanWarning("MultiDrawElementsIndirectCount: No indirect buffer bound.");
                return;
            }

            if (_boundMeshRendererForIndirect is null || _boundIndexCount == 0)
            {
                Debug.VulkanWarning("MultiDrawElementsIndirectCount: No indexed mesh renderer bound.");
                return;
            }

            if (_boundParameterBuffer?.BufferHandle is null)
            {
                Debug.VulkanWarning("MultiDrawElementsIndirectCount: No parameter (count) buffer bound. Falling back to regular indirect draw.");
                MultiDrawElementsIndirectWithOffset(maxDrawCount, stride, byteOffset);
                return;
            }

            int passIndex = Engine.Rendering.State.CurrentRenderGraphPassIndex;
            EnqueueFrameOp(new IndirectDrawOp(
                EnsureValidPassIndex(passIndex, "IndirectCountDraw"),
                _boundIndirectBuffer,
                _boundParameterBuffer,
                maxDrawCount,
                stride,
                byteOffset,
                UseCount: true,
                CaptureFrameOpContext()));

            Engine.Rendering.Stats.IncrementMultiDrawCalls();
            // Actual draw count is determined by GPU; we track max as approximation
            Engine.Rendering.Stats.IncrementDrawCalls((int)maxDrawCount);
        }

        private static IndexType ToVkIndexType(IndexSize size)
            => size switch
            {
                IndexSize.Byte => IndexType.Uint8Ext,
                IndexSize.TwoBytes => IndexType.Uint16,
                IndexSize.FourBytes => IndexType.Uint32,
                _ => IndexType.Uint32
            };

        public override void ApplyRenderParameters(XREngine.Rendering.Models.Materials.RenderingParameters parameters)
        {
            if (parameters is null)
                return;

            // Apply color write mask
            _state.SetColorMask(parameters.WriteRed, parameters.WriteGreen, parameters.WriteBlue, parameters.WriteAlpha);
            _state.SetCullMode(ToVulkanCullMode(ResolveCullMode(parameters.CullMode)));
            _state.SetFrontFace(ToVulkanFrontFace(ResolveWinding(parameters.Winding)));

            // Apply depth test settings
            var depthTest = parameters.DepthTest;
            if (depthTest.Enabled == XREngine.Rendering.Models.Materials.ERenderParamUsage.Enabled)
            {
                _state.SetDepthTestEnabled(true);
                _state.SetDepthWriteEnabled(depthTest.UpdateDepth);
                _state.SetDepthCompare(ToVulkanCompareOp(Engine.Rendering.State.MapDepthComparison(depthTest.Function)));
            }
            else if (depthTest.Enabled == XREngine.Rendering.Models.Materials.ERenderParamUsage.Disabled)
            {
                _state.SetDepthTestEnabled(false);
                _state.SetDepthWriteEnabled(false);
            }

            var stencilTest = parameters.StencilTest;
            if (stencilTest.Enabled == XREngine.Rendering.Models.Materials.ERenderParamUsage.Enabled)
            {
                _state.SetStencilEnabled(true);
                _state.SetStencilStates(ToVulkanStencilState(stencilTest.FrontFace), ToVulkanStencilState(stencilTest.BackFace));
                _state.SetStencilWriteMask(stencilTest.FrontFace.WriteMask);
            }
            else if (stencilTest.Enabled == XREngine.Rendering.Models.Materials.ERenderParamUsage.Disabled)
            {
                _state.SetStencilEnabled(false);
                _state.SetStencilStates(default, default);
                _state.SetStencilWriteMask(0);
            }

            BlendMode? blend = ResolveBlendMode(parameters);
            if (blend is not null && blend.Enabled == XREngine.Rendering.Models.Materials.ERenderParamUsage.Enabled)
            {
                _state.SetBlendState(
                    true,
                    ToVulkanBlendOp(blend.RgbEquation),
                    ToVulkanBlendOp(blend.AlphaEquation),
                    ToVulkanBlendFactor(blend.RgbSrcFactor),
                    ToVulkanBlendFactor(blend.RgbDstFactor),
                    ToVulkanBlendFactor(blend.AlphaSrcFactor),
                    ToVulkanBlendFactor(blend.AlphaDstFactor));
            }
            else if (blend is not null && blend.Enabled == XREngine.Rendering.Models.Materials.ERenderParamUsage.Disabled)
            {
                _state.SetBlendState(false, BlendOp.Add, BlendOp.Add, BlendFactor.One, BlendFactor.Zero, BlendFactor.One, BlendFactor.Zero);
            }
            else if (blend is null)
            {
                _state.SetBlendState(false, BlendOp.Add, BlendOp.Add, BlendFactor.One, BlendFactor.Zero, BlendFactor.One, BlendFactor.Zero);
            }

            MarkCommandBuffersDirty();
        }

        public override bool SupportsIndirectCountDraw()
        {
            return _supportsDrawIndirectCount;
        }

        public override void ConfigureVAOAttributesForProgram(XRRenderProgram program, XRMeshRenderer.BaseVersion? version)
        {
            // Vulkan does not use VAOs; pipeline vertex input state handles this.
            // No-op for now.
        }

        public override void SetEngineUniforms(XRRenderProgram program, XRCamera camera)
        {
            if (program is null)
                return;

            bool stereoPass = Engine.Rendering.State.IsStereoPass;
            if (stereoPass)
            {
                var rightCam = Engine.Rendering.State.RenderingStereoRightEyeCamera;
                PassCameraUniforms(program, camera, EEngineUniform.LeftEyeInverseViewMatrix, EEngineUniform.LeftEyeProjMatrix);
                PassCameraUniforms(program, rightCam, EEngineUniform.RightEyeInverseViewMatrix, EEngineUniform.RightEyeProjMatrix);
            }
            else
            {
                PassCameraUniforms(program, camera, EEngineUniform.InverseViewMatrix, EEngineUniform.ProjMatrix);
            }
        }

        public override void SetMaterialUniforms(XRMaterial material, XRRenderProgram program)
        {
            if (material is null || program is null)
                return;

            if (material.RenderOptions is not null)
                ApplyRenderParameters(material.RenderOptions);

            foreach (ShaderVar param in material.Parameters)
                param.SetUniform(program, forceUpdate: true);

            for (int i = 0; i < material.Textures.Count; i++)
            {
                XRTexture? texture = material.Textures[i];
                if (texture is null)
                    continue;

                string samplerName = texture.ResolveSamplerName(i, null);
                program.Sampler(samplerName, texture, i);
            }

            _materialUniformSecondsLive += Engine.Time.Timer.Update.Delta;
            var reqs = material.RenderOptions.RequiredEngineUniforms;

            if (reqs.HasFlag(EUniformRequirements.Camera))
            {
                Engine.Rendering.State.RenderingCamera?.SetUniforms(program, true);
                Engine.Rendering.State.RenderingStereoRightEyeCamera?.SetUniforms(program, false);
            }

            if (reqs.HasFlag(EUniformRequirements.Lights))
                Engine.Rendering.State.RenderingWorld?.Lights?.SetForwardLightingUniforms(program);

            if (reqs.HasFlag(EUniformRequirements.RenderTime))
                program.Uniform(nameof(EUniformRequirements.RenderTime), _materialUniformSecondsLive);

            if (reqs.HasFlag(EUniformRequirements.ViewportDimensions))
            {
                var area = Engine.Rendering.State.RenderArea;
                program.Uniform(EEngineUniform.ScreenWidth.ToStringFast(), (float)area.Width);
                program.Uniform(EEngineUniform.ScreenHeight.ToStringFast(), (float)area.Height);
            }

            material.OnSettingUniforms(program);
        }

        private static void PassCameraUniforms(XRRenderProgram program, XRCamera? camera, EEngineUniform inverseViewName, EEngineUniform projectionName)
        {
            Matrix4x4 viewMatrix;
            Matrix4x4 inverseViewMatrix;
            Matrix4x4 projectionMatrix;
            if (camera is not null)
            {
                viewMatrix = camera.Transform.InverseRenderMatrix;
                inverseViewMatrix = camera.Transform.RenderMatrix;
                bool useUnjittered = Engine.Rendering.State.RenderingPipelineState?.UseUnjitteredProjection ?? false;
                projectionMatrix = useUnjittered ? camera.ProjectionMatrixUnjittered : camera.ProjectionMatrix;
            }
            else
            {
                viewMatrix = Matrix4x4.Identity;
                inverseViewMatrix = Matrix4x4.Identity;
                projectionMatrix = Matrix4x4.Identity;
            }

            program.Uniform(EEngineUniform.ViewMatrix.ToVertexUniformName(), viewMatrix);
            program.Uniform(inverseViewName.ToVertexUniformName(), inverseViewMatrix);
            program.Uniform(projectionName.ToVertexUniformName(), projectionMatrix);
        }

        private static ECullMode ResolveCullMode(ECullMode mode)
        {
            if (!Engine.Rendering.State.ReverseCulling)
                return mode;

            return mode switch
            {
                ECullMode.Front => ECullMode.Back,
                ECullMode.Back => ECullMode.Front,
                _ => mode
            };
        }

        private static EWinding ResolveWinding(EWinding winding)
        {
            if (!Engine.Rendering.State.ReverseWinding)
                return winding;

            return winding == EWinding.Clockwise ? EWinding.CounterClockwise : EWinding.Clockwise;
        }

        private static BlendMode? ResolveBlendMode(RenderingParameters parameters)
        {
            if (parameters.BlendModeAllDrawBuffers is not null)
                return parameters.BlendModeAllDrawBuffers;

            if (parameters.BlendModesPerDrawBuffer is not null && parameters.BlendModesPerDrawBuffer.Count > 0)
            {
                if (parameters.BlendModesPerDrawBuffer.TryGetValue(0u, out BlendMode? primary))
                    return primary;

                return parameters.BlendModesPerDrawBuffer.Values.FirstOrDefault();
            }

            return null;
        }

        private static CullModeFlags ToVulkanCullMode(ECullMode mode)
            => mode switch
            {
                ECullMode.None => CullModeFlags.None,
                ECullMode.Back => CullModeFlags.BackBit,
                ECullMode.Front => CullModeFlags.FrontBit,
                ECullMode.Both => CullModeFlags.FrontAndBack,
                _ => CullModeFlags.BackBit
            };

        private static FrontFace ToVulkanFrontFace(EWinding winding)
            => winding switch
            {
                EWinding.Clockwise => FrontFace.Clockwise,
                EWinding.CounterClockwise => FrontFace.CounterClockwise,
                _ => FrontFace.CounterClockwise
            };

        private static StencilOpState ToVulkanStencilState(StencilTestFace face)
            => new()
            {
                FailOp = ToVulkanStencilOp(face.BothFailOp),
                PassOp = ToVulkanStencilOp(face.BothPassOp),
                DepthFailOp = ToVulkanStencilOp(face.StencilPassDepthFailOp),
                CompareOp = ToVulkanCompareOp(face.Function),
                CompareMask = face.ReadMask,
                WriteMask = face.WriteMask,
                Reference = (uint)Math.Max(face.Reference, 0)
            };

        private static StencilOp ToVulkanStencilOp(EStencilOp op)
            => op switch
            {
                EStencilOp.Zero => StencilOp.Zero,
                EStencilOp.Invert => StencilOp.Invert,
                EStencilOp.Keep => StencilOp.Keep,
                EStencilOp.Replace => StencilOp.Replace,
                EStencilOp.Incr => StencilOp.IncrementAndClamp,
                EStencilOp.Decr => StencilOp.DecrementAndClamp,
                EStencilOp.IncrWrap => StencilOp.IncrementAndWrap,
                EStencilOp.DecrWrap => StencilOp.DecrementAndWrap,
                _ => StencilOp.Keep
            };

        private static BlendOp ToVulkanBlendOp(EBlendEquationMode mode)
            => mode switch
            {
                EBlendEquationMode.FuncAdd => BlendOp.Add,
                EBlendEquationMode.FuncSubtract => BlendOp.Subtract,
                EBlendEquationMode.FuncReverseSubtract => BlendOp.ReverseSubtract,
                EBlendEquationMode.Min => BlendOp.Min,
                EBlendEquationMode.Max => BlendOp.Max,
                _ => BlendOp.Add
            };

        private static BlendFactor ToVulkanBlendFactor(EBlendingFactor factor)
            => factor switch
            {
                EBlendingFactor.Zero => BlendFactor.Zero,
                EBlendingFactor.One => BlendFactor.One,
                EBlendingFactor.SrcColor => BlendFactor.SrcColor,
                EBlendingFactor.OneMinusSrcColor => BlendFactor.OneMinusSrcColor,
                EBlendingFactor.DstColor => BlendFactor.DstColor,
                EBlendingFactor.OneMinusDstColor => BlendFactor.OneMinusDstColor,
                EBlendingFactor.SrcAlpha => BlendFactor.SrcAlpha,
                EBlendingFactor.OneMinusSrcAlpha => BlendFactor.OneMinusSrcAlpha,
                EBlendingFactor.DstAlpha => BlendFactor.DstAlpha,
                EBlendingFactor.OneMinusDstAlpha => BlendFactor.OneMinusDstAlpha,
                EBlendingFactor.SrcAlphaSaturate => BlendFactor.SrcAlphaSaturate,
                EBlendingFactor.ConstantColor => BlendFactor.ConstantColor,
                EBlendingFactor.OneMinusConstantColor => BlendFactor.OneMinusConstantColor,
                EBlendingFactor.ConstantAlpha => BlendFactor.ConstantAlpha,
                EBlendingFactor.OneMinusConstantAlpha => BlendFactor.OneMinusConstantAlpha,
                EBlendingFactor.Src1Color => BlendFactor.Src1Color,
                EBlendingFactor.OneMinusSrc1Color => BlendFactor.OneMinusSrc1Color,
                EBlendingFactor.Src1Alpha => BlendFactor.Src1Alpha,
                EBlendingFactor.OneMinusSrc1Alpha => BlendFactor.OneMinusSrc1Alpha,
                _ => BlendFactor.One
            };

        private readonly struct BlitImageInfo
        {
            public BlitImageInfo(
                Image image,
                Format format,
                ImageAspectFlags aspectMask,
                uint baseArrayLayer,
                uint layerCount,
                uint mipLevel,
                ImageLayout preferredLayout,
                PipelineStageFlags stageMask,
                AccessFlags accessMask)
            {
                Image = image;
                Format = format;
                AspectMask = aspectMask;
                BaseArrayLayer = baseArrayLayer;
                LayerCount = layerCount;
                MipLevel = mipLevel;
                PreferredLayout = preferredLayout;
                StageMask = stageMask;
                AccessMask = accessMask;
            }

            public Image Image { get; }
            public Format Format { get; }
            public ImageAspectFlags AspectMask { get; }
            public uint BaseArrayLayer { get; }
            public uint LayerCount { get; }
            public uint MipLevel { get; }
            public ImageLayout PreferredLayout { get; }
            public PipelineStageFlags StageMask { get; }
            public AccessFlags AccessMask { get; }
            public bool IsValid => Image.Handle != 0;
        }

        private bool TryResolveBlitImage(
            XRFrameBuffer? frameBuffer,
            uint swapchainImageIndex,
            EReadBufferMode readBufferMode,
            bool wantColor,
            bool wantDepth,
            bool wantStencil,
            out BlitImageInfo info,
            bool isSource)
        {
            if (frameBuffer is null)
            {
                info = ResolveSwapchainBlitImage(swapchainImageIndex, wantColor, wantDepth, wantStencil);
                return info.IsValid;
            }

            var targets = frameBuffer.Targets;
            if (targets is null)
            {
                info = default;
                return false;
            }

            int desiredColorIndex = isSource ? ResolveReadBufferColorAttachmentIndex(readBufferMode) : 0;
            EFrameBufferAttachment desiredColorAttachment = (EFrameBufferAttachment)((int)EFrameBufferAttachment.ColorAttachment0 + desiredColorIndex);

            foreach (var (target, attachment, mipLevel, layerIndex) in targets)
            {
                ImageAspectFlags aspect = ImageAspectFlags.None;

                if (IsColorAttachment(attachment) && wantColor)
                {
                    if (attachment != desiredColorAttachment)
                        continue;
                    aspect = ImageAspectFlags.ColorBit;
                }
                else if (attachment == EFrameBufferAttachment.DepthStencilAttachment && (wantDepth || wantStencil))
                {
                    aspect = (wantDepth, wantStencil) switch
                    {
                        (true, true) => ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit,
                        (true, false) => ImageAspectFlags.DepthBit,
                        (false, true) => ImageAspectFlags.StencilBit,
                        _ => ImageAspectFlags.None
                    };
                }
                else if (attachment == EFrameBufferAttachment.DepthAttachment && wantDepth)
                    aspect = ImageAspectFlags.DepthBit;
                else if (attachment == EFrameBufferAttachment.StencilAttachment && wantStencil)
                    aspect = ImageAspectFlags.StencilBit;

                if (aspect == ImageAspectFlags.None)
                    continue;

                if (TryResolveAttachmentImage(target, mipLevel, layerIndex, aspect, out info))
                    return true;
            }

            info = default;
            return false;
        }

        private bool TryResolveAttachmentImage(IFrameBufferAttachement attachment, int mipLevel, int layerIndex, ImageAspectFlags aspectMask, out BlitImageInfo info)
        {
            info = default;

            ImageLayout layout = aspectMask.HasFlag(ImageAspectFlags.ColorBit)
                ? ImageLayout.ColorAttachmentOptimal
                : ImageLayout.DepthStencilAttachmentOptimal;

            PipelineStageFlags stage = aspectMask.HasFlag(ImageAspectFlags.ColorBit)
                ? PipelineStageFlags.ColorAttachmentOutputBit
                : PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit;

            AccessFlags access = aspectMask.HasFlag(ImageAspectFlags.ColorBit)
                ? AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit
                : AccessFlags.DepthStencilAttachmentReadBit | AccessFlags.DepthStencilAttachmentWriteBit;

            switch (attachment)
            {
                case XRTexture texture:
                    return TryResolveTextureBlitImage(texture, mipLevel, layerIndex, aspectMask, layout, stage, access, out info);
                case XRRenderBuffer renderBuffer when GetOrCreateAPIRenderObject(renderBuffer, true) is VkRenderBuffer vkRenderBuffer:
                    // Allow depth/stencil or color depending on the requested aspect and buffer format.
                    if (IsDepthOrStencilAspect(aspectMask) && (vkRenderBuffer.Aspect & aspectMask) != aspectMask)
                        return false;
                    info = new BlitImageInfo(
                        vkRenderBuffer.Image,
                        vkRenderBuffer.Format,
                        aspectMask,
                        0,
                        1,
                        0,
                        layout,
                        stage,
                        access);
                    return info.IsValid;
                default:
                    return false;
            }
        }

        private bool TryResolveTextureBlitImage(
            XRTexture texture,
            int mipLevel,
            int layerIndex,
            ImageAspectFlags aspectMask,
            ImageLayout layout,
            PipelineStageFlags stage,
            AccessFlags access,
            out BlitImageInfo info)
        {
            info = default;
            if (GetOrCreateAPIRenderObject(texture, true) is not IVkImageDescriptorSource source)
                return false;

            if (source.DescriptorImage.Handle == 0)
                return false;

            Format format = source.DescriptorFormat;
            if (IsDepthOrStencilAspect(aspectMask))
            {
                if (!IsDepthOrStencilFormat(format))
                    return false;
            }
            else if (!aspectMask.HasFlag(ImageAspectFlags.ColorBit))
            {
                return false;
            }

            uint baseArrayLayer = ResolveBlitBaseArrayLayer(texture, layerIndex);
            if (texture is XRTexture3D)
                baseArrayLayer = 0;

            info = new BlitImageInfo(
                source.DescriptorImage,
                format,
                aspectMask,
                baseArrayLayer,
                1,
                (uint)Math.Max(mipLevel, 0),
                layout,
                stage,
                access);

            return info.IsValid;
        }

        private static bool IsColorAttachment(EFrameBufferAttachment attachment)
            => attachment >= EFrameBufferAttachment.ColorAttachment0 && attachment <= EFrameBufferAttachment.ColorAttachment31;

        private static bool IsDepthOrStencilAspect(ImageAspectFlags aspectMask)
            => (aspectMask & (ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit)) != 0;

        private static int ResolveReadBufferColorAttachmentIndex(EReadBufferMode mode)
        {
            if (mode >= EReadBufferMode.ColorAttachment0 && mode <= EReadBufferMode.ColorAttachment31)
                return (int)mode - (int)EReadBufferMode.ColorAttachment0;

            return 0;
        }

        private static bool IsDepthOrStencilFormat(Format format)
            => format is Format.D16Unorm
                or Format.D32Sfloat
                or Format.D24UnormS8Uint
                or Format.D32SfloatS8Uint
                or Format.D16UnormS8Uint
                or Format.X8D24UnormPack32;

        private static bool IsCombinedDepthStencilFormat(Format format)
            => format is Format.D24UnormS8Uint
                or Format.D32SfloatS8Uint
                or Format.D16UnormS8Uint;

        private static ImageAspectFlags NormalizeBarrierAspectMask(Format format, ImageAspectFlags aspectMask)
        {
            if (!IsCombinedDepthStencilFormat(format))
                return aspectMask;

            if ((aspectMask & (ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit)) == 0)
                return aspectMask;

            return aspectMask | ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit;
        }

        private BlitImageInfo ResolveSwapchainBlitImage(uint swapchainImageIndex, bool wantColor, bool wantDepth, bool wantStencil)
        {
            if (wantColor && swapChainImages is not null && swapchainImageIndex < swapChainImages.Length)
            {
                return new BlitImageInfo(
                    swapChainImages[swapchainImageIndex],
                    swapChainImageFormat,
                    ImageAspectFlags.ColorBit,
                    0,
                    1,
                    0,
                    ImageLayout.ColorAttachmentOptimal,
                    PipelineStageFlags.ColorAttachmentOutputBit,
                    AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit);
            }

            if ((wantDepth || wantStencil) && _swapchainDepthImage.Handle != 0)
            {
                ImageAspectFlags depthAspect = (wantDepth, wantStencil) switch
                {
                    (true, true) => _swapchainDepthAspect,
                    (true, false) => ImageAspectFlags.DepthBit,
                    (false, true) => _swapchainDepthAspect.HasFlag(ImageAspectFlags.StencilBit) ? ImageAspectFlags.StencilBit : ImageAspectFlags.None,
                    _ => ImageAspectFlags.None
                };

                if (depthAspect != ImageAspectFlags.None)
                {
                    return new BlitImageInfo(
                        _swapchainDepthImage,
                        _swapchainDepthFormat,
                        depthAspect,
                        0,
                        1,
                        0,
                        ImageLayout.DepthStencilAttachmentOptimal,
                        PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit,
                        AccessFlags.DepthStencilAttachmentReadBit | AccessFlags.DepthStencilAttachmentWriteBit);
                }
            }

            return default;
        }

        private static uint ResolveLayerIndex(int layerIndex)
            => layerIndex >= 0 ? (uint)layerIndex : 0u;

        private static uint ResolveBlitBaseArrayLayer(XRTexture texture, int layerIndex)
        {
            uint resolvedLayer = ResolveLayerIndex(layerIndex);
            return texture switch
            {
                XRTexture1D => 0,
                XRTexture2D => 0,
                XRTexture3D => 0,
                XRTextureRectangle => 0,
                XRTextureViewBase view => ResolveViewBlitBaseLayer(view, resolvedLayer),
                _ => resolvedLayer
            };
        }

        private static uint ResolveViewBlitBaseLayer(XRTextureViewBase view, uint resolvedLayer)
            => view.TextureTarget switch
            {
                ETextureTarget.Texture1D => view.MinLayer,
                ETextureTarget.Texture2D => view.MinLayer,
                ETextureTarget.Texture3D => view.MinLayer,
                ETextureTarget.TextureRectangle => view.MinLayer,
                _ => view.MinLayer + resolvedLayer
            };

        private static ImageBlit BuildImageBlit(
            BlitImageInfo source,
            BlitImageInfo destination,
            int inX, int inY, uint inW, uint inH,
            int outX, int outY, uint outW, uint outH)
        {
            ImageBlit region = new()
            {
                SrcSubresource = new ImageSubresourceLayers
                {
                    AspectMask = source.AspectMask,
                    MipLevel = source.MipLevel,
                    BaseArrayLayer = source.BaseArrayLayer,
                    LayerCount = source.LayerCount
                },
                DstSubresource = new ImageSubresourceLayers
                {
                    AspectMask = destination.AspectMask,
                    MipLevel = destination.MipLevel,
                    BaseArrayLayer = destination.BaseArrayLayer,
                    LayerCount = destination.LayerCount
                }
            };

            region.SrcOffsets.Element0 = new Offset3D { X = inX, Y = inY, Z = 0 };
            region.SrcOffsets.Element1 = new Offset3D { X = inX + (int)inW, Y = inY + (int)inH, Z = 1 };
            region.DstOffsets.Element0 = new Offset3D { X = outX, Y = outY, Z = 0 };
            region.DstOffsets.Element1 = new Offset3D { X = outX + (int)outW, Y = outY + (int)outH, Z = 1 };

            return region;
        }

        private void TransitionForBlit(
            CommandBuffer commandBuffer,
            BlitImageInfo info,
            ImageLayout oldLayout,
            ImageLayout newLayout,
            AccessFlags srcAccess,
            AccessFlags dstAccess,
            PipelineStageFlags srcStage,
            PipelineStageFlags dstStage)
        {
            ImageAspectFlags barrierAspectMask = NormalizeBarrierAspectMask(info.Format, info.AspectMask);

            ImageMemoryBarrier barrier = new()
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = srcAccess,
                DstAccessMask = dstAccess,
                OldLayout = oldLayout,
                NewLayout = newLayout,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = info.Image,
                SubresourceRange = new ImageSubresourceRange
                {
                    AspectMask = barrierAspectMask,
                    BaseMipLevel = info.MipLevel,
                    LevelCount = 1,
                    BaseArrayLayer = info.BaseArrayLayer,
                    LayerCount = info.LayerCount
                }
            };

            ImageMemoryBarrier* barrierPtr = stackalloc ImageMemoryBarrier[1];
            barrierPtr[0] = barrier;

            Api!.CmdPipelineBarrier(
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
        }

        private void TransitionSwapchainImage(CommandBuffer commandBuffer, Image image, ImageLayout oldLayout, ImageLayout newLayout)
        {
            ImageMemoryBarrier barrier = new()
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = AccessFlags.MemoryReadBit,
                DstAccessMask = newLayout == ImageLayout.TransferSrcOptimal ? AccessFlags.TransferReadBit : AccessFlags.MemoryReadBit,
                OldLayout = oldLayout,
                NewLayout = newLayout,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = image,
                SubresourceRange = new ImageSubresourceRange
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    BaseMipLevel = 0,
                    LevelCount = 1,
                    BaseArrayLayer = 0,
                    LayerCount = 1
                }
            };

            ImageMemoryBarrier* barrierPtr = stackalloc ImageMemoryBarrier[1];
            barrierPtr[0] = barrier;

            Api!.CmdPipelineBarrier(
                commandBuffer,
                PipelineStageFlags.AllCommandsBit,
                newLayout == ImageLayout.TransferSrcOptimal ? PipelineStageFlags.TransferBit : PipelineStageFlags.AllCommandsBit,
                DependencyFlags.None,
                0,
                null,
                0,
                null,
                1,
                barrierPtr);
        }

        private static void ClampReadbackRegion(BoundingRectangle region, uint sourceWidth, uint sourceHeight, out int x, out int y, out int width, out int height)
        {
            int maxX = Math.Max((int)sourceWidth - 1, 0);
            int maxY = Math.Max((int)sourceHeight - 1, 0);
            x = Math.Clamp(region.X, 0, maxX);
            y = Math.Clamp(region.Y, 0, maxY);

            int requestedWidth = region.Width > 0 ? region.Width : (int)sourceWidth;
            int requestedHeight = region.Height > 0 ? region.Height : (int)sourceHeight;
            int availableWidth = Math.Max((int)sourceWidth - x, 1);
            int availableHeight = Math.Max((int)sourceHeight - y, 1);

            width = Math.Clamp(requestedWidth, 1, availableWidth);
            height = Math.Clamp(requestedHeight, 1, availableHeight);
        }

        private bool TryReadColorPixel(in BlitImageInfo source, int x, int y, out ColorF4 color)
        {
            color = ColorF4.Transparent;

            if (!TryReadColorRegionRgba8(source, x, y, 1, 1, out byte[] rgba) || rgba.Length < 4)
                return false;

            color = new ColorF4(
                rgba[0] / 255f,
                rgba[1] / 255f,
                rgba[2] / 255f,
                rgba[3] / 255f);
            return true;
        }

        private bool TryReadColorRegionRgba8(in BlitImageInfo source, int x, int y, int width, int height, out byte[] rgbaPixels)
        {
            rgbaPixels = [];

            if (!source.IsValid || !source.AspectMask.HasFlag(ImageAspectFlags.ColorBit))
                return false;

            uint sourcePixelSize = GetColorFormatPixelSize(source.Format);
            if (sourcePixelSize == 0)
                return false;

            ulong rawByteCount = (ulong)(width * height) * sourcePixelSize;
            var (stagingBuffer, stagingMemory) = CreateBuffer(
                rawByteCount,
                BufferUsageFlags.TransferDstBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                null);

            try
            {
                using var scope = NewCommandScope();

                TransitionForBlit(
                    scope.CommandBuffer,
                    source,
                    source.PreferredLayout,
                    ImageLayout.TransferSrcOptimal,
                    source.AccessMask,
                    AccessFlags.TransferReadBit,
                    source.StageMask,
                    PipelineStageFlags.TransferBit);

                BufferImageCopy copy = new()
                {
                    BufferOffset = 0,
                    BufferRowLength = 0,
                    BufferImageHeight = 0,
                    ImageSubresource = new ImageSubresourceLayers
                    {
                        AspectMask = ImageAspectFlags.ColorBit,
                        MipLevel = source.MipLevel,
                        BaseArrayLayer = source.BaseArrayLayer,
                        LayerCount = source.LayerCount,
                    },
                    ImageOffset = new Offset3D { X = x, Y = y, Z = 0 },
                    ImageExtent = new Extent3D { Width = (uint)width, Height = (uint)height, Depth = 1 }
                };

                Api!.CmdCopyImageToBuffer(
                    scope.CommandBuffer,
                    source.Image,
                    ImageLayout.TransferSrcOptimal,
                    stagingBuffer,
                    1,
                    &copy);

                TransitionForBlit(
                    scope.CommandBuffer,
                    source,
                    ImageLayout.TransferSrcOptimal,
                    source.PreferredLayout,
                    AccessFlags.TransferReadBit,
                    source.AccessMask,
                    PipelineStageFlags.TransferBit,
                    source.StageMask);
            }
            catch
            {
                DestroyBuffer(stagingBuffer, stagingMemory);
                return false;
            }

            void* mappedPtr;
            if (Api!.MapMemory(device, stagingMemory, 0, rawByteCount, 0, &mappedPtr) != Result.Success)
            {
                DestroyBuffer(stagingBuffer, stagingMemory);
                return false;
            }

            try
            {
                rgbaPixels = new byte[width * height * 4];
                return TryConvertColorPixelsToRgba8(mappedPtr, source.Format, width * height, rgbaPixels);
            }
            finally
            {
                Api!.UnmapMemory(device, stagingMemory);
                DestroyBuffer(stagingBuffer, stagingMemory);
            }
        }

        private static bool TryConvertColorPixelsToRgba8(void* srcPtr, Format format, int pixelCount, byte[] dstRgba)
        {
            if (pixelCount <= 0 || dstRgba.Length < pixelCount * 4)
                return false;

            static byte FloatToByte(float v)
            {
                float clamped = Math.Clamp(v, 0.0f, 1.0f);
                return (byte)Math.Clamp((int)MathF.Round(clamped * 255.0f), 0, 255);
            }

            byte* src = (byte*)srcPtr;

            switch (format)
            {
                case Format.R8G8B8A8Unorm:
                case Format.R8G8B8A8Srgb:
                    for (int i = 0; i < pixelCount; i++)
                    {
                        int srcIndex = i * 4;
                        int dstIndex = i * 4;
                        dstRgba[dstIndex + 0] = src[srcIndex + 0];
                        dstRgba[dstIndex + 1] = src[srcIndex + 1];
                        dstRgba[dstIndex + 2] = src[srcIndex + 2];
                        dstRgba[dstIndex + 3] = src[srcIndex + 3];
                    }
                    return true;

                case Format.B8G8R8A8Unorm:
                case Format.B8G8R8A8Srgb:
                    for (int i = 0; i < pixelCount; i++)
                    {
                        int srcIndex = i * 4;
                        int dstIndex = i * 4;
                        dstRgba[dstIndex + 0] = src[srcIndex + 2];
                        dstRgba[dstIndex + 1] = src[srcIndex + 1];
                        dstRgba[dstIndex + 2] = src[srcIndex + 0];
                        dstRgba[dstIndex + 3] = src[srcIndex + 3];
                    }
                    return true;

                case Format.R16G16B16A16Unorm:
                    for (int i = 0; i < pixelCount; i++)
                    {
                        int srcIndex = i * 8;
                        int dstIndex = i * 4;
                        ushort* p = (ushort*)(src + srcIndex);
                        dstRgba[dstIndex + 0] = FloatToByte(p[0] / 65535.0f);
                        dstRgba[dstIndex + 1] = FloatToByte(p[1] / 65535.0f);
                        dstRgba[dstIndex + 2] = FloatToByte(p[2] / 65535.0f);
                        dstRgba[dstIndex + 3] = FloatToByte(p[3] / 65535.0f);
                    }
                    return true;

                case Format.R16G16B16A16Sfloat:
                    for (int i = 0; i < pixelCount; i++)
                    {
                        int srcIndex = i * 8;
                        int dstIndex = i * 4;
                        ushort* p = (ushort*)(src + srcIndex);
                        dstRgba[dstIndex + 0] = FloatToByte((float)BitConverter.UInt16BitsToHalf(p[0]));
                        dstRgba[dstIndex + 1] = FloatToByte((float)BitConverter.UInt16BitsToHalf(p[1]));
                        dstRgba[dstIndex + 2] = FloatToByte((float)BitConverter.UInt16BitsToHalf(p[2]));
                        dstRgba[dstIndex + 3] = FloatToByte((float)BitConverter.UInt16BitsToHalf(p[3]));
                    }
                    return true;

                case Format.R32G32B32A32Sfloat:
                    for (int i = 0; i < pixelCount; i++)
                    {
                        int srcIndex = i * 16;
                        int dstIndex = i * 4;
                        float* p = (float*)(src + srcIndex);
                        dstRgba[dstIndex + 0] = FloatToByte(p[0]);
                        dstRgba[dstIndex + 1] = FloatToByte(p[1]);
                        dstRgba[dstIndex + 2] = FloatToByte(p[2]);
                        dstRgba[dstIndex + 3] = FloatToByte(p[3]);
                    }
                    return true;
            }

            return false;
        }

        private static uint GetColorFormatPixelSize(Format format)
            => format switch
            {
                Format.R8G8B8A8Unorm => 4,
                Format.R8G8B8A8Srgb => 4,
                Format.B8G8R8A8Unorm => 4,
                Format.B8G8R8A8Srgb => 4,
                Format.R16G16B16A16Unorm => 8,
                Format.R16G16B16A16Sfloat => 8,
                Format.R32G32B32A32Sfloat => 16,
                _ => 0,
            };

        private bool TryReadDepthPixel(in BlitImageInfo source, int x, int y, out float depth)
        {
            depth = 1.0f;

            if (!source.IsValid || !IsDepthOrStencilAspect(source.AspectMask))
                return false;

            uint pixelSize = GetDepthFormatPixelSize(source.Format);
            if (pixelSize == 0)
                return false;

            ulong bufferSize = pixelSize;
            var (stagingBuffer, stagingMemory) = CreateBuffer(
                bufferSize,
                BufferUsageFlags.TransferDstBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                null);

            try
            {
                using var scope = NewCommandScope();

                TransitionForBlit(
                    scope.CommandBuffer,
                    source,
                    source.PreferredLayout,
                    ImageLayout.TransferSrcOptimal,
                    source.AccessMask,
                    AccessFlags.TransferReadBit,
                    source.StageMask,
                    PipelineStageFlags.TransferBit);

                BufferImageCopy copy = new()
                {
                    BufferOffset = 0,
                    BufferRowLength = 0,
                    BufferImageHeight = 0,
                    ImageSubresource = new ImageSubresourceLayers
                    {
                        AspectMask = source.AspectMask.HasFlag(ImageAspectFlags.DepthBit)
                            ? ImageAspectFlags.DepthBit
                            : source.AspectMask,
                        MipLevel = source.MipLevel,
                        BaseArrayLayer = source.BaseArrayLayer,
                        LayerCount = source.LayerCount,
                    },
                    ImageOffset = new Offset3D { X = x, Y = y, Z = 0 },
                    ImageExtent = new Extent3D { Width = 1, Height = 1, Depth = 1 }
                };

                Api!.CmdCopyImageToBuffer(
                    scope.CommandBuffer,
                    source.Image,
                    ImageLayout.TransferSrcOptimal,
                    stagingBuffer,
                    1,
                    &copy);

                TransitionForBlit(
                    scope.CommandBuffer,
                    source,
                    ImageLayout.TransferSrcOptimal,
                    source.PreferredLayout,
                    AccessFlags.TransferReadBit,
                    source.AccessMask,
                    PipelineStageFlags.TransferBit,
                    source.StageMask);
            }
            catch
            {
                DestroyBuffer(stagingBuffer, stagingMemory);
                return false;
            }

            void* mappedPtr;
            if (Api!.MapMemory(device, stagingMemory, 0, bufferSize, 0, &mappedPtr) != Result.Success)
            {
                DestroyBuffer(stagingBuffer, stagingMemory);
                return false;
            }

            depth = ReadDepthValue(mappedPtr, source.Format);
            Api!.UnmapMemory(device, stagingMemory);
            DestroyBuffer(stagingBuffer, stagingMemory);
            return true;
        }

        /// <summary>
        /// Gets the byte size of a single pixel for a given depth format.
        /// </summary>
        private static uint GetDepthFormatPixelSize(Format format) => format switch
        {
            Format.D16Unorm => 2,
            Format.D32Sfloat => 4,
            Format.D24UnormS8Uint => 4, // 3 bytes depth + 1 byte stencil
            Format.D32SfloatS8Uint => 5, // 4 bytes depth + 1 byte stencil (may be 8 with padding)
            _ => 0, // Unknown format
        };

        /// <summary>
        /// Reads a depth value from a mapped buffer based on the depth format.
        /// </summary>
        private static float ReadDepthValue(void* ptr, Format format)
        {
            return format switch
            {
                Format.D16Unorm => *(ushort*)ptr / 65535f,
                Format.D32Sfloat => *(float*)ptr,
                Format.D24UnormS8Uint => (*(uint*)ptr & 0x00FFFFFF) / 16777215f,
                Format.D32SfloatS8Uint => *(float*)ptr,
                _ => 1.0f,
            };
        }
    }
}
