using System;
using System.Collections.Generic;

namespace XREngine.Rendering.Vulkan
{
    public unsafe partial class VulkanRenderer
    {
        /// <summary>
        /// Stack-only state shared by the command-buffer scheduling phases for one
        /// swapchain image. Keeping the mutable phase data here avoids both per-frame
        /// allocations and sprawling helper parameter lists.
        /// </summary>
        private ref struct CommandBufferLifecycleState
        {
            public CommandBufferLifecycleState(
                uint imageIndex,
                bool preserveSwapchainForOverlay)
            {
                this = default;
                ImageIndex = imageIndex;
                PreserveSwapchainForOverlay = preserveSwapchainForOverlay;
                FrameOperations = Array.Empty<FrameOp>();
                DynamicUiOperations = Array.Empty<FrameOp>();
                Scratch = null!;
                PrimaryCommandPlan = null!;
                Variant = null!;
            }

            public uint ImageIndex;
            public bool PreserveSwapchainForOverlay;
            public bool ImageForcedDirty;
            public long EnsureStartDirtyGeneration;
            public bool FrameOpSignatureDirty;
            public bool PlannerDirty;
            public bool ProfilerDirty;
            public bool FrameDataDirty;
            public bool DynamicUiDirty;
            public bool SwapchainLifecycleDirty;
            public bool CommandChainPrimaryDirty;
            public bool PrimaryFrameStateDirty;
            public string? PrimaryFrameStateDirtyReason;
            public VulkanImageEntryStateMismatch PrimaryImageEntryStateMismatch;
            public PrimaryCommandBufferDirtyReason CommandChainPrimaryDirtyReason;
            public int CommandBufferImageSlot;
            public bool SwapchainImageEverPresentedAtRecord;
            public bool GpuPipelineProfilingActive;
            public bool GpuProfilerCommandBufferStateDirty;

            /// <summary>
            /// Mutable producer stream until <see cref="SealFramePlan"/> replaces
            /// it with frame-slot-owned immutable storage.
            /// </summary>
            public FrameOp[] FrameOperations;
            public ulong RawFrameOpsSignature;
            public FrameOp[] DynamicUiOperations;
            public bool HasFrameOperations;
            public ulong FrameOperationsSignature;
            public ulong DynamicUiSignature;
            /// <summary>
            /// Immutable frame-slot-owned lowering of the final static and dynamic
            /// operation arrays. It is available to later lifecycle/recording
            /// stages without re-reading mutable producer collections.
            /// </summary>
            public FramePlan? SealedFramePlan;
            public CommandBufferRecordingScratch Scratch;
            public VulkanPrimaryCommandPlan PrimaryCommandPlan;
            public bool HasQueryFrameOperations;
            public bool RequiresTrackedPresentSourceRefresh;

            public ulong PlannerRevision;
            public FrameOpContext FallbackContext;
            public ulong FrameOpContextFingerprint;
            public ulong FrameOpContextId;
            public CommandBufferGenerationDomains CurrentGenerations;
            public CommandRecordingDependencySignature CurrentDependencySignature;
            public ulong ImageLayoutStartSignature;
            public ulong PreparedCommandChainFastScheduleSignature;
            public bool HasPreparedCommandChainFastScheduleSignature;

            public CommandChainSchedule? CommandChainSchedule;
            public Dictionary<CommandChainKey, CommandChain>? CommandChainCache;
            public VulkanCommandIdentityComponents CommandChainPrimaryIdentityComponents;
            public ulong CommandChainPrimaryGroupSignature;
            public int CommandChainPrimaryGroupCount;
            public bool AllPreparedDrawBindingsUseSecondaryBuffers;
            public ulong CommandChainPrimarySkeletonSignature;
            public PrimaryCommandArtifactOwner Variant;
            public string? ForcedVariantDirtyReason;
            public bool Dirty;
            public bool ForcedDirty;
            public bool HasTextureUploadFrameOperations;
            public CommandRecordingDependencyMismatch DependencyMismatch;
            public bool DynamicUiSecondaryReady;
            public bool DynamicUiFrameDataNeedsRerecord;

            public bool RecordedDynamicUiSecondaryReady;
            public int RecordedSwapchainWriteCount;
            public bool QueryFrameOperationsRequireRerecord;

            public readonly bool HasStaticFrameOperations => FrameOperations.Length > 0;
            public readonly bool HasDynamicUiOperations => DynamicUiOperations.Length > 0;
            public readonly bool DelayDynamicUiOverlayRecording =>
                PreserveSwapchainForOverlay && HasDynamicUiOperations;
            public readonly bool UsingCommandChains => CommandChainSchedule is not null;

            public void SealFramePlan()
            {
                if (SealedFramePlan is not null)
                    return;

                SealedFramePlan = FramePlanBuilder.GetCurrentThread().BuildAndSeal(
                    CommandBufferImageSlot,
                    PlannerRevision,
                    FrameOperationsSignature,
                    DynamicUiSignature,
                    FrameOperations,
                    DynamicUiOperations);
                FrameOperations = SealedFramePlan.GetNativeStaticOperationsForRecording();
                DynamicUiOperations = SealedFramePlan.GetNativeDynamicOverlayOperationsForRecording();
            }

            /// <summary>
            /// Replaces the old publication after the resource planner has
            /// produced a different coherent revision. Existing asynchronous
            /// consumers keep their leased plan slot; the builder selects a new
            /// slot when necessary.
            /// </summary>
            public void ResealFramePlan()
            {
                SealedFramePlan = null;
                SealFramePlan();
            }

            public readonly bool IsSealedFramePlanCurrent()
                => SealedFramePlan is { } plan && plan.MatchesPublication(
                    RuntimeRenderingHostServices.FrameTiming.CurrentRenderFrameId,
                    PlannerRevision,
                    FrameOperationsSignature,
                    DynamicUiSignature);
        }
    }
}
