using System;
using ImageMagick;
using Silk.NET.Vulkan;
using System.Numerics;
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
            // True async implementation using Vulkan fences.
            // The GPU work is submitted immediately, and we poll the fence on a background thread.
            _ = fbo; // Currently ignores FBO and reads from swapchain depth

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

            if (Api!.QueueSubmit(graphicsQueue, 1, ref submitInfo, fence) != Result.Success)
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

            if (inFBO is null || outFBO is null)
            {
                Debug.LogWarning("Vulkan Blit currently requires both source and destination framebuffers.");
                return;
            }

            if (inW == 0 || inH == 0 || outW == 0 || outH == 0)
                return;

            _ = readBufferMode;

            EnsureFrameBufferRegistered(inFBO);
            EnsureFrameBufferAttachmentsRegistered(inFBO);
            EnsureFrameBufferRegistered(outFBO);
            EnsureFrameBufferAttachmentsRegistered(outFBO);

            int passIndex = Engine.Rendering.State.CurrentRenderGraphPassIndex;
            EnqueueFrameOp(new BlitOp(
                passIndex,
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
                linearFilter));
        }
        public override void GetScreenshotAsync(BoundingRectangle region, bool withTransparency, Action<MagickImage, int> imageCallback)
        {
            // Capture the specified region of the last presented swapchain image.
            if (swapChainImages is null || swapChainImages.Length == 0)
            {
                imageCallback?.Invoke(null!, 0);
                return;
            }

            _ = withTransparency; // Vulkan swapchain is always opaque

            int x = Math.Max(0, region.X);
            int y = Math.Max(0, region.Y);
            int w = Math.Clamp(region.Width, 1, Math.Max(1, (int)swapChainExtent.Width - x));
            int h = Math.Clamp(region.Height, 1, Math.Max(1, (int)swapChainExtent.Height - y));

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

            // Map and create MagickImage
            void* mappedPtr;
            if (Api!.MapMemory(device, stagingMemory, 0, bufferSize, 0, &mappedPtr) != Result.Success)
            {
                DestroyBuffer(stagingBuffer, stagingMemory);
                imageCallback?.Invoke(null!, 0);
                return;
            }

            try
            {
                // Convert BGRA to MagickImage
                byte[] pixels = new byte[bufferSize];
                Marshal.Copy((IntPtr)mappedPtr, pixels, 0, (int)bufferSize);

                // Convert BGRA to RGBA for ImageMagick
                for (int i = 0; i < pixels.Length; i += 4)
                {
                    (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]); // Swap B and R
                }

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
                Debug.LogWarning($"GetScreenshotAsync failed to create image: {ex.Message}");
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
            uint mipWidth = Math.Max(1, texture.Width >> mipLevel);
            uint mipHeight = Math.Max(1, texture.Height >> mipLevel);

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
                    ImageExtent = new Extent3D { Width = mipWidth, Height = mipHeight, Depth = 1 }
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
            uint mipWidth = Math.Max(1, texture.Width >> mipLevel);
            uint mipHeight = Math.Max(1, texture.Height >> mipLevel);

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
                    ImageExtent = new Extent3D { Width = mipWidth, Height = mipHeight, Depth = 1 }
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
                //XRTexture1D data => new VkTexture1D(this, data),
                //XRTexture1DArray data => new VkTexture1DArray(this, data),
                XRTextureViewBase data => new VkTextureView(this, data),
                //XRTexture1DArrayView data => new VkTextureView(this, data),

                //Texture 2D
                XRTexture2D data => new VkTexture2D(this, data),
                XRTexture2DArray data => new VkTexture2DArray(this, data),
                //XRTexture2DView data => new VkTextureView(this, data),
                //XRTexture2DArrayView data => new VkTextureView(this, data),

                //Texture 3D
                XRTexture3D data => new VkTexture3D(this, data),
                //XRTexture3DArray data => new VkTexture3DArray(this, data),
                //XRTexture3DView data => new VkTextureView(this, data),

                //Texture Cube
                XRTextureCube data => new VkTextureCube(this, data),
                //XRTextureCubeArray data => new VkTextureCubeArray(this, data),
                //XRTextureCubeView data => new VkTextureView(this, data),

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

        private int currentFrame = 0;
        private ulong _vkDebugFrameCounter = 0;

        protected override void WindowRenderCallback(double delta)
        {
            _vkDebugFrameCounter++;

            // If the window resized (or other framebuffer-dependent state changed), rebuild swapchain resources
            // before we acquire/record/submit. Waiting until after present can cause visible stretching/borders.
            if (_frameBufferInvalidated)
            {
                _frameBufferInvalidated = false;
                RecreateSwapChain();
            }

            // 1. Wait for the previous frame to finish
            Api!.WaitForFences(device, 1, ref inFlightFences![currentFrame], true, ulong.MaxValue);

            // Helpful when tracking down DPI / resize issues.
            Debug.RenderingEvery(
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

            Debug.RenderingEvery(
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

            if (Api!.QueueSubmit(graphicsQueue, 1, ref submitInfo, inFlightFences[currentFrame]) != Result.Success)
                throw new Exception("Failed to submit draw command buffer.");

            Debug.RenderingEvery(
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

            result = khrSwapChain.QueuePresent(presentQueue, ref presentInfo);
            _lastPresentedImageIndex = imageIndex;

            Debug.RenderingEvery(
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
            // Vulkan has no VAO; this is a no-op for now.
        }

        public override bool ValidateIndexedVAO(XRMeshRenderer.BaseVersion? version)
        {
            // Vulkan path not implemented yet
            return false;
        }

        public override bool TryGetIndexBufferInfo(XRMeshRenderer.BaseVersion? version, out IndexSize indexElementSize, out uint indexCount)
        {
            // Vulkan path not implemented yet
            indexElementSize = IndexSize.FourBytes;
            indexCount = 0;
            return false;
        }

        public override bool TrySyncMeshRendererIndexBuffer(XRMeshRenderer meshRenderer, XRDataBuffer indexBuffer, IndexSize elementSize)
        {
            // Vulkan path not implemented yet
            return false;
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
                Debug.LogWarning("MultiDrawElementsIndirectWithOffset: No indirect buffer bound.");
                return;
            }

            int passIndex = Engine.Rendering.State.CurrentRenderGraphPassIndex;
            EnqueueFrameOp(new IndirectDrawOp(
                passIndex,
                _boundIndirectBuffer,
                _boundParameterBuffer,
                drawCount,
                stride,
                byteOffset,
                UseCount: false));

            Engine.Rendering.Stats.IncrementMultiDrawCalls();
            Engine.Rendering.Stats.IncrementDrawCalls((int)drawCount);
        }

        public override void MultiDrawElementsIndirectCount(uint maxDrawCount, uint stride, nuint byteOffset)
        {
            if (!_supportsDrawIndirectCount)
            {
                Debug.LogWarning("MultiDrawElementsIndirectCount called but VK_KHR_draw_indirect_count is not supported. Falling back to regular indirect draw.");
                MultiDrawElementsIndirectWithOffset(maxDrawCount, stride, byteOffset);
                return;
            }

            if (_boundIndirectBuffer?.BufferHandle is null)
            {
                Debug.LogWarning("MultiDrawElementsIndirectCount: No indirect buffer bound.");
                return;
            }

            if (_boundParameterBuffer?.BufferHandle is null)
            {
                Debug.LogWarning("MultiDrawElementsIndirectCount: No parameter (count) buffer bound. Falling back to regular indirect draw.");
                MultiDrawElementsIndirectWithOffset(maxDrawCount, stride, byteOffset);
                return;
            }

            int passIndex = Engine.Rendering.State.CurrentRenderGraphPassIndex;
            EnqueueFrameOp(new IndirectDrawOp(
                passIndex,
                _boundIndirectBuffer,
                _boundParameterBuffer,
                maxDrawCount,
                stride,
                byteOffset,
                UseCount: true));

            Engine.Rendering.Stats.IncrementMultiDrawCalls();
            // Actual draw count is determined by GPU; we track max as approximation
            Engine.Rendering.Stats.IncrementDrawCalls((int)maxDrawCount);
        }

        public override void ApplyRenderParameters(XREngine.Rendering.Models.Materials.RenderingParameters parameters)
        {
            if (parameters is null)
                return;

            // Apply color write mask
            _state.SetColorMask(parameters.WriteRed, parameters.WriteGreen, parameters.WriteBlue, parameters.WriteAlpha);

            // Apply depth test settings
            var depthTest = parameters.DepthTest;
            if (depthTest.Enabled == XREngine.Rendering.Models.Materials.ERenderParamUsage.Enabled)
            {
                _state.SetDepthTestEnabled(true);
                _state.SetDepthWriteEnabled(depthTest.UpdateDepth);
                _state.SetDepthCompare(ToVulkanCompareOp(depthTest.Function));
            }
            else if (depthTest.Enabled == XREngine.Rendering.Models.Materials.ERenderParamUsage.Disabled)
            {
                _state.SetDepthTestEnabled(false);
            }

            // Apply stencil write mask (simplified - full stencil state would require pipeline recreation)
            var stencilTest = parameters.StencilTest;
            if (stencilTest.Enabled == XREngine.Rendering.Models.Materials.ERenderParamUsage.Enabled)
            {
                // Use front face write mask as the primary mask
                _state.SetStencilWriteMask(stencilTest.FrontFace.WriteMask);
            }
            else if (stencilTest.Enabled == XREngine.Rendering.Models.Materials.ERenderParamUsage.Disabled)
            {
                _state.SetStencilWriteMask(0);
            }

            // Note: Culling, winding, and blend modes require pipeline state object recreation in Vulkan.
            // These are tracked for pipeline key generation but not applied as dynamic state here.
            // Full implementation would store these in VulkanStateTracker and use them when creating pipelines.

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
            // Not implemented: Vulkan path will set camera data via descriptor sets / push constants
        }

        public override void SetMaterialUniforms(XRMaterial material, XRRenderProgram program)
        {
            // Not implemented: Vulkan path will set material parameters via descriptor sets
        }

        private readonly struct BlitImageInfo
        {
            public BlitImageInfo(
                Image image,
                ImageAspectFlags aspectMask,
                uint baseArrayLayer,
                uint layerCount,
                uint mipLevel,
                ImageLayout preferredLayout,
                PipelineStageFlags stageMask,
                AccessFlags accessMask)
            {
                Image = image;
                AspectMask = aspectMask;
                BaseArrayLayer = baseArrayLayer;
                LayerCount = layerCount;
                MipLevel = mipLevel;
                PreferredLayout = preferredLayout;
                StageMask = stageMask;
                AccessMask = accessMask;
            }

            public Image Image { get; }
            public ImageAspectFlags AspectMask { get; }
            public uint BaseArrayLayer { get; }
            public uint LayerCount { get; }
            public uint MipLevel { get; }
            public ImageLayout PreferredLayout { get; }
            public PipelineStageFlags StageMask { get; }
            public AccessFlags AccessMask { get; }
            public bool IsValid => Image.Handle != 0;
        }

        private bool TryResolveBlitImage(XRFrameBuffer frameBuffer, bool wantColor, bool wantDepth, bool wantStencil, out BlitImageInfo info)
        {
            var targets = frameBuffer.Targets;
            if (targets is null)
            {
                info = default;
                return false;
            }

            foreach (var (target, attachment, mipLevel, layerIndex) in targets)
            {
                ImageAspectFlags aspect = ImageAspectFlags.None;

                if (IsColorAttachment(attachment) && wantColor)
                    aspect = ImageAspectFlags.ColorBit;
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
                case XRTexture2D tex2D when GetOrCreateAPIRenderObject(tex2D, true) is VkTexture2D vkTex2D:
                    if (!aspectMask.HasFlag(ImageAspectFlags.ColorBit))
                        return false; // Only treat XRTexture2D as color here.
                    info = new BlitImageInfo(
                        vkTex2D.Image,
                        aspectMask,
                        0,
                        1,
                        (uint)Math.Max(mipLevel, 0),
                        layout,
                        stage,
                        access);
                    return info.IsValid;
                case XRTexture2DArray texArray when GetOrCreateAPIRenderObject(texArray, true) is VkTexture2DArray vkArray:
                    if (!aspectMask.HasFlag(ImageAspectFlags.ColorBit))
                        return false;
                    info = new BlitImageInfo(
                        vkArray.Image,
                        aspectMask,
                        ResolveLayerIndex(layerIndex),
                        1,
                        (uint)Math.Max(mipLevel, 0),
                        layout,
                        stage,
                        access);
                    return info.IsValid;
                case XRTextureCube texCube when GetOrCreateAPIRenderObject(texCube, true) is VkTextureCube vkCube:
                    if (!aspectMask.HasFlag(ImageAspectFlags.ColorBit))
                        return false;
                    info = new BlitImageInfo(
                        vkCube.Image,
                        aspectMask,
                        ResolveLayerIndex(layerIndex),
                        1,
                        (uint)Math.Max(mipLevel, 0),
                        layout,
                        stage,
                        access);
                    return info.IsValid;
                case XRRenderBuffer renderBuffer when GetOrCreateAPIRenderObject(renderBuffer, true) is VkRenderBuffer vkRenderBuffer:
                    // Allow depth/stencil or color depending on the requested aspect and buffer format.
                    if (aspectMask.HasFlag(ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit) && !vkRenderBuffer.Aspect.HasFlag(aspectMask))
                        return false;
                    info = new BlitImageInfo(
                        vkRenderBuffer.Image,
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

        private static bool IsColorAttachment(EFrameBufferAttachment attachment)
            => attachment >= EFrameBufferAttachment.ColorAttachment0 && attachment <= EFrameBufferAttachment.ColorAttachment31;

        private static uint ResolveLayerIndex(int layerIndex)
            => layerIndex >= 0 ? (uint)layerIndex : 0u;

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
                    AspectMask = info.AspectMask,
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
