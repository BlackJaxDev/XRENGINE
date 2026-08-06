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
    public unsafe partial class VulkanRenderer
    {
        private bool RecordCommandBufferLifecycle(
            ref VulkanCommandRecordingContext context)
        {
            scoped PrimaryCommandBufferRecordingState recordingState = default;
            recordingState.RecordedSwapchainWriteCount = ref context.RecordedSwapchainWriteCount;
            recordingState.RecordedSwapchainFinalLayout = ref context.RecordedSwapchainFinalLayout;
            recordingState.RecordingDeferredReason = ref context.RecordingDeferredReason;
            recordingState.QueryFrameOpsRequireRerecord = ref context.QueryFrameOpsRequireRerecord;
            CapturePrimaryCommandBufferRecordingContext(in context, ref recordingState);

            using DesktopSwapchainBarrierExclusionScope desktopSwapchainBarrierExclusion =
                new(SynchronizationThreadContext, recordingState.ExcludeDesktopSwapchainBarriers);
            InitializePrimaryCommandBufferRecordingState(ref recordingState);
            if (!TryPreparePrimaryFrameData(
                    ref recordingState,
                    out VulkanMeshFrameDataReservationManifest frameDataManifest))
                return false;

            using VulkanMeshFrameDataManifestRecordingScope frameDataManifestScope = new(frameDataManifest);
            using VulkanCpuStageScope primaryCommandEncodingStage =
                new(EVulkanCpuStage.PrimaryCommandEncoding);
            PreparePrimaryCommandEncoding(ref recordingState);
            using FrameOpResourcePlannerRecordingScope frameOpResourcePlannerRecordingScope =
                EnterFrameOpResourcePlannerRecordingScope();
            FrameOpResourcePlannerSwitchingState plannerSwitchingState =
                ActiveFrameOpResourcePlannerSwitchingState;
            if (plannerSwitchingState.ActiveKeys.Count > 0 &&
                FrameOpContextHasPlannerResources(recordingState.InitialContext) &&
                !TryActivateFrameOpResourcePlannerState(recordingState.InitialContext))
            {
                throw new VulkanPlanPreconditionException(
                    "Primary command encoding could not activate the sealed planner state for its initial frame-op context.");
            }
            InitializePrimaryCommandEncodingState(ref recordingState);

            try
            {
                RecordPrimaryOperations(ref recordingState);

                FinalizePrimaryCommandRecording(ref recordingState);

                if (!EndPrimaryCommandBuffer(ref recordingState))
                    return false;
            }
            catch (VulkanPlanPreconditionException exception)
            {
                recordingState.RecordingDeferredReason = exception.Message;
                _ = TryAbandonCommandBufferRecording(recordingState.CommandBuffer);
                return false;
            }
            finally
            {
                CleanupPrimaryCommandRecording(ref recordingState);
            }

            PublishPrimaryCommandRecordingResults(ref recordingState);
            return true;
        }

        internal static bool ShouldRefreshUnwrittenSwapchainForPresent(
            bool touchedSwapchain,
            bool transitionSwapchainToPresent)
            => !touchedSwapchain && transitionSwapchainToPresent;

        internal static bool ShouldRecordUnwrittenSwapchainInitializationClear(
            bool hasRecordedSwapchainWrite,
            bool transitionSwapchainToPresent,
            bool imageWasEverPresented,
            bool refreshedFromLastPresentSource)
            => !hasRecordedSwapchainWrite &&
               transitionSwapchainToPresent &&
               !imageWasEverPresented &&
               !refreshedFromLastPresentSource;
    }
}
