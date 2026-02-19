using System;
using System.Diagnostics;
using Silk.NET.Vulkan;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace XREngine.Rendering.Vulkan
{
    public unsafe partial class VulkanRenderer
    {
        // =========== Basic Render State ===========

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

        public override void ClearColor(ColorF4 color)
        {
            _state.SetClearColor(color);
            MarkCommandBuffersDirty();
        }

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

        // =========== API Render Object Factory ===========

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

        // =========== Frame Loop ===========

        private const int MAX_FRAMES_IN_FLIGHT = 2;
        private static readonly TimeSpan SwapchainRecreateDebounce = TimeSpan.FromMilliseconds(100);
        private static readonly TimeSpan SwapchainResizeSettleDelay = TimeSpan.FromMilliseconds(250);

        private int currentFrame = 0;
        private ulong _vkDebugFrameCounter = 0;
        private long _swapchainRecreateRequestedAt;
        private long _swapchainResizeLastChangedAt;
        private uint _pendingSurfaceWidth;
        private uint _pendingSurfaceHeight;
        private long _lastFrameCompletedTimestamp;
        private int _consecutiveNotReadyCount;
        private const int MaxConsecutiveNotReadyBeforeRecreate = 3;

        private void ScheduleSwapchainRecreate(string reason)
        {
            long now = Stopwatch.GetTimestamp();
            bool wasInvalidated = _frameBufferInvalidated;
            _frameBufferInvalidated = true;

            if (!wasInvalidated || _swapchainRecreateRequestedAt == 0)
                _swapchainRecreateRequestedAt = now;

            Debug.VulkanEvery(
                $"Vulkan.Frame.{GetHashCode()}.RecreateScheduled",
                TimeSpan.FromSeconds(1),
                "[Vulkan] Scheduled debounced swapchain recreate. Reason={0} RequestedAtTicks={1} WasInvalidated={2}",
                reason,
                _swapchainRecreateRequestedAt,
                wasInvalidated);
        }

        private void RecreateSwapchainImmediately(string reason)
        {
            _frameBufferInvalidated = false;
            _swapchainRecreateRequestedAt = 0;
            _swapchainResizeLastChangedAt = 0;
            _pendingSurfaceWidth = 0;
            _pendingSurfaceHeight = 0;

            Debug.VulkanEvery(
                $"Vulkan.Frame.{GetHashCode()}.RecreateImmediate",
                TimeSpan.FromSeconds(1),
                "[Vulkan] Recreating swapchain immediately. Reason={0}",
                reason);

            RecreateSwapChain();
        }

        private bool ShouldRunDebouncedSwapchainRecreate()
        {
            if (!_frameBufferInvalidated)
                return false;

            if (_swapchainRecreateRequestedAt == 0)
                return true;

            return Stopwatch.GetElapsedTime(_swapchainRecreateRequestedAt) >= SwapchainRecreateDebounce;
        }

        protected override void WindowRenderCallback(double delta)
        {
            if (_deviceLost)
                throw new InvalidOperationException(
                    "Vulkan device is lost. Cannot render until the device is recreated.");

            _vkDebugFrameCounter++;

            long frameStartTimestamp = Stopwatch.GetTimestamp();

            // Log large gaps between render frames — helps identify CPU-side stalls that
            // could lead to stale GPU state or TDR timeouts.
            if (_lastFrameCompletedTimestamp != 0)
            {
                TimeSpan gap = Stopwatch.GetElapsedTime(_lastFrameCompletedTimestamp, frameStartTimestamp);
                if (gap > TimeSpan.FromSeconds(5))
                {
                    Debug.VulkanWarning(
                        $"[Vulkan] Frame {_vkDebugFrameCounter}: {gap.TotalSeconds:F1}s gap since last frame completed. " +
                        $"Slot={currentFrame} SlotTimelineValue={_frameSlotTimelineValues?[currentFrame]}");
                }
            }

            TimeSpan waitFenceTime = TimeSpan.Zero;
            TimeSpan acquireImageTime = TimeSpan.Zero;
            TimeSpan recordCommandBufferTime = TimeSpan.Zero;
            TimeSpan submitQueueTime = TimeSpan.Zero;
            TimeSpan trimStagingTime = TimeSpan.Zero;
            TimeSpan presentQueueTime = TimeSpan.Zero;

            try
            {
            // Some platforms/drivers do not reliably emit out-of-date/suboptimal or resize callbacks
            // on every size transition. Proactively compare the live framebuffer size to the current
            // swapchain extent and trigger a rebuild when they diverge.
            var liveFramebufferSize = Window!.FramebufferSize;
            var liveWindowSize = Window.Size;
            uint liveSurfaceWidth = (uint)Math.Max(Math.Max(liveFramebufferSize.X, liveWindowSize.X), 0);
            uint liveSurfaceHeight = (uint)Math.Max(Math.Max(liveFramebufferSize.Y, liveWindowSize.Y), 0);

            bool liveSurfaceValid = liveSurfaceWidth > 0 && liveSurfaceHeight > 0;

            if (liveSurfaceValid)
            {
                if (_pendingSurfaceWidth != liveSurfaceWidth || _pendingSurfaceHeight != liveSurfaceHeight)
                {
                    _pendingSurfaceWidth = liveSurfaceWidth;
                    _pendingSurfaceHeight = liveSurfaceHeight;
                    _swapchainResizeLastChangedAt = Stopwatch.GetTimestamp();
                }
            }
            else
            {
                _pendingSurfaceWidth = 0;
                _pendingSurfaceHeight = 0;
                _swapchainResizeLastChangedAt = 0;
            }

            if (liveSurfaceValid &&
                (liveSurfaceWidth != swapChainExtent.Width || liveSurfaceHeight != swapChainExtent.Height))
            {
                ScheduleSwapchainRecreate("Surface/swapchain size mismatch");

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
            else if (_pendingSurfaceWidth == swapChainExtent.Width && _pendingSurfaceHeight == swapChainExtent.Height)
            {
                _pendingSurfaceWidth = 0;
                _pendingSurfaceHeight = 0;
                _swapchainResizeLastChangedAt = 0;
            }

            // If the window resized (or other framebuffer-dependent state changed), rebuild swapchain resources
            // before we acquire/record/submit. Waiting until after present can cause visible stretching/borders.
            if (ShouldRunDebouncedSwapchainRecreate())
            {
                bool hasPendingSurfaceSize = _pendingSurfaceWidth > 0 && _pendingSurfaceHeight > 0;
                bool pendingMatchesLive = !hasPendingSurfaceSize ||
                    (_pendingSurfaceWidth == liveSurfaceWidth && _pendingSurfaceHeight == liveSurfaceHeight);
                bool resizeSettled = !hasPendingSurfaceSize ||
                    (_swapchainResizeLastChangedAt != 0 &&
                     Stopwatch.GetElapsedTime(_swapchainResizeLastChangedAt) >= SwapchainResizeSettleDelay);

                if (pendingMatchesLive && resizeSettled)
                {
                    RecreateSwapchainImmediately("Debounce elapsed before frame acquire (resize settled)");
                }
                else
                {
                    Debug.VulkanEvery(
                        $"Vulkan.Frame.{GetHashCode()}.RecreateDeferredForResizeSettle",
                        TimeSpan.FromSeconds(1),
                        "[Vulkan] Debounce elapsed but resize is still active. Deferring swapchain recreate. Pending={0}x{1} Live={2}x{3} Settled={4}",
                        _pendingSurfaceWidth,
                        _pendingSurfaceHeight,
                        liveSurfaceWidth,
                        liveSurfaceHeight,
                        resizeSettled);
                }
            }

            // 1. Wait for the previous submission associated with this in-flight slot.
            long stageStartTimestamp = Stopwatch.GetTimestamp();
            ulong slotWaitValue = _frameSlotTimelineValues![currentFrame];
            WaitForTimelineValue(_graphicsTimelineSemaphore, slotWaitValue);
            waitFenceTime += Stopwatch.GetElapsedTime(stageStartTimestamp);
            SampleFrameTimingQueries(currentFrame);

            // Now that the GPU has finished all work for this frame slot, destroy
            // buffers and images that were retired during its previous recording.
            DrainRetiredBuffers();
            DrainRetiredImages();

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
            stageStartTimestamp = Stopwatch.GetTimestamp();
            Semaphore acquireSemaphore = acquireBridgeSemaphores![currentFrame];
            var result = khrSwapChain!.AcquireNextImage(device, swapChain, ulong.MaxValue, acquireSemaphore, default, ref imageIndex);
            acquireImageTime += Stopwatch.GetElapsedTime(stageStartTimestamp);

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
                ScheduleSwapchainRecreate("AcquireNextImage returned ErrorOutOfDateKhr");
                return;
            }
            else if (result == Result.ErrorSurfaceLostKhr)
            {
                RecreateSwapchainImmediately("AcquireNextImage returned ErrorSurfaceLostKhr");
                return;
            }
            else if (result == Result.SuboptimalKhr)
            {
                ScheduleSwapchainRecreate("AcquireNextImage returned SuboptimalKhr");
                return;
            }
            else if (result == Result.NotReady)
            {
                _consecutiveNotReadyCount++;
                if (_consecutiveNotReadyCount >= MaxConsecutiveNotReadyBeforeRecreate)
                {
                    Debug.VulkanWarningEvery(
                        $"Vulkan.Frame.{GetHashCode()}.AcquireNotReady.Recreate",
                        TimeSpan.FromSeconds(1),
                        "[Vulkan] AcquireNextImage returned NotReady {0} consecutive times. Recreating swapchain to recover.",
                        _consecutiveNotReadyCount);
                    _consecutiveNotReadyCount = 0;
                    RecreateSwapchainImmediately("Persistent NotReady after failed frame");
                }
                else
                {
                    Debug.VulkanWarningEvery(
                        $"Vulkan.Frame.{GetHashCode()}.AcquireNotReady",
                        TimeSpan.FromSeconds(1),
                        "[Vulkan] AcquireNextImage returned NotReady ({0}/{1}). Skipping this frame.",
                        _consecutiveNotReadyCount,
                        MaxConsecutiveNotReadyBeforeRecreate);
                }
                return;
            }
            else if (result != Result.Success && result != Result.SuboptimalKhr)
            {
                Debug.VulkanWarningEvery(
                    $"Vulkan.Frame.{GetHashCode()}.AcquireFailure.{(int)result}",
                    TimeSpan.FromSeconds(1),
                    "[Vulkan] AcquireNextImage failed with {0}.",
                    result);

                throw new Exception($"Failed to acquire swap chain image ({result}).");
            }

            // Successful acquire — reset the NotReady counter.
            _consecutiveNotReadyCount = 0;

            // 3. Bridge the binary acquire semaphore into the timeline and serialize image reuse by timeline value.
            _acquireTimelineValue = Math.Max(_acquireTimelineValue + 1, _graphicsTimelineValue + 1);

            ulong* acquireSignalValues = stackalloc ulong[1] { _acquireTimelineValue };
            ulong* acquireWaitValues = stackalloc ulong[1] { 0UL };
            Semaphore* acquireWaitSemaphores = stackalloc Semaphore[1] { acquireSemaphore };
            Semaphore* acquireSignalSemaphores = stackalloc Semaphore[1] { _graphicsTimelineSemaphore };
            PipelineStageFlags* acquireWaitStages = stackalloc PipelineStageFlags[1] { PipelineStageFlags.TopOfPipeBit };

            TimelineSemaphoreSubmitInfo acquireBridgeTimelineInfo = new()
            {
                SType = StructureType.TimelineSemaphoreSubmitInfo,
                WaitSemaphoreValueCount = 1,
                PWaitSemaphoreValues = acquireWaitValues,
                SignalSemaphoreValueCount = 1,
                PSignalSemaphoreValues = acquireSignalValues,
            };

            SubmitInfo acquireBridgeSubmit = new()
            {
                SType = StructureType.SubmitInfo,
                PNext = &acquireBridgeTimelineInfo,
                WaitSemaphoreCount = 1,
                PWaitSemaphores = acquireWaitSemaphores,
                PWaitDstStageMask = acquireWaitStages,
                CommandBufferCount = 0,
                PCommandBuffers = null,
                SignalSemaphoreCount = 1,
                PSignalSemaphores = acquireSignalSemaphores,
            };

            Result bridgeResult = Api!.QueueSubmit(graphicsQueue, 1, ref acquireBridgeSubmit, default);
            if (bridgeResult != Result.Success)
            {
                if (bridgeResult == Result.ErrorDeviceLost)
                    _deviceLost = true;

                throw new Exception($"Failed to bridge swapchain acquire semaphore to timeline ({bridgeResult}).");
            }

            if (_swapchainImageTimelineValues is not null && imageIndex < _swapchainImageTimelineValues.Length)
                WaitForTimelineValue(_graphicsTimelineSemaphore, _swapchainImageTimelineValues[imageIndex]);

            // 4. Record the command buffer
            // Note: This currently records a default pass (Clear + ImGui). 
            // Full integration with the engine's render queue happens via frame operations enqueued during the frame.
            stageStartTimestamp = Stopwatch.GetTimestamp();
            try
            {
                EnsureCommandBufferRecorded(imageIndex);
            }
            catch (Exception recordEx)
            {
                recordCommandBufferTime += Stopwatch.GetElapsedTime(stageStartTimestamp);

                // Recording failed (e.g. OOM during resource allocation). The acquire bridge
                // already consumed the binary semaphore and advanced the timeline, but we have
                // no valid command buffer to submit. Advance currentFrame so the next attempt
                // uses the other in-flight slot, and schedule a swapchain recreate which calls
                // DeviceWaitIdle + destroys/recreates all swapchain objects — this returns the
                // acquired image to the presentation engine and resets semaphore state.
                currentFrame = (currentFrame + 1) % MAX_FRAMES_IN_FLIGHT;

                Debug.VulkanWarningEvery(
                    $"Vulkan.Frame.{GetHashCode()}.RecordFailed",
                    TimeSpan.FromSeconds(1),
                    "[Vulkan] Command buffer recording failed. Scheduling swapchain recreate to recover. {0}",
                    recordEx.Message);

                RecreateSwapchainImmediately("Command buffer recording failed — recovering timeline/present state");
                throw; // Re-throw so XRWindow's circuit breaker can track failure count
            }
            recordCommandBufferTime += Stopwatch.GetElapsedTime(stageStartTimestamp);

            // 5. Submit the command buffer with timeline sync.
            _graphicsTimelineValue = Math.Max(_graphicsTimelineValue + 1, _acquireTimelineValue + 1);
            ulong graphicsSignalValue = _graphicsTimelineValue;

            ulong* waitTimelineValues = stackalloc ulong[1] { _acquireTimelineValue };
            ulong* signalTimelineValues = stackalloc ulong[2] { graphicsSignalValue, 0UL };
            Semaphore* waitSemaphores = stackalloc Semaphore[1] { _graphicsTimelineSemaphore };
            var waitStages = stackalloc[] { PipelineStageFlags.ColorAttachmentOutputBit };
            var buffer = _commandBuffers![imageIndex];
            Semaphore presentSemaphore = presentBridgeSemaphores![imageIndex];
            Semaphore* signalSemaphores = stackalloc Semaphore[2] { _graphicsTimelineSemaphore, presentSemaphore };

            TimelineSemaphoreSubmitInfo timelineSubmitInfo = new()
            {
                SType = StructureType.TimelineSemaphoreSubmitInfo,
                WaitSemaphoreValueCount = 1,
                PWaitSemaphoreValues = waitTimelineValues,
                SignalSemaphoreValueCount = 2,
                PSignalSemaphoreValues = signalTimelineValues,
            };

            SubmitInfo submitInfo = new()
            {
                SType = StructureType.SubmitInfo,
                PNext = &timelineSubmitInfo,
            };

            submitInfo = submitInfo with
            {
                WaitSemaphoreCount = 1,
                PWaitSemaphores = waitSemaphores,
                PWaitDstStageMask = waitStages,
                CommandBufferCount = 1,
                PCommandBuffers = &buffer
            };

            submitInfo = submitInfo with
            {
                SignalSemaphoreCount = 2,
                PSignalSemaphores = signalSemaphores,
            };

            stageStartTimestamp = Stopwatch.GetTimestamp();
            Result submitResult;
            lock (_oneTimeSubmitLock)
                submitResult = Api!.QueueSubmit(graphicsQueue, 1, ref submitInfo, default);

            if (submitResult != Result.Success)
            {
                if (submitResult == Result.ErrorDeviceLost)
                    _deviceLost = true;

                throw new Exception($"Failed to submit draw command buffer ({submitResult}).");
            }
            submitQueueTime += Stopwatch.GetElapsedTime(stageStartTimestamp);
            MarkFrameTimingSubmitted(currentFrame);

            _frameSlotTimelineValues[currentFrame] = graphicsSignalValue;
            if (_swapchainImageTimelineValues is not null && imageIndex < _swapchainImageTimelineValues.Length)
                _swapchainImageTimelineValues[imageIndex] = graphicsSignalValue;

            // Trim idle staging buffers so the pool does not grow unbounded.
            stageStartTimestamp = Stopwatch.GetTimestamp();
            _stagingManager.Trim(this);
            trimStagingTime += Stopwatch.GetElapsedTime(stageStartTimestamp);

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
                PWaitSemaphores = &presentSemaphore,
                SwapchainCount = 1,
                PSwapchains = swapChains,
                PImageIndices = &imageIndex
            };

            stageStartTimestamp = Stopwatch.GetTimestamp();
            lock (_oneTimeSubmitLock)
                result = khrSwapChain.QueuePresent(presentQueue, ref presentInfo);
            presentQueueTime += Stopwatch.GetElapsedTime(stageStartTimestamp);
            _lastPresentedImageIndex = imageIndex;

            // Track that this swapchain image has been presented at least once,
            // so future frames use PresentSrcKhr (not Undefined) as old layout.
            if (_swapchainImageEverPresented is not null && imageIndex < _swapchainImageEverPresented.Length)
                _swapchainImageEverPresented[imageIndex] = true;

            Debug.VulkanEvery(
                $"Vulkan.Frame.{GetHashCode()}.Present",
                TimeSpan.FromSeconds(1),
                "[Vulkan] Frame={0} PresentedImage={1} Result={2}",
                _vkDebugFrameCounter,
                imageIndex,
                result);

            if (result == Result.ErrorOutOfDateKhr)
            {
                ScheduleSwapchainRecreate("QueuePresent returned ErrorOutOfDateKhr");
            }
            else if (result == Result.SuboptimalKhr)
            {
                ScheduleSwapchainRecreate("QueuePresent returned SuboptimalKhr");
            }
            else if (result != Result.Success)
                throw new Exception("Failed to present swap chain image.");

            if (ShouldRunDebouncedSwapchainRecreate())
                RecreateSwapchainImmediately("Debounce elapsed after present");

            currentFrame = (currentFrame + 1) % MAX_FRAMES_IN_FLIGHT;
            _lastFrameCompletedTimestamp = Stopwatch.GetTimestamp();
            }
            finally
            {
                TimeSpan totalFrameTime = Stopwatch.GetElapsedTime(frameStartTimestamp);
                Engine.Rendering.Stats.RecordVulkanFrameLifecycleTiming(
                    waitFenceTime,
                    acquireImageTime,
                    recordCommandBufferTime,
                    submitQueueTime,
                    trimStagingTime,
                    presentQueueTime,
                    totalFrameTime);
            }
        }
    }
}
