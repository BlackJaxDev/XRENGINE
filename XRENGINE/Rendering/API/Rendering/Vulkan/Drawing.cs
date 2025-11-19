using ImageMagick;
using Silk.NET.Vulkan;
using System.Numerics;
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
            XRFrameBuffer inFBO,
            XRFrameBuffer outFBO,
            int inX, int inY, uint inW, uint inH,
            int outX, int outY, uint outW, uint outH,
            EReadBufferMode readBufferMode,
            bool colorBit, bool depthBit, bool stencilBit,
            bool linearFilter)
        {
            if (inFBO is null || outFBO is null)
                return;
            //var commandBuffer = BeginSingleTimeCommands();
            ImageBlit region = new()
            {
                SrcSubresource = new ImageSubresourceLayers
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    MipLevel = 0,
                    BaseArrayLayer = 0,
                    LayerCount = 1
                },
                SrcOffsets = new ImageBlit.SrcOffsetsBuffer()
                {
                    Element0 = new Offset3D { X = inX, Y = inY, Z = 0 },
                    Element1 = new Offset3D { X = (int)inW, Y = (int)inH, Z = 1 }
                },
                DstSubresource = new ImageSubresourceLayers
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    MipLevel = 0,
                    BaseArrayLayer = 0,
                    LayerCount = 1
                },
                DstOffsets = new ImageBlit.DstOffsetsBuffer()
                {
                    Element0 = new Offset3D { X = outX, Y = outY, Z = 0 },
                    Element1 = new Offset3D { X = (int)outW, Y = (int)outH, Z = 1 }
                }
            };
            //commandBuffer.CmdBlitImage(
            //    inFBO.ColorImage,
            //    ImageLayout.TransferSrcOptimal,
            //    outFBO.ColorImage,
            //    ImageLayout.TransferDstOptimal,
            //    1,
            //    &region,
            //    Filter.Linear);
            //EndSingleTimeCommands(commandBuffer);
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
    }
}