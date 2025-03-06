using ImageMagick;
using Silk.NET.Vulkan;
using System.Numerics;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;

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
            throw new NotImplementedException();
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
        public override void GetScreenshotAsync(BoundingRectangle region, bool withTransparency, Action<MagickImage> imageCallback)
        {
            throw new NotImplementedException();
        }
        public override void ClearColor(ColorF4 color)
        {
            throw new NotImplementedException();
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
        {
            throw new NotImplementedException();
        }
        public override void CropRenderArea(BoundingRectangle region)
        {
            throw new NotImplementedException();
        }
        public override void SetRenderArea(BoundingRectangle region)
        {
            throw new NotImplementedException();
        }

        private const int MAX_FRAMES_IN_FLIGHT = 2;

        private int currentFrame = 0;

        protected override void WindowRenderCallback(double delta)
        {
            Api!.WaitForFences(device, 1, ref inFlightFences![currentFrame], true, ulong.MaxValue);

            uint imageIndex = 0;
            var result = khrSwapChain!.AcquireNextImage(device, swapChain, ulong.MaxValue, imageAvailableSemaphores![currentFrame], default, ref imageIndex);

            if (result == Result.ErrorOutOfDateKhr)
            {
                RecreateSwapChain();
                return;
            }
            else if (result != Result.Success && result != Result.SuboptimalKhr)
                throw new Exception("Failed to acquire swap chain image.");

            //SubmitUniformBuffers(imageIndex);
            //TODO: submit all engine-queued render commands

            if (imagesInFlight![imageIndex].Handle != default)
                Api!.WaitForFences(device, 1, ref imagesInFlight[imageIndex], true, ulong.MaxValue);

            imagesInFlight[imageIndex] = inFlightFences[currentFrame];

            SubmitInfo submitInfo = new()
            {
                SType = StructureType.SubmitInfo,
            };

            var waitSemaphores = stackalloc[] { imageAvailableSemaphores[currentFrame] };
            var waitStages = stackalloc[] { PipelineStageFlags.ColorAttachmentOutputBit };

            var buffer = commandBuffers![imageIndex];

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
    }
}