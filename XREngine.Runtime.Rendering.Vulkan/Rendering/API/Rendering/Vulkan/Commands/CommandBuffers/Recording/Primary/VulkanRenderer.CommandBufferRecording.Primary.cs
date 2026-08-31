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
        private bool RecordCommandBufferLifecycle(
            ref VulkanCommandRecordingContext context)
        {
            scoped PrimaryCommandBufferRecordingState recordingState = default;
            recordingState.RecordedSwapchainWriteCount = ref context.RecordedSwapchainWriteCount;
            recordingState.RecordedSwapchainFinalLayout = ref context.RecordedSwapchainFinalLayout;
            recordingState.RecordingDeferredReason = ref context.RecordingDeferredReason;
            recordingState.FailureKind = ref context.FailureKind;
            recordingState.FrameOpsRequireRerecord = ref context.FrameOpsRequireRerecord;
            CapturePrimaryCommandBufferRecordingContext(in context, ref recordingState);

            InitializePrimaryCommandBufferRecordingState(ref recordingState);
            using VulkanResourceRuntime.ReadOnlyStorageRecordingScope
                readOnlyStorageScope = ResourceRuntime.EnterReadOnlyStorageRecordingScope(
                    recordingState.ReadOnlyStorageAuthority);
            if (!TryPreparePrimaryFrameData(
                    ref recordingState,
                    out VulkanMeshFrameDataReservationManifest frameDataManifest))
                return false;

            using VulkanMeshFrameDataManifestRecordingScope frameDataManifestScope = new(frameDataManifest);
            using VulkanCpuStageScope primaryCommandEncodingStage =
                new(_frameTelemetry, EVulkanCpuStage.PrimaryCommandEncoding);
            using (VulkanCpuStageScope encodingSetupStage =
                   new(_frameTelemetry, EVulkanCpuStage.PrimaryEncodingSetup))
            {
                PreparePrimaryCommandEncoding(ref recordingState);
                InitializePrimaryCommandEncodingState(ref recordingState);
            }

            try
            {
                bool primaryOperationsRecorded;
                using (VulkanCpuStageScope operationLoopStage =
                       new(_frameTelemetry, EVulkanCpuStage.PrimaryOperationLoop))
                {
                    primaryOperationsRecorded =
                        RecordPrimaryOperations(ref recordingState);
                }
                if (!primaryOperationsRecorded)
                {
                    // A rejected plan can leave dynamic rendering active. Do not
                    // attempt to publish or end partial work; the next recording
                    // reset is the Vulkan-authoritative way to discard it.
                    _ = TryAbandonCommandBufferRecording(recordingState.CommandBuffer);
                    return false;
                }

                using (VulkanCpuStageScope finalizationStage =
                       new(_frameTelemetry, EVulkanCpuStage.PrimaryFinalization))
                {
                    FinalizePrimaryCommandRecording(ref recordingState);
                }

                using (VulkanCpuStageScope endCommandBufferStage =
                       new(_frameTelemetry, EVulkanCpuStage.PrimaryEndCommandBuffer))
                {
                    if (!EndPrimaryCommandBuffer(ref recordingState))
                        return false;
                }
            }
            catch (VulkanPlanPreconditionException exception)
            {
                recordingState.RecordingDeferredReason = exception.Message;
                recordingState.FailureKind =
                    EVulkanCommandRecordingFailureKind.ReplanRequired;
                // Plan preconditions are allowed to fail from inside a render
                // scope. Abandon the partial recording directly so Vulkan never
                // receives vkEndCommandBuffer while that scope is active.
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
