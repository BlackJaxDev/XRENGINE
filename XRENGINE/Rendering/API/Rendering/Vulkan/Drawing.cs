using System;
using ImageMagick;
using Silk.NET.Vulkan;
using System.Numerics;
using System.Runtime.InteropServices;
using XREngine;
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
            throw new NotImplementedException();
        }
        public override void CalcDotLuminanceAsync(XRTexture2DArray texture, Action<bool, float> callback, Vector3 luminance, bool genMipmapsNow = true)
        {
            throw new NotImplementedException();
        }
        public override void CalcDotLuminanceFrontAsyncCompute(BoundingRectangle region, bool withTransparency, Vector3 luminance, Action<bool, float> callback)
        {
            throw new NotImplementedException();
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
            throw new NotImplementedException();
        }
        public override void GetDepthAsync(XRFrameBuffer fbo, int x, int y, Action<float> depthCallback)
        {
            throw new NotImplementedException();
        }
        public override void GetPixelAsync(int x, int y, bool withTransparency, Action<ColorF4> colorCallback)
        {
            throw new NotImplementedException();
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

            using var scope = NewCommandScope();

            void DoBlit(BlitImageInfo source, BlitImageInfo destination, Filter filter)
            {
                TransitionForBlit(scope.CommandBuffer, source, source.PreferredLayout, ImageLayout.TransferSrcOptimal, source.AccessMask, AccessFlags.TransferReadBit, source.StageMask, PipelineStageFlags.TransferBit);
                TransitionForBlit(scope.CommandBuffer, destination, destination.PreferredLayout, ImageLayout.TransferDstOptimal, destination.AccessMask, AccessFlags.TransferWriteBit, destination.StageMask, PipelineStageFlags.TransferBit);

                ImageBlit region = BuildImageBlit(source, destination, inX, inY, inW, inH, outX, outY, outW, outH);

                Api!.CmdBlitImage(
                    scope.CommandBuffer,
                    source.Image,
                    ImageLayout.TransferSrcOptimal,
                    destination.Image,
                    ImageLayout.TransferDstOptimal,
                    1,
                    &region,
                    filter);

                TransitionForBlit(scope.CommandBuffer, source, ImageLayout.TransferSrcOptimal, source.PreferredLayout, AccessFlags.TransferReadBit, source.AccessMask, PipelineStageFlags.TransferBit, source.StageMask);
                TransitionForBlit(scope.CommandBuffer, destination, ImageLayout.TransferDstOptimal, destination.PreferredLayout, AccessFlags.TransferWriteBit, destination.AccessMask, PipelineStageFlags.TransferBit, destination.StageMask);
            }

            if (colorBit)
            {
                if (!TryResolveBlitImage(inFBO, wantColor: true, wantDepth: false, wantStencil: false, out BlitImageInfo srcColor))
                {
                    Debug.LogWarning($"Vulkan Blit: Unable to resolve source color attachment for '{inFBO.Name ?? "<unnamed>"}'.");
                }
                else if (!TryResolveBlitImage(outFBO, wantColor: true, wantDepth: false, wantStencil: false, out BlitImageInfo dstColor))
                {
                    Debug.LogWarning($"Vulkan Blit: Unable to resolve destination color attachment for '{outFBO.Name ?? "<unnamed>"}'.");
                }
                else
                {
                    Filter filter = linearFilter ? Filter.Linear : Filter.Nearest;
                    DoBlit(srcColor, dstColor, filter);
                }
            }

            if (depthBit || stencilBit)
            {
                Filter depthFilter = Filter.Nearest; // Vulkan spec: depth/stencil blits must use nearest.
                if (!TryResolveBlitImage(inFBO, wantColor: false, wantDepth: depthBit, wantStencil: stencilBit, out BlitImageInfo srcDepth))
                {
                    Debug.LogWarning($"Vulkan Blit: Skipping depth/stencil blit; source attachment not compatible for '{inFBO.Name ?? "<unnamed>"}'.");
                }
                else if (!TryResolveBlitImage(outFBO, wantColor: false, wantDepth: depthBit, wantStencil: stencilBit, out BlitImageInfo dstDepth))
                {
                    Debug.LogWarning($"Vulkan Blit: Skipping depth/stencil blit; destination attachment not compatible for '{outFBO.Name ?? "<unnamed>"}'.");
                }
                else
                {
                    DoBlit(srcDepth, dstDepth, depthFilter);
                }
            }
        }
        public override void GetScreenshotAsync(BoundingRectangle region, bool withTransparency, Action<MagickImage, int> imageCallback)
        {
            throw new NotImplementedException();
        }
        public override void ClearColor(ColorF4 color)
        {
            _state.SetClearColor(color);
            MarkCommandBuffersDirty();
        }
        public override bool CalcDotLuminance(XRTexture2DArray texture, Vector3 luminance, out float dotLuminance, bool genMipmapsNow)
        {
            throw new NotImplementedException();
        }
        public override bool CalcDotLuminance(XRTexture2D texture, Vector3 luminance, out float dotLuminance, bool genMipmapsNow)
        {
            throw new NotImplementedException();
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

        protected override void WindowRenderCallback(double delta)
        {
            // 1. Wait for the previous frame to finish
            Api!.WaitForFences(device, 1, ref inFlightFences![currentFrame], true, ulong.MaxValue);

            // 2. Acquire the next image from the swap chain
            uint imageIndex = 0;
            var result = khrSwapChain!.AcquireNextImage(device, swapChain, ulong.MaxValue, imageAvailableSemaphores![currentFrame], default, ref imageIndex);

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
            // TODO: This currently records a default pass (Clear + ImGui). 
            // We need to integrate the engine's render queue here or ensure commands were recorded during the frame.
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

        public override void BindDrawIndirectBuffer(XRDataBuffer buffer)
        {
            // TODO: Record binding in command buffer in a later Vulkan implementation
        }

        public override void UnbindDrawIndirectBuffer()
        {
            // No-op for Vulkan for now
        }

        public override void BindParameterBuffer(XRDataBuffer buffer)
        {
            // TODO: Hook up to VK_KHR_draw_indirect_count when implemented
        }

        public override void UnbindParameterBuffer()
        {
            // No-op
        }

        public override void MultiDrawElementsIndirect(uint drawCount, uint stride)
        {
            // TODO: Record vkCmdDrawIndexedIndirect with drawCount/stride when implemented
            throw new NotImplementedException();
        }

        public override void MultiDrawElementsIndirectWithOffset(uint drawCount, uint stride, nuint byteOffset)
        {
            // TODO: Record vkCmdDrawIndexedIndirect with first=offset when implemented
            throw new NotImplementedException();
        }

        public override void MultiDrawElementsIndirectCount(uint maxDrawCount, uint stride, nuint byteOffset)
        {
            // TODO: Use vkCmdDrawIndexedIndirectCountKHR if VK_KHR_draw_indirect_count is available
            throw new NotImplementedException();
        }

        public override void ApplyRenderParameters(XREngine.Rendering.Models.Materials.RenderingParameters parameters)
        {
            // TODO: Bake into pipeline state / dynamic state for Vulkan path
            throw new NotImplementedException();
        }

        public override bool SupportsIndirectCountDraw()
        {
            // TODO: query VK_KHR_draw_indirect_count at device creation
            return false;
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
    }
}
