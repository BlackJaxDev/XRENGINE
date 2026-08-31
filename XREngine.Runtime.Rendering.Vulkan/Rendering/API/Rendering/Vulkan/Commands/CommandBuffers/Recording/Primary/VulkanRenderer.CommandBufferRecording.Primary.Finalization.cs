using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Silk.NET.Vulkan;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering.Vulkan
{
    internal sealed partial class VulkanCommandRuntime
    {
        private unsafe void FinalizePrimaryCommandRecording(
            scoped ref PrimaryCommandBufferRecordingState recordingState)
        {
            using (RuntimeRenderingHostServices.Profiling.StartProfileScope("Vulkan.RecordPrimary.FinalOverlayAndDiagnostics"))
            {
                if (recordingState.PassIndexLabelActive)
                {
                    _deviceContext.CmdEndLabel(recordingState.CommandBuffer);
                    recordingState.PassIndexLabelActive = false;
                }

                bool forceMagentaSwapchain = XREngine.Rendering.RenderDiagnosticsFlags.VkForceSwapchainMagenta;
                bool isPresentNowTransaction =
                    recordingState.Policy.WorkClass ==
                        ERenderOutputWorkClass.PresentNow &&
                    recordingState.TransitionSwapchainToPresent;
                bool requiresFreshEmptyTerminalWrite =
                    recordingState.FramePlan?.RequiresFreshEmptyTerminalWrite == true &&
                    isPresentNowTransaction;
                if (requiresFreshEmptyTerminalWrite &&
                    recordingState.ActualSwapchainWriteCount == 0)
                {
                    RecordFreshEmptyPresentNowTerminalClear(ref recordingState);
                }
                int sceneActualSwapchainWritesBeforeOverlay = recordingState.ActualSwapchainWriteCount;

                ExecuteDynamicUiBatchTextOverlay(ref recordingState);

                bool touchSwapchainForFinalOverlay =
                    sceneActualSwapchainWritesBeforeOverlay > 0 ||
                    forceMagentaSwapchain;

                if (TargetTraceEnabled)
                {
                    Debug.Vulkan(
                        "[VulkanTarget] finalOverlay sceneActualWrites={0} actualWritesAfterOverlay={1} logicalSceneWriters={2} overlayWriters={3} forceMagenta={4} touch={5} renderScope.Target='{6}' activePass={7} activePassName='{8}'",
                        sceneActualSwapchainWritesBeforeOverlay,
                        recordingState.ActualSwapchainWriteCount,
                        recordingState.SceneSwapchainWriters,
                        recordingState.OverlaySwapchainWriters,
                        forceMagentaSwapchain,
                        touchSwapchainForFinalOverlay,
                        recordingState.RenderScope.Target?.Name ?? "<swapchain>",
                        recordingState.ActivePassIndex,
                        recordingState.ActivePassIndex != int.MinValue ? ResolvePassName((recordingState.HasActiveContext ? recordingState.ActiveContext : recordingState.InitialContext).PassMetadata, recordingState.ActivePassIndex) : "<none>");
                }

                if (touchSwapchainForFinalOverlay)
                {
                    // Finish with a swapchain render pass only when this command buffer has
                    // actual swapchain work. Opening an otherwise-empty pass clears the
                    // editor window to the clear color and hides the last valid frame.
                    if (!recordingState.RenderScope.MatchesTarget(null))
                    {
                        EndActiveRenderPass(ref recordingState);
                        BeginRenderPassForTarget(ref recordingState,
                            null,
                            recordingState.ActivePassIndex != int.MinValue ? recordingState.ActivePassIndex : VulkanBarrierPlanner.SwapchainPassIndex,
                            recordingState.HasActiveContext ? recordingState.ActiveContext : recordingState.InitialContext);
                    }

                    // For presentation we want deterministic full-surface state regardless of prior per-viewport scissor.
                    // This also makes resize issues obvious (the clear should cover the entire swapchain extent).
                    Viewport swapViewport = VulkanCommandRuntime.CreateVulkanViewport(recordingState.SwapchainRecordExtent);

                    Rect2D swapScissor = new()
                    {
                        Offset = new Offset2D(0, 0),
                        Extent = recordingState.SwapchainRecordExtent
                    };

                    Api!.CmdSetViewport(recordingState.CommandBuffer, 0, 1, &swapViewport);
                    Api!.CmdSetScissor(recordingState.CommandBuffer, 0, 1, &swapScissor);
                }
                else
                {
                    EndActiveRenderPass(ref recordingState);
                    if (isPresentNowTransaction &&
                        recordingState.ActualSwapchainWriteCount == 0)
                    {
                        throw new VulkanPlanPreconditionException(
                            $"PresentNow frame {recordingState.Policy.SourceFrameId} " +
                            "recorded no fresh swapchain terminal. Replaying a " +
                            "previous presentation source is forbidden.");
                    }

                    bool refreshRequested =
                        !isPresentNowTransaction &&
                        ShouldRefreshUnwrittenSwapchainForPresent(
                            touchSwapchainForFinalOverlay,
                            recordingState.TransitionSwapchainToPresent);
                    bool refreshedFromLastPresentSource =
                        refreshRequested &&
                        TryRefreshUnwrittenSwapchainFromLastWindowPresentSource(ref recordingState);
                    if (ShouldRecordUnwrittenSwapchainInitializationClear(
                            recordingState.ActualSwapchainWriteCount > 0,
                            recordingState.TransitionSwapchainToPresent,
                            recordingState.ImageWasEverPresentedAtRecordStart,
                            refreshedFromLastPresentSource))
                    {
                        int initializationPassIndex = recordingState.ActivePassIndex != int.MinValue
                            ? recordingState.ActivePassIndex
                            : VulkanBarrierPlanner.SwapchainPassIndex;
                        FrameOpContext initializationContext =
                            recordingState.HasActiveContext ? recordingState.ActiveContext : recordingState.InitialContext;
                        BeginRenderPassForTarget(ref recordingState,
                            null,
                            initializationPassIndex,
                            initializationContext);
                        recordingState.SwapchainWriteCount++;
                        recordingState.ActualSwapchainWriteCount++;
                        recordingState.Metrics.SwapchainClearWrites++;
                        recordingState.Metrics.ForcedDiagnosticSwapchainWriters++;
                        MarkSwapchainStaticWriter(ref recordingState,
                            "InitializationClear",
                            "initialized an unwritten swapchain image before its first present",
                            initializationPassIndex,
                            recordingState.Ops.Length,
                            initializationContext.PipelineIdentity);

                        Debug.VulkanEvery(
                            $"Vulkan.UnwrittenSwapchainInitializationClear.{GetHashCode()}",
                            TimeSpan.FromSeconds(1),
                            "[Vulkan] Cleared previously unwritten swapchain image {0} before its first present because no completed present source was available.",
                            recordingState.ImageIndex);
                    }
                    else if (refreshRequested && !refreshedFromLastPresentSource)
                    {
                        TransitionUnwrittenSwapchainToPresent(ref recordingState);
                    }
                }

                bool hasSceneFrameWork = recordingState.Metrics.ClearCount > 0 || recordingState.Metrics.DrawCount > 0 || recordingState.Metrics.BlitCount > 0 || recordingState.Metrics.ComputeCount > 0;
                bool expectsSceneSwapchainWriters =
                    recordingState.TransitionSwapchainToPresent &&
                    !recordingState.Policy.IsExternalSwapchainTarget;
                bool preservingOverlayOnlyFrame =
                    sceneActualSwapchainWritesBeforeOverlay == 0 &&
                    recordingState.SceneSwapchainWriters == 0 &&
                    recordingState.OverlaySwapchainWriters > 0 &&
                    !forceMagentaSwapchain;
                bool preservingPresentedSwapchainImage =
                    sceneActualSwapchainWritesBeforeOverlay == 0 &&
                    recordingState.ActualSwapchainWriteCount == 0 &&
                    recordingState.ImageWasEverPresentedAtRecordStart &&
                    !forceMagentaSwapchain;
                bool missingSceneSwapchainWriters =
                    expectsSceneSwapchainWriters &&
                    hasSceneFrameWork &&
                    recordingState.SceneSwapchainWriters == 0 &&
                    recordingState.ActualSwapchainWriteCount == 0 &&
                    !preservingOverlayOnlyFrame &&
                    !preservingPresentedSwapchainImage;
                if (missingSceneSwapchainWriters)
                {
                    Debug.VulkanWarningEvery(
                        $"Vulkan.MissingSceneSwapchainWrites.{GetHashCode()}",
                        TimeSpan.FromSeconds(10),
                        "[Vulkan][FrameFailure] Scene frame recorded zero pre-overlay swapchain writers (clears={0}, draws={1}, blits={2}, computes={3}, fboOnlyDraws={4}, fboOnlyBlits={5}). Overlay or diagnostic clears may still present.",
                        recordingState.Metrics.ClearCount,
                        recordingState.Metrics.DrawCount,
                        recordingState.Metrics.BlitCount,
                        recordingState.Metrics.ComputeCount,
                        recordingState.Metrics.FboOnlyDrawOps,
                        recordingState.Metrics.FboOnlyBlitOps);
                }
                else if (expectsSceneSwapchainWriters &&
                         recordingState.SwapchainWriteCount == 0 &&
                         !preservingPresentedSwapchainImage)
                {
                    Debug.VulkanWarningEvery(
                        $"Vulkan.NoSwapchainWrites.{GetHashCode()}",
                        TimeSpan.FromSeconds(10),
                        "[Vulkan] No swapchain write commands were recorded this frame (clears={0}, draws={1}, blits={2}, computes={3}). Preserving acquired swapchain image contents when already initialised.",
                        recordingState.Metrics.ClearCount,
                        recordingState.Metrics.DrawCount,
                        recordingState.Metrics.BlitCount,
                        recordingState.Metrics.ComputeCount);
                }

                if (forceMagentaSwapchain)
                {
                    ClearAttachment magentaAttachment = new()
                    {
                        AspectMask = ImageAspectFlags.ColorBit,
                        ColorAttachment = 0,
                        ClearValue = new ClearValue
                        {
                            Color = new ClearColorValue(1f, 0f, 1f, 1f)
                        }
                    };

                    ClearRect clearRect = new()
                    {
                        Rect = new Rect2D
                        {
                            Offset = new Offset2D(0, 0),
                            Extent = recordingState.SwapchainRecordExtent
                        },
                        BaseArrayLayer = 0,
                        LayerCount = 1
                    };

                    Api!.CmdClearAttachments(recordingState.CommandBuffer, 1, &magentaAttachment, 1, &clearRect);
                    recordingState.SwapchainWriteCount++;
                    recordingState.ActualSwapchainWriteCount++;
                    recordingState.Metrics.SwapchainClearWrites++;
                    recordingState.Metrics.ForcedDiagnosticSwapchainWriters++;
                    MarkSwapchainStaticWriter(ref recordingState, "ForceMagenta", "forced debug clear", recordingState.ActivePassIndex, recordingState.Ops.Length, recordingState.HasActiveContext ? recordingState.ActiveContext.PipelineIdentity : 0);

                    Debug.VulkanEvery(
                        $"Vulkan.ForceSwapchainMagenta.{GetHashCode()}",
                        TimeSpan.FromSeconds(1),
                        "[Vulkan] Forced magenta swapchain clear due to XRE_FORCE_SWAPCHAIN_MAGENTA=1.");
                }

                bool needsFrameDiagnosticSummary = recordingState.Metrics.DroppedFrameOps > 0 || missingSceneSwapchainWriters;
                bool shouldUpdateOnScreenDiagnostic =
                    needsFrameDiagnosticSummary ||
                    recordingState.Metrics.DroppedDrawOps > 0 ||
                    Debug.ShouldLogEvery($"Vulkan.OnScreenDiagnostic.{GetHashCode()}", TimeSpan.FromSeconds(1));
                string? swapchainWriterSummary = shouldUpdateOnScreenDiagnostic || needsFrameDiagnosticSummary
                    ? $"{recordingState.SwapchainLastWriter}@p{recordingState.SwapchainLastWriterPass}:w{recordingState.SwapchainWriteCount}(scene={recordingState.SceneSwapchainWriters} overlay={recordingState.OverlaySwapchainWriters} diag={recordingState.Metrics.ForcedDiagnosticSwapchainWriters} C{recordingState.Metrics.SwapchainClearWrites}D{recordingState.SwapchainDrawWrites}B{recordingState.SwapchainBlitWrites}) presentTransitions={recordingState.SwapchainPresentTransitions} ops={recordingState.Ops.Length} fboD={recordingState.Metrics.FboOnlyDrawOps} fboB={recordingState.Metrics.FboOnlyBlitOps} comp={recordingState.Metrics.ComputeCount}"
                    : null;
                if (shouldUpdateOnScreenDiagnostic)
                {
                    string pipelineLabel = recordingState.HasActiveContext
                        ? (!string.IsNullOrWhiteSpace(recordingState.ActiveContext.PipelineInstance?.Pipeline?.GetType().Name)
                            ? $"{recordingState.ActiveContext.PipelineInstance!.Pipeline!.GetType().Name}#{recordingState.ActiveContext.PipelineIdentity}"
                            : $"Pipeline#{recordingState.ActiveContext.PipelineIdentity}")
                        : "None";
                    UpdateVulkanOnScreenDiagnostic(
                        pipelineLabel,
                        recordingState.ClearState.ClearColor,
                        recordingState.Metrics.DroppedDrawOps,
                        recordingState.Metrics.DroppedFrameOps,
                        swapchainWriterSummary!);
                }

                string? frameDiagnosticSummary = null;
                if (needsFrameDiagnosticSummary)
                {
                    frameDiagnosticSummary = BuildVulkanFrameDiagnosticSummary(
                        recordingState.Ops,
                        recordingState.Metrics.ClearCount,
                        recordingState.Metrics.DrawCount,
                        recordingState.Metrics.BlitCount,
                        recordingState.Metrics.ComputeCount,
                        recordingState.SceneSwapchainWriters,
                        recordingState.OverlaySwapchainWriters,
                        recordingState.Metrics.ForcedDiagnosticSwapchainWriters,
                        recordingState.Metrics.FboOnlyDrawOps,
                        recordingState.Metrics.FboOnlyBlitOps,
                        swapchainWriterSummary!,
                        recordingState.HasActiveContext ? recordingState.ActiveContext : recordingState.InitialContext,
                        recordingState.Metrics.FirstFailure);
                }

                RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanFrameDiagnostics(
                    recordingState.Metrics.DroppedFrameOps,
                    recordingState.Metrics.DroppedDrawOps,
                    recordingState.Metrics.DroppedComputeOps,
                    recordingState.SceneSwapchainWriters,
                    recordingState.OverlaySwapchainWriters,
                    recordingState.Metrics.ForcedDiagnosticSwapchainWriters,
                    recordingState.Metrics.FboOnlyDrawOps,
                    recordingState.Metrics.FboOnlyBlitOps,
                    missingSceneSwapchainWriters,
                    recordingState.Metrics.FirstFailure?.OpType,
                    recordingState.Metrics.FirstFailure?.PassIndex ?? int.MinValue,
                    recordingState.Metrics.FirstFailure?.PipelineIdentity ?? 0,
                    recordingState.Metrics.FirstFailure?.ViewportIdentity ?? 0,
                    recordingState.Metrics.FirstFailure?.TargetName,
                    recordingState.Metrics.FirstFailure?.MaterialName,
                    recordingState.Metrics.FirstFailure?.ShaderName,
                    recordingState.Metrics.FirstFailure?.Message,
                    frameDiagnosticSummary);

                recordingState.RecordingScratch.RecordSwapchainWriterCapacityHint = Math.Max(1, recordingState.SwapchainWritesByPipeline.Count);
                recordingState.RecordingScratch.RecordPipelineNameCapacityHint = Math.Max(1, recordingState.PipelineNameByIdentity.Count);
                recordingState.RecordingScratch.RecordMeshDrawSlotCapacityHint = Math.Max(1, recordingState.MeshDrawSlotsByRendererFamily.Count);
                recordingState.RecordingScratch.RecordFboLayoutCapacityHint = Math.Max(1, recordingState.FboLayoutTracking.Count);

                System.Diagnostics.Debug.Assert(
                    recordingState.PrimaryCommandPlan.HasTerminalAction(
                        EVulkanPrimaryPlanAction.EndRendering),
                    "Every primary plan must terminate its final render scope.");
                bool preparePresent =
                    recordingState.PrimaryCommandPlan.HasTerminalAction(
                        EVulkanPrimaryPlanAction.PreparePresent);
                EndActiveRenderPass(ref recordingState, finalClose: preparePresent);

                int expectedPresentTransitions = recordingState.PreserveSwapchainForOverlay || !recordingState.TransitionSwapchainToPresent ? 0 : 1;
                if (recordingState.UsedSwapchainDynamicRendering && recordingState.SwapchainPresentTransitions != expectedPresentTransitions)
                {
                    Debug.VulkanWarningEvery(
                        $"Vulkan.DynamicRendering.PresentTransitions.{GetHashCode()}",
                        TimeSpan.FromSeconds(1),
                        "[Vulkan] Dynamic-rendering output transitioned to its required final layout {0} times this command buffer; expected {1}.",
                        recordingState.SwapchainPresentTransitions,
                        expectedPresentTransitions);
                }

                EndFrameTimingQueries(recordingState.CommandBuffer, recordingState.CommandBufferImageSlot);

                _deviceContext.CmdEndLabel(recordingState.CommandBuffer);

                if (recordingState.OpenXrTargetContext is { } externalTarget &&
                    recordingState.PrimaryCommandPlan.HasTerminalAction(
                        EVulkanPrimaryPlanAction.ReleaseExternalImageOwnership))
                {
                    RecordOpenXrExternalImageReleasePending(
                        recordingState.CommandBuffer,
                        externalTarget.Image,
                        CreateOpenXrRuntimeColorSubresourceRange());
                }
            }
        }

        private void RecordFreshEmptyPresentNowTerminalClear(
            scoped ref PrimaryCommandBufferRecordingState recordingState)
        {
            EndActiveRenderPass(ref recordingState);
            int passIndex = recordingState.ActivePassIndex != int.MinValue
                ? recordingState.ActivePassIndex
                : VulkanBarrierPlanner.SwapchainPassIndex;
            FrameOpContext context = recordingState.HasActiveContext
                ? recordingState.ActiveContext
                : recordingState.InitialContext;
            BeginRenderPassForTarget(
                ref recordingState,
                null,
                passIndex,
                context);
            recordingState.SwapchainWriteCount++;
            recordingState.ActualSwapchainWriteCount++;
            recordingState.Metrics.SwapchainClearWrites++;
            recordingState.Metrics.ForcedDiagnosticSwapchainWriters++;
            MarkSwapchainStaticWriter(
                ref recordingState,
                "EmptyPresentNowClear",
                "published a fresh deterministic terminal for an empty PresentNow frame",
                passIndex,
                recordingState.Ops.Length,
                context.PipelineIdentity);
            Debug.VulkanEvery(
                $"Vulkan.EmptyPresentNowTerminalClear.{GetHashCode()}",
                TimeSpan.FromSeconds(1),
                "[Vulkan] Published a fresh full-surface clear for empty PresentNow frame {0} on image {1}.",
                recordingState.Policy.SourceFrameId,
                recordingState.ImageIndex);
        }

        private bool EndPrimaryCommandBuffer(
            scoped ref PrimaryCommandBufferRecordingState recordingState)
        {
            using (RuntimeRenderingHostServices.Profiling.StartProfileScope("Vulkan.RecordPrimary.EndCommandBuffer"))
            {
                Result endResult = EndCommandBufferTracked(
                    recordingState.CommandBuffer,
                    cacheVariant: true,
                    out string trackingFailure);
                if (endResult != Result.Success)
                    throw new Exception("Failed to record command buffer.");
                if (!string.IsNullOrEmpty(trackingFailure))
                {
                    recordingState.RecordingDeferredReason = trackingFailure;
                    return false;
                }
            }
            return true;
        }

        private static void CleanupPrimaryCommandRecording(
            scoped ref PrimaryCommandBufferRecordingState recordingState)
        {
            if (recordingState.ActiveResourcePlannerScopeSet)
                recordingState.ActiveResourcePlannerScope.Dispose();
            if (recordingState.ActivePipelineOverrideScopeSet)
                recordingState.ActivePipelineOverrideScope.Dispose();
        }

        private static void PublishPrimaryCommandRecordingResults(
            scoped ref PrimaryCommandBufferRecordingState recordingState)
        {
            recordingState.RecordedSwapchainWriteCount =
                recordingState.ActualSwapchainWriteCount;
            recordingState.RecordedSwapchainFinalLayout =
                recordingState.SwapchainFinalLayout;
            recordingState.FrameOpsRequireRerecord =
                recordingState.FrameOpsRequireRerecordLocal;
        }

    }
}
