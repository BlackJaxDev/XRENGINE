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
using XREngine.Rendering.Vulkan.RenderGraph;

namespace XREngine.Rendering.Vulkan
{

    /// <summary>
    /// Mutable stack-only state shared by primary command-buffer recording phases.
    /// The state owns no resources and introduces no per-frame allocations.
    /// </summary>
    internal ref struct PrimaryCommandBufferRecordingState
    {
        public uint ImageIndex;
        public CommandBuffer CommandBuffer;
        public CommandBuffer DynamicUiBatchTextSecondaryCommandBuffer;
        public FrameOperationSequence Ops;
        public int DynamicUiBatchTextOpCount;
        public bool PreserveSwapchainForOverlay;
        public bool TransitionSwapchainToPresent;
        public bool ExcludeDesktopSwapchainBarriers;
        public bool FrameOpsRequireRerecordLocal;
        public uint FrameDataImageIndex;
        public uint? FrameDataImageIndexOverride;
        public int CommandBufferImageSlot;
        public SwapchainRecordingTarget SwapchainTarget;
        public Extent2D SwapchainRecordExtent;
        public bool ImageWasEverPresentedAtRecordStart;
        public CommandBufferRecordingScratch RecordingScratch;
        public VulkanPrimaryCommandPlan PrimaryCommandPlan;
        public FramePlan? FramePlan;
        public ulong RecordingStaticOperationSignature;
        public VulkanPresentationSourceTuple PresentationSource;
        public VulkanCommandRecordingPolicySnapshot Policy;
        public VulkanPreparedResourcePlanStamp ResourcePlanStamp;
        public VulkanRenderGraphPlan RenderGraphPlan;
        public VulkanCommandClearStateSnapshot ClearState;
        public int[] MeshDrawUniformSlotsByOpIndex;
        public bool[]
            ScheduledCommandChainFrameDataRefreshedByOpIndex;
        public bool[] CommandChainRecordingAdmittedByOpIndex;
        public bool ProgressiveCommandChainPublicationPending;
        public bool CanProgressivelyDeferCommandChainPublication;
        public bool CommandChainPublicationDeferred;
        public int ProgressiveCommandChainAdmittedJobs;
        public int ProgressiveCommandChainAdmittedOperations;
        public int ProgressiveCommandChainDeferredJobs;
        public HashSet<nint> ExecutedCommandChainSecondaryHandles;
        public VulkanPrimarySecondaryArtifactSequence ExecutedCommandChainSecondaryArtifactSequence;
        public Dictionary<VulkanMeshFrameDataRendererFamilyKey, int> MeshDrawSlotsByRendererFamily;
        public Dictionary<VulkanMeshFrameDataRendererFamilyKey, int> MeshFrameDataFamilyBases;
        // Deferred pipeline readiness is keyed by sealed stream ordinal.
        public HashSet<int> PipelineDeferredOperationIndices;
        public FrameOpContext InitialContext;
        public CommandChainKey[]? ScheduledCommandChainKeysByOpIndex;
        public CommandChain?[]? ScheduledCommandChainsByOpIndex;
        public Dictionary<CommandChainKey, CommandChain>? ScheduledCommandChainCache;
        public int MeshSecondaryFallbackEndIndex;
        public int SwapchainPresentTransitions;
        public bool UsedSwapchainDynamicRendering;
        public bool SwapchainInColorAttachmentLayout;
        public ImageLayout SwapchainFinalTargetLayout;
        public ImageLayout SwapchainFinalLayout;
        public int SwapchainWriteCount;
        public int SwapchainDrawWrites;
        public int SwapchainBlitWrites;
        public int SceneSwapchainWriters;
        public int OverlaySwapchainWriters;
        public string SwapchainLastWriter;
        public int SwapchainLastWriterPass;
        public int SwapchainLastWriterOpIndex;
        public Dictionary<int, int> SwapchainWritesByPipeline;
        public Dictionary<int, string> SwapchainWriterLabelByPipeline;
        public Dictionary<int, string> SwapchainWriterDetailByPipeline;
        public Dictionary<int, int> SwapchainWriterDynamicUiDrawCountByPipeline;
        public Dictionary<int, int> SwapchainWriterPassByPipeline;
        public Dictionary<int, int> SwapchainWriterOpIndexByPipeline;
        public Dictionary<int, string> PipelineNameByIdentity;
        public VulkanRenderScopeController RenderScope;
        public VkRenderQuery? ActiveInlineQuery;
        public bool ActiveInlineQueryRecordedDraw;
        public int ActivePassIndex;
        public int ActiveSchedulingIdentity;
        public FrameOpContext ActiveContext;
        public bool HasActiveContext;
        public bool RenderPassLabelActive;
        public RuntimeEngine.Rendering.RenderingPipelineOverrideScope ActivePipelineOverrideScope;
        public bool ActivePipelineOverrideScopeSet;
        public bool SwapchainClearedThisFrame;
        public bool SkipUiPipelineOps;
        public bool SkipUiBatchTextOps;
        public bool SwapchainWrittenOutsideRenderPass;
        public int ActualSwapchainWriteCount;
        public Dictionary<XRFrameBuffer, ImageLayout[]> FboLayoutTracking;
        public CommandChainSchedule? CommandChainSchedule;
        public OpenXrEyeRenderTargetContext? OpenXrTargetContext;
        public ref int RecordedSwapchainWriteCount;
        public ref ImageLayout RecordedSwapchainFinalLayout;
        public ref string RecordingDeferredReason;
        public ref EVulkanCommandRecordingFailureKind FailureKind;
        public ref bool FrameOpsRequireRerecord;
        public ImageLayout InitialSwapchainColorLayout;
        public List<VulkanSecondaryRecordingBucket> SecondaryBuckets;
        public Dictionary<int, VulkanSecondaryRecordingBucket>? SecondaryBucketByStart;
        public FrameOpContext PlannerContext;
        public bool HasPlannerContext;
        public bool PassIndexLabelActive;
        public PrimaryCommandBufferRecordingMetrics Metrics;
    }

}
