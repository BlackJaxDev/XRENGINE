using System;
using System.Diagnostics;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering.Vulkan
{
    internal sealed unsafe partial class VulkanFrameLoop
    {
        private void DrainSkippedResizeFrameOps(string reason)
        {
            FrameOp[] droppedOps = _framePlanner.Operations.DrainPending();
            VulkanCommandSynchronizationState.FailUnsubmittedSubmissionMarkers(
                droppedOps);
            var liveFramebufferSize = DesktopWsiOutput.EffectiveFramebufferSize;
            var resizeExtents = DesktopWsiOutput.ResizeExtents;

            Debug.VulkanEvery(
                $"Vulkan.Frame.{GetHashCode()}.ResizeSkip",
                TimeSpan.FromMilliseconds(500),
                "[Vulkan] Skipping present tick while resize/presentation resources settle. Reason={0} DroppedFrameOps={1} Live={2}x{3} Swapchain={4}x{5} Presentation={6}x{7} Output={8}x{9} Internal={10}x{11}",
                reason,
                droppedOps.Length,
                liveFramebufferSize.X,
                liveFramebufferSize.Y,
                OutputRuntime.Desktop.Extent.Width,
                OutputRuntime.Desktop.Extent.Height,
                resizeExtents.PresentationExtent.X,
                resizeExtents.PresentationExtent.Y,
                resizeExtents.PipelineOutputExtent.X,
                resizeExtents.PipelineOutputExtent.Y,
                resizeExtents.FullInternalExtent.X,
                resizeExtents.FullInternalExtent.Y);
        }


        private bool TryGetViewportResourceBlocker(bool allowInteractiveDisplayMismatch, out string reason)
        {
            reason = string.Empty;

            var viewports = DesktopWsiOutput.Window.Viewports;
            for (int i = 0; i < viewports.Count; i++)
            {
                XRViewport viewport = viewports[i];
                if (viewport.Width <= 0 || viewport.Height <= 0)
                    continue;

                XRRenderPipelineInstance instance = viewport.RenderPipelineInstance;
                if (instance.SkippedResizeCatchUpThisFrame)
                {
                    reason = $"VP[{viewport.Index}] skipped command-chain execution this frame while resize resources catch up";
                    RecordResourceCatchUpProgress(viewport, instance.ActiveGeneration, instance.PendingGeneration, reason);
                    return true;
                }

                RenderResourceGeneration? activeGeneration = instance.ActiveGeneration;
                RenderResourceGeneration? pendingGeneration = instance.PendingGeneration;
                uint displayWidth = (uint)Math.Max(1, viewport.Width);
                uint displayHeight = (uint)Math.Max(1, viewport.Height);
                uint internalWidth = (uint)Math.Max(1, viewport.InternalWidth);
                uint internalHeight = (uint)Math.Max(1, viewport.InternalHeight);

                if (activeGeneration is null)
                {
                    reason = $"VP[{viewport.Index}] has no active resource generation; pending={pendingGeneration?.Key.ToString() ?? "<none>"}";
                    RecordResourceCatchUpProgress(viewport, activeGeneration, pendingGeneration, reason);
                    return true;
                }

                ResourceGenerationKey key = activeGeneration.Key;
                if (key.DisplayWidth == displayWidth &&
                    key.DisplayHeight == displayHeight &&
                    key.InternalWidth == internalWidth &&
                    key.InternalHeight == internalHeight)
                {
                    continue;
                }

                bool internalMatches =
                    key.InternalWidth == internalWidth &&
                    key.InternalHeight == internalHeight;

                if (allowInteractiveDisplayMismatch && internalMatches)
                {
                    Debug.VulkanEvery(
                        $"Vulkan.Frame.{GetHashCode()}.InteractiveDisplayResourceMismatch.{viewport.Index}",
                        TimeSpan.FromMilliseconds(500),
                        "[Vulkan] Allowing presentation-only display mismatch during interactive resize. VP[{0}] activeDisplay={1}x{2} currentDisplay={3}x{4} internal={5}x{6}",
                        viewport.Index,
                        key.DisplayWidth,
                        key.DisplayHeight,
                        displayWidth,
                        displayHeight,
                        internalWidth,
                        internalHeight);
                    continue;
                }

                bool pendingMatchesCurrent =
                    pendingGeneration is not null &&
                    pendingGeneration.Key.DisplayWidth == displayWidth &&
                    pendingGeneration.Key.DisplayHeight == displayHeight &&
                    pendingGeneration.Key.InternalWidth == internalWidth &&
                    pendingGeneration.Key.InternalHeight == internalHeight;

                if (!pendingMatchesCurrent)
                {
                    _ = instance.RequestResourceGeneration(
                        (int)displayWidth,
                        (int)displayHeight,
                        (int)internalWidth,
                        (int)internalHeight,
                        "VulkanResizeResourceMismatch");

                    pendingGeneration = instance.PendingGeneration;
                    pendingMatchesCurrent =
                        pendingGeneration is not null &&
                        pendingGeneration.Key.DisplayWidth == displayWidth &&
                        pendingGeneration.Key.DisplayHeight == displayHeight &&
                        pendingGeneration.Key.InternalWidth == internalWidth &&
                        pendingGeneration.Key.InternalHeight == internalHeight;
                }

                if (pendingMatchesCurrent)
                {
                    reason =
                        $"VP[{viewport.Index}] swapchain extent converged; presentation remains paused while generation catches up. " +
                        $"Active={key} Pending={pendingGeneration!.Key}";
                    RecordResourceCatchUpProgress(viewport, activeGeneration, pendingGeneration, reason);
                    return true;
                }

                reason =
                    $"VP[{viewport.Index}] active={key.DisplayWidth}x{key.DisplayHeight}/{key.InternalWidth}x{key.InternalHeight} " +
                    $"current={displayWidth}x{displayHeight}/{internalWidth}x{internalHeight} pending={pendingGeneration?.Key.ToString() ?? "<none>"}";
                RecordResourceCatchUpProgress(viewport, activeGeneration, pendingGeneration, reason);
                return true;
            }

            ResetResourceCatchUpProgress();
            return false;
        }

        private void RecordResourceCatchUpProgress(
            XRViewport viewport,
            RenderResourceGeneration? activeGeneration,
            RenderResourceGeneration? pendingGeneration,
            string reason)
        {
            long now = Stopwatch.GetTimestamp();
            (ulong blockedFrames, TimeSpan elapsed) =
                RecordResourceCatchUpProgress(now);
            Debug.VulkanEvery(
                $"Vulkan.Frame.{GetHashCode()}.ResourceCatchUpProgress.{viewport.Index}",
                TimeSpan.FromMilliseconds(250),
                "[Vulkan][ResizeConvergence] Managed resources catching up after swapchain convergence. VP={0} BlockedFrames={1} ElapsedMs={2:F1} Swapchain={3}x{4} Active={5} Pending={6} PendingStatus={7} Reason={8}",
                viewport.Index,
                blockedFrames,
                elapsed.TotalMilliseconds,
                OutputRuntime.Desktop.Extent.Width,
                OutputRuntime.Desktop.Extent.Height,
                activeGeneration?.Key.ToString() ?? "<none>",
                pendingGeneration?.Key.ToString() ?? "<none>",
                pendingGeneration?.Status.ToString() ?? "<none>",
                reason);
        }

    }
}
