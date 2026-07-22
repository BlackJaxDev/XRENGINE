using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Rendering;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private const int OpenXrEyeResourcePlannerStateCount = 2;
    private const uint OpenXrExternalSwapchainTargetImageIndex = 0;
    private const string OpenXrLeftExternalSwapchainTargetName = "<openxr-left-swapchain>";
    private const string OpenXrRightExternalSwapchainTargetName = "<openxr-right-swapchain>";
    private const string OpenXrExternalSwapchainTargetName = "<openxr-swapchain>";
    private const ulong MinDesktopFramesBeforeOpenXrRuntimeSessionStart = 4;
    private const double OpenXrVulkanAllocatorPressureDeferRatio = 0.9;
    private const long OpenXrVulkanAllocatorPressureReserveBytes = 512L * 1024L * 1024L;
    private const double OpenXrVulkanImageAllocationPressurePreflightRatio = 0.84;
    private const long OpenXrVulkanImageAllocationPressureReserveBytes = 768L * 1024L * 1024L;
    private const double OpenXrVulkanImageAllocationCountPreflightRatio = 0.80;
    private const int OpenXrVulkanImageAllocationCountReserve = 768;
    private static readonly TimeSpan OpenXrRuntimeSessionStartDirtyQuietPeriod = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan OpenXrRuntimeSessionStartDirtyMaxWait = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan OpenXrRuntimeSessionStartPendingFrameMaxWait = TimeSpan.FromSeconds(2);
    private static bool TraceOpenXrStereoBlits =>
        XREngine.Rendering.RenderDiagnosticsFlags.VkTraceDraw ||
        XREngine.Rendering.RenderDiagnosticsFlags.VkTraceSwapDraw;

    private readonly record struct OpenXrSwapchainImageViewCacheEntry(ImageView View, Format Format);
    private readonly record struct ResourceRegistryWrapperRefreshStamp(
        int InstanceRevision,
        int DescriptorRevision,
        ulong ResourcePlannerRevision,
        int ResourceAllocatorIdentity);

    private readonly Dictionary<RenderResourceRegistry, ResourceRegistryWrapperRefreshStamp>
        _resourceRegistryWrapperRefreshStamps = new(ReferenceEqualityComparer.Instance);
    private long _openXrRuntimeSessionStartDirtyWaitStartTimestamp;
    private long _openXrRuntimeSessionStartPendingFrameWaitStartTimestamp;

    internal static bool IsOpenXrStrictSpsFaultBoundary(
        EOpenXrStrictSpsFaultInjectionStage requested,
        EOpenXrStrictSpsFaultInjectionStage boundary)
        => requested != EOpenXrStrictSpsFaultInjectionStage.None && requested == boundary;

    internal static bool ShouldFreeTemporaryOpenXrCommandBuffer(
        EVulkanQueueSubmissionDisposition disposition)
        => disposition != EVulkanQueueSubmissionDisposition.SubmittedIncomplete;

    private readonly Dictionary<ulong, OpenXrSwapchainImageViewCacheEntry> _openXrSwapchainImageViews = new();
    private readonly Dictionary<ulong, List<CommandBufferCacheVariant>> _openXrPrimaryCommandBufferVariants = new();
    private readonly object _openXrPrimaryCommandBufferVariantsLock = new();
    private readonly CommandPool[] _openXrEyeCommandPools = new CommandPool[OpenXrEyeResourcePlannerStateCount];
    private readonly object _openXrEyeCommandPoolsLock = new();
    private readonly List<VulkanImportedTexturePendingUpload>[] _openXrEyeRecordedTextureUploadsForSubmit = [new(), new()];
    private readonly List<VulkanImportedTexturePendingUpload> _openXrRecordedTextureUploadsForSubmit = new();
    private readonly OpenXrDepthTarget[] _openXrCachedDepthTargets = new OpenXrDepthTarget[OpenXrEyeResourcePlannerStateCount];
    private readonly Extent2D[] _openXrCachedDepthExtents = new Extent2D[OpenXrEyeResourcePlannerStateCount];
    private int _openXrExternalSwapchainRenderDepth;
    private BoundingRectangle _openXrExternalSwapchainTargetRegion;
    [ThreadStatic]
    private static VulkanRenderer? _threadOpenXrExternalSwapchainRenderer;
    [ThreadStatic]
    private static int _threadOpenXrExternalSwapchainRenderDepth;
    [ThreadStatic]
    private static BoundingRectangle _threadOpenXrExternalSwapchainTargetRegion;
    [ThreadStatic]
    private static int _threadOpenXrExternalSwapchainTargetIdentity;
    [ThreadStatic]
    private static string? _threadOpenXrExternalSwapchainTargetName;
    [ThreadStatic]
    private static EVulkanFrameOpContextKind _threadOpenXrExternalSwapchainContextKind;
    private int _openXrExternalSwapchainPrewarmDepth;
    private int _synchronousResourceUploadBlockDepth;
    [ThreadStatic]
    private static VulkanRenderer? _threadSynchronousResourceUploadBlockRenderer;
    [ThreadStatic]
    private static int _threadSynchronousResourceUploadBlockDepth;
    private readonly Dictionary<OpenXrViewResourcePlannerContextKey, ResourcePlannerRuntimeState> _openXrResourcePlannerStates = new();
    private readonly object _openXrResourcePlannerStatesLock = new();
    [ThreadStatic]
    private static VulkanRenderer? _threadOpenXrResourcePlannerScopeRenderer;
    [ThreadStatic]
    private static OpenXrViewResourcePlannerContextKey _threadOpenXrResourcePlannerScopeKey;
    [ThreadStatic]
    private static int _threadOpenXrResourcePlannerScopeDepth;

    public override bool IsRenderingExternalSwapchainTarget => IsThreadOpenXrExternalSwapchainTarget;
    internal bool IsPrewarmingOpenXrExternalSwapchainTarget =>
        IsThreadOpenXrExternalSwapchainTarget &&
        Volatile.Read(ref _openXrExternalSwapchainPrewarmDepth) > 0;
    public override bool AllowSynchronousResourceUploads
        => !IsThreadSynchronousResourceUploadBlocked &&
           Volatile.Read(ref _synchronousResourceUploadBlockDepth) == 0;

    private bool IsThreadOpenXrExternalSwapchainTarget =>
        ReferenceEquals(_threadOpenXrExternalSwapchainRenderer, this) &&
        _threadOpenXrExternalSwapchainRenderDepth > 0;

    private bool IsThreadSynchronousResourceUploadBlocked =>
        ReferenceEquals(_threadSynchronousResourceUploadBlockRenderer, this) &&
        _threadSynchronousResourceUploadBlockDepth > 0;

    internal IDisposable BlockSynchronousResourceUploads(string reason)
    {
        return new SynchronousResourceUploadBlockScope(this, reason);
    }

    private void LogSynchronousResourceUploadBlock(string reason)
    {
        if (OpenXrVulkanTraceEnabled || DescriptorTraceEnabled)
            Debug.VulkanWarningEvery(
                $"Vulkan.SyncUploads.Blocked.{reason}.{GetHashCode()}",
                TimeSpan.FromSeconds(2),
                "[VulkanDescriptor] syncUploads=blocked reason={0} depth={1}",
                reason,
                Math.Max(
                    _threadSynchronousResourceUploadBlockDepth,
                    Volatile.Read(ref _synchronousResourceUploadBlockDepth)));
    }

    private void ReserveOpenXrFrameDataSlotsIfRequired(string reason)
    {
        if (!ShouldReserveOpenXrFrameDataSlots())
            return;

        int frameDataSlotCount = ResolveOpenXrFrameDataSlotCount(swapChainImages?.Length ?? 0);
        EnsureOpenXrFrameDataSlotCapacity(frameDataSlotCount);
        bool grew = EnsureDescriptorFrameSlotFrameCountFloor(frameDataSlotCount);
        if (grew || OpenXrVulkanTraceEnabled)
        {
            Debug.Vulkan(
                "[OpenXR] Reserved Vulkan frame-data slots for OpenXR. Reason={0} desktopSwapchainImages={1} frameDataSlots={2} descriptorFrameSlots={3}",
                reason,
                swapChainImages?.Length ?? 0,
                frameDataSlotCount,
                DescriptorFrameSlotFrameCount);
        }
    }

    private static bool ShouldReserveOpenXrFrameDataSlots()
        => RuntimeEngine.GameSettings?.VRRuntime == EVRRuntime.OpenXR ||
           RuntimeEngine.VRState.IsOpenXRActive ||
           IsUnitTestingOpenXrLaunchMode() ||
           IsTruthyEnvironmentValue(Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.UnitTestUseOpenXr));

    private static bool IsUnitTestingOpenXrLaunchMode()
    {
        string? unitTestVrMode = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.UnitTestVrMode);
        return string.Equals(unitTestVrMode, "MonadoOpenXR", StringComparison.OrdinalIgnoreCase)
            || string.Equals(unitTestVrMode, "OpenXR", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTruthyEnvironmentValue(string? value)
        => !string.IsNullOrWhiteSpace(value) &&
           (string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "on", StringComparison.OrdinalIgnoreCase));

    private void MarkOpenXrPrimaryCommandBufferVariantsDirty()
    {
        lock (_openXrPrimaryCommandBufferVariantsLock)
        {
            foreach (List<CommandBufferCacheVariant> variants in _openXrPrimaryCommandBufferVariants.Values)
            {
                for (int i = 0; i < variants.Count; i++)
                    variants[i].Dirty = true;
            }
        }
    }

    private void MarkUnsubmittedOpenXrPrimaryCommandBufferDirty(
        in OpenXrRecordedEyeCommandBuffer recorded,
        string reason)
    {
        if (!recorded.OwnedByOpenXrPrimaryCache || recorded.CommandBuffer.Handle == 0)
            return;

        lock (_openXrPrimaryCommandBufferVariantsLock)
        {
            foreach (List<CommandBufferCacheVariant> variants in _openXrPrimaryCommandBufferVariants.Values)
            {
                for (int i = 0; i < variants.Count; i++)
                {
                    CommandBufferCacheVariant variant = variants[i];
                    if (variant.PrimaryCommandBuffer.Handle != recorded.CommandBuffer.Handle)
                        continue;

                    variant.Dirty = true;
                    variant.DirtyReason = reason;
                    return;
                }
            }
        }
    }

    public override bool TryGetExternalSwapchainTargetRegion(out BoundingRectangle region)
    {
        if (IsThreadOpenXrExternalSwapchainTarget &&
            _threadOpenXrExternalSwapchainTargetRegion.Width > 0 &&
            _threadOpenXrExternalSwapchainTargetRegion.Height > 0)
        {
            region = _threadOpenXrExternalSwapchainTargetRegion;
            return true;
        }

        region = default;
        return false;
    }

    private bool TryGetExternalSwapchainTargetIdentity(out int targetIdentity, out string? targetName)
    {
        if (IsThreadOpenXrExternalSwapchainTarget &&
            _threadOpenXrExternalSwapchainTargetIdentity != 0)
        {
            targetIdentity = _threadOpenXrExternalSwapchainTargetIdentity;
            targetName = _threadOpenXrExternalSwapchainTargetName;
            return true;
        }

        targetIdentity = 0;
        targetName = null;
        return false;
    }

    internal IDisposable EnterOpenXrExternalSwapchainRenderScope(
        uint width,
        uint height,
        int targetIdentity = 0,
        string? targetName = null,
        EVulkanFrameOpContextKind contextKind = EVulkanFrameOpContextKind.OpenXrEye)
    {
        if (width == 0 || height == 0)
            throw new InvalidOperationException("OpenXR external swapchain render scope requires a non-zero target extent.");

        if (width > int.MaxValue || height > int.MaxValue)
            throw new InvalidOperationException($"OpenXR external swapchain extent {width}x{height} exceeds supported render-region dimensions.");

        BoundingRectangle region = new(
            0,
            0,
            (int)width,
            (int)height);

        return new OpenXrExternalSwapchainRenderScope(this, region, targetIdentity, targetName, contextKind);
    }

    /// <summary>
    /// Identifies the allocator-owned render plan for an OpenXR view family. The acquired runtime
    /// image is deliberately excluded: runtime image handles, image-view handles, and frame-slot
    /// identity belong to command-buffer and submission variants, while the engine-owned intermediate
    /// resources remain compatible as the runtime rotates swapchain images.
    /// </summary>
    internal static int BuildOpenXrExternalSwapchainPlannerTargetIdentity(uint openXrViewIndex, ulong viewBatchStructuralIdentity = 0UL)
    {
        unchecked
        {
            int hash = 0x4F585254;
            hash = (hash * 397) ^ (int)openXrViewIndex;
            hash = (hash * 397) ^ 0x53494E54;
            hash = (hash * 397) ^ unchecked((int)viewBatchStructuralIdentity);
            hash = (hash * 397) ^ unchecked((int)(viewBatchStructuralIdentity >> 32));
            return hash == 0 ? 1 : hash;
        }
    }

    private static uint ResolveOpenXrExternalSwapchainViewIndex(int resourcePlannerStateIndex)
        => resourcePlannerStateIndex <= 0 ? 0u : (uint)resourcePlannerStateIndex;

    private static string ResolveOpenXrExternalSwapchainTargetName(uint openXrViewIndex)
        => openXrViewIndex switch
        {
            0u => OpenXrLeftExternalSwapchainTargetName,
            1u => OpenXrRightExternalSwapchainTargetName,
            _ => OpenXrExternalSwapchainTargetName,
        };

    internal bool TryRenderOpenXrEyeSwapchain(
        Image image,
        Format format,
        Extent2D extent,
        int resourcePlannerStateIndex,
        uint openXrViewIndex,
        uint openXrImageIndex,
        ViewFoveationContext foveation,
        Action emitFrameOps)
    {
        var request = new OpenXrEyeSwapchainRenderRequest(
            image,
            format,
            extent,
            resourcePlannerStateIndex,
            openXrViewIndex,
            openXrImageIndex,
            foveation,
            emitFrameOps);

        List<VulkanImportedTexturePendingUpload> eyeUploads = GetOpenXrEyeRecordedTextureUploads(openXrViewIndex);
        eyeUploads.Clear();
        if (!TryRecordOpenXrEyeSwapchainCommandBuffer(request, out OpenXrRecordedEyeCommandBuffer recorded))
            return false;

        bool submitted = false;
        bool commandBufferCompleted = false;
        try
        {
            VulkanSubmissionDiagnosticContext diagnosticContext =
                CreateOpenXrSubmissionDiagnosticContext(
                    "OpenXrEyeSubmit",
                    "OpenXrEye",
                    recorded.OpenXrViewIndex,
                    recorded.OpenXrImageIndex,
                    recorded.FrameDataSlotIndex,
                    request.Extent,
                    recorded.FrameOpsSignature,
                    recorded.PlannerRevision,
                    recorded.FrameOpContextId,
                    recorded.ResourceGeneration,
                    recorded.DescriptorGeneration);
            submitted = SubmitAndWaitOpenXrCommandBuffer(recorded.CommandBuffer, out commandBufferCompleted, diagnosticContext);
            if (submitted)
            {
                int publishCount = eyeUploads.Count;
                CompleteOpenXrGpuProfilerSubmission(in recorded);
                PublishRecordedTextureUploadsAfterCompletedSubmit(eyeUploads, "OpenXR eye");
                DrainRetiredResourcesFromCompletedSubmittedFrameSlots();
                if (OpenXrVulkanTraceEnabled)
                {
                    Debug.Vulkan(
                        "[OpenXrVulkan] eye submit completed eye={0} imageIndex={1} frameSlot={2} publishedUploads={3} retiredFlushSlots={4}",
                        recorded.OpenXrViewIndex,
                        recorded.OpenXrImageIndex,
                        recorded.FrameDataSlotIndex,
                        publishCount,
                        MAX_FRAMES_IN_FLIGHT);
                }
            }
            else if (!commandBufferCompleted && !IsDeviceLost)
            {
                int cancelCount = eyeUploads.Count;
                CancelRecordedTextureUploads(eyeUploads, "OpenXR eye command buffer did not complete");
                if (OpenXrVulkanTraceEnabled)
                {
                    Debug.Vulkan(
                        "[OpenXrVulkan] eye submit did not complete eye={0} imageIndex={1} frameSlot={2} cancelledUploads={3}",
                        recorded.OpenXrViewIndex,
                        recorded.OpenXrImageIndex,
                        recorded.FrameDataSlotIndex,
                        cancelCount);
                }
            }

            return submitted;
        }
        finally
        {
            if (!submitted && !commandBufferCompleted && !IsDeviceLost)
            {
                int cancelCount = eyeUploads.Count;
                CancelRecordedTextureUploads(eyeUploads, "OpenXR eye command buffer submit failed");
                if (OpenXrVulkanTraceEnabled)
                {
                    Debug.Vulkan(
                        "[OpenXrVulkan] eye submit failed eye={0} imageIndex={1} frameSlot={2} cancelledUploads={3}",
                        recorded.OpenXrViewIndex,
                        recorded.OpenXrImageIndex,
                        recorded.FrameDataSlotIndex,
                        cancelCount);
                }
            }

            FreeOpenXrRecordedEyeCommandBuffer(recorded);
            eyeUploads.Clear();
        }
    }

    internal bool TryRenderOpenXrEyeSwapchains(
        in OpenXrEyeSwapchainRenderRequest firstEye,
        in OpenXrEyeSwapchainRenderRequest secondEye)
    {
        ClearOpenXrEyeRecordedTextureUploads();
        OpenXrRecordedEyeCommandBuffer firstRecorded = default;
        OpenXrRecordedEyeCommandBuffer secondRecorded = default;
        OpenXrPreparedEyeCommandBufferInput firstPrepared = default;
        OpenXrPreparedEyeCommandBufferInput secondPrepared = default;
        bool hasFirst = false;
        bool hasSecond = false;
        bool submitted = false;
        bool commandBuffersCompleted = false;

        try
        {
            // Planner replacement can retire descriptor references globally. Finish both
            // eyes' resource preparation before either command buffer captures descriptors.
            using (RuntimeRenderingHostServices.Current.StartProfileScope("OpenXR.Vulkan.Batch.PrepareLeftEye"))
            {
                if (!TryPrepareOpenXrEyeSwapchainCommandBuffer(firstEye, out firstPrepared))
                    return false;
            }

            using (RuntimeRenderingHostServices.Current.StartProfileScope("OpenXR.Vulkan.Batch.PrepareRightEye"))
            {
                if (!TryPrepareOpenXrEyeSwapchainCommandBuffer(secondEye, out secondPrepared))
                    return false;
            }

            // Preparing the second eye can grow shared mesh-renderer descriptor/
            // uniform capacity. Re-prewarm both complete op streams only after
            // both reservations are known and before either command buffer is
            // recorded, so no recorded generation can retire between eyes.
            using (RuntimeRenderingHostServices.Current.StartProfileScope("OpenXR.Vulkan.Batch.FinalizeSharedCapacity"))
            {
                PrewarmOpenXrFrameOpResources(firstPrepared.Ops, firstPrepared.TargetContext.FrameDataSlotIndex);
                PrewarmOpenXrFrameOpResources(secondPrepared.Ops, secondPrepared.TargetContext.FrameDataSlotIndex);
            }

            using (RuntimeRenderingHostServices.Current.StartProfileScope("OpenXR.Vulkan.Batch.RecordLeftEye"))
                hasFirst = TryRecordPreparedOpenXrEyeSwapchainCommandBuffer(in firstPrepared, out firstRecorded);
            if (!hasFirst)
                return false;

            using (RuntimeRenderingHostServices.Current.StartProfileScope("OpenXR.Vulkan.Batch.RecordRightEye"))
                hasSecond = TryRecordPreparedOpenXrEyeSwapchainCommandBuffer(in secondPrepared, out secondRecorded);
            if (!hasSecond)
                return false;

            using (RuntimeRenderingHostServices.Current.StartProfileScope("OpenXR.Vulkan.Batch.SubmitAndWait"))
            {
                submitted = SubmitAndWaitOpenXrCommandBuffers(
                    firstRecorded.CommandBuffer,
                    secondRecorded.CommandBuffer,
                    out commandBuffersCompleted,
                    CreateOpenXrBatchSubmissionDiagnosticContext(
                        "OpenXrEyeBatchSubmit",
                        "OpenXrEyeBatch",
                        in firstRecorded,
                        in secondRecorded,
                        firstEye.Extent));
            }

            if (submitted)
            {
                int publishCount = CountOpenXrEyeRecordedTextureUploads();
                CompleteOpenXrGpuProfilerSubmission(in firstRecorded);
                CompleteOpenXrGpuProfilerSubmission(in secondRecorded);
                using (RuntimeRenderingHostServices.Current.StartProfileScope("OpenXR.Vulkan.Batch.PublishUploads"))
                    PublishOpenXrEyeRecordedTextureUploadsAfterCompletedSubmit("OpenXR eye batch");
                using (RuntimeRenderingHostServices.Current.StartProfileScope("OpenXR.Vulkan.Batch.FlushRetired"))
                    DrainRetiredResourcesFromCompletedSubmittedFrameSlots();
                if (OpenXrVulkanTraceEnabled)
                {
                    Debug.Vulkan(
                        "[OpenXrVulkan] eye batch submit completed leftFrameSlot={0} rightFrameSlot={1} publishedUploads={2} retiredFlushSlots={3}",
                        firstRecorded.FrameDataSlotIndex,
                        secondRecorded.FrameDataSlotIndex,
                        publishCount,
                        MAX_FRAMES_IN_FLIGHT);
                }
            }
            else if (!commandBuffersCompleted && !IsDeviceLost)
            {
                int cancelCount = CountOpenXrEyeRecordedTextureUploads();
                CancelOpenXrEyeRecordedTextureUploads("OpenXR eye batch command buffers did not complete");
                if (OpenXrVulkanTraceEnabled)
                {
                    Debug.Vulkan(
                        "[OpenXrVulkan] eye batch submit did not complete leftFrameSlot={0} rightFrameSlot={1} cancelledUploads={2}",
                        firstRecorded.FrameDataSlotIndex,
                        secondRecorded.FrameDataSlotIndex,
                        cancelCount);
                }
            }

            return submitted;
        }
        finally
        {
            if (!submitted && !commandBuffersCompleted && !IsDeviceLost)
            {
                int cancelCount = CountOpenXrEyeRecordedTextureUploads();
                CancelOpenXrEyeRecordedTextureUploads("OpenXR eye batch command buffer submit failed");
                if (OpenXrVulkanTraceEnabled)
                {
                    Debug.Vulkan(
                        "[OpenXrVulkan] eye batch submit failed leftFrameSlot={0} rightFrameSlot={1} cancelledUploads={2}",
                        firstRecorded.FrameDataSlotIndex,
                        secondRecorded.FrameDataSlotIndex,
                        cancelCount);
                }
            }

            if (hasSecond)
                FreeOpenXrRecordedEyeCommandBuffer(secondRecorded);
            if (hasFirst)
                FreeOpenXrRecordedEyeCommandBuffer(firstRecorded);

            ClearOpenXrEyeRecordedTextureUploads();
        }
    }

    internal bool TryRenderOpenXrEyeSwapchainsSinglePassStereo(
        in OpenXrEyeSwapchainRenderRequest leftEye,
        in OpenXrEyeSwapchainRenderRequest rightEye)
    {
        using (RuntimeRenderingHostServices.Current.StartProfileScope("OpenXR.Vulkan.SinglePassStereo.RecordSubmit"))
            return TryRenderOpenXrEyeSwapchains(leftEye, rightEye);
    }

    internal bool TryRenderOpenXrEyeSwapchainsParallelCommandBufferRecording(
        in OpenXrEyeSwapchainRenderRequest leftEye,
        in OpenXrEyeSwapchainRenderRequest rightEye)
    {
        using (RuntimeRenderingHostServices.Current.StartProfileScope("OpenXR.Vulkan.ParallelCommandBufferRecording.RecordSubmit"))
            return TryRenderOpenXrEyeSwapchainsWithParallelEyeWorkers(leftEye, rightEye);
    }

    private bool TryRecordOpenXrEyeSwapchainCommandBuffer(
        in OpenXrEyeSwapchainRenderRequest request,
        out OpenXrRecordedEyeCommandBuffer recorded)
    {
        recorded = default;
        return TryPrepareOpenXrEyeSwapchainCommandBuffer(request, out OpenXrPreparedEyeCommandBufferInput prepared) &&
               TryRecordPreparedOpenXrEyeSwapchainCommandBuffer(in prepared, out recorded);
    }

    private bool TryPrepareOpenXrEyeSwapchainCommandBuffer(
        in OpenXrEyeSwapchainRenderRequest request,
        out OpenXrPreparedEyeCommandBufferInput prepared)
    {
        prepared = default;
        if (request.Image.Handle == 0 || request.Extent.Width == 0 || request.Extent.Height == 0)
            return false;

        if (!UseDynamicRenderingRenderTargets)
        {
            Debug.VulkanWarningEvery(
                $"OpenXR.Vulkan.DynamicRenderingRequired.{GetHashCode()}",
                TimeSpan.FromSeconds(1),
                "[OpenXR] Vulkan OpenXR eye rendering requires dynamic rendering render targets.");
            return false;
        }

        bool drainedFrameOps = false;

        int desktopSwapchainImageCount = swapChainImages?.Length ?? 0;
        int externalTargetIdentity = BuildOpenXrExternalSwapchainPlannerTargetIdentity(
            request.OpenXrViewIndex,
            request.ViewBatchStructuralIdentity);
        using IDisposable externalScope = EnterOpenXrExternalSwapchainRenderScope(
            request.Extent.Width,
            request.Extent.Height,
            externalTargetIdentity,
            ResolveOpenXrExternalSwapchainTargetName(request.OpenXrViewIndex));
        int openXrFrameDataSlotCount = ResolveOpenXrFrameDataSlotCount(desktopSwapchainImageCount);
        uint recordImageIndex = ResolveOpenXrRecordImageIndex(
            request.ResourcePlannerStateIndex,
            desktopSwapchainImageCount);
        uint openXrCommandChainImageIndex = BuildOpenXrCommandChainImageIndex(
            request.OpenXrViewIndex,
            request.OpenXrImageIndex,
            request.Image);
        OpenXrEyeRenderTargetContext targetContext = default;

        try
        {
            using (RuntimeRenderingHostServices.Current.StartProfileScope("OpenXR.Vulkan.RecordEye.PrepareFrameSlot"))
            {
                EnsureOpenXrFrameDataSlotCapacity(openXrFrameDataSlotCount);
                EnsureDescriptorFrameSlotFrameCountFloor(openXrFrameDataSlotCount);
                WaitForOpenXrFrameDataSlot(recordImageIndex, "eye swapchain render");
                DrainRetiredResourcesFromCompletedSubmittedFrameSlots();
                DrainCompletedRecordedTextureUploadPublications();
            }

            if (ShouldDeferOpenXrEyeRenderingWork(out string resourceWorkReason))
            {
                Debug.VulkanWarningEvery(
                    $"OpenXR.Vulkan.DeferEyeResourceWork.{GetHashCode()}",
                    TimeSpan.FromSeconds(1),
                    "[OpenXR] Deferring Vulkan eye command buffer preparation: {0}",
                    resourceWorkReason);
                return false;
            }

            using (RuntimeRenderingHostServices.Current.StartProfileScope("OpenXR.Vulkan.RecordEye.PrepareTargets"))
            {
                ImageView openXrImageView = GetOrCreateOpenXrSwapchainImageView(request.Image, request.Format);
                OpenXrDepthTarget depthTarget = GetOrCreateOpenXrDepthTarget(request.OpenXrViewIndex, request.Extent);

                targetContext = CreateOpenXrEyeRenderTargetContext(
                    request,
                    openXrImageView,
                    depthTarget,
                    recordImageIndex,
                    openXrCommandChainImageIndex);
            }

            using ThreadRenderStateScope renderStateScope = EnterThreadRenderStateScope(
                CreateOpenXrEyeRenderStateTracker(in targetContext));
            using (EnterOpenXrResourcePlannerThreadScope(OpenXrViewResourcePlannerContextKey.FromTarget(in targetContext)))
            {
                if (ShouldDeferOpenXrEyeRenderingWork(out string scopedResourceWorkReason))
                {
                    Debug.VulkanWarningEvery(
                        $"OpenXR.Vulkan.DeferEyeScopedResourceWork.{GetHashCode()}.{targetContext.OpenXrViewIndex}",
                        TimeSpan.FromSeconds(1),
                        "[OpenXR] Deferring Vulkan eye command buffer preparation: {0}",
                        scopedResourceWorkReason);
                    return false;
                }

                ResetDynamicUniformRingBuffer(recordImageIndex);
                FrameOp[] ops;
                using (RuntimeRenderingHostServices.Current.StartProfileScope("OpenXR.Vulkan.RecordEye.EmitFrameOps"))
                    ops = CaptureFrameOpsExcludingTextureUploads(request.EmitFrameOps, out _);
                drainedFrameOps = true;
                ops = FilterDiagnosticSkippedFrameOps(ops);
                if (ops.Length == 0)
                {
                    Debug.VulkanWarningEvery(
                        $"OpenXR.Vulkan.NoEyeFrameOps.{GetHashCode()}",
                        TimeSpan.FromSeconds(1),
                        "[OpenXR] Vulkan eye rendering produced no frame operations.");
                    return false;
                }
                ops = NormalizeOpenXrExternalSwapchainFrameOps(ops, request.Extent);
                ValidateOpenXrExternalFrameOpContexts(
                    ops,
                    request.Extent,
                    request.OpenXrViewIndex,
                    "eye swapchain render");

                ulong plannerRevision;
                ulong frameOpsSignature;
                CommandChainSchedule? commandChainSchedule;
                FrameOpContext plannerContext;
                using (RuntimeRenderingHostServices.Current.StartProfileScope("OpenXR.Vulkan.RecordEye.PlanAndSchedule"))
                {
                    using (RuntimeRenderingHostServices.Current.StartProfileScope("OpenXR.Vulkan.RecordEye.PlanAndSchedule.Sort"))
                        ops = VulkanRenderGraphCompiler.SortFrameOps(ops, CompiledRenderGraph);
                    if (TryDescribeRecentResourceAllocationFailure(out string prePlanFailureReason))
                    {
                        Debug.VulkanWarningEvery(
                            $"OpenXR.Vulkan.EyeFrameOpPlanDeferred.{GetHashCode()}.{targetContext.OpenXrViewIndex}",
                            TimeSpan.FromSeconds(1),
                            "[OpenXR] Deferring Vulkan eye command buffer preparation: {0}",
                            prePlanFailureReason);
                        return false;
                    }

                    plannerContext = PrepareResourcePlannerForFrameOps(ops);
                    if (TryDescribeRecentResourceAllocationFailure(out string postPlanFailureReason))
                    {
                        Debug.VulkanWarningEvery(
                            $"OpenXR.Vulkan.EyeFrameOpPlanFailed.{GetHashCode()}.{targetContext.OpenXrViewIndex}",
                            TimeSpan.FromSeconds(1),
                            "[OpenXR] Deferring Vulkan eye command buffer preparation: {0}",
                            postPlanFailureReason);
                        return false;
                    }

                    if (!TryRefreshFrameOpResourceWrappers(
                        ops,
                        plannerContext,
                        "OpenXR eye prepared frame-op resource refresh",
                        AllowSynchronousResourceUploads,
                        out string refreshFailureReason))
                    {
                        Debug.VulkanWarningEvery(
                            $"OpenXR.Vulkan.EyeFrameOpRefreshDeferred.{GetHashCode()}.{targetContext.OpenXrViewIndex}",
                            TimeSpan.FromSeconds(1),
                            "[OpenXR] Deferring Vulkan eye command buffer preparation: {0}",
                            refreshFailureReason);
                        return false;
                    }
                    if (!PrewarmOpenXrFrameOpResources(
                            ops,
                            targetContext.FrameDataSlotIndex,
                            sealFrameManifest: true))
                    {
                        return false;
                    }
                    plannerRevision = ResourcePlannerRevision;
                    using (RuntimeRenderingHostServices.Current.StartProfileScope("OpenXR.Vulkan.RecordEye.PlanAndSchedule.Signature"))
                    {
                        frameOpsSignature = ComputeFrameOpsSignature(ops);
                    }
                    if (RuntimeEngine.EffectiveSettings.GpuOcclusionCullingMode == EOcclusionCullingMode.CpuQueryAsync)
                    {
                        // Query probes and the visible mesh subset change as prior
                        // results arrive. The current per-draw secondary cache is
                        // neither bounded nor sufficiently reusable for that
                        // external-image workload, so keep the OpenXR primary inline.
                        // CpuQueryAsync itself remains enabled and submits fresh probes.
                        commandChainSchedule = null;
                    }
                    else
                    {
                        commandChainSchedule = TryBuildOpenXrEyeCommandChainSchedule(
                            targetContext.CommandChainImageKey,
                            targetContext.OpenXrViewIndex,
                            targetContext.OpenXrImageIndex,
                            targetContext.Image,
                            ops,
                            frameOpsSignature,
                            plannerRevision);
                    }
                }

                prepared = new OpenXrPreparedEyeCommandBufferInput(
                    request,
                    targetContext,
                    CloneFrameOpsForPreparedOpenXrEye(ops),
                    plannerContext,
                    frameOpsSignature,
                    plannerRevision,
                    commandChainSchedule);

                if (OpenXrVulkanTraceEnabled)
                {
                    Debug.Vulkan(
                        "[OpenXrVulkan] prepared eye={0} swapchainImage={1} ops={2} plannerRevision={3} frameOps=0x{4:X16}",
                        targetContext.OpenXrViewIndex,
                        targetContext.OpenXrImageIndex,
                        ops.Length,
                        plannerRevision,
                        frameOpsSignature);
                }

                return true;
            }
        }
        catch (Exception ex)
        {
            if (!drainedFrameOps)
                _ = DrainFrameOpsExcludingTextureUploads(out _);
            if (IsOpenXrStrictExtentFailure(ex))
                throw;

            Debug.VulkanWarningEvery(
                $"OpenXR.Vulkan.RenderEyeFailed.{GetHashCode()}",
                TimeSpan.FromSeconds(1),
                "[OpenXR] Vulkan eye render failed. Target={0}. Error={1}",
                targetContext.IsValid ? DescribeOpenXrEyeRenderTargetContext(in targetContext) : "<not prepared>",
                ex.Message);
            return false;
        }
    }

    private bool TryRecordPreparedOpenXrEyeSwapchainCommandBuffer(
        in OpenXrPreparedEyeCommandBufferInput prepared,
        out OpenXrRecordedEyeCommandBuffer recorded)
    {
        recorded = default;
        OpenXrEyeRenderTargetContext targetContext = prepared.TargetContext;
        if (!targetContext.IsValid)
            return false;

        using IDisposable externalScope = EnterOpenXrExternalSwapchainRenderScope(
            targetContext.Extent.Width,
            targetContext.Extent.Height,
            BuildOpenXrExternalSwapchainPlannerTargetIdentity(
                targetContext.OpenXrViewIndex),
            ResolveOpenXrExternalSwapchainTargetName(targetContext.OpenXrViewIndex));
        using ThreadRenderStateScope renderStateScope = EnterThreadRenderStateScope(
            CreateOpenXrEyeRenderStateTracker(in targetContext));

        try
        {
            using (EnterOpenXrResourcePlannerThreadScope(OpenXrViewResourcePlannerContextKey.FromTarget(in targetContext)))
            {
                CommandBuffer commandBuffer;
                bool reusedPrimary;
                using (RuntimeRenderingHostServices.Current.StartProfileScope("OpenXR.Vulkan.RecordEye.ReuseOrRecordPrimary"))
                {
                    ulong imageLayoutStartSignature = ComputeImageLayoutStateSignature();
                    FrameOpContext fallbackContext = prepared.Ops.Length > 0
                        ? prepared.Ops[0].Context
                        : prepared.PlannerContext;
                    ulong frameOpContextFingerprint = ComputeCommandBufferFrameOpContextFingerprint(
                        prepared.Ops,
                        Array.Empty<FrameOp>(),
                        fallbackContext);
                    ulong frameOpContextId = ResolveCommandBufferFrameOpContextId(
                        prepared.Ops,
                        Array.Empty<FrameOp>(),
                        fallbackContext);
                    reusedPrimary = TryReuseOpenXrPrimaryCommandBuffer(
                        targetContext.FrameDataSlotIndex,
                        targetContext.CommandChainImageKey,
                        targetContext,
                        prepared.Request,
                        prepared.Ops,
                        prepared.FrameOpsSignature,
                        frameOpContextFingerprint,
                        frameOpContextId,
                        prepared.PlannerRevision,
                        imageLayoutStartSignature,
                        prepared.CommandChainSchedule,
                        out commandBuffer);

                    if (!reusedPrimary)
                    {
                        commandBuffer = RecordOpenXrPrimaryCommandBuffer(
                            targetContext.FrameDataSlotIndex,
                            targetContext.CommandChainImageKey,
                            targetContext,
                            prepared.Request,
                            prepared.Ops,
                            prepared.FrameOpsSignature,
                            frameOpContextFingerprint,
                            frameOpContextId,
                            prepared.PlannerRevision,
                            imageLayoutStartSignature,
                            prepared.CommandChainSchedule);
                        if (commandBuffer.Handle == 0)
                            return false;
                    }
                }

                List<VulkanImportedTexturePendingUpload> eyeUploads = GetOpenXrEyeRecordedTextureUploads(targetContext.OpenXrViewIndex);
                MoveRecordedTextureUploadsForSubmitTo(eyeUploads);
                if (OpenXrVulkanTraceEnabled)
                {
                    Debug.Vulkan(
                        "[OpenXrVulkan] eye={0} swapchainImage={1} commandBuffer=0x{2:X} cached={3} pendingUploads={4}",
                        targetContext.OpenXrViewIndex,
                        targetContext.OpenXrImageIndex,
                        commandBuffer.Handle,
                        reusedPrimary,
                        eyeUploads.Count);
                }

                recorded = new OpenXrRecordedEyeCommandBuffer(
                    commandBuffer,
                    targetContext.OpenXrViewIndex,
                    targetContext.OpenXrImageIndex,
                    targetContext.FrameDataSlotIndex,
                    prepared.FrameOpsSignature,
                    prepared.PlannerRevision,
                    prepared.PlannerContext.ContextId,
                    prepared.PlannerContext.ResourceGeneration,
                    prepared.PlannerContext.DescriptorGeneration,
                    OwnedByOpenXrPrimaryCache: true);
                return true;
            }
        }
        catch (Exception ex)
        {
            Debug.VulkanWarningEvery(
                $"OpenXR.Vulkan.RenderPreparedEyeFailed.{GetHashCode()}",
                TimeSpan.FromSeconds(1),
                "[OpenXR] Vulkan prepared eye record failed. Target={0}. Error={1}",
                DescribeOpenXrEyeRenderTargetContext(in targetContext),
                ex.Message);
            return false;
        }
    }

    internal static int ResolveOpenXrEyeUploadPublicationBufferIndex(uint openXrViewIndex)
        => (int)Math.Min(openXrViewIndex, (uint)(OpenXrEyeResourcePlannerStateCount - 1));

    private List<VulkanImportedTexturePendingUpload> GetOpenXrEyeRecordedTextureUploads(uint openXrViewIndex)
        => _openXrEyeRecordedTextureUploadsForSubmit[ResolveOpenXrEyeUploadPublicationBufferIndex(openXrViewIndex)];

    private void ClearOpenXrEyeRecordedTextureUploads()
    {
        for (int i = 0; i < _openXrEyeRecordedTextureUploadsForSubmit.Length; i++)
            _openXrEyeRecordedTextureUploadsForSubmit[i].Clear();
    }

    private int CountOpenXrEyeRecordedTextureUploads()
    {
        int count = 0;
        for (int i = 0; i < _openXrEyeRecordedTextureUploadsForSubmit.Length; i++)
            count += _openXrEyeRecordedTextureUploadsForSubmit[i].Count;
        return count;
    }

    private CommandPool GetOrCreateOpenXrEyeCommandPool(uint openXrViewIndex)
    {
        int poolIndex = ResolveOpenXrEyeUploadPublicationBufferIndex(openXrViewIndex);
        lock (_openXrEyeCommandPoolsLock)
        {
            CommandPool existing = _openXrEyeCommandPools[poolIndex];
            if (existing.Handle != 0)
                return existing;

            uint graphicsFamily = FamilyQueueIndices.GraphicsFamilyIndex
                ?? throw new InvalidOperationException("Graphics queue family is not available.");
            CommandPool created = CreateCommandPoolForFamily(graphicsFamily);
            _openXrEyeCommandPools[poolIndex] = created;
            SetDebugObjectName(
                ObjectType.CommandPool,
                unchecked((ulong)created.Handle),
                $"OpenXR eye primary command pool[{poolIndex}]");
            return created;
        }
    }

    private void DestroyOpenXrEyeCommandPools()
    {
        lock (_openXrEyeCommandPoolsLock)
        {
            for (int i = 0; i < _openXrEyeCommandPools.Length; i++)
            {
                CommandPool pool = _openXrEyeCommandPools[i];
                if (pool.Handle == 0)
                    continue;

                Api!.DestroyCommandPool(device, pool, null);
                _openXrEyeCommandPools[i] = default;
            }
        }
    }

    private void PublishOpenXrEyeRecordedTextureUploadsAfterCompletedSubmit(string uploadSource)
    {
        for (int i = 0; i < _openXrEyeRecordedTextureUploadsForSubmit.Length; i++)
            PublishRecordedTextureUploadsAfterCompletedSubmit(_openXrEyeRecordedTextureUploadsForSubmit[i], uploadSource);
    }

    private void CancelOpenXrEyeRecordedTextureUploads(string reason)
    {
        for (int i = 0; i < _openXrEyeRecordedTextureUploadsForSubmit.Length; i++)
            CancelRecordedTextureUploads(_openXrEyeRecordedTextureUploadsForSubmit[i], reason);
    }

    internal static OpenXrEyeRenderTargetContext CreateOpenXrEyeRenderTargetContext(
        in OpenXrEyeSwapchainRenderRequest request,
        ImageView imageView,
        in OpenXrDepthTarget depthTarget,
        uint frameDataSlotIndex,
        uint commandChainImageKey)
    {
        BoundingRectangle externalTargetRegion = new(
            0,
            0,
            (int)Math.Min(request.Extent.Width, (uint)int.MaxValue),
            (int)Math.Min(request.Extent.Height, (uint)int.MaxValue));

        return new OpenXrEyeRenderTargetContext(
            request.OpenXrViewIndex,
            request.OpenXrImageIndex,
            request.Image,
            imageView,
            request.Format,
            request.Extent,
            depthTarget.Image,
            depthTarget.Memory,
            depthTarget.View,
            depthTarget.Format,
            depthTarget.Aspect,
            externalTargetRegion,
            commandChainImageKey,
            frameDataSlotIndex,
            request.ResourcePlannerStateIndex,
            FoveationResourceKey: request.Foveation.BackendResourceKey,
            FoveationAttachmentKind: request.Foveation.Attachment.Kind,
            FoveationAttachmentOwnedByResourcePlanner: request.Foveation.Attachment.OwnedByResourcePlanner);
    }

    private static VulkanStateTracker CreateOpenXrEyeRenderStateTracker(
        in OpenXrEyeRenderTargetContext context)
        => CreateOpenXrRenderStateTracker(context.Extent);

    private static VulkanStateTracker CreateOpenXrPrewarmRenderStateTracker(Extent2D extent)
        => CreateOpenXrRenderStateTracker(extent);

    private static VulkanStateTracker CreateOpenXrRenderStateTracker(Extent2D extent)
    {
        VulkanStateTracker state = new();
        state.SetSwapchainExtent(extent);
        state.SetCurrentTargetExtent(extent);
        return state;
    }

    private static string DescribeOpenXrEyeRenderTargetContext(in OpenXrEyeRenderTargetContext context)
        => $"eye={context.OpenXrViewIndex} imageIndex={context.OpenXrImageIndex} image=0x{context.Image.Handle:X} " +
           $"view=0x{context.ImageView.Handle:X} depth=0x{context.DepthImage.Handle:X}/0x{context.DepthView.Handle:X} " +
           $"format={context.ImageFormat} extent={context.Extent.Width}x{context.Extent.Height} " +
           $"frameSlot={context.FrameDataSlotIndex} planner={context.ResourcePlannerStateIndex} " +
           $"foveationKey=0x{context.FoveationResourceKey:X} foveationAttachment={context.FoveationAttachmentKind} " +
           $"foveationOwned={context.FoveationAttachmentOwnedByResourcePlanner} commandKey={context.CommandChainImageKey}";

    private bool TryReuseOpenXrPrimaryCommandBuffer(
        uint recordImageIndex,
        uint commandChainImageIndex,
        in OpenXrEyeRenderTargetContext targetContext,
        in OpenXrEyeSwapchainRenderRequest request,
        FrameOp[] ops,
        ulong frameOpsSignature,
        ulong frameOpContextFingerprint,
        ulong frameOpContextId,
        ulong plannerRevision,
        ulong imageLayoutStartSignature,
        CommandChainSchedule? commandChainSchedule,
        out CommandBuffer commandBuffer)
    {
        commandBuffer = default;
        if (!OpenXrVulkanPrimaryReuseEnabled)
        {
            if (OpenXrVulkanTraceEnabled)
                RecordOpenXrPrimaryReuseMiss("openxr-primary-miss:disabled");
            return false;
        }

        // CpuQueryAsync makes visibility decisions while mesh frame operations are
        // lowered. Reusing a previously recorded primary would freeze that decision
        // set (and can preserve an empty startup frame after commands are published).
        // Re-recording keeps the stereo POV query lifecycle current; the normal
        // reusable-primary path remains available for static/GPU-owned visibility.
        if (RuntimeEngine.EffectiveSettings.GpuOcclusionCullingMode == EOcclusionCullingMode.CpuQueryAsync)
        {
            if (OpenXrVulkanTraceEnabled)
                RecordOpenXrPrimaryReuseMiss("openxr-primary-miss:cpu-query-async");
            return false;
        }

        ulong cacheKey = BuildOpenXrPrimaryCommandBufferCacheKey(commandChainImageIndex, targetContext);
        lock (_openXrPrimaryCommandBufferVariantsLock)
        {
            if (!_openXrPrimaryCommandBufferVariants.TryGetValue(cacheKey, out List<CommandBufferCacheVariant>? variants))
            {
                if (OpenXrVulkanTraceEnabled)
                    RecordOpenXrPrimaryReuseMiss($"openxr-primary-miss:no-variants key=0x{cacheKey:X16}");
                else
                    RecordOpenXrPrimaryReuseMiss("openxr-primary-miss:no-variants");
                return false;
            }

            bool gpuPipelineProfilingActive =
                IsVulkanGpuProfilerCommandBufferInstrumentationEnabled &&
                RenderPipelineGpuProfiler.Instance.IsProfilingActive;
            int commandBufferImageSlot = unchecked((int)Math.Min(recordImageIndex, int.MaxValue));
            bool usingCommandChains = commandChainSchedule is not null;
            bool requiresExactFrameOps = true;
            if (!TryComputeOpenXrPrimaryCommandBufferGroupSignature(
                    commandChainImageIndex,
                    commandChainSchedule,
                    requireReusableChains: true,
                    out global::System.UInt64 commandChainPrimaryGroupSignature,
                    out global::System.Int32 commandChainPrimaryGroupCount))
            {
                if (OpenXrVulkanTraceEnabled)
                {
                    RecordOpenXrPrimaryReuseMiss(
                        $"openxr-primary-miss:chains-not-reusable key=0x{cacheKey:X16} {DescribeOpenXrPrimaryReusableChainMiss(commandChainImageIndex, commandChainSchedule)}");
                }
                else
                {
                    RecordOpenXrPrimaryReuseMiss("openxr-primary-miss:chains-not-reusable");
                }
                return false;
            }

            bool swapchainImageEverPresented = IsSwapchainImageEverPresented(OpenXrExternalSwapchainTargetImageIndex);
            for (int i = 0; i < variants.Count; i++)
            {
                CommandBufferCacheVariant variant = variants[i];
                if (variant.Dirty ||
                    variant.PrimaryCommandBuffer.Handle == 0 ||
                    (requiresExactFrameOps && variant.FrameOpsSignature != frameOpsSignature) ||
                    !TryValidateCommandBufferVariantContext(
                        recordImageIndex,
                        variant,
                        frameOpContextFingerprint,
                        frameOpContextId,
                        "openxr-eye-primary") ||
                    (!usingCommandChains && variant.PlannerRevision != plannerRevision) ||
                    IsCommandBufferVariantImageLayoutStateDirty(variant, imageLayoutStartSignature) ||
                    variant.RecordedSwapchainImageEverPresented != swapchainImageEverPresented ||
                    variant.CommandChainScheduleSignature != (commandChainSchedule?.StructuralSignature ?? ulong.MaxValue) ||
                    variant.CommandChainPrimaryGroupSignature != (commandChainSchedule is null ? ulong.MaxValue : commandChainPrimaryGroupSignature) ||
                    variant.CommandChainPrimaryGroupCount != (commandChainSchedule is null ? -1 : commandChainPrimaryGroupCount) ||
                    IsCommandBufferVariantGpuProfilerStateDirty(variant, gpuPipelineProfilingActive, commandBufferImageSlot))
                {
                    continue;
                }

                _lastReusableFrameDataRefreshFailureReason = null;
                using (RuntimeRenderingHostServices.Current.StartProfileScope("OpenXR.Vulkan.RecordEye.RefreshFrameData"))
                {
                    if (!TryRefreshReusableCommandBufferFrameData(recordImageIndex, ops))
                        return false;
                }

                variant.GpuProfilerActive = gpuPipelineProfilingActive;
                variant.GpuProfilerFrameSlot = gpuPipelineProfilingActive ? commandBufferImageSlot : -1;

                if (HasQueryFrameOps(ops) &&
                    !PrepareQueryFrameOpsForCommandBufferReuse(variant.PrimaryCommandBuffer, ops))
                {
                    if (OpenXrVulkanTraceEnabled)
                        RecordOpenXrPrimaryReuseMiss("openxr-primary-miss:query-pool-prepare");
                    return false;
                }

                variant.LastUsedFrameId = VulkanFrameCounter;
                StoreFrameOpSignatureDebugParts(variant, ops);
                RestoreRecordedImageLayoutEndState(variant);
                PrepareVulkanGpuProfilerReusableSubmission(
                    commandBufferImageSlot,
                    variant,
                    gpuPipelineProfilingActive);
                UpdateVulkanGpuProfilerCommandBufferState(
                    recordImageIndex,
                    gpuPipelineProfilingActive,
                    commandBufferImageSlot);

                RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanCommandBufferCacheOutcome(
                    reusedClean: true,
                    recorded: false,
                    forcedDirty: false,
                    frameOpSignatureDirty: false,
                    plannerDirty: false,
                    profilerDirty: false,
                    dirtyReason: null);
                RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanCommandChainMetrics(primaryCommandBuffersReused: 1);

                EnsureCommandBufferVariantContextBeforeSubmit(
                    recordImageIndex,
                    variant,
                    frameOpContextFingerprint,
                    frameOpContextId,
                    "openxr-eye-primary");
                commandBuffer = variant.PrimaryCommandBuffer;
                if (OpenXrVulkanTraceEnabled)
                {
                    Debug.Vulkan(
                        "[OpenXrVulkan] reused primary eye={0} swapchainImage={1} commandKey={2} recorderSlot={3} commandBuffer=0x{4:X}",
                        targetContext.OpenXrViewIndex,
                        targetContext.OpenXrImageIndex,
                        commandChainImageIndex,
                        recordImageIndex,
                        commandBuffer.Handle);
                }

                return true;
            }

            string compactMissReason = ClassifyOpenXrPrimaryVariantMismatch(
                variants,
                false,
                requiresExactFrameOps,
                usingCommandChains,
                frameOpsSignature,
                frameOpContextFingerprint,
                plannerRevision,
                imageLayoutStartSignature,
                ContainsQueryFrameOp(ops),
                true,
                swapchainImageEverPresented,
                commandChainSchedule,
                commandChainPrimaryGroupSignature,
                commandChainPrimaryGroupCount,
                gpuPipelineProfilingActive,
                commandBufferImageSlot);
            if (OpenXrVulkanTraceEnabled)
            {
                RecordOpenXrPrimaryReuseMiss(
                    $"openxr-primary-miss:no-matching-variant key=0x{cacheKey:X16} variants={variants.Count} first={DescribeOpenXrPrimaryVariantMismatch(
                        variants,
                        requiresExactFrameOps,
                        usingCommandChains,
                        frameOpsSignature,
                        frameOpContextFingerprint,
                        frameOpContextId,
                        plannerRevision,
                        imageLayoutStartSignature,
                        true,
                        swapchainImageEverPresented,
                        commandChainSchedule,
                        commandChainPrimaryGroupSignature,
                        commandChainPrimaryGroupCount,
                        gpuPipelineProfilingActive,
                        commandBufferImageSlot)}");
            }
            else
            {
                RecordOpenXrPrimaryReuseMiss(compactMissReason);
            }
            return false;
        }
    }

    private CommandBuffer RecordOpenXrPrimaryCommandBuffer(
        uint recordImageIndex,
        uint commandChainImageIndex,
        in OpenXrEyeRenderTargetContext targetContext,
        in OpenXrEyeSwapchainRenderRequest request,
        FrameOp[] ops,
        ulong frameOpsSignature,
        ulong frameOpContextFingerprint,
        ulong frameOpContextId,
        ulong plannerRevision,
        ulong imageLayoutStartSignature,
        CommandChainSchedule? commandChainSchedule)
    {
        ulong cacheKey = BuildOpenXrPrimaryCommandBufferCacheKey(commandChainImageIndex, targetContext);
        CommandBufferCacheVariant variant = GetOrCreateOpenXrPrimaryCommandBufferVariant(
            cacheKey,
            commandChainSchedule,
            commandChainImageIndex,
            recordImageIndex,
            targetContext);

        bool gpuPipelineProfilingActive =
            IsVulkanGpuProfilerCommandBufferInstrumentationEnabled &&
            RenderPipelineGpuProfiler.Instance.IsProfilingActive;
        int commandBufferImageSlot = unchecked((int)Math.Min(recordImageIndex, int.MaxValue));
        ulong commandChainPrimaryGroupSignature = ulong.MaxValue;
        int commandChainPrimaryGroupCount = -1;
        if (commandChainSchedule is not null)
        {
            _ = TryComputeOpenXrPrimaryCommandBufferGroupSignature(
                commandChainImageIndex,
                commandChainSchedule,
                requireReusableChains: false,
                out commandChainPrimaryGroupSignature,
                out commandChainPrimaryGroupCount);
            commandChainPrimaryGroupCount = commandChainSchedule.Groups.Length;
        }

        long recordStart = Stopwatch.GetTimestamp();
        _isRecordingCommandBuffer = true;
        int recordedSwapchainWriteCount = 0;
        bool queryFrameOpsRequireRerecord = false;
        ImageLayout swapchainLayoutAfterCommandBuffer;
        try
        {
            BeginRecordedTextureUploadSubmitBatch();
            if (OpenXrVulkanTraceEnabled)
            {
                Debug.Vulkan(
                    "[OpenXrVulkan] record primary target=({0}) targetSlot={1} ops={2}",
                    DescribeOpenXrEyeRenderTargetContext(in targetContext),
                    OpenXrExternalSwapchainTargetImageIndex,
                    ops.Length);
            }

            if (!TryRecordCommandBuffer(
                imageIndex: OpenXrExternalSwapchainTargetImageIndex,
                variant.PrimaryCommandBuffer,
                dynamicUiBatchTextSecondaryCommandBuffer: default,
                ops,
                dynamicUiBatchTextOpCount: 0,
                commandChainSchedule,
                preserveSwapchainForOverlay: false,
                recordedSwapchainWriteCount: out recordedSwapchainWriteCount,
                recordedSwapchainFinalLayout: out swapchainLayoutAfterCommandBuffer,
                recordingDeferredReason: out string recordingDeferredReason,
                queryFrameOpsRequireRerecord: out queryFrameOpsRequireRerecord,
                transitionSwapchainToPresent: false,
                frameDataImageIndexOverride: recordImageIndex,
                openXrTargetContext: targetContext))
            {
                CancelRecordedTextureUploadSubmitBatch(
                    $"OpenXR eye command buffer recording deferred: {recordingDeferredReason}");
                Debug.VulkanWarningEvery(
                    $"OpenXR.Vulkan.EyePrimaryRecordDeferred.{GetHashCode()}",
                    TimeSpan.FromSeconds(1),
                    "[OpenXR] Deferring Vulkan eye primary command buffer recording before vkBeginCommandBuffer: {0}",
                    recordingDeferredReason);
                return default;
            }
        }
        catch
        {
            CancelRecordedTextureUploadSubmitBatch("OpenXR eye command buffer recording failed before upload submit");
            throw;
        }
        finally
        {
            _isRecordingCommandBuffer = false;
        }

        bool wasDirty = variant.Dirty;
        variant.Dirty = false;
        variant.FrameOpsSignature = frameOpsSignature;
        variant.DynamicUiSignature = 0;
        variant.DynamicUiOpCount = 0;
        variant.DynamicUiSecondaryRecorded = false;
        variant.PreserveSwapchainForOverlay = false;
        variant.RecordedFrameOpContextFingerprint = frameOpContextFingerprint;
        variant.RecordedFrameOpContextId = frameOpContextId;
        variant.RecordedSwapchainImageEverPresented = false;
        variant.RecordedSwapchainFinalLayout = swapchainLayoutAfterCommandBuffer;
        variant.RecordedSwapchainWriteCount = recordedSwapchainWriteCount;
        variant.RecordedSwapchainRefreshFromLastPresentSource = false;
        variant.RecordedImageLayoutStartSignature = imageLayoutStartSignature;
        CaptureCommandBufferVariantImageLayoutEndState(variant);
        variant.CommandChainScheduleSignature = commandChainSchedule?.StructuralSignature ?? ulong.MaxValue;
        if (!TryComputeOpenXrPrimaryCommandBufferGroupSignature(
                commandChainImageIndex,
                commandChainSchedule,
                requireReusableChains: false,
                out commandChainPrimaryGroupSignature,
                out commandChainPrimaryGroupCount))
        {
            commandChainPrimaryGroupSignature = ulong.MaxValue;
            commandChainPrimaryGroupCount = -1;
        }
        variant.CommandChainPrimaryGroupSignature = commandChainPrimaryGroupSignature;
        variant.CommandChainPrimaryGroupCount = commandChainPrimaryGroupCount;
        variant.PlannerRevision = plannerRevision;
        variant.GpuProfilerActive = gpuPipelineProfilingActive;
        variant.GpuProfilerFrameSlot = gpuPipelineProfilingActive ? commandBufferImageSlot : -1;
        variant.LastUsedFrameId = VulkanFrameCounter;
        CaptureVulkanGpuProfilerVariantScopes(commandBufferImageSlot, variant);
        StoreFrameOpSignatureDebugParts(variant, ops);
        if (queryFrameOpsRequireRerecord)
            MarkCommandBufferVariantTransient(variant, "query draw was not recorded");
        UpdateVulkanGpuProfilerCommandBufferState(
            recordImageIndex,
            gpuPipelineProfilingActive,
            commandBufferImageSlot);

        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanCommandBufferCacheOutcome(
            reusedClean: false,
            recorded: true,
            forcedDirty: wasDirty,
            frameOpSignatureDirty: false,
            plannerDirty: false,
            profilerDirty: false,
            dirtyReason: wasDirty ? "forced" : null);
        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanCommandChainMetrics(primaryCommandBuffersRecorded: 1);

        if (OpenXrVulkanTraceEnabled)
        {
            double recordMs = (Stopwatch.GetTimestamp() - recordStart) * 1000.0 / Stopwatch.Frequency;
            Debug.Vulkan(
                "[OpenXrVulkan] recorded primary target=({0}) recorderSlot={1} commandBuffer=0x{2:X} recordMs={3:F3}",
                DescribeOpenXrEyeRenderTargetContext(in targetContext),
                recordImageIndex,
                variant.PrimaryCommandBuffer.Handle,
                recordMs);
        }

        EnsureCommandBufferVariantContextBeforeSubmit(
            recordImageIndex,
            variant,
            frameOpContextFingerprint,
            frameOpContextId,
            "recorded-openxr-eye-primary");
        return variant.PrimaryCommandBuffer;
    }

    private CommandBufferCacheVariant GetOrCreateOpenXrPrimaryCommandBufferVariant(
        ulong cacheKey,
        CommandChainSchedule? commandChainSchedule,
        uint commandChainImageIndex,
        uint recordImageIndex)
        => GetOrCreateOpenXrPrimaryCommandBufferVariant(
            cacheKey,
            commandChainSchedule,
            commandChainImageIndex,
            recordImageIndex,
            commandPool,
            "OpenXR mirror primary command buffer variant");

    private CommandBufferCacheVariant GetOrCreateOpenXrPrimaryCommandBufferVariant(
        ulong cacheKey,
        CommandChainSchedule? commandChainSchedule,
        uint commandChainImageIndex,
        uint recordImageIndex,
        in OpenXrEyeRenderTargetContext targetContext)
    {
        CommandPool eyeCommandPool = GetOrCreateOpenXrEyeCommandPool(targetContext.OpenXrViewIndex);
        return GetOrCreateOpenXrPrimaryCommandBufferVariant(
            cacheKey,
            commandChainSchedule,
            commandChainImageIndex,
            recordImageIndex,
            eyeCommandPool,
            $"OpenXR eye primary command buffer variant eye={targetContext.OpenXrViewIndex}");
    }

    private CommandBufferCacheVariant GetOrCreateOpenXrPrimaryCommandBufferVariant(
        ulong cacheKey,
        CommandChainSchedule? commandChainSchedule,
        uint commandChainImageIndex,
        uint recordImageIndex,
        CommandPool ownerPool,
        string allocationLabel)
    {
        lock (_openXrPrimaryCommandBufferVariantsLock)
        {
            if (!_openXrPrimaryCommandBufferVariants.TryGetValue(cacheKey, out List<CommandBufferCacheVariant>? variants))
            {
                variants = [];
                _openXrPrimaryCommandBufferVariants[cacheKey] = variants;
            }

            ulong scheduleSignature = commandChainSchedule?.StructuralSignature ?? ulong.MaxValue;
            ulong groupSignature = ulong.MaxValue;
            int groupCount = -1;
            _ = TryComputeOpenXrPrimaryCommandBufferGroupSignature(
                commandChainImageIndex,
                commandChainSchedule,
                requireReusableChains: false,
                out groupSignature,
                out groupCount);

            for (int i = 0; i < variants.Count; i++)
            {
                CommandBufferCacheVariant variant = variants[i];
                if (variant.CommandChainScheduleSignature == scheduleSignature &&
                    variant.CommandChainPrimaryGroupSignature == groupSignature &&
                    variant.CommandChainPrimaryGroupCount == groupCount)
                {
                    RegisterCommandBufferImageIndex(variant.PrimaryCommandBuffer, recordImageIndex);
                    return variant;
                }
            }

            CommandBuffer primary = AllocateCommandBuffer(
                CommandBufferLevel.Primary,
                allocationLabel,
                ownerPool);
            RegisterCommandBufferImageIndex(primary, recordImageIndex);
            CommandBufferCacheVariant created = new(
                primary,
                dynamicUiSecondaryCommandBuffer: default,
                ownerPool,
                dynamicUiSecondaryCommandPool: default,
                ownsPrimaryCommandBuffer: true,
                ownsDynamicUiSecondaryCommandBuffer: false);
            variants.Add(created);
            return created;
        }
    }

    internal static ulong BuildOpenXrPrimaryCommandBufferCacheKey(
        uint commandChainImageIndex,
        in OpenXrEyeRenderTargetContext targetContext)
    {
        HashCode hash = new();
        hash.Add(0x53574150);
        hash.Add(commandChainImageIndex);
        hash.Add(targetContext.Image.Handle);
        hash.Add(targetContext.ImageView.Handle);
        hash.Add((int)targetContext.ImageFormat);
        hash.Add(targetContext.Extent.Width);
        hash.Add(targetContext.Extent.Height);
        hash.Add(targetContext.DepthImage.Handle);
        hash.Add(targetContext.DepthView.Handle);
        hash.Add((int)targetContext.DepthFormat);
        hash.Add((uint)targetContext.DepthAspect);
        hash.Add(targetContext.OpenXrViewIndex);
        hash.Add(targetContext.OpenXrImageIndex);
        hash.Add(targetContext.FrameDataSlotIndex);
        hash.Add(targetContext.ResourcePlannerStateIndex);
        hash.Add(targetContext.FoveationResourceKey);
        hash.Add((int)targetContext.FoveationAttachmentKind);
        hash.Add(targetContext.FoveationAttachmentOwnedByResourcePlanner);
        return unchecked((ulong)hash.ToHashCode());
    }

    private static ulong BuildOpenXrMirrorPrimaryCommandBufferCacheKey(
        uint commandChainImageIndex,
        in OpenXrEyeMirrorRenderRequest request)
    {
        HashCode hash = new();
        hash.Add(0x4D495252);
        hash.Add(commandChainImageIndex);
        hash.Add(RuntimeHelpers.GetHashCode(request.TargetFrameBuffer));
        hash.Add(request.Extent.Width);
        hash.Add(request.Extent.Height);
        hash.Add(request.OpenXrViewIndex);
        hash.Add(request.ViewBatchStructuralIdentity);
        return unchecked((ulong)hash.ToHashCode());
    }

    private bool TryComputeOpenXrPrimaryCommandBufferGroupSignature(
        uint commandChainImageIndex,
        CommandChainSchedule? schedule,
        bool requireReusableChains,
        out ulong signature,
        out int groupCount)
    {
        signature = ulong.MaxValue;
        groupCount = -1;
        if (schedule is null)
            return true;

        Dictionary<CommandChainKey, CommandChain> commandChainCache = GetCommandChainCache(commandChainImageIndex);
        if (requireReusableChains && !OpenXrPrimaryCommandChainScheduleIsReusable(schedule, commandChainCache))
            return false;

        signature = ComputeOpenXrPrimaryCommandBufferGroupHandleSignature(schedule, commandChainCache);
        groupCount = schedule.Groups.Length;
        return true;
    }

    private static bool OpenXrPrimaryCommandChainScheduleIsReusable(
        CommandChainSchedule schedule,
        IReadOnlyDictionary<CommandChainKey, CommandChain> chains)
    {
        ReadOnlySpan<RenderPassChainGroup> groups = schedule.Groups.Span;
        for (int i = 0; i < groups.Length; i++)
        {
            ReadOnlySpan<CommandChainKey> keys = groups[i].ChainKeys.Span;
            for (int keyIndex = 0; keyIndex < keys.Length; keyIndex++)
            {
                if (!chains.TryGetValue(keys[keyIndex], out CommandChain? chain) ||
                    chain.SecondaryCommandBuffer.Handle == 0 ||
                    !chain.SecondaryCommandBufferExecutable ||
                    chain.State is not (CommandChainState.Reused or CommandChainState.FrameDataRefreshed) ||
                    (chain.State == CommandChainState.FrameDataRefreshed && chain.FrameDataRefreshTouchedDescriptors))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private void RecordOpenXrPrimaryReuseMiss(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return;

        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanCommandBufferCacheOutcome(
            reusedClean: false,
            recorded: false,
            forcedDirty: false,
            frameOpSignatureDirty: false,
            plannerDirty: false,
            profilerDirty: false,
            dirtyReason: reason);
    }

    private string DescribeOpenXrPrimaryReusableChainMiss(
        uint commandChainImageIndex,
        CommandChainSchedule? schedule)
    {
        if (schedule is null)
            return "schedule=null";

        Dictionary<CommandChainKey, CommandChain> commandChainCache = GetCommandChainCache(commandChainImageIndex);
        ReadOnlySpan<RenderPassChainGroup> groups = schedule.Groups.Span;
        for (int groupIndex = 0; groupIndex < groups.Length; groupIndex++)
        {
            ReadOnlySpan<CommandChainKey> keys = groups[groupIndex].ChainKeys.Span;
            for (int keyIndex = 0; keyIndex < keys.Length; keyIndex++)
            {
                CommandChainKey key = keys[keyIndex];
                if (!commandChainCache.TryGetValue(key, out CommandChain? chain))
                    return $"group={groupIndex} key={keyIndex} missing chain={key}";
                if (chain.SecondaryCommandBuffer.Handle == 0)
                    return $"group={groupIndex} key={keyIndex} no-secondary chain={key} state={chain.State} dirty={chain.DirtyReason}";
                if (!chain.SecondaryCommandBufferExecutable)
                    return $"group={groupIndex} key={keyIndex} secondary-not-executable chain={key} state={chain.State} dirty={chain.DirtyReason}";
                if (chain.State is not (CommandChainState.Reused or CommandChainState.FrameDataRefreshed))
                    return $"group={groupIndex} key={keyIndex} state={chain.State} dirty={chain.DirtyReason} chain={key}";
                if (chain.State == CommandChainState.FrameDataRefreshed && chain.FrameDataRefreshTouchedDescriptors)
                    return $"group={groupIndex} key={keyIndex} descriptor-refresh chain={key} state={chain.State} dirty={chain.DirtyReason}";
            }
        }

        return "all-reusable";
    }

    private static string ClassifyOpenXrPrimaryVariantMismatch(
        List<CommandBufferCacheVariant> variants,
        bool mirror,
        bool requiresExactFrameOps,
        bool usingCommandChains,
        ulong frameOpsSignature,
        ulong frameOpContextFingerprint,
        ulong plannerRevision,
        ulong imageLayoutStartSignature,
        bool hasQueryFrameOps,
        bool compareSwapchainImageEverPresented,
        bool swapchainImageEverPresented,
        CommandChainSchedule? commandChainSchedule,
        ulong commandChainPrimaryGroupSignature,
        int commandChainPrimaryGroupCount,
        bool gpuPipelineProfilingActive,
        int commandBufferImageSlot)
    {
        if (variants.Count == 0)
            return mirror ? "openxr-mirror-primary-miss:no-variants" : "openxr-primary-miss:no-variants";

        CommandBufferCacheVariant variant = variants[0];
        if (variant.Dirty)
            return mirror ? "openxr-mirror-primary-miss:dirty" : "openxr-primary-miss:dirty";
        if (variant.PrimaryCommandBuffer.Handle == 0)
            return mirror ? "openxr-mirror-primary-miss:empty-handle" : "openxr-primary-miss:empty-handle";
        if (requiresExactFrameOps && variant.FrameOpsSignature != frameOpsSignature)
        {
            if (hasQueryFrameOps)
                return mirror ? "openxr-mirror-primary-miss:frame-ops-query" : "openxr-primary-miss:frame-ops-query";

            return mirror ? "openxr-mirror-primary-miss:frame-ops" : "openxr-primary-miss:frame-ops";
        }
        if (IsCommandBufferVariantFrameOpContextDirty(variant, frameOpContextFingerprint))
            return mirror ? "openxr-mirror-primary-miss:context" : "openxr-primary-miss:context";
        if (!usingCommandChains && variant.PlannerRevision != plannerRevision)
            return mirror ? "openxr-mirror-primary-miss:planner" : "openxr-primary-miss:planner";
        if (IsCommandBufferVariantImageLayoutStateDirty(variant, imageLayoutStartSignature))
            return mirror ? "openxr-mirror-primary-miss:image-layout" : "openxr-primary-miss:image-layout";
        if (compareSwapchainImageEverPresented && variant.RecordedSwapchainImageEverPresented != swapchainImageEverPresented)
            return mirror ? "openxr-mirror-primary-miss:swapchain-presented" : "openxr-primary-miss:swapchain-presented";

        ulong scheduleSignature = commandChainSchedule?.StructuralSignature ?? ulong.MaxValue;
        ulong groupSignature = commandChainSchedule is null ? ulong.MaxValue : commandChainPrimaryGroupSignature;
        int groupCount = commandChainSchedule is null ? -1 : commandChainPrimaryGroupCount;
        if (variant.CommandChainScheduleSignature != scheduleSignature)
            return mirror ? "openxr-mirror-primary-miss:schedule" : "openxr-primary-miss:schedule";
        if (variant.CommandChainPrimaryGroupSignature != groupSignature)
            return mirror ? "openxr-mirror-primary-miss:group" : "openxr-primary-miss:group";
        if (variant.CommandChainPrimaryGroupCount != groupCount)
            return mirror ? "openxr-mirror-primary-miss:group-count" : "openxr-primary-miss:group-count";
        if (variant.GpuProfilerActive != gpuPipelineProfilingActive ||
            (gpuPipelineProfilingActive && variant.GpuProfilerFrameSlot != commandBufferImageSlot))
        {
            return mirror ? "openxr-mirror-primary-miss:profiler" : "openxr-primary-miss:profiler";
        }

        return mirror ? "openxr-mirror-primary-miss:unknown" : "openxr-primary-miss:unknown";
    }

    private static string DescribeOpenXrPrimaryVariantMismatch(
        List<CommandBufferCacheVariant> variants,
        bool requiresExactFrameOps,
        bool usingCommandChains,
        ulong frameOpsSignature,
        ulong frameOpContextFingerprint,
        ulong frameOpContextId,
        ulong plannerRevision,
        ulong imageLayoutStartSignature,
        bool compareSwapchainImageEverPresented,
        bool swapchainImageEverPresented,
        CommandChainSchedule? commandChainSchedule,
        ulong commandChainPrimaryGroupSignature,
        int commandChainPrimaryGroupCount,
        bool gpuPipelineProfilingActive,
        int commandBufferImageSlot)
    {
        if (variants.Count == 0)
            return "none";

        CommandBufferCacheVariant variant = variants[0];
        if (variant.Dirty)
            return $"dirty:{variant.DirtyReason ?? "unknown"}";
        if (variant.PrimaryCommandBuffer.Handle == 0)
            return "empty-handle";
        if (requiresExactFrameOps && variant.FrameOpsSignature != frameOpsSignature)
            return $"frame-ops recorded=0x{variant.FrameOpsSignature:X16} current=0x{frameOpsSignature:X16}";
        if (IsCommandBufferVariantFrameOpContextDirty(variant, frameOpContextFingerprint))
            return $"context recordedId={variant.RecordedFrameOpContextId} recorded=0x{variant.RecordedFrameOpContextFingerprint:X16} currentId={frameOpContextId} current=0x{frameOpContextFingerprint:X16}";
        if (!usingCommandChains && variant.PlannerRevision != plannerRevision)
            return $"planner recorded={variant.PlannerRevision} current={plannerRevision}";
        if (IsCommandBufferVariantImageLayoutStateDirty(variant, imageLayoutStartSignature))
            return $"image-layout recorded=0x{variant.RecordedImageLayoutStartSignature:X16} current=0x{imageLayoutStartSignature:X16} hasEnd={variant.RecordedImageLayoutEndState is not null}";
        if (compareSwapchainImageEverPresented && variant.RecordedSwapchainImageEverPresented != swapchainImageEverPresented)
            return $"swapchain-presented recorded={variant.RecordedSwapchainImageEverPresented} current={swapchainImageEverPresented}";

        ulong scheduleSignature = commandChainSchedule?.StructuralSignature ?? ulong.MaxValue;
        ulong groupSignature = commandChainSchedule is null ? ulong.MaxValue : commandChainPrimaryGroupSignature;
        int groupCount = commandChainSchedule is null ? -1 : commandChainPrimaryGroupCount;
        if (variant.CommandChainScheduleSignature != scheduleSignature)
            return $"schedule recorded=0x{variant.CommandChainScheduleSignature:X16} current=0x{scheduleSignature:X16}";
        if (variant.CommandChainPrimaryGroupSignature != groupSignature)
            return $"group recorded=0x{variant.CommandChainPrimaryGroupSignature:X16} current=0x{groupSignature:X16}";
        if (variant.CommandChainPrimaryGroupCount != groupCount)
            return $"group-count recorded={variant.CommandChainPrimaryGroupCount} current={groupCount}";
        if (variant.GpuProfilerActive != gpuPipelineProfilingActive ||
            (gpuPipelineProfilingActive && variant.GpuProfilerFrameSlot != commandBufferImageSlot))
            return $"profiler recorded=({variant.GpuProfilerActive},{variant.GpuProfilerFrameSlot}) current=({gpuPipelineProfilingActive},{commandBufferImageSlot})";

        return "unknown";
    }

    private static ulong ComputeOpenXrPrimaryCommandBufferGroupHandleSignature(
        CommandChainSchedule schedule,
        IReadOnlyDictionary<CommandChainKey, CommandChain> chains)
    {
        FrameOpSignatureHasher hash = new();
        ReadOnlySpan<RenderPassChainGroup> groups = schedule.Groups.Span;
        hash.Add(groups.Length);
        for (int i = 0; i < groups.Length; i++)
        {
            RenderPassChainGroup group = groups[i];
            hash.Add(group.PassIndex);
            hash.Add(group.TargetIdentity);
            hash.Add(group.StructuralSignature);
            hash.Add(group.SupportsSecondaryCommandBuffers);
            hash.Add(group.DynamicOverlay);

            ReadOnlySpan<CommandChainKey> keys = group.ChainKeys.Span;
            hash.Add(keys.Length);
            for (int keyIndex = 0; keyIndex < keys.Length; keyIndex++)
            {
                CommandChainKey key = keys[keyIndex];
                hash.Add(key.FrameSlot);
                hash.Add(key.PassIndex);
                hash.Add(key.TargetIdentity);
                hash.Add(key.ChainOrdinal);
                hash.Add(key.ViewKey.PipelineIdentity);
                hash.Add(key.ViewKey.ViewportIdentity);
                hash.Add(key.ViewKey.ViewIndex);
                hash.Add((int)key.ViewKey.Kind);
                hash.Add(key.ViewKey.LightIdentity);
                hash.Add(key.ViewKey.CascadeIndex);
                if (chains.TryGetValue(key, out CommandChain? chain))
                {
                    hash.Add(chain.SecondaryCommandBuffer.Handle);
                    hash.Add(chain.SecondaryCommandBufferGeneration);
                }
                else
                {
                    hash.Add(0UL);
                    hash.Add(0UL);
                }
            }
        }

        return hash.ToHash();
    }

    private void FreeOpenXrRecordedEyeCommandBuffer(OpenXrRecordedEyeCommandBuffer recorded)
    {
        if (recorded.OwnedByOpenXrPrimaryCache)
            return;

        CommandBuffer commandBuffer = recorded.CommandBuffer;
        if (commandBuffer.Handle != 0)
            FreeVulkanCommandBufferTracked(commandPool, ref commandBuffer, "OpenXR.RecordedEye");
    }

    private void CompleteOpenXrGpuProfilerSubmission(in OpenXrRecordedEyeCommandBuffer recorded)
    {
        if (recorded.CommandBuffer.Handle == 0)
            return;

        int frameSlot = unchecked((int)Math.Min(recorded.FrameDataSlotIndex, int.MaxValue));
        MarkVulkanGpuProfilerSubmitted(frameSlot);
        SampleVulkanGpuProfilerQueries(frameSlot);
    }

    internal bool TryRenderOpenXrEyeMirrorFrameBuffer(
        XRFrameBuffer targetFrameBuffer,
        Extent2D extent,
        int resourcePlannerStateIndex,
        uint openXrViewIndex,
        uint openXrImageIndex,
        Action emitFrameOps)
    {
        var request = new OpenXrEyeMirrorRenderRequest(
            targetFrameBuffer,
            extent,
            resourcePlannerStateIndex,
            openXrViewIndex,
            openXrImageIndex,
            emitFrameOps);

        return TryRenderOpenXrEyeMirrorFrameBuffer(in request);
    }

    internal bool TryRenderOpenXrEyeMirrorFrameBuffer(
        in OpenXrEyeMirrorRenderRequest request)
    {
        _openXrRecordedTextureUploadsForSubmit.Clear();
        bool hasRecorded = false;
        bool submitted = false;
        bool commandBufferCompleted = false;
        OpenXrRecordedEyeCommandBuffer recorded = default;

        try
        {
            hasRecorded = TryRecordOpenXrEyeMirrorFrameBufferCommandBuffer(in request, out recorded);
            if (!hasRecorded)
                return false;

            submitted = SubmitAndWaitOpenXrCommandBuffer(
                recorded.CommandBuffer,
                out commandBufferCompleted,
                CreateOpenXrSubmissionDiagnosticContext(
                    "OpenXrEyeMirrorSubmit",
                    "OpenXrEyeMirror",
                    recorded.OpenXrViewIndex,
                    recorded.OpenXrImageIndex,
                    recorded.FrameDataSlotIndex,
                    request.Extent,
                    recorded.FrameOpsSignature,
                    recorded.PlannerRevision,
                    recorded.FrameOpContextId,
                    recorded.ResourceGeneration,
                    recorded.DescriptorGeneration));
            if (submitted)
            {
                CompleteOpenXrGpuProfilerSubmission(in recorded);
                PublishRecordedTextureUploadsAfterCompletedSubmit(_openXrRecordedTextureUploadsForSubmit, "OpenXR eye mirror");
                DrainRetiredResourcesFromCompletedSubmittedFrameSlots();
            }
            else if (!commandBufferCompleted && !IsDeviceLost)
            {
                CancelRecordedTextureUploads(_openXrRecordedTextureUploadsForSubmit, "OpenXR eye mirror command buffer did not complete");
            }

            return submitted;
        }
        finally
        {
            if (!submitted && !commandBufferCompleted && !IsDeviceLost)
                CancelRecordedTextureUploads(_openXrRecordedTextureUploadsForSubmit, "OpenXR eye mirror command buffer submit failed");

            if (hasRecorded)
                FreeOpenXrRecordedEyeCommandBuffer(recorded);

            _openXrRecordedTextureUploadsForSubmit.Clear();
        }
    }

    internal bool TryRenderOpenXrEyeMirrorFrameBuffers(
        in OpenXrEyeMirrorRenderRequest firstEye,
        in OpenXrEyeMirrorRenderRequest secondEye)
    {
        _openXrRecordedTextureUploadsForSubmit.Clear();
        OpenXrRecordedEyeCommandBuffer firstRecorded = default;
        OpenXrRecordedEyeCommandBuffer secondRecorded = default;
        bool hasFirst = false;
        bool hasSecond = false;
        bool submitted = false;
        bool commandBuffersCompleted = false;

        try
        {
            hasFirst = TryRecordOpenXrEyeMirrorFrameBufferCommandBuffer(firstEye, out firstRecorded);
            if (!hasFirst)
                return false;

            hasSecond = TryRecordOpenXrEyeMirrorFrameBufferCommandBuffer(secondEye, out secondRecorded);
            if (!hasSecond)
                return false;

            submitted = SubmitAndWaitOpenXrCommandBuffers(
                firstRecorded.CommandBuffer,
                secondRecorded.CommandBuffer,
                out commandBuffersCompleted,
                CreateOpenXrBatchSubmissionDiagnosticContext(
                    "OpenXrEyeMirrorBatchSubmit",
                    "OpenXrEyeMirrorBatch",
                    in firstRecorded,
                    in secondRecorded,
                    firstEye.Extent));

            if (submitted)
            {
                CompleteOpenXrGpuProfilerSubmission(in firstRecorded);
                CompleteOpenXrGpuProfilerSubmission(in secondRecorded);
                PublishRecordedTextureUploadsAfterCompletedSubmit(_openXrRecordedTextureUploadsForSubmit, "OpenXR eye mirror batch");
                DrainRetiredResourcesFromCompletedSubmittedFrameSlots();
            }
            else if (!commandBuffersCompleted && !IsDeviceLost)
            {
                CancelRecordedTextureUploads(_openXrRecordedTextureUploadsForSubmit, "OpenXR eye mirror batch command buffers did not complete");
            }

            return submitted;
        }
        finally
        {
            if (!submitted && !commandBuffersCompleted && !IsDeviceLost)
                CancelRecordedTextureUploads(_openXrRecordedTextureUploadsForSubmit, "OpenXR eye mirror batch command buffer submit failed");

            if (hasSecond)
                FreeOpenXrRecordedEyeCommandBuffer(secondRecorded);
            if (hasFirst)
                FreeOpenXrRecordedEyeCommandBuffer(firstRecorded);

            _openXrRecordedTextureUploadsForSubmit.Clear();
        }
    }

    internal bool TryRenderAndPublishOpenXrEyeMirrorFrameBuffers(
        in OpenXrEyeMirrorRenderRequest firstEye,
        in OpenXrEyeMirrorRenderRequest secondEye,
        in OpenXrEyeMirrorPublishRequest firstPublish,
        in OpenXrEyeMirrorPublishRequest secondPublish,
        out bool firstPreviewCopied,
        out bool secondPreviewCopied)
    {
        firstPreviewCopied = false;
        secondPreviewCopied = false;

        _openXrRecordedTextureUploadsForSubmit.Clear();
        OpenXrRecordedEyeCommandBuffer firstRecorded = default;
        OpenXrRecordedEyeCommandBuffer secondRecorded = default;
        CommandBuffer publishCommandBuffer = default;
        bool hasFirst = false;
        bool hasSecond = false;
        bool hasPublish = false;
        bool submitted = false;
        bool commandBuffersCompleted = false;
        EVulkanQueueSubmissionDisposition submissionDisposition =
            EVulkanQueueSubmissionDisposition.NotSubmitted;

        try
        {
            hasFirst = TryRecordOpenXrEyeMirrorFrameBufferCommandBuffer(firstEye, out firstRecorded);
            if (!hasFirst)
                return false;

            hasSecond = TryRecordOpenXrEyeMirrorFrameBufferCommandBuffer(secondEye, out secondRecorded);
            if (!hasSecond)
                return false;

            if (!TryPrepareOpenXrEyeMirrorPublish(firstPublish, out OpenXrEyeMirrorPublishPlan firstPlan) ||
                !TryPrepareOpenXrEyeMirrorPublish(secondPublish, out OpenXrEyeMirrorPublishPlan secondPlan))
                return false;

            hasPublish = TryRecordOpenXrEyeMirrorPublishCommandBuffer(
                in firstPlan,
                in secondPlan,
                out publishCommandBuffer,
                out firstPreviewCopied,
                out secondPreviewCopied);
            if (!hasPublish)
                return false;

            CommandBuffer* commandBuffers = stackalloc CommandBuffer[3];
            commandBuffers[0] = firstRecorded.CommandBuffer;
            commandBuffers[1] = secondRecorded.CommandBuffer;
            commandBuffers[2] = publishCommandBuffer;

            submitted = SubmitAndWaitOpenXrCommandBuffers(
                commandBuffers,
                3,
                out commandBuffersCompleted,
                out submissionDisposition,
                out _,
                CreateOpenXrBatchSubmissionDiagnosticContext(
                    "OpenXrEyeMirrorRenderPublishSubmit",
                    "OpenXrEyeMirrorRenderPublish",
                    in firstRecorded,
                    in secondRecorded,
                    firstPublish.Extent));

            if (submitted)
            {
                CompleteOpenXrGpuProfilerSubmission(in firstRecorded);
                CompleteOpenXrGpuProfilerSubmission(in secondRecorded);
                PublishRecordedTextureUploadsAfterCompletedSubmit(_openXrRecordedTextureUploadsForSubmit, "OpenXR eye mirror render+publish batch");
                DrainRetiredResourcesFromCompletedSubmittedFrameSlots();
            }
            else if (!commandBuffersCompleted && !IsDeviceLost)
            {
                CancelRecordedTextureUploads(_openXrRecordedTextureUploadsForSubmit, "OpenXR eye mirror render+publish batch command buffers did not complete");
            }

            return submitted;
        }
        catch (Exception ex)
        {
            Debug.VulkanWarningEvery(
                $"OpenXR.Vulkan.Mirror.RenderPublishBatchFailed.{GetHashCode()}",
                TimeSpan.FromSeconds(2),
                "[OpenXR] Vulkan eye mirror render+publish batch failed: {0}",
                ex.Message);
            return false;
        }
        finally
        {
            if (!submitted && !commandBuffersCompleted && !IsDeviceLost)
                CancelRecordedTextureUploads(_openXrRecordedTextureUploadsForSubmit, "OpenXR eye mirror render+publish batch command buffer submit failed");

            if (hasPublish)
                FreeOpenXrMirrorPublishCommandBuffer(publishCommandBuffer, submissionDisposition);
            if (hasSecond)
                FreeOpenXrRecordedEyeCommandBuffer(secondRecorded);
            if (hasFirst)
                FreeOpenXrRecordedEyeCommandBuffer(firstRecorded);

            _openXrRecordedTextureUploadsForSubmit.Clear();
        }
    }

    internal bool TryRenderAndBlitTextureArrayLayersToOpenXrSwapchainImages(
        in OpenXrEyeMirrorRenderRequest renderRequest,
        XRRenderPipelineInstance? renderPipelineInstance,
        XRTexture2DArray? sourceTexture,
        Image leftDestinationImage,
        Format leftDestinationFormat,
        Extent2D leftDestinationExtent,
        string leftDestinationLabel,
        Image rightDestinationImage,
        Format rightDestinationFormat,
        Extent2D rightDestinationExtent,
        string rightDestinationLabel,
        bool flipY,
        EOpenXrStrictSpsFaultInjectionStage faultInjectionStage,
        out EOpenXrStrictSpsFaultInjectionStage injectedFailureStage)
    {
        injectedFailureStage = EOpenXrStrictSpsFaultInjectionStage.None;
        _openXrRecordedTextureUploadsForSubmit.Clear();
        OpenXrRecordedEyeCommandBuffer recorded = default;
        CommandBuffer publishCommandBuffer = default;
        bool hasRecorded = false;
        bool hasPublish = false;
        bool submitted = false;
        bool commandBuffersCompleted = false;
        EVulkanQueueSubmissionDisposition submissionDisposition =
            EVulkanQueueSubmissionDisposition.NotSubmitted;

        try
        {
            // Keep the same planner context active until the array-layer publish
            // command has captured its source image. Leaving the mirror-record
            // scope first can refresh the logical texture wrapper back to its
            // dedicated fallback image even though the recorded render targeted
            // the planner-owned physical image.
            using IDisposable sourcePlannerScope = EnterOpenXrResourcePlannerThreadScope(
                renderRequest.ResourcePlannerStateIndex,
                EOpenXrResourcePlannerPurpose.Mirror);

            hasRecorded = TryRecordOpenXrEyeMirrorFrameBufferCommandBuffer(in renderRequest, out recorded);
            if (!hasRecorded)
                return false;

            if (IsOpenXrStrictSpsFaultBoundary(
                    faultInjectionStage,
                    EOpenXrStrictSpsFaultInjectionStage.Recording))
            {
                injectedFailureStage = EOpenXrStrictSpsFaultInjectionStage.Recording;
                return false;
            }

            if (renderPipelineInstance?.SkippedResizeCatchUpThisFrame == true)
                return false;

            if (!TryPrepareStereoLayerBlit(
                    sourceTexture,
                    recorded.CommandBuffer,
                    leftDestinationImage,
                    leftDestinationFormat,
                    leftDestinationExtent,
                    leftDestinationLabel,
                    rightDestinationImage,
                    rightDestinationFormat,
                    rightDestinationExtent,
                    rightDestinationLabel,
                    flipY,
                    out OpenXrStereoLayerBlitPlan plan))
            {
                return false;
            }

            hasPublish = TryRecordStereoLayerBlitCommandBuffer(in plan, out publishCommandBuffer);
            if (!hasPublish)
                return false;

            CommandBuffer* commandBuffers = stackalloc CommandBuffer[2];
            commandBuffers[0] = recorded.CommandBuffer;
            commandBuffers[1] = publishCommandBuffer;

            submitted = SubmitAndWaitOpenXrCommandBuffers(
                commandBuffers,
                2,
                out commandBuffersCompleted,
                out submissionDisposition,
                out injectedFailureStage,
                CreateOpenXrPublishBatchSubmissionDiagnosticContext(
                    "OpenXrStereoLayerRenderPublishSubmit",
                    "OpenXrStereoLayerRenderPublish",
                    in recorded,
                    leftDestinationExtent,
                    leftDestinationLabel) with
                {
                    OpenXrStrictSpsFaultInjectionStage = faultInjectionStage,
                });

            if (submitted)
            {
                CompleteOpenXrGpuProfilerSubmission(in recorded);
                UpdateStereoLayerBlitTrackedLayouts(in plan);
                PublishRecordedTextureUploadsAfterCompletedSubmit(_openXrRecordedTextureUploadsForSubmit, "OpenXR true stereo render+publish batch");
                DrainRetiredResourcesFromCompletedSubmittedFrameSlots();
            }
            else if (!commandBuffersCompleted && !IsDeviceLost)
            {
                CancelRecordedTextureUploads(_openXrRecordedTextureUploadsForSubmit, "OpenXR true stereo render+publish batch command buffers did not complete");
            }

            return submitted;
        }
        catch (Exception ex)
        {
            Debug.VulkanWarningEvery(
                $"OpenXR.Vulkan.TrueStereo.RenderPublishBatchFailed.{GetHashCode()}",
                TimeSpan.FromSeconds(2),
                "[OpenXR] Vulkan true stereo render+publish batch failed: {0}",
                ex.Message);
            return false;
        }
        finally
        {
            if (!submitted &&
                submissionDisposition == EVulkanQueueSubmissionDisposition.NotSubmitted &&
                hasRecorded)
            {
                MarkUnsubmittedOpenXrPrimaryCommandBufferDirty(
                    in recorded,
                    "OpenXR true stereo render+publish batch was not submitted");
            }

            if (!submitted && !commandBuffersCompleted && !IsDeviceLost)
                CancelRecordedTextureUploads(_openXrRecordedTextureUploadsForSubmit, "OpenXR true stereo render+publish batch command buffer submit failed");

            if (hasPublish)
                FreeOpenXrMirrorPublishCommandBuffer(publishCommandBuffer, submissionDisposition);
            if (hasRecorded)
                FreeOpenXrRecordedEyeCommandBuffer(recorded);

            _openXrRecordedTextureUploadsForSubmit.Clear();
        }
    }

    private bool TryRecordOpenXrEyeMirrorFrameBufferCommandBuffer(
        in OpenXrEyeMirrorRenderRequest request,
        out OpenXrRecordedEyeCommandBuffer recorded)
    {
        recorded = default;
        if (request.TargetFrameBuffer is null || request.Extent.Width == 0 || request.Extent.Height == 0)
            return false;

        CommandBuffer commandBuffer = default;
        bool drainedFrameOps = false;
        int openXrFrameDataSlotCount = ResolveOpenXrFrameDataSlotCount(swapChainImages?.Length ?? 0);
        uint recordImageIndex = ResolveOpenXrRecordImageIndex(
            request.ResourcePlannerStateIndex,
            swapChainImages?.Length ?? 0);

        using IDisposable? externalScope = request.RendersExternalSwapchainTarget
            ? EnterOpenXrExternalSwapchainRenderScope(
                request.Extent.Width,
                request.Extent.Height,
                BuildOpenXrExternalSwapchainPlannerTargetIdentity(
                    request.OpenXrViewIndex,
                    request.ViewBatchStructuralIdentity),
                ResolveOpenXrExternalSwapchainTargetName(request.OpenXrViewIndex),
                EVulkanFrameOpContextKind.OpenXrMirror)
            : null;

        try
        {
            EnsureOpenXrFrameDataSlotCapacity(openXrFrameDataSlotCount);
            EnsureDescriptorFrameSlotFrameCountFloor(openXrFrameDataSlotCount);
            WaitForOpenXrFrameDataSlot(recordImageIndex, "eye mirror render");
            DrainRetiredResourcesFromCompletedSubmittedFrameSlots();
            DrainCompletedRecordedTextureUploadPublications();

            using ThreadRenderStateScope renderStateScope = EnterThreadRenderStateScope(
                CreateOpenXrPrewarmRenderStateTracker(request.Extent));
            using (EnterOpenXrResourcePlannerThreadScope(
                request.ResourcePlannerStateIndex,
                EOpenXrResourcePlannerPurpose.Mirror))
            {
                FrameOp[] ops = CaptureFrameOpsExcludingTextureUploads(request.EmitFrameOps, out _);
                drainedFrameOps = true;
                ops = FilterDiagnosticSkippedFrameOps(ops);
                if (ops.Length == 0)
                {
                    Debug.VulkanWarningEvery(
                        $"OpenXR.Vulkan.NoEyeMirrorFrameOps.{GetHashCode()}",
                        TimeSpan.FromSeconds(1),
                        "[OpenXR] Vulkan eye mirror rendering produced no frame operations.");
                    return false;
                }
                if (request.RendersExternalSwapchainTarget)
                {
                    ops = NormalizeOpenXrExternalSwapchainFrameOps(ops, request.Extent);
                    ValidateOpenXrExternalFrameOpContexts(
                        ops,
                        request.Extent,
                        request.OpenXrViewIndex,
                        "eye mirror render");
                }

                using (RuntimeRenderingHostServices.Current.StartProfileScope("OpenXR.Vulkan.RecordMirror.PlanAndSchedule.Sort"))
                    ops = VulkanRenderGraphCompiler.SortFrameOps(ops, CompiledRenderGraph);
                if (TryDescribeRecentResourceAllocationFailure(out string prePlanFailureReason))
                {
                    Debug.VulkanWarningEvery(
                        $"OpenXR.Vulkan.EyeMirrorFrameOpPlanDeferred.{GetHashCode()}.{request.OpenXrViewIndex}",
                        TimeSpan.FromSeconds(1),
                        "[OpenXR] Deferring Vulkan eye mirror command buffer preparation: {0}",
                        prePlanFailureReason);
                    return false;
                }

                FrameOpContext plannerContext = PrepareResourcePlannerForFrameOps(ops);
                if (TryDescribeRecentResourceAllocationFailure(out string postPlanFailureReason))
                {
                    Debug.VulkanWarningEvery(
                        $"OpenXR.Vulkan.EyeMirrorFrameOpPlanFailed.{GetHashCode()}.{request.OpenXrViewIndex}",
                        TimeSpan.FromSeconds(1),
                        "[OpenXR] Deferring Vulkan eye mirror command buffer preparation: {0}",
                        postPlanFailureReason);
                    return false;
                }

                if (!TryRefreshFrameOpResourceWrappers(
                    ops,
                    plannerContext,
                    "OpenXR eye mirror prepared frame-op resource refresh",
                    AllowSynchronousResourceUploads,
                    out string refreshFailureReason))
                {
                    Debug.VulkanWarningEvery(
                        $"OpenXR.Vulkan.EyeMirrorFrameOpRefreshDeferred.{GetHashCode()}.{request.OpenXrViewIndex}",
                        TimeSpan.FromSeconds(1),
                        "[OpenXR] Deferring Vulkan eye mirror command buffer preparation: {0}",
                        refreshFailureReason);
                    return false;
                }
                // This is the render-to-array path used by strict SPS. Reserve
                // every repeated direct and indirect use before command-chain
                // workers or the primary command buffer record any dependency.
                if (!PrewarmOpenXrFrameOpResources(
                        ops,
                        recordImageIndex,
                        sealFrameManifest: true))
                {
                    return false;
                }
                ulong plannerRevision = ResourcePlannerRevision;
                ulong frameOpsSignature;
                using (RuntimeRenderingHostServices.Current.StartProfileScope("OpenXR.Vulkan.RecordMirror.PlanAndSchedule.Signature"))
                {
                    frameOpsSignature = ComputeFrameOpsSignature(ops);
                }
                uint mirrorCommandChainImageIndex = recordImageIndex;

                CommandChainSchedule? commandChainSchedule = TryBuildOpenXrEyeCommandChainSchedule(
                    mirrorCommandChainImageIndex,
                    request.OpenXrViewIndex,
                    request.OpenXrImageIndex,
                    default,
                    ops,
                    frameOpsSignature,
                    plannerRevision);

                ulong imageLayoutStartSignature = ComputeImageLayoutStateSignature();
                FrameOpContext fallbackContext = ops.Length > 0 ? ops[0].Context : plannerContext;
                ulong frameOpContextFingerprint = ComputeCommandBufferFrameOpContextFingerprint(
                    ops,
                    Array.Empty<FrameOp>(),
                    fallbackContext);
                ulong frameOpContextId = ResolveCommandBufferFrameOpContextId(
                    ops,
                    Array.Empty<FrameOp>(),
                    fallbackContext);
                bool reusedPrimary = TryReuseOpenXrMirrorPrimaryCommandBuffer(
                    recordImageIndex,
                    mirrorCommandChainImageIndex,
                    request,
                    ops,
                    frameOpsSignature,
                    frameOpContextFingerprint,
                    frameOpContextId,
                    plannerRevision,
                    imageLayoutStartSignature,
                    commandChainSchedule,
                    out commandBuffer);

                if (!reusedPrimary)
                {
                    commandBuffer = RecordOpenXrMirrorPrimaryCommandBuffer(
                        recordImageIndex,
                        mirrorCommandChainImageIndex,
                        request,
                        ops,
                        frameOpsSignature,
                        frameOpContextFingerprint,
                        frameOpContextId,
                        plannerRevision,
                        imageLayoutStartSignature,
                        commandChainSchedule);
                    if (commandBuffer.Handle == 0)
                        return false;
                }

                recorded = new OpenXrRecordedEyeCommandBuffer(
                    commandBuffer,
                    request.OpenXrViewIndex,
                    request.OpenXrImageIndex,
                    recordImageIndex,
                    frameOpsSignature,
                    plannerRevision,
                    frameOpContextId,
                    fallbackContext.ResourceGeneration,
                    fallbackContext.DescriptorGeneration,
                    OwnedByOpenXrPrimaryCache: true);
                return true;
            }
        }
        catch (Exception ex)
        {
            if (!drainedFrameOps)
                _ = DrainFrameOpsExcludingTextureUploads(out _);
            if (IsOpenXrStrictExtentFailure(ex))
                throw;

            Debug.VulkanWarningEvery(
                $"OpenXR.Vulkan.RenderEyeMirrorFailed.{GetHashCode()}",
                TimeSpan.FromSeconds(1),
                "[OpenXR] Vulkan eye mirror render failed: {0}",
                ex.Message);
            return false;
        }
    }

    private bool TryReuseOpenXrMirrorPrimaryCommandBuffer(
        uint recordImageIndex,
        uint commandChainImageIndex,
        in OpenXrEyeMirrorRenderRequest request,
        FrameOp[] ops,
        ulong frameOpsSignature,
        ulong frameOpContextFingerprint,
        ulong frameOpContextId,
        ulong plannerRevision,
        ulong imageLayoutStartSignature,
        CommandChainSchedule? commandChainSchedule,
        out CommandBuffer commandBuffer)
    {
        commandBuffer = default;
        if (!OpenXrVulkanPrimaryReuseEnabled)
        {
            if (OpenXrVulkanTraceEnabled)
                RecordOpenXrPrimaryReuseMiss("openxr-mirror-primary-miss:disabled");
            return false;
        }

        ulong cacheKey = BuildOpenXrMirrorPrimaryCommandBufferCacheKey(commandChainImageIndex, request);
        lock (_openXrPrimaryCommandBufferVariantsLock)
        {
            if (!_openXrPrimaryCommandBufferVariants.TryGetValue(cacheKey, out List<CommandBufferCacheVariant>? variants))
            {
                if (OpenXrVulkanTraceEnabled)
                    RecordOpenXrPrimaryReuseMiss($"openxr-mirror-primary-miss:no-variants key=0x{cacheKey:X16}");
                else
                    RecordOpenXrPrimaryReuseMiss("openxr-mirror-primary-miss:no-variants");
                return false;
            }

            bool gpuPipelineProfilingActive =
                IsVulkanGpuProfilerCommandBufferInstrumentationEnabled &&
                RenderPipelineGpuProfiler.Instance.IsProfilingActive;
            int commandBufferImageSlot = unchecked((int)Math.Min(recordImageIndex, int.MaxValue));
            ulong commandChainPrimaryGroupSignature = ulong.MaxValue;
            int commandChainPrimaryGroupCount = -1;
            bool usingCommandChains = commandChainSchedule is not null;
            bool requiresExactFrameOps = true;
            if (!TryComputeOpenXrPrimaryCommandBufferGroupSignature(
                    commandChainImageIndex,
                    commandChainSchedule,
                    requireReusableChains: true,
                    out commandChainPrimaryGroupSignature,
                    out commandChainPrimaryGroupCount))
            {
                if (OpenXrVulkanTraceEnabled)
                {
                    RecordOpenXrPrimaryReuseMiss(
                        $"openxr-mirror-primary-miss:chains-not-reusable key=0x{cacheKey:X16} {DescribeOpenXrPrimaryReusableChainMiss(commandChainImageIndex, commandChainSchedule)}");
                }
                else
                {
                    RecordOpenXrPrimaryReuseMiss("openxr-mirror-primary-miss:chains-not-reusable");
                }
                return false;
            }

            for (int i = 0; i < variants.Count; i++)
            {
                CommandBufferCacheVariant variant = variants[i];
                if (variant.Dirty ||
                    variant.PrimaryCommandBuffer.Handle == 0 ||
                    (requiresExactFrameOps && variant.FrameOpsSignature != frameOpsSignature) ||
                    !TryValidateCommandBufferVariantContext(
                        recordImageIndex,
                        variant,
                        frameOpContextFingerprint,
                        frameOpContextId,
                        "openxr-mirror-primary") ||
                    (!usingCommandChains && variant.PlannerRevision != plannerRevision) ||
                    IsCommandBufferVariantImageLayoutStateDirty(variant, imageLayoutStartSignature) ||
                    variant.CommandChainScheduleSignature != (commandChainSchedule?.StructuralSignature ?? ulong.MaxValue) ||
                    variant.CommandChainPrimaryGroupSignature != (commandChainSchedule is null ? ulong.MaxValue : commandChainPrimaryGroupSignature) ||
                    variant.CommandChainPrimaryGroupCount != (commandChainSchedule is null ? -1 : commandChainPrimaryGroupCount) ||
                    IsCommandBufferVariantGpuProfilerStateDirty(variant, gpuPipelineProfilingActive, commandBufferImageSlot))
                {
                    continue;
                }

                _lastReusableFrameDataRefreshFailureReason = null;
                using (RuntimeRenderingHostServices.Current.StartProfileScope("OpenXR.Vulkan.MirrorPrimary.RefreshFrameData"))
                {
                    if (!TryRefreshReusableCommandBufferFrameData(recordImageIndex, ops))
                        return false;
                }

                variant.GpuProfilerActive = gpuPipelineProfilingActive;
                variant.GpuProfilerFrameSlot = gpuPipelineProfilingActive ? commandBufferImageSlot : -1;

                if (HasQueryFrameOps(ops) &&
                    !PrepareQueryFrameOpsForCommandBufferReuse(variant.PrimaryCommandBuffer, ops))
                {
                    if (OpenXrVulkanTraceEnabled)
                        RecordOpenXrPrimaryReuseMiss("openxr-mirror-primary-miss:query-pool-prepare");
                    return false;
                }

                variant.LastUsedFrameId = VulkanFrameCounter;
                StoreFrameOpSignatureDebugParts(variant, ops);
                RestoreRecordedImageLayoutEndState(variant);
                PrepareVulkanGpuProfilerReusableSubmission(
                    commandBufferImageSlot,
                    variant,
                    gpuPipelineProfilingActive);
                UpdateVulkanGpuProfilerCommandBufferState(
                    recordImageIndex,
                    gpuPipelineProfilingActive,
                    commandBufferImageSlot);

                RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanCommandBufferCacheOutcome(
                    reusedClean: true,
                    recorded: false,
                    forcedDirty: false,
                    frameOpSignatureDirty: false,
                    plannerDirty: false,
                    profilerDirty: false,
                    dirtyReason: null);
                RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanCommandChainMetrics(primaryCommandBuffersReused: 1);

                EnsureCommandBufferVariantContextBeforeSubmit(
                    recordImageIndex,
                    variant,
                    frameOpContextFingerprint,
                    frameOpContextId,
                    "openxr-mirror-primary");
                commandBuffer = variant.PrimaryCommandBuffer;
                if (OpenXrVulkanTraceEnabled)
                {
                    Debug.Vulkan(
                        "[OpenXrVulkan] mirror reused primary eye={0} swapchainImage={1} commandKey={2} recorderSlot={3} target='{4}' commandBuffer=0x{5:X}",
                        request.OpenXrViewIndex,
                        request.OpenXrImageIndex,
                        commandChainImageIndex,
                        recordImageIndex,
                        request.TargetFrameBuffer.Name ?? "<unnamed FBO>",
                        commandBuffer.Handle);
                }

                return true;
            }

            string compactMissReason = ClassifyOpenXrPrimaryVariantMismatch(
                variants,
                true,
                requiresExactFrameOps,
                usingCommandChains,
                frameOpsSignature,
                frameOpContextFingerprint,
                plannerRevision,
                imageLayoutStartSignature,
                ContainsQueryFrameOp(ops),
                false,
                false,
                commandChainSchedule,
                commandChainPrimaryGroupSignature,
                commandChainPrimaryGroupCount,
                gpuPipelineProfilingActive,
                commandBufferImageSlot);
            if (OpenXrVulkanTraceEnabled)
            {
                RecordOpenXrPrimaryReuseMiss(
                    $"openxr-mirror-primary-miss:no-matching-variant key=0x{cacheKey:X16} variants={variants.Count} first={DescribeOpenXrPrimaryVariantMismatch(
                        variants,
                        requiresExactFrameOps,
                        usingCommandChains,
                        frameOpsSignature,
                        frameOpContextFingerprint,
                        frameOpContextId,
                        plannerRevision,
                        imageLayoutStartSignature,
                        false,
                        false,
                        commandChainSchedule,
                        commandChainPrimaryGroupSignature,
                        commandChainPrimaryGroupCount,
                        gpuPipelineProfilingActive,
                        commandBufferImageSlot)}");
            }
            else
            {
                RecordOpenXrPrimaryReuseMiss(compactMissReason);
            }
            return false;
        }
    }

    private CommandBuffer RecordOpenXrMirrorPrimaryCommandBuffer(
        uint recordImageIndex,
        uint commandChainImageIndex,
        in OpenXrEyeMirrorRenderRequest request,
        FrameOp[] ops,
        ulong frameOpsSignature,
        ulong frameOpContextFingerprint,
        ulong frameOpContextId,
        ulong plannerRevision,
        ulong imageLayoutStartSignature,
        CommandChainSchedule? commandChainSchedule)
    {
        ulong cacheKey = BuildOpenXrMirrorPrimaryCommandBufferCacheKey(commandChainImageIndex, request);
        CommandBufferCacheVariant variant = GetOrCreateOpenXrPrimaryCommandBufferVariant(
            cacheKey,
            commandChainSchedule,
            commandChainImageIndex,
            recordImageIndex);

        bool gpuPipelineProfilingActive =
            IsVulkanGpuProfilerCommandBufferInstrumentationEnabled &&
            RenderPipelineGpuProfiler.Instance.IsProfilingActive;
        int commandBufferImageSlot = unchecked((int)Math.Min(recordImageIndex, int.MaxValue));
        ulong commandChainPrimaryGroupSignature = ulong.MaxValue;
        int commandChainPrimaryGroupCount = -1;
        _ = TryComputeOpenXrPrimaryCommandBufferGroupSignature(
            commandChainImageIndex,
            commandChainSchedule,
            requireReusableChains: false,
            out commandChainPrimaryGroupSignature,
            out commandChainPrimaryGroupCount);

        long recordStart = Stopwatch.GetTimestamp();
        _isRecordingCommandBuffer = true;
        bool queryFrameOpsRequireRerecord = false;
        try
        {
            BeginRecordedTextureUploadSubmitBatch();
            if (OpenXrVulkanTraceEnabled)
            {
                Debug.Vulkan(
                    "[OpenXrVulkan] mirror record eye={0} swapchainImage={1} commandKey={2} commandSlot={3} target='{4}' extent={5}x{6} ops={7}",
                    request.OpenXrViewIndex,
                    request.OpenXrImageIndex,
                    commandChainImageIndex,
                    recordImageIndex,
                    request.TargetFrameBuffer.Name ?? "<unnamed FBO>",
                    request.Extent.Width,
                    request.Extent.Height,
                    ops.Length);
            }

            // Strict SPS renders into the engine-owned layered FBO. This command
            // buffer must not inherit desktop swapchain image 0 ownership or a
            // present transition merely because it reuses the primary recorder.
            bool swapchainImageEverPresented = false;
            if (!TryRecordCommandBuffer(
                OpenXrExternalSwapchainTargetImageIndex,
                variant.PrimaryCommandBuffer,
                dynamicUiBatchTextSecondaryCommandBuffer: default,
                ops,
                dynamicUiBatchTextOpCount: 0,
                commandChainSchedule,
                preserveSwapchainForOverlay: false,
                recordedSwapchainWriteCount: out int recordedSwapchainWriteCount,
                recordedSwapchainFinalLayout: out ImageLayout swapchainLayoutAfterCommandBuffer,
                recordingDeferredReason: out string recordingDeferredReason,
                queryFrameOpsRequireRerecord: out queryFrameOpsRequireRerecord,
                transitionSwapchainToPresent: false,
                frameDataImageIndexOverride: recordImageIndex,
                excludeDesktopSwapchainBarriers: true))
            {
                CancelRecordedTextureUploadSubmitBatch(
                    $"OpenXR eye mirror command buffer recording deferred: {recordingDeferredReason}");
                Debug.VulkanWarningEvery(
                    $"OpenXR.Vulkan.EyeMirrorPrimaryRecordDeferred.{GetHashCode()}",
                    TimeSpan.FromSeconds(1),
                    "[OpenXR] Deferring Vulkan eye mirror primary command buffer recording before vkBeginCommandBuffer: {0}",
                    recordingDeferredReason);
                return default;
            }

            bool wasDirty = variant.Dirty;
            variant.Dirty = false;
            variant.FrameOpsSignature = frameOpsSignature;
            variant.DynamicUiSignature = 0;
            variant.DynamicUiOpCount = 0;
            variant.DynamicUiSecondaryRecorded = false;
            variant.PreserveSwapchainForOverlay = false;
            variant.RecordedFrameOpContextFingerprint = frameOpContextFingerprint;
            variant.RecordedFrameOpContextId = frameOpContextId;
            variant.RecordedSwapchainImageEverPresented = swapchainImageEverPresented;
            variant.RecordedSwapchainFinalLayout = swapchainLayoutAfterCommandBuffer;
            variant.RecordedSwapchainWriteCount = recordedSwapchainWriteCount;
            variant.RecordedSwapchainRefreshFromLastPresentSource = false;
            variant.RecordedImageLayoutStartSignature = imageLayoutStartSignature;
            CaptureCommandBufferVariantImageLayoutEndState(variant);
            variant.CommandChainScheduleSignature = commandChainSchedule?.StructuralSignature ?? ulong.MaxValue;
            if (!TryComputeOpenXrPrimaryCommandBufferGroupSignature(
                    commandChainImageIndex,
                    commandChainSchedule,
                    requireReusableChains: false,
                    out commandChainPrimaryGroupSignature,
                    out commandChainPrimaryGroupCount))
            {
                commandChainPrimaryGroupSignature = ulong.MaxValue;
                commandChainPrimaryGroupCount = -1;
            }
            variant.CommandChainPrimaryGroupSignature = commandChainPrimaryGroupSignature;
            variant.CommandChainPrimaryGroupCount = commandChainPrimaryGroupCount;
            variant.PlannerRevision = plannerRevision;
            variant.GpuProfilerActive = gpuPipelineProfilingActive;
            variant.GpuProfilerFrameSlot = gpuPipelineProfilingActive ? commandBufferImageSlot : -1;
            variant.LastUsedFrameId = VulkanFrameCounter;
            CaptureVulkanGpuProfilerVariantScopes(commandBufferImageSlot, variant);
            StoreFrameOpSignatureDebugParts(variant, ops);
            if (queryFrameOpsRequireRerecord)
                MarkCommandBufferVariantTransient(variant, "query draw was not recorded");
            UpdateVulkanGpuProfilerCommandBufferState(
                recordImageIndex,
                gpuPipelineProfilingActive,
                commandBufferImageSlot);

            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanCommandBufferCacheOutcome(
                reusedClean: false,
                recorded: true,
                forcedDirty: wasDirty,
                frameOpSignatureDirty: false,
                plannerDirty: false,
                profilerDirty: false,
                dirtyReason: wasDirty ? "forced" : null);
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanCommandChainMetrics(primaryCommandBuffersRecorded: 1);
        }
        catch
        {
            CancelRecordedTextureUploadSubmitBatch("OpenXR eye mirror command buffer recording failed before upload submit");
            throw;
        }
        finally
        {
            _isRecordingCommandBuffer = false;
        }

        MoveRecordedTextureUploadsForSubmitTo(_openXrRecordedTextureUploadsForSubmit);

        if (OpenXrVulkanTraceEnabled)
        {
            double recordMs = (Stopwatch.GetTimestamp() - recordStart) * 1000.0 / Stopwatch.Frequency;
            Debug.Vulkan(
                "[OpenXrVulkan] mirror recorded primary eye={0} swapchainImage={1} commandKey={2} recorderSlot={3} target='{4}' commandBuffer=0x{5:X} recordMs={6:F3} pendingUploads={7}",
                request.OpenXrViewIndex,
                request.OpenXrImageIndex,
                commandChainImageIndex,
                recordImageIndex,
                request.TargetFrameBuffer.Name ?? "<unnamed FBO>",
                variant.PrimaryCommandBuffer.Handle,
                recordMs,
                _openXrRecordedTextureUploadsForSubmit.Count);
        }

        EnsureCommandBufferVariantContextBeforeSubmit(
            recordImageIndex,
            variant,
            frameOpContextFingerprint,
            frameOpContextId,
            "recorded-openxr-mirror-primary");
        return variant.PrimaryCommandBuffer;
    }

    private static int ResolveOpenXrFrameDataSlotCount(int desktopSwapchainImageCount)
        => ResolveOpenXrDesktopFrameDataSlotCount(desktopSwapchainImageCount) + OpenXrEyeResourcePlannerStateCount;

    private static int ResolveOpenXrDesktopFrameDataSlotCount(int desktopSwapchainImageCount)
        => Math.Max(Math.Max(desktopSwapchainImageCount, MAX_FRAMES_IN_FLIGHT), 1);

    private static uint ResolveOpenXrRecordImageIndex(
        int resourcePlannerStateIndex,
        int desktopSwapchainImageCount)
    {
        int eyeIndex = NormalizeOpenXrResourcePlannerStateIndex(resourcePlannerStateIndex);
        int desktopFrameDataSlotCount = ResolveOpenXrDesktopFrameDataSlotCount(desktopSwapchainImageCount);
        return (uint)(desktopFrameDataSlotCount + eyeIndex);
    }

    private void EnsureOpenXrFrameDataSlotCapacity(int frameDataSlotCount)
    {
        EnsureCommandBufferFrameDataSlotCapacity(frameDataSlotCount);
    }

    private CommandChainSchedule? TryBuildOpenXrEyeCommandChainSchedule(
        uint commandChainImageIndex,
        uint openXrViewIndex,
        uint openXrImageIndex,
        Image openXrImage,
        FrameOp[] ops,
        ulong frameOpsSignature,
        ulong resourcePlanRevision)
    {
        CommandChainSchedule? schedule = TryBuildCommandChainSchedule(
            imageIndex: commandChainImageIndex,
            staticOps: ops,
            volatileOps: Array.Empty<FrameOp>(),
            frameOpsSignature: frameOpsSignature,
            volatileSignature: 0,
            resourcePlanRevision: resourcePlanRevision,
            allowExternalSwapchainTarget: true,
            stats: out CommandChainLoweringStats stats);
        if (schedule is null)
            return null;

        if (OpenXrVulkanTraceEnabled)
        {
            Debug.Vulkan(
                "[OpenXrVulkan] schedule eye={0} swapchainImage={1} image=0x{2:X} commandKey={3} chains={4} groups={5} recorded={6} reused={7}",
                openXrViewIndex,
                openXrImageIndex,
                openXrImage.Handle,
                commandChainImageIndex,
                stats.ChainsScheduled,
                schedule.Groups.Length,
                stats.ChainsRecorded,
                stats.ChainsReused);
        }

        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanCommandChainMetrics(
            chainsScheduled: stats.ChainsScheduled,
            chainsRecorded: stats.ChainsRecorded,
            chainsReused: stats.ChainsReused,
            chainsFrameDataRefreshed: stats.ChainsFrameDataRefreshed,
            volatileChainsRecorded: stats.VolatileChainsRecorded,
            secondaryCommandBuffers: stats.SecondaryCommandBuffers,
            visibilityPackets: stats.VisibilityPackets,
            renderPackets: stats.RenderPackets,
            chainWorkerRecordTime: stats.WorkerRecordTime,
            renderThreadWaitForWorkersTime: stats.WaitForWorkersTime,
            firstStructuralDirtyReason: stats.FirstStructuralDirtyReason,
            firstDescriptorGenerationMismatch: stats.FirstDescriptorGenerationMismatch,
            firstResourcePlanRevisionMismatch: stats.FirstResourcePlanRevisionMismatch);

        return schedule;
    }

    private static uint BuildOpenXrCommandChainImageIndex(uint viewIndex, uint imageIndex, Image image)
    {
        int hash = HashCode.Combine("OpenXR", viewIndex, imageIndex);
        return 1_000_000u + (uint)(hash & 0x0FFF_FFFF);
    }

    internal bool TryCopyOpenXrEyeSwapchainImageToTexture(
        Image sourceImage,
        Format sourceFormat,
        Extent2D sourceExtent,
        XRTexture2D? destinationTexture,
        string destinationLabel,
        bool flipY = false)
    {
        try
        {
            var request = new OpenXrEyePreviewCopyRequest(
                sourceImage,
                sourceFormat,
                sourceExtent,
                destinationTexture,
                destinationLabel,
                flipY);

            bool prepared = false;
            OpenXrEyePreviewCopyPlan plan = default;
            if (ShouldDeferOpenXrEyePreviewCopyWork(out string resourceWorkReason) &&
                !(prepared = TryPrepareOpenXrEyeSwapchainPreviewCopy(in request, allowDestinationGeneration: false, out plan)))
            {
                Debug.VulkanWarningEvery(
                    $"OpenXR.Vulkan.Mirror.DeferCopy.{GetHashCode()}.{destinationLabel}",
                    TimeSpan.FromSeconds(1),
                    "[OpenXR] Deferring Vulkan eye mirror copy to '{0}': {1}",
                    destinationLabel,
                    resourceWorkReason);
                return false;
            }

            if (!prepared &&
                !TryPrepareOpenXrEyeSwapchainPreviewCopy(in request, allowDestinationGeneration: true, out plan))
            {
                return false;
            }

            using CommandScope scope = NewCommandScope();
            RecordOpenXrEyeSwapchainPreviewCopy(scope.CommandBuffer, in plan);

            return true;
        }
        catch (Exception ex)
        {
            Debug.VulkanWarningEvery(
                $"OpenXR.Vulkan.Mirror.CopyFailed.{GetHashCode()}.{destinationLabel}",
                TimeSpan.FromSeconds(2),
                "[OpenXR] Vulkan eye mirror copy to '{0}' failed: {1}",
                destinationLabel,
                ex.Message);
            return false;
        }
    }

    private bool TryPrepareOpenXrEyeSwapchainPreviewCopy(
        in OpenXrEyePreviewCopyRequest request,
        bool allowDestinationGeneration,
        out OpenXrEyePreviewCopyPlan plan)
    {
        plan = default;
        if (request.SourceImage.Handle == 0 ||
            request.SourceExtent.Width == 0 ||
            request.SourceExtent.Height == 0 ||
            request.DestinationTexture is null)
        {
            return false;
        }

        XRTexture2D destinationTexture = request.DestinationTexture;
        AbstractRenderAPIObject? destinationObject;
        if (allowDestinationGeneration)
        {
            destinationObject = GetOrCreateAPIRenderObject(destinationTexture, generateNow: true);
        }
        else if (!TryGetAPIRenderObject(destinationTexture, out destinationObject) ||
                 destinationObject is null ||
                 !destinationObject.IsGenerated)
        {
            return false;
        }

        if (destinationObject is not IVkImageDescriptorSource destinationSource)
            return false;

        if (!destinationSource.TryEnsureDescriptorReadyForUse(
                $"OpenXR Vulkan eye mirror copy ({request.DestinationLabel})",
                AllowSynchronousResourceUploads))
        {
            Debug.VulkanWarningEvery(
                $"OpenXR.Vulkan.Mirror.DestinationNotReady.{GetHashCode()}.{request.DestinationLabel}",
                TimeSpan.FromSeconds(2),
                "[OpenXR] Vulkan eye mirror target '{0}' is not descriptor-ready.",
                request.DestinationLabel);
            return false;
        }

        Image destinationImage = destinationSource.DescriptorImage;
        if (destinationImage.Handle == 0)
            return false;

        Extent2D destinationExtent = ResolveOpenXrMirrorDestinationExtent(request.DestinationTexture, destinationSource);
        if (destinationExtent.Width == 0 || destinationExtent.Height == 0)
            return false;

        plan = new OpenXrEyePreviewCopyPlan(
            request.SourceImage,
            request.SourceFormat,
            request.SourceExtent,
            ResolveOpenXrSwapchainImageTrackedLayout(request.SourceImage),
            destinationSource,
            destinationImage,
            destinationExtent,
            ResolveOpenXrMirrorDestinationLayout(destinationSource),
            NormalizeOpenXrMirrorAspect(destinationSource.DescriptorFormat, destinationSource.DescriptorAspect),
            request.DestinationLabel,
            request.FlipY);
        return true;
    }

    private void RecordOpenXrEyeSwapchainPreviewCopy(
        CommandBuffer commandBuffer,
        in OpenXrEyePreviewCopyPlan plan)
    {
        TransitionOpenXrMirrorImage(
            commandBuffer,
            plan.SourceImage,
            plan.SourceFormat,
            plan.SourceOldLayout,
            ImageLayout.TransferSrcOptimal,
            ImageAspectFlags.ColorBit);

        TransitionOpenXrMirrorImage(
            commandBuffer,
            plan.DestinationImage,
            plan.DestinationSource.DescriptorFormat,
            plan.DestinationOldLayout,
            ImageLayout.TransferDstOptimal,
            plan.DestinationAspect);

        ImageBlit blit = new()
        {
            SrcSubresource = new ImageSubresourceLayers
            {
                AspectMask = ImageAspectFlags.ColorBit,
                MipLevel = 0,
                BaseArrayLayer = 0,
                LayerCount = 1
            },
            DstSubresource = new ImageSubresourceLayers
            {
                AspectMask = plan.DestinationAspect,
                MipLevel = 0,
                BaseArrayLayer = 0,
                LayerCount = 1
            }
        };
        blit.SrcOffsets.Element0 = new Offset3D { X = 0, Y = 0, Z = 0 };
        blit.SrcOffsets.Element1 = new Offset3D
        {
            X = checked((int)Math.Min(plan.SourceExtent.Width, (uint)int.MaxValue)),
            Y = checked((int)Math.Min(plan.SourceExtent.Height, (uint)int.MaxValue)),
            Z = 1
        };

        int destinationWidth = checked((int)Math.Min(plan.DestinationExtent.Width, (uint)int.MaxValue));
        int destinationHeight = checked((int)Math.Min(plan.DestinationExtent.Height, (uint)int.MaxValue));
        blit.DstOffsets.Element0 = new Offset3D
        {
            X = 0,
            Y = plan.FlipY ? destinationHeight : 0,
            Z = 0
        };
        blit.DstOffsets.Element1 = new Offset3D
        {
            X = destinationWidth,
            Y = plan.FlipY ? 0 : destinationHeight,
            Z = 1
        };

        CmdBlitImageTracked(
            commandBuffer,
            plan.SourceImage,
            ImageLayout.TransferSrcOptimal,
            plan.DestinationImage,
            ImageLayout.TransferDstOptimal,
            1,
            ref blit,
            Filter.Nearest);

        TransitionOpenXrMirrorImage(
            commandBuffer,
            plan.SourceImage,
            plan.SourceFormat,
            ImageLayout.TransferSrcOptimal,
            ImageLayout.ColorAttachmentOptimal,
            ImageAspectFlags.ColorBit);

        TransitionOpenXrMirrorImage(
            commandBuffer,
            plan.DestinationImage,
            plan.DestinationSource.DescriptorFormat,
            ImageLayout.TransferDstOptimal,
            ImageLayout.ShaderReadOnlyOptimal,
            plan.DestinationAspect);

        if (plan.DestinationSource is IVkFrameBufferAttachmentSource attachmentSource)
            attachmentSource.UpdateAttachmentTrackedLayout(ImageLayout.ShaderReadOnlyOptimal, 0, 0);
    }

    internal bool TryPublishOpenXrEyeMirrorTextures(
        in OpenXrEyeMirrorPublishRequest firstEye,
        in OpenXrEyeMirrorPublishRequest secondEye,
        out bool firstPreviewCopied,
        out bool secondPreviewCopied)
    {
        firstPreviewCopied = false;
        secondPreviewCopied = false;

        try
        {
            if (!TryPrepareOpenXrEyeMirrorPublish(firstEye, out OpenXrEyeMirrorPublishPlan firstPlan) ||
                !TryPrepareOpenXrEyeMirrorPublish(secondEye, out OpenXrEyeMirrorPublishPlan secondPlan))
            {
                return false;
            }

            using CommandScope scope = NewCommandScope();
            RecordOpenXrEyeMirrorPublish(scope.CommandBuffer, in firstPlan, out firstPreviewCopied);
            RecordOpenXrEyeMirrorPublish(scope.CommandBuffer, in secondPlan, out secondPreviewCopied);
            return true;
        }
        catch (Exception ex)
        {
            Debug.VulkanWarningEvery(
                $"OpenXR.Vulkan.Mirror.BatchPublishFailed.{GetHashCode()}",
                TimeSpan.FromSeconds(2),
                "[OpenXR] Vulkan eye mirror batch publish failed: {0}",
                ex.Message);
            return false;
        }
    }

    private bool TryRecordOpenXrEyeMirrorPublishCommandBuffer(
        in OpenXrEyeMirrorPublishPlan firstPlan,
        in OpenXrEyeMirrorPublishPlan secondPlan,
        out CommandBuffer commandBuffer,
        out bool firstPreviewCopied,
        out bool secondPreviewCopied)
    {
        commandBuffer = default;
        firstPreviewCopied = false;
        secondPreviewCopied = false;

        CommandBufferAllocateInfo allocateInfo = new()
        {
            SType = StructureType.CommandBufferAllocateInfo,
            Level = CommandBufferLevel.Primary,
            CommandPool = commandPool,
            CommandBufferCount = 1,
        };

        Result allocateResult = AllocateVulkanCommandBuffersTracked(ref allocateInfo, out commandBuffer, "OpenXR.CommandBuffer");
        if (allocateResult != Result.Success || commandBuffer.Handle == 0)
        {
            Debug.VulkanWarning($"[OpenXR] Failed to allocate eye mirror publish command buffer: {allocateResult}");
            commandBuffer = default;
            return false;
        }

        bool begun = false;
        try
        {
            CommandBufferBeginInfo beginInfo = new()
            {
                SType = StructureType.CommandBufferBeginInfo,
                Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
            };

            Result beginResult = Api!.BeginCommandBuffer(commandBuffer, ref beginInfo);
            if (beginResult != Result.Success)
            {
                Debug.VulkanWarning($"[OpenXR] Failed to begin eye mirror publish command buffer: {beginResult}");
                FreeOpenXrMirrorPublishCommandBuffer(
                    commandBuffer,
                    EVulkanQueueSubmissionDisposition.Completed);
                commandBuffer = default;
                return false;
            }

            begun = true;
            ResetCommandBufferBindState(commandBuffer);
            RecordOpenXrEyeMirrorPublish(commandBuffer, in firstPlan, out firstPreviewCopied);
            RecordOpenXrEyeMirrorPublish(commandBuffer, in secondPlan, out secondPreviewCopied);

            Result endResult = EndCommandBufferTracked(commandBuffer);
            if (endResult != Result.Success)
            {
                Debug.VulkanWarning($"[OpenXR] Failed to end eye mirror publish command buffer: {endResult}");
                FreeOpenXrMirrorPublishCommandBuffer(
                    commandBuffer,
                    EVulkanQueueSubmissionDisposition.Completed);
                commandBuffer = default;
                return false;
            }

            return true;
        }
        catch
        {
            if (begun)
                RemoveCommandBufferBindState(commandBuffer);
            FreeOpenXrMirrorPublishCommandBuffer(
                commandBuffer,
                EVulkanQueueSubmissionDisposition.Completed);
            commandBuffer = default;

            throw;
        }
    }

    private void FreeOpenXrMirrorPublishCommandBuffer(
        CommandBuffer commandBuffer,
        EVulkanQueueSubmissionDisposition submissionDisposition)
    {
        if (commandBuffer.Handle == 0)
            return;

        if (!ShouldFreeTemporaryOpenXrCommandBuffer(submissionDisposition))
        {
            RemoveCommandBufferBindState(commandBuffer);
            return;
        }

        FreeVulkanCommandBufferTracked(commandPool, ref commandBuffer, "OpenXR.Temporary");
        RemoveCommandBufferBindState(commandBuffer);
    }

    private bool TryPrepareOpenXrEyeMirrorPublish(
        in OpenXrEyeMirrorPublishRequest request,
        out OpenXrEyeMirrorPublishPlan plan)
    {
        plan = default;
        if (request.SourceTexture is null ||
            request.SwapchainImage.Handle == 0 ||
            request.Extent.Width == 0 ||
            request.Extent.Height == 0)
        {
            return false;
        }

        if (GetOrCreateAPIRenderObject(request.SourceTexture, generateNow: true) is not IVkImageDescriptorSource source)
            return false;

        if (!source.TryEnsureDescriptorReadyForUse(
                $"OpenXR Vulkan eye mirror publish source ({request.DestinationLabel})",
                AllowSynchronousResourceUploads))
        {
            Debug.VulkanWarningEvery(
                $"OpenXR.Vulkan.Mirror.PublishSourceNotReady.{GetHashCode()}.{request.DestinationLabel}",
                TimeSpan.FromSeconds(2),
                "[OpenXR] Vulkan eye mirror publish source '{0}' is not descriptor-ready.",
                request.SourceTexture.Name ?? "<unnamed>");
            return false;
        }

        Image sourceImage = source.DescriptorImage;
        if (sourceImage.Handle == 0)
            return false;

        Extent2D sourceExtent = ResolveOpenXrMirrorDestinationExtent(request.SourceTexture, source);
        if (sourceExtent.Width == 0 || sourceExtent.Height == 0)
            return false;

        ImageLayout sourceOldLayout = ResolveOpenXrMirrorDestinationLayout(source);
        if (sourceOldLayout == ImageLayout.Undefined)
            sourceOldLayout = ImageLayout.ShaderReadOnlyOptimal;

        ImageAspectFlags sourceAspect = NormalizeOpenXrMirrorAspect(source.DescriptorFormat, source.DescriptorAspect);

        IVkImageDescriptorSource? previewSource = null;
        Image previewImage = default;
        Extent2D previewExtent = default;
        ImageLayout previewOldLayout = ImageLayout.Undefined;
        ImageAspectFlags previewAspect = ImageAspectFlags.ColorBit;

        if (request.PreviewTexture is not null)
        {
            if (GetOrCreateAPIRenderObject(request.PreviewTexture, generateNow: true) is IVkImageDescriptorSource destination &&
                destination.TryEnsureDescriptorReadyForUse(
                    $"OpenXR Vulkan eye mirror publish preview ({request.DestinationLabel})",
                    AllowSynchronousResourceUploads))
            {
                previewImage = destination.DescriptorImage;
                previewExtent = ResolveOpenXrMirrorDestinationExtent(request.PreviewTexture, destination);
                if (previewImage.Handle != 0 && previewExtent.Width > 0 && previewExtent.Height > 0)
                {
                    previewSource = destination;
                    previewOldLayout = ResolveOpenXrMirrorDestinationLayout(destination);
                    previewAspect = NormalizeOpenXrMirrorAspect(destination.DescriptorFormat, destination.DescriptorAspect);
                }
            }

            if (previewSource is null)
            {
                Debug.VulkanWarningEvery(
                    $"OpenXR.Vulkan.Mirror.PublishPreviewNotReady.{GetHashCode()}.{request.DestinationLabel}",
                    TimeSpan.FromSeconds(2),
                    "[OpenXR] Vulkan eye mirror preview target '{0}' is not descriptor-ready.",
                    request.PreviewTexture.Name ?? "<unnamed>");
            }
        }

        plan = new OpenXrEyeMirrorPublishPlan(
            source,
            sourceImage,
            source.DescriptorFormat,
            sourceExtent,
            sourceOldLayout,
            sourceAspect,
            request.SwapchainImage,
            request.SwapchainFormat,
            request.Extent,
            previewSource,
            previewImage,
            previewExtent,
            previewOldLayout,
            previewAspect,
            request.DestinationLabel,
            request.FlipPreviewY);
        return true;
    }

    private void RecordOpenXrEyeMirrorPublish(
        CommandBuffer commandBuffer,
        in OpenXrEyeMirrorPublishPlan plan,
        out bool previewCopied)
    {
        previewCopied = false;

        TransitionOpenXrMirrorImage(
            commandBuffer,
            plan.SourceImage,
            plan.SourceFormat,
            plan.SourceOldLayout,
            ImageLayout.TransferSrcOptimal,
            plan.SourceAspect);

        TransitionOpenXrMirrorImage(
            commandBuffer,
            plan.SwapchainImage,
            plan.SwapchainFormat,
            ImageLayout.Undefined,
            ImageLayout.TransferDstOptimal,
            ImageAspectFlags.ColorBit);

        ImageBlit swapchainBlit = CreateOpenXrMirrorBlit(
            plan.SourceAspect,
            ImageAspectFlags.ColorBit,
            plan.SourceExtent,
            plan.SwapchainExtent,
            flipDestinationY: false);

        CmdBlitImageTracked(
            commandBuffer,
            plan.SourceImage,
            ImageLayout.TransferSrcOptimal,
            plan.SwapchainImage,
            ImageLayout.TransferDstOptimal,
            1,
            ref swapchainBlit,
            Filter.Nearest);

        TransitionOpenXrMirrorImage(
            commandBuffer,
            plan.SwapchainImage,
            plan.SwapchainFormat,
            ImageLayout.TransferDstOptimal,
            ImageLayout.ColorAttachmentOptimal,
            ImageAspectFlags.ColorBit);

        if (plan.PreviewSource is not null && plan.PreviewImage.Handle != 0)
        {
            TransitionOpenXrMirrorImage(
                commandBuffer,
                plan.PreviewImage,
                plan.PreviewSource.DescriptorFormat,
                plan.PreviewOldLayout,
                ImageLayout.TransferDstOptimal,
                plan.PreviewAspect);

            ImageBlit previewBlit = CreateOpenXrMirrorBlit(
                plan.SourceAspect,
                plan.PreviewAspect,
                plan.SourceExtent,
                plan.PreviewExtent,
                plan.FlipPreviewY);

            CmdBlitImageTracked(
                commandBuffer,
                plan.SourceImage,
                ImageLayout.TransferSrcOptimal,
                plan.PreviewImage,
                ImageLayout.TransferDstOptimal,
                1,
                ref previewBlit,
                Filter.Nearest);

            TransitionOpenXrMirrorImage(
                commandBuffer,
                plan.PreviewImage,
                plan.PreviewSource.DescriptorFormat,
                ImageLayout.TransferDstOptimal,
                ImageLayout.ShaderReadOnlyOptimal,
                plan.PreviewAspect);

            if (plan.PreviewSource is IVkFrameBufferAttachmentSource previewAttachmentSource)
                previewAttachmentSource.UpdateAttachmentTrackedLayout(ImageLayout.ShaderReadOnlyOptimal, 0, 0);

            previewCopied = true;
        }

        TransitionOpenXrMirrorImage(
            commandBuffer,
            plan.SourceImage,
            plan.SourceFormat,
            ImageLayout.TransferSrcOptimal,
            plan.SourceOldLayout,
            plan.SourceAspect);

        if (plan.Source is IVkFrameBufferAttachmentSource sourceAttachmentSource)
            sourceAttachmentSource.UpdateAttachmentTrackedLayout(plan.SourceOldLayout, 0, 0);
    }

    private static ImageBlit CreateOpenXrMirrorBlit(
        ImageAspectFlags sourceAspect,
        ImageAspectFlags destinationAspect,
        Extent2D sourceExtent,
        Extent2D destinationExtent,
        bool flipDestinationY)
    {
        ImageBlit blit = new()
        {
            SrcSubresource = new ImageSubresourceLayers
            {
                AspectMask = sourceAspect,
                MipLevel = 0,
                BaseArrayLayer = 0,
                LayerCount = 1
            },
            DstSubresource = new ImageSubresourceLayers
            {
                AspectMask = destinationAspect,
                MipLevel = 0,
                BaseArrayLayer = 0,
                LayerCount = 1
            }
        };

        blit.SrcOffsets.Element0 = new Offset3D { X = 0, Y = 0, Z = 0 };
        blit.SrcOffsets.Element1 = new Offset3D
        {
            X = checked((int)Math.Min(sourceExtent.Width, (uint)int.MaxValue)),
            Y = checked((int)Math.Min(sourceExtent.Height, (uint)int.MaxValue)),
            Z = 1
        };

        int destinationWidth = checked((int)Math.Min(destinationExtent.Width, (uint)int.MaxValue));
        int destinationHeight = checked((int)Math.Min(destinationExtent.Height, (uint)int.MaxValue));
        blit.DstOffsets.Element0 = new Offset3D
        {
            X = 0,
            Y = flipDestinationY ? destinationHeight : 0,
            Z = 0
        };
        blit.DstOffsets.Element1 = new Offset3D
        {
            X = destinationWidth,
            Y = flipDestinationY ? 0 : destinationHeight,
            Z = 1
        };

        return blit;
    }

    internal bool TryCopyOpenXrEyeMirrorTexture(
        XRTexture? sourceTexture,
        XRTexture2D? destinationTexture,
        string destinationLabel,
        bool flipY = false)
    {
        if (IsDeviceLost || sourceTexture is null || destinationTexture is null)
            return false;

        try
        {
            if (GetOrCreateAPIRenderObject(sourceTexture, generateNow: true) is not IVkImageDescriptorSource source)
                return false;

            if (!source.TryEnsureDescriptorReadyForUse(
                    $"OpenXR Vulkan eye mirror source copy ({destinationLabel})",
                    AllowSynchronousResourceUploads))
            {
                Debug.VulkanWarningEvery(
                    $"OpenXR.Vulkan.Mirror.SourceNotReady.{GetHashCode()}.{destinationLabel}",
                    TimeSpan.FromSeconds(2),
                    "[OpenXR] Vulkan eye mirror source '{0}' is not descriptor-ready.",
                    sourceTexture.Name ?? "<unnamed>");
                return false;
            }

            if (GetOrCreateAPIRenderObject(destinationTexture, generateNow: true) is not IVkImageDescriptorSource destination)
                return false;

            if (!destination.TryEnsureDescriptorReadyForUse(
                    $"OpenXR Vulkan eye mirror destination copy ({destinationLabel})",
                    AllowSynchronousResourceUploads))
            {
                Debug.VulkanWarningEvery(
                    $"OpenXR.Vulkan.Mirror.DestinationNotReady.{GetHashCode()}.{destinationLabel}",
                    TimeSpan.FromSeconds(2),
                    "[OpenXR] Vulkan eye mirror destination '{0}' is not descriptor-ready.",
                    destinationTexture.Name ?? "<unnamed>");
                return false;
            }

            Image sourceImage = source.DescriptorImage;
            Image destinationImage = destination.DescriptorImage;
            if (sourceImage.Handle == 0 || destinationImage.Handle == 0)
                return false;

            Extent2D sourceExtent = ResolveOpenXrMirrorSourceExtent(sourceTexture, source);
            Extent2D destinationExtent = ResolveOpenXrMirrorDestinationExtent(destinationTexture, destination);
            if (sourceExtent.Width == 0 || sourceExtent.Height == 0 ||
                destinationExtent.Width == 0 || destinationExtent.Height == 0)
                return false;

            ImageLayout sourceOldLayout = ResolveOpenXrMirrorDestinationLayout(source);
            if (sourceOldLayout == ImageLayout.Undefined)
                sourceOldLayout = ImageLayout.ColorAttachmentOptimal;

            ImageLayout destinationOldLayout = ResolveOpenXrMirrorDestinationLayout(destination);

            ImageAspectFlags sourceAspect = NormalizeOpenXrMirrorAspect(source.DescriptorFormat, source.DescriptorAspect);
            ImageAspectFlags destinationAspect = NormalizeOpenXrMirrorAspect(destination.DescriptorFormat, destination.DescriptorAspect);
            uint sourceBaseArrayLayer = ResolveOpenXrMirrorBaseArrayLayer(sourceTexture);

            using CommandScope scope = NewCommandScope();
            CommandBuffer commandBuffer = scope.CommandBuffer;

            TransitionOpenXrMirrorImage(
                commandBuffer,
                sourceImage,
                source.DescriptorFormat,
                sourceOldLayout,
                ImageLayout.TransferSrcOptimal,
                sourceAspect,
                sourceBaseArrayLayer,
                1u);

            TransitionOpenXrMirrorImage(
                commandBuffer,
                destinationImage,
                destination.DescriptorFormat,
                destinationOldLayout,
                ImageLayout.TransferDstOptimal,
                destinationAspect);

            ImageBlit blit = new()
            {
                SrcSubresource = new ImageSubresourceLayers
                {
                    AspectMask = sourceAspect,
                    MipLevel = 0,
                    BaseArrayLayer = sourceBaseArrayLayer,
                    LayerCount = 1
                },
                DstSubresource = new ImageSubresourceLayers
                {
                    AspectMask = destinationAspect,
                    MipLevel = 0,
                    BaseArrayLayer = 0,
                    LayerCount = 1
                }
            };
            blit.SrcOffsets.Element0 = new Offset3D { X = 0, Y = 0, Z = 0 };
            blit.SrcOffsets.Element1 = new Offset3D
            {
                X = checked((int)Math.Min(sourceExtent.Width, (uint)int.MaxValue)),
                Y = checked((int)Math.Min(sourceExtent.Height, (uint)int.MaxValue)),
                Z = 1
            };

            int destinationWidth = checked((int)Math.Min(destinationExtent.Width, (uint)int.MaxValue));
            int destinationHeight = checked((int)Math.Min(destinationExtent.Height, (uint)int.MaxValue));
            blit.DstOffsets.Element0 = new Offset3D
            {
                X = 0,
                Y = flipY ? destinationHeight : 0,
                Z = 0
            };
            blit.DstOffsets.Element1 = new Offset3D
            {
                X = destinationWidth,
                Y = flipY ? 0 : destinationHeight,
                Z = 1
            };

            CmdBlitImageTracked(
                commandBuffer,
                sourceImage,
                ImageLayout.TransferSrcOptimal,
                destinationImage,
                ImageLayout.TransferDstOptimal,
                1,
                ref blit,
                Filter.Nearest);

            TransitionOpenXrMirrorImage(
                commandBuffer,
                sourceImage,
                source.DescriptorFormat,
                ImageLayout.TransferSrcOptimal,
                sourceOldLayout,
                sourceAspect,
                sourceBaseArrayLayer,
                1u);

            TransitionOpenXrMirrorImage(
                commandBuffer,
                destinationImage,
                destination.DescriptorFormat,
                ImageLayout.TransferDstOptimal,
                ImageLayout.ShaderReadOnlyOptimal,
                destinationAspect);

            if (destination is IVkFrameBufferAttachmentSource destinationAttachmentSource)
                destinationAttachmentSource.UpdateAttachmentTrackedLayout(ImageLayout.ShaderReadOnlyOptimal, 0, 0);
            if (source is IVkFrameBufferAttachmentSource sourceAttachmentSource)
                sourceAttachmentSource.UpdateAttachmentTrackedLayout(sourceOldLayout, 0, 0);

            return true;
        }
        catch (Exception ex)
        {
            Debug.VulkanWarningEvery(
                $"OpenXR.Vulkan.Mirror.TextureCopyFailed.{GetHashCode()}.{destinationLabel}",
                TimeSpan.FromSeconds(2),
                "[OpenXR] Vulkan eye mirror texture copy to '{0}' failed: {1}",
                destinationLabel,
                ex.Message);
            return false;
        }
    }

    private static uint ResolveOpenXrMirrorBaseArrayLayer(XRTexture texture)
        => texture is XRTextureViewBase view ? view.MinLayer : 0u;

    internal bool TryBlitTextureToOpenXrSwapchainImage(
        XRTexture2D? sourceTexture,
        Image destinationImage,
        Format destinationFormat,
        Extent2D destinationExtent,
        string destinationLabel)
    {
        if (sourceTexture is null || destinationImage.Handle == 0 || destinationExtent.Width == 0 || destinationExtent.Height == 0)
            return false;

        try
        {
            if (GetOrCreateAPIRenderObject(sourceTexture, generateNow: true) is not IVkImageDescriptorSource source)
                return false;

            if (!source.TryEnsureDescriptorReadyForUse(
                    $"OpenXR Vulkan eye source blit ({destinationLabel})",
                    AllowSynchronousResourceUploads))
            {
                Debug.VulkanWarningEvery(
                    $"OpenXR.Vulkan.Blit.SourceNotReady.{GetHashCode()}.{destinationLabel}",
                    TimeSpan.FromSeconds(2),
                    "[OpenXR] Vulkan eye blit source '{0}' is not descriptor-ready.",
                    sourceTexture.Name ?? "<unnamed>");
                return false;
            }

            Image sourceImage = source.DescriptorImage;
            if (sourceImage.Handle == 0)
                return false;

            Extent2D sourceExtent = ResolveOpenXrMirrorDestinationExtent(sourceTexture, source);
            if (sourceExtent.Width == 0 || sourceExtent.Height == 0)
                return false;

            ImageLayout sourceOldLayout = ResolveOpenXrMirrorDestinationLayout(source);
            if (sourceOldLayout == ImageLayout.Undefined)
                sourceOldLayout = ImageLayout.ColorAttachmentOptimal;

            using CommandScope scope = NewCommandScope();
            CommandBuffer commandBuffer = scope.CommandBuffer;

            TransitionOpenXrMirrorImage(
                commandBuffer,
                sourceImage,
                source.DescriptorFormat,
                sourceOldLayout,
                ImageLayout.TransferSrcOptimal,
                source.DescriptorAspect);

            TransitionOpenXrMirrorImage(
                commandBuffer,
                destinationImage,
                destinationFormat,
                ImageLayout.Undefined,
                ImageLayout.TransferDstOptimal,
                ImageAspectFlags.ColorBit);

            ImageBlit blit = new()
            {
                SrcSubresource = new ImageSubresourceLayers
                {
                    AspectMask = NormalizeOpenXrMirrorAspect(source.DescriptorFormat, source.DescriptorAspect),
                    MipLevel = 0,
                    BaseArrayLayer = 0,
                    LayerCount = 1
                },
                DstSubresource = new ImageSubresourceLayers
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    MipLevel = 0,
                    BaseArrayLayer = 0,
                    LayerCount = 1
                }
            };
            blit.SrcOffsets.Element0 = new Offset3D { X = 0, Y = 0, Z = 0 };
            blit.SrcOffsets.Element1 = new Offset3D
            {
                X = checked((int)Math.Min(sourceExtent.Width, (uint)int.MaxValue)),
                Y = checked((int)Math.Min(sourceExtent.Height, (uint)int.MaxValue)),
                Z = 1
            };
            blit.DstOffsets.Element0 = new Offset3D { X = 0, Y = 0, Z = 0 };
            blit.DstOffsets.Element1 = new Offset3D
            {
                X = checked((int)Math.Min(destinationExtent.Width, (uint)int.MaxValue)),
                Y = checked((int)Math.Min(destinationExtent.Height, (uint)int.MaxValue)),
                Z = 1
            };

            CmdBlitImageTracked(
                commandBuffer,
                sourceImage,
                ImageLayout.TransferSrcOptimal,
                destinationImage,
                ImageLayout.TransferDstOptimal,
                1,
                ref blit,
                Filter.Nearest);

            TransitionOpenXrMirrorImage(
                commandBuffer,
                sourceImage,
                source.DescriptorFormat,
                ImageLayout.TransferSrcOptimal,
                sourceOldLayout,
                source.DescriptorAspect);

            TransitionOpenXrMirrorImage(
                commandBuffer,
                destinationImage,
                destinationFormat,
                ImageLayout.TransferDstOptimal,
                ImageLayout.ColorAttachmentOptimal,
                ImageAspectFlags.ColorBit);

            return true;
        }
        catch (Exception ex)
        {
            Debug.VulkanWarningEvery(
                $"OpenXR.Vulkan.Blit.CopyFailed.{GetHashCode()}.{destinationLabel}",
                TimeSpan.FromSeconds(2),
                "[OpenXR] Vulkan eye blit to '{0}' failed: {1}",
                destinationLabel,
                ex.Message);
            return false;
        }
    }

    internal bool TryBlitTextureArrayLayerToOpenXrSwapchainImage(
        XRTexture2DArray? sourceTexture,
        uint sourceLayer,
        Image destinationImage,
        Format destinationFormat,
        Extent2D destinationExtent,
        string destinationLabel,
        bool flipY = false)
    {
        if (sourceTexture is null || destinationImage.Handle == 0 || destinationExtent.Width == 0 || destinationExtent.Height == 0)
            return false;

        try
        {
            if (GetOrCreateAPIRenderObject(sourceTexture, generateNow: true) is not IVkImageDescriptorSource source)
                return false;

            uint sourceLayerCount = Math.Max(source.DescriptorArrayLayers, sourceTexture.Depth);
            if (sourceLayer >= sourceLayerCount)
            {
                Debug.VulkanWarningEvery(
                    $"OpenXR.Vulkan.StereoBlit.LayerOutOfRange.{GetHashCode()}.{destinationLabel}",
                    TimeSpan.FromSeconds(2),
                    "[OpenXR] Vulkan stereo blit source layer {0} is out of range for '{1}' ({2} layers).",
                    sourceLayer,
                    sourceTexture.Name ?? "<unnamed>",
                    sourceLayerCount);
                return false;
            }

            if (!source.TryEnsureDescriptorReadyForUse(
                    $"OpenXR Vulkan stereo array source blit ({destinationLabel})",
                    AllowSynchronousResourceUploads))
            {
                Debug.VulkanWarningEvery(
                    $"OpenXR.Vulkan.StereoBlit.SourceNotReady.{GetHashCode()}.{destinationLabel}",
                    TimeSpan.FromSeconds(2),
                    "[OpenXR] Vulkan stereo blit source '{0}' is not descriptor-ready.",
                    sourceTexture.Name ?? "<unnamed>");
                return false;
            }

            Image sourceImage = source.DescriptorImage;
            if (sourceImage.Handle == 0)
                return false;

            Extent2D sourceExtent = ResolveOpenXrMirrorDestinationExtent(sourceTexture, source, sourceLayer);
            if (sourceExtent.Width == 0 || sourceExtent.Height == 0)
                return false;

            ImageAspectFlags sourceAspect = NormalizeOpenXrMirrorAspect(source.DescriptorFormat, source.DescriptorAspect);
            ImageLayout sourceOldLayout = ResolveOpenXrAttachmentLayout(source, sourceLayer);
            if (sourceOldLayout == ImageLayout.Undefined)
            {
                Debug.VulkanWarningEvery(
                    $"OpenXR.Vulkan.StereoBlit.SourceLayoutUndefined.{GetHashCode()}.{sourceTexture.GetHashCode()}.{sourceLayer}",
                    TimeSpan.FromSeconds(1),
                    "[OpenXR] Vulkan stereo blit source layer {0} of '{1}' had undefined tracked layout before publishing to '{2}'; falling back to ShaderReadOnlyOptimal.",
                    sourceLayer,
                    sourceTexture.Name ?? "<unnamed>",
                    destinationLabel);
                sourceOldLayout = ImageLayout.ShaderReadOnlyOptimal;
            }

            if (TraceOpenXrStereoBlits)
            {
                Debug.VulkanEvery(
                    $"OpenXR.Vulkan.StereoBlit.Source.{GetHashCode()}.{sourceTexture.GetHashCode()}.{sourceLayer}",
                    TimeSpan.FromSeconds(1),
                    "[OpenXR] Vulkan stereo blit source='{0}' layer={1}/{2} oldLayout={3} aspect={4} image=0x{5:X} dst='{6}' dstImage=0x{7:X} extent={8}x{9}",
                    sourceTexture.Name ?? "<unnamed>",
                    sourceLayer,
                    sourceLayerCount,
                    sourceOldLayout,
                    sourceAspect,
                    sourceImage.Handle,
                    destinationLabel,
                    destinationImage.Handle,
                    destinationExtent.Width,
                    destinationExtent.Height);
            }

            using CommandScope scope = NewCommandScope();
            CommandBuffer commandBuffer = scope.CommandBuffer;

            TransitionOpenXrMirrorImage(
                commandBuffer,
                sourceImage,
                source.DescriptorFormat,
                sourceOldLayout,
                ImageLayout.TransferSrcOptimal,
                sourceAspect,
                sourceLayer,
                1u);

            TransitionOpenXrMirrorImage(
                commandBuffer,
                destinationImage,
                destinationFormat,
                ImageLayout.Undefined,
                ImageLayout.TransferDstOptimal,
                ImageAspectFlags.ColorBit);

            ImageBlit blit = new()
            {
                SrcSubresource = new ImageSubresourceLayers
                {
                    AspectMask = sourceAspect,
                    MipLevel = 0,
                    BaseArrayLayer = sourceLayer,
                    LayerCount = 1
                },
                DstSubresource = new ImageSubresourceLayers
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    MipLevel = 0,
                    BaseArrayLayer = 0,
                    LayerCount = 1
                }
            };
            blit.SrcOffsets.Element0 = new Offset3D { X = 0, Y = 0, Z = 0 };
            blit.SrcOffsets.Element1 = new Offset3D
            {
                X = checked((int)Math.Min(sourceExtent.Width, (uint)int.MaxValue)),
                Y = checked((int)Math.Min(sourceExtent.Height, (uint)int.MaxValue)),
                Z = 1
            };
            int destinationWidth = checked((int)Math.Min(destinationExtent.Width, (uint)int.MaxValue));
            int destinationHeight = checked((int)Math.Min(destinationExtent.Height, (uint)int.MaxValue));
            blit.DstOffsets.Element0 = new Offset3D
            {
                X = 0,
                Y = flipY ? destinationHeight : 0,
                Z = 0
            };
            blit.DstOffsets.Element1 = new Offset3D
            {
                X = destinationWidth,
                Y = flipY ? 0 : destinationHeight,
                Z = 1
            };

            CmdBlitImageTracked(
                commandBuffer,
                sourceImage,
                ImageLayout.TransferSrcOptimal,
                destinationImage,
                ImageLayout.TransferDstOptimal,
                1,
                ref blit,
                Filter.Nearest);

            TransitionOpenXrMirrorImage(
                commandBuffer,
                sourceImage,
                source.DescriptorFormat,
                ImageLayout.TransferSrcOptimal,
                sourceOldLayout,
                sourceAspect,
                sourceLayer,
                1u);

            TransitionOpenXrMirrorImage(
                commandBuffer,
                destinationImage,
                destinationFormat,
                ImageLayout.TransferDstOptimal,
                ImageLayout.ColorAttachmentOptimal,
                ImageAspectFlags.ColorBit);

            if (source is IVkFrameBufferAttachmentSource attachmentSource)
                attachmentSource.UpdateAttachmentTrackedLayout(sourceOldLayout, 0, checked((int)sourceLayer));

            return true;
        }
        catch (Exception ex)
        {
            Debug.VulkanWarningEvery(
                $"OpenXR.Vulkan.StereoBlit.CopyFailed.{GetHashCode()}.{destinationLabel}",
                TimeSpan.FromSeconds(2),
                "[OpenXR] Vulkan stereo layer blit to '{0}' failed: {1}",
                destinationLabel,
                ex.Message);
            return false;
        }
    }

    internal bool TryBlitTextureArrayLayersToOpenXrSwapchainImages(
        XRTexture2DArray? sourceTexture,
        Image leftDestinationImage,
        Format leftDestinationFormat,
        Extent2D leftDestinationExtent,
        string leftDestinationLabel,
        Image rightDestinationImage,
        Format rightDestinationFormat,
        Extent2D rightDestinationExtent,
        string rightDestinationLabel,
        bool flipY = false)
    {
        try
        {
            if (!TryPrepareStereoLayerBlit(
                    sourceTexture,
                    default,
                    leftDestinationImage,
                    leftDestinationFormat,
                    leftDestinationExtent,
                    leftDestinationLabel,
                    rightDestinationImage,
                    rightDestinationFormat,
                    rightDestinationExtent,
                    rightDestinationLabel,
                    flipY,
                    out OpenXrStereoLayerBlitPlan plan))
            {
                return false;
            }

            using CommandScope scope = NewCommandScope();
            RecordStereoLayerBlits(scope.CommandBuffer, in plan);
            UpdateStereoLayerBlitTrackedLayouts(in plan);

            return true;
        }
        catch (Exception ex)
        {
            Debug.VulkanWarningEvery(
                $"OpenXR.Vulkan.StereoBlit.BatchedCopyFailed.{GetHashCode()}",
                TimeSpan.FromSeconds(2),
                "[OpenXR] Vulkan stereo batched layer blit failed: {0}",
                ex.Message);
            return false;
        }
    }

    private bool TryRecordStereoLayerBlitCommandBuffer(
        in OpenXrStereoLayerBlitPlan plan,
        out CommandBuffer commandBuffer)
    {
        commandBuffer = default;
        CommandBufferAllocateInfo allocateInfo = new()
        {
            SType = StructureType.CommandBufferAllocateInfo,
            Level = CommandBufferLevel.Primary,
            CommandPool = commandPool,
            CommandBufferCount = 1,
        };

        Result allocateResult = AllocateVulkanCommandBuffersTracked(ref allocateInfo, out commandBuffer, "OpenXR.CommandBuffer");
        if (allocateResult != Result.Success || commandBuffer.Handle == 0)
        {
            Debug.VulkanWarning($"[OpenXR] Failed to allocate stereo layer publish command buffer: {allocateResult}");
            commandBuffer = default;
            return false;
        }

        bool begun = false;
        try
        {
            CommandBufferBeginInfo beginInfo = new()
            {
                SType = StructureType.CommandBufferBeginInfo,
                Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
            };

            Result beginResult = Api!.BeginCommandBuffer(commandBuffer, ref beginInfo);
            if (beginResult != Result.Success)
            {
                Debug.VulkanWarning($"[OpenXR] Failed to begin stereo layer publish command buffer: {beginResult}");
                FreeOpenXrMirrorPublishCommandBuffer(
                    commandBuffer,
                    EVulkanQueueSubmissionDisposition.Completed);
                commandBuffer = default;
                return false;
            }

            begun = true;
            ResetCommandBufferBindState(commandBuffer);
            RecordStereoLayerBlits(commandBuffer, in plan);

            Result endResult = EndCommandBufferTracked(commandBuffer);
            if (endResult != Result.Success)
            {
                Debug.VulkanWarning($"[OpenXR] Failed to end stereo layer publish command buffer: {endResult}");
                FreeOpenXrMirrorPublishCommandBuffer(
                    commandBuffer,
                    EVulkanQueueSubmissionDisposition.Completed);
                commandBuffer = default;
                return false;
            }

            return true;
        }
        catch
        {
            if (begun)
                RemoveCommandBufferBindState(commandBuffer);
            FreeOpenXrMirrorPublishCommandBuffer(
                commandBuffer,
                EVulkanQueueSubmissionDisposition.Completed);
            commandBuffer = default;
            throw;
        }
    }

    private bool TryPrepareStereoLayerBlit(
        XRTexture2DArray? sourceTexture,
        CommandBuffer recordedSourceCommandBuffer,
        Image leftDestinationImage,
        Format leftDestinationFormat,
        Extent2D leftDestinationExtent,
        string leftDestinationLabel,
        Image rightDestinationImage,
        Format rightDestinationFormat,
        Extent2D rightDestinationExtent,
        string rightDestinationLabel,
        bool flipY,
        out OpenXrStereoLayerBlitPlan plan)
    {
        plan = default;
        if (sourceTexture is null ||
            leftDestinationImage.Handle == 0 ||
            rightDestinationImage.Handle == 0 ||
            leftDestinationExtent.Width == 0 ||
            leftDestinationExtent.Height == 0 ||
            rightDestinationExtent.Width == 0 ||
            rightDestinationExtent.Height == 0)
        {
            return false;
        }

        // Strict SPS publishes with transfer commands and therefore does not create the
        // per-eye image views used by the direct-render path. Register the runtime-owned
        // images explicitly so a VkImage handle recycled from a completed engine resource
        // receives a fresh lifetime generation before command-buffer dependency tracking.
        RegisterVulkanResource(
            ObjectType.Image,
            leftDestinationImage.Handle,
            $"OpenXR.SwapchainImage.{leftDestinationLabel}",
            externallyOwned: true);
        RegisterVulkanResource(
            ObjectType.Image,
            rightDestinationImage.Handle,
            $"OpenXR.SwapchainImage.{rightDestinationLabel}",
            externallyOwned: true);

        if (GetOrCreateAPIRenderObject(sourceTexture, generateNow: true) is not IVkImageDescriptorSource source)
            return false;

        uint sourceLayerCount = Math.Max(source.DescriptorArrayLayers, sourceTexture.Depth);
        if (sourceLayerCount < 2)
        {
            Debug.VulkanWarningEvery(
                $"OpenXR.Vulkan.StereoBlit.LayerCountTooSmall.{GetHashCode()}.{sourceTexture.GetHashCode()}",
                TimeSpan.FromSeconds(2),
                "[OpenXR] Vulkan stereo blit source '{0}' has {1} layer(s); expected at least 2.",
                sourceTexture.Name ?? "<unnamed>",
                sourceLayerCount);
            return false;
        }

        if (!source.TryEnsureDescriptorReadyForUse(
                "OpenXR Vulkan stereo array source batched blit",
                AllowSynchronousResourceUploads))
        {
            Debug.VulkanWarningEvery(
                $"OpenXR.Vulkan.StereoBlit.SourceNotReady.{GetHashCode()}.{sourceTexture.GetHashCode()}",
                TimeSpan.FromSeconds(2),
                "[OpenXR] Vulkan stereo blit source '{0}' is not descriptor-ready.",
                sourceTexture.Name ?? "<unnamed>");
            return false;
        }

        Image sourceImage = source.DescriptorImage;
        if (sourceImage.Handle == 0)
            return false;

        ImageAspectFlags sourceAspect = NormalizeOpenXrMirrorAspect(source.DescriptorFormat, source.DescriptorAspect);
        if (!TryResolveStereoBlitLayer(0, leftDestinationLabel, out Extent2D leftSourceExtent, out ImageLayout leftSourceOldLayout) ||
            !TryResolveStereoBlitLayer(1, rightDestinationLabel, out Extent2D rightSourceExtent, out ImageLayout rightSourceOldLayout))
        {
            return false;
        }

        plan = new OpenXrStereoLayerBlitPlan(
            source,
            sourceImage,
            source.DescriptorFormat,
            sourceAspect,
            leftSourceExtent,
            leftSourceOldLayout,
            rightSourceExtent,
            rightSourceOldLayout,
            leftDestinationImage,
            leftDestinationFormat,
            leftDestinationExtent,
            rightDestinationImage,
            rightDestinationFormat,
            rightDestinationExtent,
            flipY);
        return true;

        bool TryResolveStereoBlitLayer(
            uint sourceLayer,
            string destinationLabel,
            out Extent2D sourceExtent,
            out ImageLayout sourceOldLayout)
        {
            sourceExtent = ResolveOpenXrMirrorDestinationExtent(sourceTexture, source, sourceLayer);
            if (sourceExtent.Width == 0 || sourceExtent.Height == 0)
            {
                sourceOldLayout = ImageLayout.Undefined;
                return false;
            }

            ImageSubresourceRange sourceRange = new()
            {
                AspectMask = sourceAspect,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = sourceLayer,
                LayerCount = 1,
            };
            sourceOldLayout = recordedSourceCommandBuffer.Handle != 0 &&
                TryGetRecordedImageLayout(
                    recordedSourceCommandBuffer,
                    sourceImage,
                    sourceRange,
                    out ImageLayout recordedSourceLayout)
                    ? recordedSourceLayout
                    : ResolveOpenXrAttachmentLayout(source, sourceLayer);
            if (sourceOldLayout == ImageLayout.Undefined)
            {
                Debug.VulkanWarningEvery(
                    $"OpenXR.Vulkan.StereoBlit.SourceLayoutUndefined.{GetHashCode()}.{sourceTexture.GetHashCode()}.{sourceLayer}",
                    TimeSpan.FromSeconds(1),
                    "[OpenXR] Vulkan stereo blit source layer {0} of '{1}' had undefined tracked layout before publishing to '{2}'; falling back to ShaderReadOnlyOptimal.",
                    sourceLayer,
                    sourceTexture.Name ?? "<unnamed>",
                    destinationLabel);
                sourceOldLayout = ImageLayout.ShaderReadOnlyOptimal;
            }

            if (TraceOpenXrStereoBlits)
            {
                Debug.VulkanEvery(
                    $"OpenXR.Vulkan.StereoBlit.Source.{GetHashCode()}.{sourceTexture.GetHashCode()}.{sourceLayer}",
                    TimeSpan.FromSeconds(1),
                    "[OpenXR] Vulkan stereo blit source='{0}' layer={1}/{2} oldLayout={3} aspect={4} image=0x{5:X} dst='{6}'",
                    sourceTexture.Name ?? "<unnamed>",
                    sourceLayer,
                    sourceLayerCount,
                    sourceOldLayout,
                    sourceAspect,
                    sourceImage.Handle,
                    destinationLabel);
            }
            return true;
        }
    }

    private void RecordStereoLayerBlits(
        CommandBuffer commandBuffer,
        in OpenXrStereoLayerBlitPlan plan)
    {
        EmitStereoLayerBlit(
            commandBuffer,
            plan,
            sourceLayer: 0,
            plan.LeftSourceExtent,
            plan.LeftSourceOldLayout,
            plan.LeftDestinationImage,
            plan.LeftDestinationFormat,
            plan.LeftDestinationExtent);
        EmitStereoLayerBlit(
            commandBuffer,
            plan,
            sourceLayer: 1,
            plan.RightSourceExtent,
            plan.RightSourceOldLayout,
            plan.RightDestinationImage,
            plan.RightDestinationFormat,
            plan.RightDestinationExtent);
    }

    private void EmitStereoLayerBlit(
        CommandBuffer commandBuffer,
        in OpenXrStereoLayerBlitPlan plan,
        uint sourceLayer,
        Extent2D sourceExtent,
        ImageLayout sourceOldLayout,
        Image destinationImage,
        Format destinationFormat,
        Extent2D destinationExtent)
    {
        TransitionOpenXrMirrorImage(
            commandBuffer,
            plan.SourceImage,
            plan.SourceFormat,
            sourceOldLayout,
            ImageLayout.TransferSrcOptimal,
            plan.SourceAspect,
            sourceLayer,
            1u);

        TransitionOpenXrMirrorImage(
            commandBuffer,
            destinationImage,
            destinationFormat,
            ImageLayout.Undefined,
            ImageLayout.TransferDstOptimal,
            ImageAspectFlags.ColorBit);

        ImageBlit blit = new()
        {
            SrcSubresource = new ImageSubresourceLayers
            {
                AspectMask = plan.SourceAspect,
                MipLevel = 0,
                BaseArrayLayer = sourceLayer,
                LayerCount = 1
            },
            DstSubresource = new ImageSubresourceLayers
            {
                AspectMask = ImageAspectFlags.ColorBit,
                MipLevel = 0,
                BaseArrayLayer = 0,
                LayerCount = 1
            }
        };
        blit.SrcOffsets.Element0 = new Offset3D { X = 0, Y = 0, Z = 0 };
        blit.SrcOffsets.Element1 = new Offset3D
        {
            X = checked((int)Math.Min(sourceExtent.Width, (uint)int.MaxValue)),
            Y = checked((int)Math.Min(sourceExtent.Height, (uint)int.MaxValue)),
            Z = 1
        };
        int destinationWidth = checked((int)Math.Min(destinationExtent.Width, (uint)int.MaxValue));
        int destinationHeight = checked((int)Math.Min(destinationExtent.Height, (uint)int.MaxValue));
        blit.DstOffsets.Element0 = new Offset3D
        {
            X = 0,
            Y = plan.FlipY ? destinationHeight : 0,
            Z = 0
        };
        blit.DstOffsets.Element1 = new Offset3D
        {
            X = destinationWidth,
            Y = plan.FlipY ? 0 : destinationHeight,
            Z = 1
        };

        CmdBlitImageTracked(
            commandBuffer,
            plan.SourceImage,
            ImageLayout.TransferSrcOptimal,
            destinationImage,
            ImageLayout.TransferDstOptimal,
            1,
            ref blit,
            Filter.Nearest);

        TransitionOpenXrMirrorImage(
            commandBuffer,
            plan.SourceImage,
            plan.SourceFormat,
            ImageLayout.TransferSrcOptimal,
            sourceOldLayout,
            plan.SourceAspect,
            sourceLayer,
            1u);

        TransitionOpenXrMirrorImage(
            commandBuffer,
            destinationImage,
            destinationFormat,
            ImageLayout.TransferDstOptimal,
            ImageLayout.ColorAttachmentOptimal,
            ImageAspectFlags.ColorBit);
    }

    private static void UpdateStereoLayerBlitTrackedLayouts(in OpenXrStereoLayerBlitPlan plan)
    {
        if (plan.Source is not IVkFrameBufferAttachmentSource attachmentSource)
            return;

        attachmentSource.UpdateAttachmentTrackedLayout(plan.LeftSourceOldLayout, 0, 0);
        attachmentSource.UpdateAttachmentTrackedLayout(plan.RightSourceOldLayout, 0, 1);
    }

    internal void PrewarmOpenXrEyeSwapchainResources(
        Format format,
        Extent2D extent,
        int resourcePlannerStateIndex,
        Action emitFrameOps)
    {
        if (extent.Width == 0 || extent.Height == 0)
            return;

        if (ShouldDeferOpenXrVulkanResourceWork(out string resourceWorkReason))
        {
            Debug.VulkanWarningEvery(
                $"OpenXR.Vulkan.PrewarmEyeDeferred.{GetHashCode()}.{resourcePlannerStateIndex}",
                TimeSpan.FromSeconds(1),
                "[OpenXR] Deferring Vulkan eye resource prewarm: {0}",
                resourceWorkReason);
            return;
        }

        int openXrFrameDataSlotCount = ResolveOpenXrFrameDataSlotCount(swapChainImages?.Length ?? 0);

        uint prewarmViewIndex = ResolveOpenXrExternalSwapchainViewIndex(resourcePlannerStateIndex);
        using IDisposable externalScope = EnterOpenXrExternalSwapchainRenderScope(
            extent.Width,
            extent.Height,
            BuildOpenXrExternalSwapchainPlannerTargetIdentity(prewarmViewIndex),
            ResolveOpenXrExternalSwapchainTargetName(prewarmViewIndex));
        using ThreadRenderStateScope renderStateScope = EnterThreadRenderStateScope(
            CreateOpenXrPrewarmRenderStateTracker(extent));
        _openXrExternalSwapchainPrewarmDepth++;

        try
        {
            EnsureOpenXrFrameDataSlotCapacity(openXrFrameDataSlotCount);
            EnsureDescriptorFrameSlotFrameCountFloor(openXrFrameDataSlotCount);
            DrainRetiredResourcesFromCompletedSubmittedFrameSlots();

            using (EnterOpenXrResourcePlannerThreadScope(
                resourcePlannerStateIndex,
                EOpenXrResourcePlannerPurpose.EyePrewarm))
            {
                if (ShouldDeferOpenXrVulkanResourceWork(out string scopedResourceWorkReason))
                {
                    Debug.VulkanWarningEvery(
                        $"OpenXR.Vulkan.PrewarmEyeScopedDeferred.{GetHashCode()}.{resourcePlannerStateIndex}",
                        TimeSpan.FromSeconds(1),
                        "[OpenXR] Deferring Vulkan eye resource prewarm: {0}",
                        scopedResourceWorkReason);
                    return;
                }

                FrameOp[] ops = CaptureFrameOpsExcludingTextureUploads(emitFrameOps, out _);
                ops = FilterDiagnosticSkippedFrameOps(ops);
                if (ops.Length == 0)
                    return;
                ops = NormalizeOpenXrExternalSwapchainFrameOps(ops, extent);
                ValidateOpenXrExternalFrameOpContexts(
                    ops,
                    extent,
                    (uint)Math.Max(resourcePlannerStateIndex, 0),
                    "eye swapchain prewarm");

                using (RuntimeRenderingHostServices.Current.StartProfileScope("OpenXR.Vulkan.PrewarmEye.Sort"))
                    ops = VulkanRenderGraphCompiler.SortFrameOps(ops, CompiledRenderGraph);
                if (TryDescribeRecentResourceAllocationFailure(out string prePlanFailureReason))
                {
                    Debug.VulkanWarningEvery(
                        $"OpenXR.Vulkan.PrewarmEyePlanDeferred.{GetHashCode()}.{resourcePlannerStateIndex}",
                        TimeSpan.FromSeconds(1),
                        "[OpenXR] Deferring Vulkan eye resource prewarm: {0}",
                        prePlanFailureReason);
                    return;
                }

                FrameOpContext plannerContext = PrepareResourcePlannerForFrameOps(ops);
                if (TryDescribeRecentResourceAllocationFailure(out string postPlanFailureReason))
                {
                    Debug.VulkanWarningEvery(
                        $"OpenXR.Vulkan.PrewarmEyePlanFailed.{GetHashCode()}.{resourcePlannerStateIndex}",
                        TimeSpan.FromSeconds(1),
                        "[OpenXR] Deferring Vulkan eye resource prewarm: {0}",
                        postPlanFailureReason);
                    return;
                }

                if (!TryRefreshFrameOpResourceWrappers(
                    ops,
                    plannerContext,
                    "OpenXR eye resource prewarm refresh",
                    AllowSynchronousResourceUploads,
                    out string refreshFailureReason))
                {
                    Debug.VulkanWarningEvery(
                        $"OpenXR.Vulkan.PrewarmEyeRefreshDeferred.{GetHashCode()}.{resourcePlannerStateIndex}",
                        TimeSpan.FromSeconds(1),
                        "[OpenXR] Deferring Vulkan eye resource prewarm: {0}",
                        refreshFailureReason);
                    return;
                }
                PrewarmOpenXrFrameOpResources(
                    ops,
                    ResolveOpenXrRecordImageIndex(resourcePlannerStateIndex, swapChainImages?.Length ?? 0));
            }
        }
        catch (Exception ex)
        {
            _ = DrainFrameOpsExcludingTextureUploads(out _);
            if (IsOpenXrStrictExtentFailure(ex))
                throw;
            Debug.VulkanWarningEvery(
                $"OpenXR.Vulkan.PrewarmEyeFailed.{GetHashCode()}",
                TimeSpan.FromSeconds(1),
                "[OpenXR] Vulkan eye resource prewarm failed: {0}",
                ex.Message);
        }
        finally
        {
            _openXrExternalSwapchainPrewarmDepth--;
        }
    }

    internal void PrewarmOpenXrEyeMirrorFrameBufferResources(
        XRFrameBuffer targetFrameBuffer,
        Extent2D extent,
        int resourcePlannerStateIndex,
        Action emitFrameOps)
    {
        if (targetFrameBuffer is null || extent.Width == 0 || extent.Height == 0)
            return;

        if (ShouldDeferOpenXrVulkanResourceWork(out string resourceWorkReason))
        {
            Debug.VulkanWarningEvery(
                $"OpenXR.Vulkan.PrewarmEyeMirrorDeferred.{GetHashCode()}.{resourcePlannerStateIndex}",
                TimeSpan.FromSeconds(1),
                "[OpenXR] Deferring Vulkan eye mirror resource prewarm: {0}",
                resourceWorkReason);
            return;
        }

        uint prewarmViewIndex = ResolveOpenXrExternalSwapchainViewIndex(resourcePlannerStateIndex);
        using IDisposable externalScope = EnterOpenXrExternalSwapchainRenderScope(
            extent.Width,
            extent.Height,
            BuildOpenXrExternalSwapchainPlannerTargetIdentity(prewarmViewIndex),
            ResolveOpenXrExternalSwapchainTargetName(prewarmViewIndex));
        _openXrExternalSwapchainPrewarmDepth++;
        int openXrFrameDataSlotCount = ResolveOpenXrFrameDataSlotCount(swapChainImages?.Length ?? 0);

        try
        {
            EnsureOpenXrFrameDataSlotCapacity(openXrFrameDataSlotCount);
            EnsureDescriptorFrameSlotFrameCountFloor(openXrFrameDataSlotCount);
            DrainRetiredResourcesFromCompletedSubmittedFrameSlots();

            using (EnterOpenXrResourcePlannerThreadScope(
                resourcePlannerStateIndex,
                EOpenXrResourcePlannerPurpose.MirrorPrewarm))
            {
                if (ShouldDeferOpenXrVulkanResourceWork(out string scopedResourceWorkReason))
                {
                    Debug.VulkanWarningEvery(
                        $"OpenXR.Vulkan.PrewarmEyeMirrorScopedDeferred.{GetHashCode()}.{resourcePlannerStateIndex}",
                        TimeSpan.FromSeconds(1),
                        "[OpenXR] Deferring Vulkan eye mirror resource prewarm: {0}",
                        scopedResourceWorkReason);
                    return;
                }

                FrameOp[] ops = CaptureFrameOpsExcludingTextureUploads(emitFrameOps, out _);
                ops = FilterDiagnosticSkippedFrameOps(ops);
                if (ops.Length == 0)
                    return;
                ops = NormalizeOpenXrExternalSwapchainFrameOps(ops, extent);
                ValidateOpenXrExternalFrameOpContexts(
                    ops,
                    extent,
                    (uint)Math.Max(resourcePlannerStateIndex, 0),
                    "eye mirror prewarm");

                using (RuntimeRenderingHostServices.Current.StartProfileScope("OpenXR.Vulkan.PrewarmEyeMirror.Sort"))
                    ops = VulkanRenderGraphCompiler.SortFrameOps(ops, CompiledRenderGraph);
                if (TryDescribeRecentResourceAllocationFailure(out string prePlanFailureReason))
                {
                    Debug.VulkanWarningEvery(
                        $"OpenXR.Vulkan.PrewarmEyeMirrorPlanDeferred.{GetHashCode()}.{resourcePlannerStateIndex}",
                        TimeSpan.FromSeconds(1),
                        "[OpenXR] Deferring Vulkan eye mirror resource prewarm: {0}",
                        prePlanFailureReason);
                    return;
                }

                FrameOpContext plannerContext = PrepareResourcePlannerForFrameOps(ops);
                if (TryDescribeRecentResourceAllocationFailure(out string postPlanFailureReason))
                {
                    Debug.VulkanWarningEvery(
                        $"OpenXR.Vulkan.PrewarmEyeMirrorPlanFailed.{GetHashCode()}.{resourcePlannerStateIndex}",
                        TimeSpan.FromSeconds(1),
                        "[OpenXR] Deferring Vulkan eye mirror resource prewarm: {0}",
                        postPlanFailureReason);
                    return;
                }

                if (!TryRefreshFrameOpResourceWrappers(
                    ops,
                    plannerContext,
                    "OpenXR eye mirror resource prewarm refresh",
                    AllowSynchronousResourceUploads,
                    out string refreshFailureReason))
                {
                    Debug.VulkanWarningEvery(
                        $"OpenXR.Vulkan.PrewarmEyeMirrorRefreshDeferred.{GetHashCode()}.{resourcePlannerStateIndex}",
                        TimeSpan.FromSeconds(1),
                        "[OpenXR] Deferring Vulkan eye mirror resource prewarm: {0}",
                        refreshFailureReason);
                    return;
                }
                PrewarmOpenXrFrameOpResources(
                    ops,
                    ResolveOpenXrRecordImageIndex(resourcePlannerStateIndex, swapChainImages?.Length ?? 0));
            }
        }
        catch (Exception ex)
        {
            _ = DrainFrameOpsExcludingTextureUploads(out _);
            if (IsOpenXrStrictExtentFailure(ex))
                throw;
            Debug.VulkanWarningEvery(
                $"OpenXR.Vulkan.PrewarmEyeMirrorFailed.{GetHashCode()}",
                TimeSpan.FromSeconds(1),
                "[OpenXR] Vulkan eye mirror resource prewarm failed: {0}",
                ex.Message);
        }
        finally
        {
            _openXrExternalSwapchainPrewarmDepth--;
        }
    }

    private static bool IsOpenXrStrictExtentFailure(Exception ex)
        => ex is InvalidOperationException &&
           ex.Message.StartsWith("OpenXR ", StringComparison.Ordinal);

    private static FrameOp[] NormalizeOpenXrExternalSwapchainFrameOps(FrameOp[] ops, in Extent2D extent)
    {
        if (extent.Width == 0 || extent.Height == 0)
            return ops;

        FrameOp[]? normalized = null;
        for (int i = 0; i < ops.Length; i++)
        {
            if (ops[i] is not BlitOp { OutFbo: null } blitOp)
                continue;

            if (IsFullOpenXrBlitDestination(blitOp, extent))
                continue;

            normalized ??= (FrameOp[])ops.Clone();
            normalized[i] = blitOp with
            {
                OutX = 0,
                OutY = 0,
                OutW = extent.Width,
                OutH = extent.Height
            };
        }

        return normalized ?? ops;
    }

    private static FrameOp[] CloneFrameOpsForPreparedOpenXrEye(FrameOp[] ops)
    {
        // CaptureFrameOpsExcludingTextureUploads uses a thread-local scratch array keyed by
        // op count. Parallel eye recording prepares both eyes before either one records, so
        // the second eye can otherwise overwrite the first prepared input when their op
        // counts match.
        return ops.Length == 0 ? ops : (FrameOp[])ops.Clone();
    }

    private static void ValidateOpenXrExternalFrameOpContexts(
        FrameOp[] ops,
        in Extent2D extent,
        uint openXrViewIndex,
        string phase)
    {
        if (extent.Width == 0 || extent.Height == 0)
            throw new InvalidOperationException($"OpenXR {phase} eye {openXrViewIndex} requires a non-zero target extent.");

        for (int i = 0; i < ops.Length; i++)
        {
            FrameOp op = ops[i];
            ValidateOpenXrExternalSwapchainWriterDrawState(op, i, extent, openXrViewIndex, phase);

            FrameOpContext context = op.Context;
            if (!FrameOpContextHasPlannerResources(context))
                continue;

            if (context.DisplayWidth == extent.Width &&
                context.DisplayHeight == extent.Height &&
                context.InternalWidth == extent.Width &&
                context.InternalHeight == extent.Height)
            {
                continue;
            }

            throw new InvalidOperationException(
                $"OpenXR {phase} eye {openXrViewIndex} captured a frame op with non-eye resource dimensions. " +
                $"OpIndex={i}; Op={op.GetType().Name}; " +
                $"Expected={extent.Width}x{extent.Height}; " +
                $"ContextDisplay={context.DisplayWidth}x{context.DisplayHeight}; " +
                $"ContextInternal={context.InternalWidth}x{context.InternalHeight}; " +
                $"Pipeline={context.PipelineIdentity}; Viewport={context.ViewportIdentity}.");
        }
    }

    private static void ValidateOpenXrExternalSwapchainWriterDrawState(
        FrameOp op,
        int opIndex,
        in Extent2D extent,
        uint openXrViewIndex,
        string phase)
    {
        switch (op)
        {
            case MeshDrawOp { Target: null } drawOp:
                ValidateOpenXrExternalSwapchainWriterDrawState(
                    drawOp.Draw,
                    opIndex,
                    nameof(MeshDrawOp),
                    extent,
                    openXrViewIndex,
                    phase);
                break;
            case IndirectDrawOp { Target: null } indirectOp:
                ValidateOpenXrExternalSwapchainWriterDrawState(
                    indirectOp.Draw,
                    opIndex,
                    nameof(IndirectDrawOp),
                    extent,
                    openXrViewIndex,
                    phase);
                break;
            case BlitOp { OutFbo: null } blitOp:
                ValidateOpenXrExternalSwapchainWriterBlitState(
                    blitOp,
                    opIndex,
                    extent,
                    openXrViewIndex,
                    phase);
                break;
        }
    }

    private static void ValidateOpenXrExternalSwapchainWriterBlitState(
        BlitOp blitOp,
        int opIndex,
        in Extent2D extent,
        uint openXrViewIndex,
        string phase)
    {
        if (IsFullOpenXrBlitDestination(blitOp, extent))
            return;

        throw new InvalidOperationException(
            $"OpenXR {phase} eye {openXrViewIndex} captured a swapchain blit that does not cover the full eye target. " +
            $"OpIndex={opIndex}; Op={nameof(BlitOp)}; Expected={extent.Width}x{extent.Height}; " +
            $"Destination=({blitOp.OutX},{blitOp.OutY},{blitOp.OutW}x{blitOp.OutH}); " +
            $"ExpectedDestination=(0,0,{extent.Width}x{extent.Height}).");
    }

    private static bool IsFullOpenXrBlitDestination(BlitOp blitOp, in Extent2D extent)
        => blitOp.OutX == 0 &&
           blitOp.OutY == 0 &&
           blitOp.OutW == extent.Width &&
           blitOp.OutH == extent.Height;

    private static void ValidateOpenXrExternalSwapchainWriterDrawState(
        in PendingMeshDraw draw,
        int opIndex,
        string opName,
        in Extent2D extent,
        uint openXrViewIndex,
        string phase)
    {
        if (draw.ViewportScissorCount != 1)
        {
            throw new InvalidOperationException(
                $"OpenXR {phase} eye {openXrViewIndex} captured a swapchain writer with indexed viewport/scissor state. " +
                $"OpIndex={opIndex}; Op={opName}; ExpectedViewportScissorCount=1; ActualViewportScissorCount={draw.ViewportScissorCount}; " +
                $"Expected={extent.Width}x{extent.Height}.");
        }

        Viewport expectedViewport = CreateVulkanViewport(extent);
        Rect2D expectedScissor = new()
        {
            Offset = new Offset2D(0, 0),
            Extent = extent
        };

        if (SameOpenXrViewport(draw.Viewport, expectedViewport) &&
            SameOpenXrScissor(draw.Scissor, expectedScissor))
        {
            return;
        }

        throw new InvalidOperationException(
            $"OpenXR {phase} eye {openXrViewIndex} captured a swapchain writer that does not cover the full eye target. " +
            $"OpIndex={opIndex}; Op={opName}; Expected={extent.Width}x{extent.Height}; " +
            $"Viewport=({draw.Viewport.X},{draw.Viewport.Y},{draw.Viewport.Width}x{draw.Viewport.Height}); " +
            $"ExpectedViewport=({expectedViewport.X},{expectedViewport.Y},{expectedViewport.Width}x{expectedViewport.Height}); " +
            $"Scissor=({draw.Scissor.Offset.X},{draw.Scissor.Offset.Y},{draw.Scissor.Extent.Width}x{draw.Scissor.Extent.Height}); " +
            $"ExpectedScissor=({expectedScissor.Offset.X},{expectedScissor.Offset.Y},{expectedScissor.Extent.Width}x{expectedScissor.Extent.Height}).");
    }

    private static bool SameOpenXrViewport(Viewport actual, Viewport expected)
        => SameOpenXrFloat(actual.X, expected.X) &&
           SameOpenXrFloat(actual.Y, expected.Y) &&
           SameOpenXrFloat(actual.Width, expected.Width) &&
           SameOpenXrFloat(actual.Height, expected.Height) &&
           SameOpenXrFloat(actual.MinDepth, expected.MinDepth) &&
           SameOpenXrFloat(actual.MaxDepth, expected.MaxDepth);

    private static bool SameOpenXrFloat(float actual, float expected)
        => MathF.Abs(actual - expected) <= 0.001f;

    private static bool SameOpenXrScissor(Rect2D actual, Rect2D expected)
        => actual.Offset.X == expected.Offset.X &&
           actual.Offset.Y == expected.Offset.Y &&
           actual.Extent.Width == expected.Extent.Width &&
           actual.Extent.Height == expected.Extent.Height;

    private void RefreshFrameOpResourceWrappers(
        FrameOp[] ops,
        FrameOpContext plannerContext,
        string reason,
        bool allowSynchronousUpload)
        => _ = TryRefreshFrameOpResourceWrappers(ops, plannerContext, reason, allowSynchronousUpload, out _);

    private bool TryRefreshFrameOpResourceWrappers(
        FrameOp[] ops,
        FrameOpContext plannerContext,
        string reason,
        bool allowSynchronousUpload,
        out string failureReason)
    {
        failureReason = string.Empty;
        RebaseFrameOpResourcesToActiveResourcePlan(ops);
        HashSet<object> visitedRegistries = _commandBufferRecordingScratch.Value!.VisitedResourceRegistries;
        visitedRegistries.Clear();
        if (!TryRefreshResourceRegistryWrappers(plannerContext.ResourceRegistry, visitedRegistries, reason, allowSynchronousUpload, out failureReason))
            return false;

        foreach (FrameOp op in ops)
        {
            if (!TryRefreshResourceRegistryWrappers(op.Context.ResourceRegistry, visitedRegistries, reason, allowSynchronousUpload, out failureReason))
                return false;
        }

        return true;
    }

    private void RebaseFrameOpResourcesToActiveResourcePlan(FrameOp[] ops)
    {
        for (int opIndex = 0; opIndex < ops.Length; opIndex++)
        {
            FrameOp capturedOp = ops[opIndex];
            XRRenderPipelineInstance? pipeline = capturedOp.Context.PipelineInstance;
            if (pipeline is null)
                continue;

            RenderResourceRegistry activeRegistry = pipeline.Resources;
            FrameOpContext context = capturedOp.Context with
            {
                ResourceRegistry = activeRegistry,
                DisplayWidth = pipeline.ResourceDisplayWidth ?? capturedOp.Context.DisplayWidth,
                DisplayHeight = pipeline.ResourceDisplayHeight ?? capturedOp.Context.DisplayHeight,
                InternalWidth = pipeline.ResourceInternalWidth ?? capturedOp.Context.InternalWidth,
                InternalHeight = pipeline.ResourceInternalHeight ?? capturedOp.Context.InternalHeight,
                ResourceGeneration = unchecked((ulong)Math.Max(pipeline.ResourceGeneration, 0)),
                DescriptorGeneration = ResolveFrameOpContextDescriptorGeneration(activeRegistry),
                ResourceRegistrySignatureSnapshot = ComputeResourceRegistrySignature(activeRegistry),
            };
            context = RefreshFrameOpContextRecordingFingerprint(context);
            FrameOp op = RebaseFrameOpTargetsToActiveResourcePlan(capturedOp with { Context = context }, activeRegistry);
            ops[opIndex] = op;

            ComputeDispatchSnapshot? snapshot = op switch
            {
                MeshDrawOp meshDraw => meshDraw.Draw.ProgramBindingSnapshot,
                ComputeDispatchOp compute => compute.Snapshot,
                _ => null,
            };
            if (snapshot is null)
                continue;

            // Frame ops are emitted before the Vulkan resource planner publishes the
            // output-specific physical plan. A captured post-process binding can
            // therefore still reference the previous viewport's texture (desktop,
            // preview, or another eye). Rebase only named pipeline resources; material
            // textures remain immutable draw inputs.
            foreach (KeyValuePair<string, XRTexture> pair in snapshot.SamplersByName)
            {
                if (TryResolveActiveFrameSourceTexture(
                    pair.Key,
                    pair.Value,
                    activeRegistry,
                    pipeline,
                    out XRTexture currentTexture))
                {
                    snapshot.SamplersByName[pair.Key] = currentTexture;
                }
            }

            foreach (KeyValuePair<uint, string> pair in snapshot.SamplerNamesByUnit)
            {
                if (snapshot.Samplers.TryGetValue(pair.Key, out XRTexture? capturedTexture) &&
                    TryResolveActiveFrameSourceTexture(
                        pair.Value,
                        capturedTexture,
                        activeRegistry,
                        pipeline,
                        out XRTexture currentTexture))
                {
                    snapshot.Samplers[pair.Key] = currentTexture;
                }
            }
        }
    }

    private static FrameOp RebaseFrameOpTargetsToActiveResourcePlan(
        FrameOp op,
        RenderResourceRegistry activeRegistry)
    {
        XRFrameBuffer? target = ResolveActiveFrameBuffer(op.Target, activeRegistry);
        return op switch
        {
            ClearOp clear => clear with { Target = target },
            MeshDrawOp meshDraw => meshDraw with { Target = target },
            QueryOp query => query with { Target = target },
            IndirectDrawOp indirect => indirect with { Target = target },
            TransformFeedbackOp transformFeedback => transformFeedback with { Target = target },
            BlitOp blit => RebaseBlitTargets(blit, activeRegistry),
            PublishFramebufferForSamplingOp publish => RebasePublishedFramebuffer(publish, activeRegistry),
            _ => op,
        };
    }

    private static BlitOp RebaseBlitTargets(BlitOp blit, RenderResourceRegistry activeRegistry)
    {
        XRFrameBuffer? inFbo = ResolveActiveFrameBuffer(blit.InFbo, activeRegistry);
        XRFrameBuffer? outFbo = ResolveActiveFrameBuffer(blit.OutFbo, activeRegistry);
        return blit with
        {
            InFbo = inFbo,
            OutFbo = outFbo,
            Target = outFbo,
        };
    }

    private static PublishFramebufferForSamplingOp RebasePublishedFramebuffer(
        PublishFramebufferForSamplingOp publish,
        RenderResourceRegistry activeRegistry)
    {
        XRFrameBuffer frameBuffer = ResolveActiveFrameBuffer(publish.FrameBuffer, activeRegistry) ?? publish.FrameBuffer;
        return publish with
        {
            FrameBuffer = frameBuffer,
            Target = frameBuffer,
        };
    }

    private static XRFrameBuffer? ResolveActiveFrameBuffer(
        XRFrameBuffer? captured,
        RenderResourceRegistry activeRegistry)
    {
        if (captured is null || string.IsNullOrWhiteSpace(captured.Name))
            return captured;

        return activeRegistry.TryGetFrameBuffer(captured.Name, out XRFrameBuffer? active)
            ? active
            : captured;
    }

    private static bool TryResolveActiveFrameSourceTexture(
        string bindingName,
        XRTexture capturedTexture,
        RenderResourceRegistry? activeRegistry,
        XRRenderPipelineInstance pipeline,
        out XRTexture currentTexture)
    {
        // Generic post-process bindings such as SourceTexture identify the logical
        // pipeline resource through the captured texture's name. Named bindings use
        // their binding name directly.
        string? resourceName = IsFrameSourceSamplerName(bindingName)
            ? capturedTexture.Name
            : bindingName;
        if (!string.IsNullOrWhiteSpace(resourceName) &&
            activeRegistry?.TryGetTexture(resourceName, out XRTexture? registryTexture) == true &&
            registryTexture is not null)
        {
            currentTexture = registryTexture;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(resourceName) &&
            pipeline.TryGetTexture(resourceName, out XRTexture? pipelineTexture) &&
            pipelineTexture is not null)
        {
            currentTexture = pipelineTexture;
            return true;
        }

        currentTexture = null!;
        return false;
    }

    private bool TryRefreshResourceRegistryWrappers(
        RenderResourceRegistry? registry,
        HashSet<object> visitedRegistries,
        string reason,
        bool allowSynchronousUpload,
        out string failureReason)
    {
        failureReason = string.Empty;
        if (registry is null)
            return true;

        if (!visitedRegistries.Add(registry))
            return true;

        ResourceRegistryWrapperRefreshStamp refreshStamp = new(
            registry.InstanceRevision,
            registry.DescriptorRevision,
            ResourcePlannerRevision,
            ResourceAllocatorIdentity);
        if (_resourceRegistryWrapperRefreshStamps.TryGetValue(registry, out ResourceRegistryWrapperRefreshStamp previousStamp) &&
            previousStamp == refreshStamp)
        {
            return true;
        }

        XRTexture[] textures = registry.GetTextureInstanceSnapshot();
        for (int textureIndex = 0; textureIndex < textures.Length; textureIndex++)
        {
            XRTexture texture = textures[textureIndex];

            // The physical render graph allocator currently materializes graph textures as 2D/layered images.
            // Do not force-generate dormant 3D texture wrappers during frame-op resource refresh.
            if (texture is XRTexture3D)
                continue;

            // A registry retains logical resources whose predicates are disabled so a later pipeline
            // generation can activate them without rebuilding the declaration set. Those dormant render
            // targets deliberately have no entry in the active Vulkan allocation plan. Trying to refresh
            // their old wrappers makes an unrelated optional target (for example the overdraw debug target)
            // defer every frame after a DLSS/DLSS-G resource-generation change.
            if ((texture.FrameBufferAttachment.HasValue || texture.RequiresStorageUsage) &&
                (string.IsNullOrWhiteSpace(texture.Name) ||
                 !ResourceAllocator.TryGetPhysicalGroupForResource(texture.Name, out VulkanPhysicalImageGroup? physicalGroup) ||
                 physicalGroup?.IsAllocated != true))
            {
                continue;
            }

            if (GetOrCreateAPIRenderObject(texture, generateNow: true) is IVkImageDescriptorSource imageSource &&
                !imageSource.TryEnsureDescriptorReadyForUse(reason, allowSynchronousUpload))
            {
                // Registry refresh is a prewarm over every declared texture, including optional resources
                // that no op in this command chain consumes. The draw/dispatch descriptor paths validate
                // their actual bindings before recording, so leave an unready unrelated wrapper for a
                // later generation instead of rejecting the entire desktop frame.
                continue;
            }
        }
        XRRenderBuffer[] renderBuffers = registry.GetRenderBufferInstanceSnapshot();
        for (int renderBufferIndex = 0; renderBufferIndex < renderBuffers.Length; renderBufferIndex++)
        {
            if (GetOrCreateAPIRenderObject(renderBuffers[renderBufferIndex], generateNow: true) is VkRenderBuffer vkRenderBuffer)
                vkRenderBuffer.RefreshIfStale();
        }

        _resourceRegistryWrapperRefreshStamps[registry] = refreshStamp;
        return true;
    }

    private bool PrewarmOpenXrFrameOpResources(
        FrameOp[] ops,
        uint frameDataImageIndex,
        bool sealFrameManifest = false)
    {
        if (ops.Length == 0)
            return true;

        CommandBufferRecordingScratch recordingScratch = _commandBufferRecordingScratch.Value!;
        Dictionary<VkMeshRenderer, int> meshDrawSlotsByRenderer = recordingScratch.MeshDrawSlotsByRenderer;
        meshDrawSlotsByRenderer.EnsureCapacity(recordingScratch.OpenXrMeshDrawSlotCapacityHint);

        // Capacity must be final before the first descriptor/uniform prewarm. Growing a renderer's
        // draw-slot capacity destroys its old descriptors and uniform buffers; doing that midway
        // through this loop can retire resources captured by an earlier draw in the same command
        // buffer. Use the same count-then-reserve contract as normal Vulkan recording.
        if (!TryRegisterFrameWideMeshFrameDataRequirements(
                ops,
                Array.Empty<FrameOp>(),
                unchecked((int)Math.Min(frameDataImageIndex, int.MaxValue)),
                sealFrameManifest,
                meshDrawSlotsByRenderer,
                recordingScratch,
                recordingScratch.OpenXrMeshFrameDataFamilyBases,
                out _,
                out string frameWideReason))
        {
            Debug.VulkanWarningEvery(
                $"OpenXR.Vulkan.FrameWideMeshFrameDataDeferred.{GetHashCode()}.{frameDataImageIndex}",
                TimeSpan.FromSeconds(1),
                "[OpenXR] Deferring Vulkan frame-data preparation: {0}",
                frameWideReason);
            return false;
        }
        int rendererCount = meshDrawSlotsByRenderer.Count;
        int descriptorFrameIndex = frameDataImageIndex > int.MaxValue
            ? int.MaxValue
            : (int)frameDataImageIndex;
        Dictionary<VulkanMeshFrameDataRendererFamilyKey, int> meshDrawSlotsByRendererFamily =
            recordingScratch.OpenXrMeshDrawSlotsByRendererFamily;
        Dictionary<VulkanMeshFrameDataRendererFamilyKey, int> meshFrameDataFamilyBases =
            recordingScratch.OpenXrMeshFrameDataFamilyBases;
        meshDrawSlotsByRendererFamily.Clear();
        bool allDrawsReady = true;

        for (int i = 0; i < ops.Length; i++)
        {
            switch (ops[i])
            {
                case MeshDrawOp drawOp:
                    PrewarmDraw(drawOp.Draw.Renderer, drawOp.Draw, drawOp.Context);
                    break;
                case IndirectDrawOp indirectDrawOp:
                    PrewarmDraw(indirectDrawOp.MeshRenderer, indirectDrawOp.Draw, indirectDrawOp.Context);
                    break;
            }
        }

        recordingScratch.OpenXrMeshDrawSlotCapacityHint = Math.Max(1, rendererCount);
        return allDrawsReady;

        void PrewarmDraw(VkMeshRenderer renderer, in PendingMeshDraw draw, in FrameOpContext context)
        {
            int drawUniformSlot = GetFrameWideMeshDrawUniformSlot(
                meshDrawSlotsByRendererFamily,
                meshFrameDataFamilyBases,
                renderer,
                descriptorFrameIndex,
                EVulkanMeshFrameDataStreamKind.Primary,
                context,
                draw);
            using var plannerScope =
                EnterFrameOpResourcePlannerReadbackScope(context);
            if (renderer.TryPrewarmFrameDataForRecording(
                    draw,
                    drawUniformSlot,
                    descriptorFrameIndex,
                    out string reason))
                return;

            Debug.VulkanWarningEvery(
                $"OpenXR.Vulkan.PrewarmDrawResourcesFailed.{GetHashCode()}.{renderer.GetHashCode()}.{drawUniformSlot}",
                TimeSpan.FromSeconds(1),
                "[OpenXR] Vulkan eye prewarm could not prepare draw resources for mesh='{0}' material='{1}' slot={2}: {3}",
                renderer.MeshRenderer.Mesh?.Name ?? "<unnamed mesh>",
                (draw.MaterialOverride ?? renderer.MeshRenderer.Material)?.Name ?? "<unnamed material>",
                drawUniformSlot,
                reason);
            allDrawsReady = false;
        }
    }

    private OpenXrResourcePlannerThreadScope EnterOpenXrResourcePlannerThreadScope(
        int stateIndex,
        EOpenXrResourcePlannerPurpose purpose)
        => new(this, CreateLegacyOpenXrResourcePlannerContextKey(stateIndex, purpose));

    private OpenXrResourcePlannerThreadScope EnterOpenXrResourcePlannerThreadScope(in OpenXrViewResourcePlannerContextKey contextKey)
        => new(this, contextKey);

    private static int NormalizeOpenXrResourcePlannerStateIndex(int stateIndex)
        => (uint)stateIndex < OpenXrEyeResourcePlannerStateCount ? stateIndex : 0;

    private static OpenXrViewResourcePlannerContextKey CreateLegacyOpenXrResourcePlannerContextKey(
        int stateIndex,
        EOpenXrResourcePlannerPurpose purpose)
    {
        int normalizedStateIndex = NormalizeOpenXrResourcePlannerStateIndex(stateIndex);
        uint legacyIndex = unchecked((uint)normalizedStateIndex);
        return new OpenXrViewResourcePlannerContextKey(
            purpose,
            normalizedStateIndex,
            legacyIndex,
            OpenXrExternalSwapchainTargetImageIndex,
            legacyIndex,
            legacyIndex,
            FoveationResourceKey: 0UL,
            FoveationAttachmentKind: EVrFoveationAttachmentKind.None,
            FoveationAttachmentOwnedByResourcePlanner: false);
    }

    private static string DescribeOpenXrResourcePlannerContextKey(in OpenXrViewResourcePlannerContextKey key)
        => $"purpose={key.Purpose} planner={key.ResourcePlannerStateIndex} eye={key.OpenXrViewIndex} imageIndex={key.OpenXrImageIndex} " +
           $"commandKey={key.CommandChainImageKey} frameSlot={key.FrameDataSlotIndex} foveationKey=0x{key.FoveationResourceKey:X} " +
           $"foveationAttachment={key.FoveationAttachmentKind} foveationOwned={key.FoveationAttachmentOwnedByResourcePlanner}";

    internal bool TryClearOpenXrSwapchainImage(Image image, Extent2D extent, ColorF4 color)
    {
        if (image.Handle == 0 || extent.Width == 0 || extent.Height == 0)
            return false;

        try
        {
            using CommandScope scope = NewCommandScope();
            CommandBuffer commandBuffer = scope.CommandBuffer;

            ImageSubresourceRange range = new()
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1
            };

            ImageMemoryBarrier toTransfer = new()
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = 0,
                DstAccessMask = AccessFlags.TransferWriteBit,
                OldLayout = ImageLayout.Undefined,
                NewLayout = ImageLayout.TransferDstOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = image,
                SubresourceRange = range
            };

            CmdPipelineBarrierTracked(
                commandBuffer,
                PipelineStageFlags.TopOfPipeBit,
                PipelineStageFlags.TransferBit,
                0,
                0,
                null,
                0,
                null,
                1,
                &toTransfer);

            ClearColorValue clearColor = new(color.R, color.G, color.B, color.A);
            CmdClearColorImageTracked(commandBuffer, image, ImageLayout.TransferDstOptimal, ref clearColor, 1, ref range);

            ImageMemoryBarrier toColorAttachment = new()
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = AccessFlags.TransferWriteBit,
                DstAccessMask = AccessFlags.ColorAttachmentReadBit,
                OldLayout = ImageLayout.TransferDstOptimal,
                NewLayout = ImageLayout.ColorAttachmentOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = image,
                SubresourceRange = range
            };

            CmdPipelineBarrierTracked(
                commandBuffer,
                PipelineStageFlags.TransferBit,
                PipelineStageFlags.ColorAttachmentOutputBit,
                0,
                0,
                null,
                0,
                null,
                1,
                &toColorAttachment);

            return true;
        }
        catch (Exception ex)
        {
            Debug.VulkanWarningEvery(
                $"OpenXR.Vulkan.ClearFailed.{GetHashCode()}",
                TimeSpan.FromSeconds(1),
                "[OpenXR] Vulkan swapchain diagnostic clear failed: {0}",
                ex.Message);
            return false;
        }
    }

    private static Extent2D ResolveOpenXrMirrorDestinationExtent(
        XRTexture2D destinationTexture,
        IVkImageDescriptorSource destinationSource)
    {
        return destinationSource is IVkFrameBufferAttachmentSource attachmentSource &&
            attachmentSource.TryGetAttachmentExtent(0, 0, out Extent2D attachmentExtent) &&
            attachmentExtent.Width > 0 &&
            attachmentExtent.Height > 0
                ? attachmentExtent
                : new Extent2D(
                Math.Max(destinationTexture.Width, 1u),
                Math.Max(destinationTexture.Height, 1u));
    }

    private static Extent2D ResolveOpenXrMirrorSourceExtent(
        XRTexture sourceTexture,
        IVkImageDescriptorSource source)
    {
        if (source is IVkFrameBufferAttachmentSource attachmentSource &&
            attachmentSource.TryGetAttachmentExtent(0, 0, out Extent2D attachmentExtent) &&
            attachmentExtent.Width > 0 &&
            attachmentExtent.Height > 0)
        {
            return attachmentExtent;
        }

        return sourceTexture switch
        {
            XRTexture2D texture2D => new Extent2D(
                Math.Max(texture2D.Width, 1u),
                Math.Max(texture2D.Height, 1u)),
            XRTexture2DArray textureArray => new Extent2D(
                Math.Max(textureArray.Width, 1u),
                Math.Max(textureArray.Height, 1u)),
            XRTexture2DArrayView textureArrayView => new Extent2D(
                Math.Max(textureArrayView.Width, 1u),
                Math.Max(textureArrayView.Height, 1u)),
            _ => new Extent2D(1u, 1u)
        };
    }

    private static Extent2D ResolveOpenXrMirrorDestinationExtent(
        XRTexture2DArray destinationTexture,
        IVkImageDescriptorSource destinationSource,
        uint layer)
    {
        return destinationSource is IVkFrameBufferAttachmentSource attachmentSource &&
            attachmentSource.TryGetAttachmentExtent(0, checked((int)layer), out Extent2D attachmentExtent) &&
            attachmentExtent.Width > 0 &&
            attachmentExtent.Height > 0
                ? attachmentExtent
                : new Extent2D(
                    Math.Max(destinationTexture.Width, 1u),
                    Math.Max(destinationTexture.Height, 1u));
    }

    private ImageLayout ResolveOpenXrAttachmentLayout(
        IVkImageDescriptorSource source,
        uint layer)
    {
        ImageSubresourceRange range = new()
        {
            AspectMask = NormalizeOpenXrMirrorAspect(source.DescriptorFormat, source.DescriptorAspect),
            BaseMipLevel = 0,
            LevelCount = 1,
            BaseArrayLayer = layer,
            LayerCount = 1,
        };
        if (source.DescriptorImage.Handle != 0 &&
            TryGetTrackedImageLayout(source.DescriptorImage, range, out ImageLayout liveLayout) &&
            liveLayout != ImageLayout.Undefined)
        {
            return liveLayout;
        }

        ImageLayout layout = ImageLayout.Undefined;
        if (source is IVkFrameBufferAttachmentSource attachmentSource)
            layout = attachmentSource.GetAttachmentTrackedLayout(0, checked((int)layer));

        if (layout == ImageLayout.Undefined)
            layout = source.TrackedImageLayout;

        return layout;
    }

    private static ImageLayout ResolveOpenXrMirrorDestinationLayout(IVkImageDescriptorSource destinationSource)
    {
        ImageLayout layout = ImageLayout.Undefined;
        if (destinationSource is IVkFrameBufferAttachmentSource attachmentSource)
            layout = attachmentSource.GetAttachmentTrackedLayout(0, 0);

        if (layout == ImageLayout.Undefined)
            layout = destinationSource.TrackedImageLayout;

        return layout;
    }

    private ImageLayout ResolveOpenXrSwapchainImageTrackedLayout(Image image)
    {
        if (image.Handle == 0)
            return ImageLayout.ColorAttachmentOptimal;

        ImageSubresourceRange colorRange = new()
        {
            AspectMask = ImageAspectFlags.ColorBit,
            BaseMipLevel = 0,
            LevelCount = 1,
            BaseArrayLayer = 0,
            LayerCount = 1
        };

        return TryGetTrackedImageLayout(image, colorRange, out ImageLayout trackedLayout) &&
            trackedLayout != ImageLayout.Undefined
                ? trackedLayout
                : ImageLayout.ColorAttachmentOptimal;
    }

    private static ImageAspectFlags NormalizeOpenXrMirrorAspect(Format format, ImageAspectFlags aspect)
    {
        if (!IsDepthStencilFormat(format))
            return ImageAspectFlags.ColorBit;

        ImageAspectFlags normalized = aspect & (ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit);
        return normalized == ImageAspectFlags.None ? ImageAspectFlags.DepthBit : normalized;
    }

    private void TransitionOpenXrMirrorImage(
        CommandBuffer commandBuffer,
        Image image,
        Format format,
        ImageLayout oldLayout,
        ImageLayout newLayout,
        ImageAspectFlags aspectMask)
        => TransitionOpenXrMirrorImage(
            commandBuffer,
            image,
            format,
            oldLayout,
            newLayout,
            aspectMask,
            baseArrayLayer: 0u,
            layerCount: 1u);

    private void TransitionOpenXrMirrorImage(
        CommandBuffer commandBuffer,
        Image image,
        Format format,
        ImageLayout oldLayout,
        ImageLayout newLayout,
        ImageAspectFlags aspectMask,
        uint baseArrayLayer,
        uint layerCount)
    {
        if (oldLayout == newLayout)
            return;

        OpenXrMirrorBarrierAccess(oldLayout, out AccessFlags srcAccess, out PipelineStageFlags srcStage);
        OpenXrMirrorBarrierAccess(newLayout, out AccessFlags dstAccess, out PipelineStageFlags dstStage);

        ImageMemoryBarrier barrier = new()
        {
            SType = StructureType.ImageMemoryBarrier,
            SrcAccessMask = srcAccess,
            DstAccessMask = dstAccess,
            OldLayout = oldLayout,
            NewLayout = newLayout,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = NormalizeOpenXrMirrorAspect(format, aspectMask),
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = baseArrayLayer,
                LayerCount = Math.Max(layerCount, 1u)
            }
        };

        CmdPipelineBarrierTracked(
            commandBuffer,
            srcStage,
            dstStage,
            DependencyFlags.None,
            0,
            null,
            0,
            null,
            1,
            &barrier);
    }

    private static void OpenXrMirrorBarrierAccess(
        ImageLayout layout,
        out AccessFlags access,
        out PipelineStageFlags stage)
    {
        switch (layout)
        {
            case ImageLayout.Undefined:
                access = 0;
                stage = PipelineStageFlags.TopOfPipeBit;
                break;
            case ImageLayout.ColorAttachmentOptimal:
                access = AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit;
                stage = PipelineStageFlags.ColorAttachmentOutputBit;
                break;
            case ImageLayout.TransferSrcOptimal:
                access = AccessFlags.TransferReadBit;
                stage = PipelineStageFlags.TransferBit;
                break;
            case ImageLayout.TransferDstOptimal:
                access = AccessFlags.TransferWriteBit;
                stage = PipelineStageFlags.TransferBit;
                break;
            case ImageLayout.ShaderReadOnlyOptimal:
                access = AccessFlags.ShaderReadBit;
                stage = PipelineStageFlags.FragmentShaderBit;
                break;
            case ImageLayout.General:
                access = AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit;
                stage = PipelineStageFlags.AllCommandsBit;
                break;
            default:
                access = AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit;
                stage = PipelineStageFlags.AllCommandsBit;
                break;
        }
    }

    private void DrainRetiredResourcesFromCompletedSubmittedFrameSlots()
    {
        if (_frameSlotTimelineValues is null)
        {
            DrainCompletedRecordedTextureUploadPublications();
            return;
        }

        int frameSlotCount = Math.Min(_frameSlotTimelineValues.Length, MAX_FRAMES_IN_FLIGHT);
        bool desktopFrameActive = Volatile.Read(ref _windowRenderCallbackInProgress) != 0;
        int activeDesktopFrameSlot = desktopFrameActive ? currentFrame : -1;
        Span<bool> drainableSlots = stackalloc bool[MAX_FRAMES_IN_FLIGHT];
        for (int i = 0; i < frameSlotCount; i++)
        {
            if (desktopFrameActive && i == activeDesktopFrameSlot)
            {
                Debug.VulkanEvery(
                    $"OpenXR.Vulkan.ActiveDesktopFrameSlotDrainSkipped.{GetHashCode()}.{i}",
                    TimeSpan.FromSeconds(1),
                    "[OpenXR] Vulkan skipped retired-resource drain for active desktop frame slot {0} while desktop frame {1} is recording.",
                    i,
                    _vkDebugFrameCounter);
                continue;
            }

            ulong value = _frameSlotTimelineValues[i];
            if (value != 0 && !HasTimelineValueCompleted(_graphicsTimelineSemaphore, value))
            {
                Debug.VulkanEvery(
                    $"OpenXR.Vulkan.PendingFrameSlotDrainSkipped.{GetHashCode()}.{i}",
                    TimeSpan.FromSeconds(1),
                    "[OpenXR] Vulkan skipped retired-resource drain before eye rendering because frame slot {0} is still pending at timeline value {1}.",
                    i,
                    value);
                continue;
            }

            drainableSlots[i] = true;
        }

        for (int i = 0; i < frameSlotCount; i++)
            if (drainableSlots[i]) DrainRetiredCommandBuffers(i, int.MaxValue);
        for (int i = 0; i < frameSlotCount; i++)
            if (drainableSlots[i]) DrainRetiredDescriptorSets(i, int.MaxValue);
        for (int i = 0; i < frameSlotCount; i++)
            if (drainableSlots[i]) DrainRetiredDescriptorPools(i, int.MaxValue);
        for (int i = 0; i < frameSlotCount; i++)
            if (drainableSlots[i]) DrainRetiredPipelines(i, int.MaxValue);
        for (int i = 0; i < frameSlotCount; i++)
            if (drainableSlots[i]) DrainRetiredPipelineLayouts(i, int.MaxValue);
        for (int i = 0; i < frameSlotCount; i++)
            if (drainableSlots[i]) DrainRetiredQueryPools(i, int.MaxValue);
        for (int i = 0; i < frameSlotCount; i++)
            if (drainableSlots[i]) DrainRetiredBufferViews(i, int.MaxValue);
        for (int i = 0; i < frameSlotCount; i++)
            if (drainableSlots[i]) DrainRetiredFramebuffers(i, int.MaxValue);
        for (int i = 0; i < frameSlotCount; i++)
            if (drainableSlots[i]) DrainRetiredBuffers(i, int.MaxValue);
        for (int pass = 0; pass < frameSlotCount; pass++)
            for (int i = 0; i < frameSlotCount; i++)
                if (drainableSlots[i]) DrainRetiredImages(i, int.MaxValue);

        DrainCompletedRecordedTextureUploadPublications();
    }

    private void WaitForOpenXrFrameDataSlot(uint frameDataImageIndex, string reason)
    {
        if (_frameSlotTimelineValues is null ||
            _graphicsTimelineSemaphore.Handle == 0 ||
            frameDataImageIndex >= _frameSlotTimelineValues.Length)
            return;

        ulong value = _frameSlotTimelineValues[frameDataImageIndex];
        if (value == 0 || HasTimelineValueCompleted(_graphicsTimelineSemaphore, value))
            return;

        Debug.VulkanEvery(
            $"OpenXR.Vulkan.WaitFrameDataSlot.{GetHashCode()}.{frameDataImageIndex}.{reason}",
            TimeSpan.FromSeconds(1),
            "[OpenXR] Vulkan waiting for frame-data slot {0} before {1}; pending timeline value {2}.",
            frameDataImageIndex,
            reason,
            value);

        WaitForTimelineValue(_graphicsTimelineSemaphore, value);
    }

    private ImageView GetOrCreateOpenXrSwapchainImageView(Image image, Format format)
    {
        ulong key = image.Handle;
        if (_openXrSwapchainImageViews.TryGetValue(key, out OpenXrSwapchainImageViewCacheEntry cached))
        {
            if (cached.Format == format && cached.View.Handle != 0)
                return cached.View;

            if (cached.View.Handle != 0 && TryBeginDestroyImageView(cached.View, "OpenXR.SwapchainImageViewFormatChanged"))
                Api!.DestroyImageView(device, cached.View, null);
            _openXrSwapchainImageViews.Remove(key);
        }

        ImageView imageView = CreateOpenXrSwapchainImageView(image, format);
        _openXrSwapchainImageViews[key] = new OpenXrSwapchainImageViewCacheEntry(imageView, format);
        return imageView;
    }

    private ImageView CreateOpenXrSwapchainImageView(Image image, Format format)
    {
        ImageViewCreateInfo viewInfo = new()
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = image,
            ViewType = ImageViewType.Type2D,
            Format = format,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1,
            }
        };

        if (Api!.CreateImageView(device, ref viewInfo, null, out ImageView imageView) != Result.Success)
            throw new InvalidOperationException("Failed to create OpenXR Vulkan swapchain image view.");

        TrackLiveImageView(imageView, in viewInfo, "OpenXR.SwapchainImageView");
        SetDebugObjectName(ObjectType.ImageView, imageView.Handle, $"OpenXR.SwapchainImageView.0x{image.Handle:X}.{format}");
        return imageView;
    }

    private OpenXrDepthTarget GetOrCreateOpenXrDepthTarget(uint openXrViewIndex, Extent2D extent)
    {
        int targetIndex = ResolveOpenXrEyeUploadPublicationBufferIndex(openXrViewIndex);
        ref OpenXrDepthTarget cachedTarget = ref _openXrCachedDepthTargets[targetIndex];
        ref Extent2D cachedExtent = ref _openXrCachedDepthExtents[targetIndex];
        if (cachedTarget.Image.Handle != 0 &&
            cachedExtent.Width == extent.Width &&
            cachedExtent.Height == extent.Height)
            return cachedTarget;

        DestroyOpenXrDepthTarget(cachedTarget);
        cachedTarget = CreateOpenXrDepthTarget(extent);
        cachedExtent = extent;
        return cachedTarget;
    }

    private OpenXrDepthTarget CreateOpenXrDepthTarget(Extent2D extent)
    {
        Format depthFormat = FindDepthFormat();
        ImageAspectFlags depthAspect = IsDepthStencilFormat(depthFormat)
            ? ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit
            : ImageAspectFlags.DepthBit;

        ImageCreateInfo imageInfo = new()
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Extent = new Extent3D(extent.Width, extent.Height, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Format = depthFormat,
            Tiling = ImageTiling.Optimal,
            InitialLayout = ImageLayout.Undefined,
            Usage = ImageUsageFlags.DepthStencilAttachmentBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit,
            Samples = SampleCountFlags.Count1Bit,
            SharingMode = SharingMode.Exclusive,
        };

        Image depthImage = default;
        ImageView depthView = default;
        VulkanMemoryAllocation allocation = VulkanMemoryAllocation.Null;
        try
        {
            if (CreateVulkanImageTracked(ref imageInfo, out depthImage, "OpenXR.DepthTarget") != Result.Success)
                throw new InvalidOperationException("Failed to create OpenXR Vulkan depth image.");

            ClearTrackedImageLayouts(depthImage);
            allocation = AllocateImageMemoryWithFallback(depthImage, MemoryPropertyFlags.DeviceLocalBit);
            _imageAllocations[depthImage.Handle] = allocation;

            if (Api!.BindImageMemory(device, depthImage, allocation.Memory, allocation.Offset) != Result.Success)
                throw new InvalidOperationException("Failed to bind OpenXR Vulkan depth image memory.");

            ImageViewCreateInfo viewInfo = new()
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = depthImage,
                ViewType = ImageViewType.Type2D,
                Format = depthFormat,
                SubresourceRange = new ImageSubresourceRange
                {
                    AspectMask = depthAspect,
                    BaseMipLevel = 0,
                    LevelCount = 1,
                    BaseArrayLayer = 0,
                    LayerCount = 1,
                }
            };

            if (Api!.CreateImageView(device, ref viewInfo, null, out depthView) != Result.Success)
                throw new InvalidOperationException("Failed to create OpenXR Vulkan depth image view.");

            TrackLiveImageView(depthView, in viewInfo, "OpenXR.DepthTarget");
            return new OpenXrDepthTarget(depthImage, allocation.Memory, depthView, depthFormat, depthAspect);
        }
        catch
        {
            if (depthView.Handle != 0 && TryBeginDestroyImageView(depthView, "CreateOpenXrDepthTargetFailed"))
                Api!.DestroyImageView(device, depthView, null);

            if (depthImage.Handle != 0)
            {
                bool hasTrackedAllocation = _imageAllocations.TryRemove(
                    depthImage.Handle,
                    out VulkanMemoryAllocation trackedAllocation);
                DestroyVulkanImageImmediateTracked(depthImage, "CreateOpenXrDepthTargetFailed");
                FreeMemoryAllocation(hasTrackedAllocation ? trackedAllocation : allocation);
            }

            throw;
        }
    }

    private void DestroyOpenXrDepthTarget(OpenXrDepthTarget target)
    {
        RetireImageResources(new RetiredImageResources(
            target.Image,
            target.Memory,
            target.View,
            [],
            default,
            0));
    }

    private void DestroyOpenXrRenderingResources()
    {
        DestroyOpenXrEyeRecordWorkers();
        DestroyOpenXrPrimaryCommandBufferCache();
        DestroyOpenXrResourcePlannerState();

        foreach (OpenXrSwapchainImageViewCacheEntry entry in _openXrSwapchainImageViews.Values)
            if (entry.View.Handle != 0 && TryBeginDestroyImageView(entry.View, "DestroyOpenXrSwapchainImageViewCache"))
                Api!.DestroyImageView(device, entry.View, null);
        
        _openXrSwapchainImageViews.Clear();

        for (int i = 0; i < _openXrCachedDepthTargets.Length; i++)
        {
            DestroyOpenXrDepthTarget(_openXrCachedDepthTargets[i]);
            _openXrCachedDepthTargets[i] = default;
            _openXrCachedDepthExtents[i] = default;
        }

    }

    internal void ResetOpenXrRenderingResourcesForRuntimeRecreate(string reason)
    {
        if (_deviceLost || Api is null || device.Handle == 0)
            return;

        Debug.VulkanWarning(
            "[OpenXR] Resetting Vulkan OpenXR render resources before runtime recreate. Reason={0}",
            string.IsNullOrWhiteSpace(reason) ? "<unspecified>" : reason);

        try
        {
            DeviceWaitIdle();
        }
        catch (Exception ex)
        {
            Debug.VulkanWarning(
                "[OpenXR] Device idle wait failed while resetting Vulkan OpenXR resources before runtime recreate. Error={0}",
                ex.Message);
        }

        if (_deviceLost)
            return;

        DestroyOpenXrRenderingResources();
        MarkCommandBuffersDirty(nameof(ResetOpenXrRenderingResourcesForRuntimeRecreate));
    }

    internal void ExecuteOpenXrRuntimeGraphicsTransition(string reason, Action transition)
    {
        ArgumentNullException.ThrowIfNull(transition);

        if (_deviceLost || Api is null || device.Handle == 0)
            throw new InvalidOperationException("Cannot initialize OpenXR Vulkan session resources after the Vulkan device was lost.");

        long lockWaitStart = Stopwatch.GetTimestamp();
        bool queueLockTaken = false;
        try
        {
            Monitor.Enter(_oneTimeSubmitLock, ref queueLockTaken);
            LogOpenXrSerializedCriticalSectionWait("RuntimeGraphicsTransition", lockWaitStart, Stopwatch.GetTimestamp());

            using (RuntimeRenderingHostServices.Current.StartProfileScope("OpenXR.Vulkan.RuntimeGraphicsTransition"))
            {
                Debug.Vulkan(
                    "[OpenXR] Beginning Vulkan runtime graphics transition. Reason={0}",
                    string.IsNullOrWhiteSpace(reason) ? "<unspecified>" : reason);

                WaitForAllInFlightWork();
                if (!_deviceLost)
                    DeviceWaitIdle();
                if (_deviceLost)
                    throw new InvalidOperationException("Vulkan device lost while waiting for idle before OpenXR session initialization.");

                transition();

                if (!_deviceLost)
                    DeviceWaitIdle();
                if (_deviceLost)
                    throw new InvalidOperationException("Vulkan device lost while waiting for idle after OpenXR session initialization.");

                Debug.Vulkan(
                    "[OpenXR] Completed Vulkan runtime graphics transition. Reason={0}",
                    string.IsNullOrWhiteSpace(reason) ? "<unspecified>" : reason);
            }
        }
        finally
        {
            if (queueLockTaken)
                Monitor.Exit(_oneTimeSubmitLock);
        }
    }

    internal bool ShouldDeferOpenXrRuntimeSessionStart(out string reason)
    {
        reason = string.Empty;

        if (_deviceLost || Api is null || device.Handle == 0)
        {
            reason = "Vulkan device is not available";
            return true;
        }

        if (RuntimeEngine.StartupPresentationEnabled)
        {
            reason = "editor startup presentation is still active";
            return true;
        }

        if (ShouldDeferOpenXrVulkanResourceWork(out string resourceWorkReason))
        {
            reason = resourceWorkReason;
            return true;
        }

        if (_vkDebugFrameCounter < MinDesktopFramesBeforeOpenXrRuntimeSessionStart)
        {
            reason = $"desktop renderer has completed too few startup frames ({_vkDebugFrameCounter}/{MinDesktopFramesBeforeOpenXrRuntimeSessionStart})";
            return true;
        }

        if (Volatile.Read(ref _lastFrameCompletedTimestamp) == 0)
        {
            reason = "desktop renderer has not completed a frame yet";
            return true;
        }

        if (Volatile.Read(ref _windowRenderCallbackInProgress) != 0)
        {
            reason = "desktop renderer is currently recording/submitting a frame";
            return true;
        }

        long lastDirtyTimestamp = Volatile.Read(ref _lastCommandBufferDirtyTimestamp);
        if (lastDirtyTimestamp != 0)
        {
            TimeSpan dirtyAge = Stopwatch.GetElapsedTime(lastDirtyTimestamp);
            if (dirtyAge < OpenXrRuntimeSessionStartDirtyQuietPeriod)
            {
                long now = Stopwatch.GetTimestamp();
                long dirtyWaitStart = Volatile.Read(ref _openXrRuntimeSessionStartDirtyWaitStartTimestamp);
                if (dirtyWaitStart == 0)
                {
                    Interlocked.CompareExchange(ref _openXrRuntimeSessionStartDirtyWaitStartTimestamp, now, 0);
                    dirtyWaitStart = Volatile.Read(ref _openXrRuntimeSessionStartDirtyWaitStartTimestamp);
                }

                TimeSpan dirtyWait = Stopwatch.GetElapsedTime(dirtyWaitStart, now);
                if (dirtyWait < OpenXrRuntimeSessionStartDirtyMaxWait)
                {
                    reason =
                        $"desktop command buffers were dirtied {dirtyAge.TotalMilliseconds:F0} ms ago (waiting {dirtyWait.TotalMilliseconds:F0}/{OpenXrRuntimeSessionStartDirtyMaxWait.TotalMilliseconds:F0} ms for a quiet window)";
                    return true;
                }

                Debug.VulkanWarningEvery(
                    $"OpenXR.Vulkan.SessionStartDirtyQuietBypassed.{GetHashCode()}",
                    TimeSpan.FromSeconds(5),
                    "[OpenXR] Proceeding with Vulkan session creation despite desktop command buffers dirtied {0:F0} ms ago after waiting {1:F0} ms. The runtime graphics transition will wait for in-flight work and idle the device.",
                    dirtyAge.TotalMilliseconds,
                    dirtyWait.TotalMilliseconds);
            }
        }

        if (TryGetPendingSubmittedFrameSlot(out int pendingSlot, out ulong pendingTimelineValue))
        {
            long now = Stopwatch.GetTimestamp();
            long pendingFrameWaitStart = Volatile.Read(ref _openXrRuntimeSessionStartPendingFrameWaitStartTimestamp);
            if (pendingFrameWaitStart == 0)
            {
                Interlocked.CompareExchange(ref _openXrRuntimeSessionStartPendingFrameWaitStartTimestamp, now, 0);
                pendingFrameWaitStart = Volatile.Read(ref _openXrRuntimeSessionStartPendingFrameWaitStartTimestamp);
            }

            TimeSpan pendingFrameWait = Stopwatch.GetElapsedTime(pendingFrameWaitStart, now);
            if (pendingFrameWait < OpenXrRuntimeSessionStartPendingFrameMaxWait)
            {
                reason =
                    $"desktop frame slot {pendingSlot} is still pending at timeline value {pendingTimelineValue} (waiting {pendingFrameWait.TotalMilliseconds:F0}/{OpenXrRuntimeSessionStartPendingFrameMaxWait.TotalMilliseconds:F0} ms for submitted desktop work to retire)";
                return true;
            }

            Debug.VulkanWarningEvery(
                $"OpenXR.Vulkan.SessionStartPendingDesktopFrameBypassed.{GetHashCode()}",
                TimeSpan.FromSeconds(5),
                "[OpenXR] Proceeding with Vulkan session creation despite desktop frame slot {0} still pending at timeline value {1} after waiting {2:F0} ms. The runtime graphics transition will wait for in-flight work and idle the device.",
                pendingSlot,
                pendingTimelineValue,
                pendingFrameWait.TotalMilliseconds);
        }

        Volatile.Write(ref _openXrRuntimeSessionStartDirtyWaitStartTimestamp, 0);
        Volatile.Write(ref _openXrRuntimeSessionStartPendingFrameWaitStartTimestamp, 0);

        return false;
    }

    internal bool ShouldDeferOpenXrEyePreviewCopyWork(out string reason)
    {
        reason = string.Empty;

        if (_deviceLost || Api is null || device.Handle == 0)
        {
            reason = "Vulkan device is not available";
            return true;
        }

        if (ImportedTextureStreamingManager.Instance.TryDescribeBlockingOpenXrEyeTextureWork(out string textureWorkReason))
        {
            reason = textureWorkReason;
            return true;
        }

        if (TryDescribeRecentResourceAllocationFailure(out string allocationFailureReason))
        {
            reason = allocationFailureReason;
            return true;
        }

        if (TryDescribeOpenXrVulkanAllocatorPressure(out string allocatorPressureReason))
        {
            reason = allocatorPressureReason;
            return true;
        }

        return false;
    }

    internal bool ShouldDeferOpenXrVulkanResourceWork(out string reason)
    {
        reason = string.Empty;

        if (_deviceLost || Api is null || device.Handle == 0)
        {
            reason = "Vulkan device is not available";
            return true;
        }

        if (ImportedTextureStreamingManager.Instance.TryDescribeActiveStartupTextureWork(out string textureWorkReason))
        {
            reason = textureWorkReason;
            return true;
        }

        if (TryDescribeRecentResourceAllocationFailure(out string allocationFailureReason))
        {
            reason = allocationFailureReason;
            return true;
        }

        if (TryDescribeOpenXrVulkanAllocatorPressure(out string allocatorPressureReason))
        {
            reason = allocatorPressureReason;
            return true;
        }

        return false;
    }

    internal bool ShouldDeferOpenXrEyeRenderingWork(out string reason)
    {
        reason = string.Empty;

        if (_deviceLost || Api is null || device.Handle == 0)
        {
            reason = "Vulkan device is not available";
            return true;
        }

        if (ImportedTextureStreamingManager.Instance.TryDescribeBlockingOpenXrEyeTextureWork(out string textureWorkReason))
        {
            reason = textureWorkReason;
            return true;
        }

        return false;
    }

    internal bool ShouldDeferTextureUploadPreparationForOpenXrPriority(out string reason)
    {
        reason = string.Empty;

        if (_deviceLost || Api is null || device.Handle == 0)
        {
            reason = "Vulkan device is not available";
            return true;
        }

        IRuntimeRenderingHostServices host = RuntimeRenderingHostServices.Current;
        if (!host.IsOpenXRActive && !host.IsInVR)
            return false;

        if (TryDescribeRecentResourceAllocationFailure(out string allocationFailureReason))
        {
            reason = allocationFailureReason;
            return true;
        }

        if (TryDescribeOpenXrVulkanAllocatorPressure(out string allocatorPressureReason))
        {
            reason = allocatorPressureReason;
            return true;
        }

        return false;
    }

    internal bool TryGetVulkanAllocatorBudgetSnapshot(
        double budgetRatio,
        long reserveBytes,
        out long allocatedBytes,
        out long budgetBytes,
        out long largestHeapBytes,
        out int activeAllocationCount)
    {
        allocatedBytes = 0L;
        budgetBytes = 0L;
        largestHeapBytes = 0L;
        activeAllocationCount = 0;

        try
        {
            activeAllocationCount = MemoryAllocator.ActiveVkAllocationCount;
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        if (MemoryAllocator is VulkanVmaAllocator vmaAllocator
            && Api is not null
            && _physicalDevice.Handle != 0)
        {
            Api.GetPhysicalDeviceMemoryProperties(_physicalDevice, out PhysicalDeviceMemoryProperties memoryProperties);
            if (vmaAllocator.TryGetDeviceLocalHeapBudgetSnapshot(
                    in memoryProperties,
                    budgetRatio,
                    reserveBytes,
                    out allocatedBytes,
                    out budgetBytes,
                    out largestHeapBytes))
            {
                return true;
            }
        }

        try
        {
            allocatedBytes = MemoryAllocator.TotalAllocatedBytes;
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        largestHeapBytes = ResolveLargestVulkanMemoryHeapBytes();
        if (largestHeapBytes <= 0)
            return false;

        double clampedRatio = Math.Clamp(budgetRatio, 0.1, 1.0);
        long ratioLimitBytes = (long)Math.Floor(largestHeapBytes * clampedRatio);
        long reserveLimitBytes = largestHeapBytes > reserveBytes
            ? largestHeapBytes - Math.Max(0L, reserveBytes)
            : largestHeapBytes;
        budgetBytes = Math.Max(0L, Math.Min(ratioLimitBytes, reserveLimitBytes));
        return budgetBytes > 0L;
    }

    private bool TryDescribeOpenXrVulkanAllocatorPressure(out string reason)
    {
        reason = string.Empty;

        if (!TryGetVulkanAllocatorBudgetSnapshot(
                OpenXrVulkanAllocatorPressureDeferRatio,
                OpenXrVulkanAllocatorPressureReserveBytes,
                out long allocatedBytes,
                out long deferLimitBytes,
                out long largestHeapBytes,
                out int activeAllocationCount))
        {
            return false;
        }

        if (allocatedBytes < deferLimitBytes)
            return false;

        reason =
            $"Vulkan allocator pressure is high (allocated={allocatedBytes}, largestHeap={largestHeapBytes}, deferLimit={deferLimitBytes}, activeVkAllocations={activeAllocationCount})";
        return true;
    }

    private long ResolveLargestVulkanMemoryHeapBytes()
    {
        if (Api is null || _physicalDevice.Handle == 0)
            return 0;

        Api.GetPhysicalDeviceMemoryProperties(_physicalDevice, out PhysicalDeviceMemoryProperties memoryProperties);
        ulong largestHeapBytes = 0;
        for (int i = 0; i < memoryProperties.MemoryHeapCount; i++)
            largestHeapBytes = Math.Max(largestHeapBytes, memoryProperties.MemoryHeaps[i].Size);

        return largestHeapBytes > long.MaxValue
            ? long.MaxValue
            : (long)largestHeapBytes;
    }

    private bool TryGetPendingSubmittedFrameSlot(out int pendingSlot, out ulong pendingTimelineValue)
    {
        pendingSlot = -1;
        pendingTimelineValue = 0;

        if (_frameSlotTimelineValues is null || _graphicsTimelineSemaphore.Handle == 0)
            return false;

        int frameSlotCount = Math.Min(_frameSlotTimelineValues.Length, MAX_FRAMES_IN_FLIGHT);
        for (int i = 0; i < frameSlotCount; i++)
        {
            ulong value = _frameSlotTimelineValues[i];
            if (value == 0 || HasTimelineValueCompleted(_graphicsTimelineSemaphore, value))
                continue;

            pendingSlot = i;
            pendingTimelineValue = value;
            return true;
        }

        return false;
    }

    private void DestroyOpenXrPrimaryCommandBufferCache()
    {
        lock (_openXrPrimaryCommandBufferVariantsLock)
        {
            if (_openXrPrimaryCommandBufferVariants.Count != 0)
            {
                foreach (List<CommandBufferCacheVariant> variants in _openXrPrimaryCommandBufferVariants.Values)
                {
                    for (int i = 0; i < variants.Count; i++)
                    {
                        CommandBufferCacheVariant variant = variants[i];
                        CommandBuffer primary = variant.PrimaryCommandBuffer;
                        if (primary.Handle != 0)
                        {
                            if (variant.OwnsPrimaryCommandBuffer && !_deviceLost)
                            {
                                CommandPool ownerPool = variant.PrimaryCommandPool.Handle != 0
                                    ? variant.PrimaryCommandPool
                                    : commandPool;
                                FreeVulkanCommandBufferTracked(ownerPool, ref primary, "OpenXR.PrimaryCache");
                            }
                            RemoveCommandBufferBindState(variant.PrimaryCommandBuffer);
                        }

                        CommandBuffer dynamicSecondary = variant.DynamicUiSecondaryCommandBuffer;
                        if (dynamicSecondary.Handle != 0)
                        {
                            if (variant.OwnsDynamicUiSecondaryCommandBuffer && !_deviceLost)
                            {
                                CommandPool ownerPool = variant.DynamicUiSecondaryCommandPool.Handle != 0
                                    ? variant.DynamicUiSecondaryCommandPool
                                    : commandPool;
                                FreeVulkanCommandBufferTracked(ownerPool, ref dynamicSecondary, "OpenXR.DynamicSecondaryCache");
                            }
                            RemoveCommandBufferBindState(variant.DynamicUiSecondaryCommandBuffer);
                        }
                    }
                }

                _openXrPrimaryCommandBufferVariants.Clear();
            }
        }

        DestroyOpenXrEyeCommandPools();
    }

    private void DestroyOpenXrResourcePlannerState()
    {
        KeyValuePair<OpenXrViewResourcePlannerContextKey, ResourcePlannerRuntimeState>[] states;
        lock (_openXrResourcePlannerStatesLock)
        {
            if (_openXrResourcePlannerStates.Count == 0)
                return;

            states = _openXrResourcePlannerStates.ToArray();
            _openXrResourcePlannerStates.Clear();
        }

        ResourcePlannerRuntimeState previousState = CaptureResourcePlannerRuntimeState();
        HashSet<VulkanResourceAllocator> retiredAllocators = new(ReferenceEqualityComparer.Instance);
        foreach (KeyValuePair<OpenXrViewResourcePlannerContextKey, ResourcePlannerRuntimeState> pair in states)
        {
            RetireResourcePlannerRuntimeStateAllocators(
                pair.Value,
                retiredAllocators,
                $"OpenXrResourcePlannerStateDestroy.{DescribeOpenXrResourcePlannerContextKey(pair.Key)}");
        }

        if (previousState.ResourceAllocator is not null && previousState.ResourceAllocator.IsRetired)
            previousState = ResourcePlannerRuntimeState.CreateEmpty();
        RestoreResourcePlannerRuntimeState(previousState);
    }

    private bool SubmitAndWaitOpenXrCommandBuffer(
        CommandBuffer commandBuffer,
        out bool commandBufferCompleted,
        VulkanSubmissionDiagnosticContext diagnosticContext = default)
    {
        CommandBuffer* commandBuffers = stackalloc CommandBuffer[1];
        commandBuffers[0] = commandBuffer;
        return SubmitAndWaitOpenXrCommandBuffers(commandBuffers, 1, out commandBufferCompleted, diagnosticContext);
    }

    private bool SubmitAndWaitOpenXrCommandBuffers(
        CommandBuffer firstCommandBuffer,
        CommandBuffer secondCommandBuffer,
        out bool commandBuffersCompleted,
        VulkanSubmissionDiagnosticContext diagnosticContext = default)
    {
        CommandBuffer* commandBuffers = stackalloc CommandBuffer[2];
        commandBuffers[0] = firstCommandBuffer;
        commandBuffers[1] = secondCommandBuffer;
        return SubmitAndWaitOpenXrCommandBuffers(commandBuffers, 2, out commandBuffersCompleted, diagnosticContext);
    }

    private bool SubmitAndWaitOpenXrCommandBuffers(
        CommandBuffer* commandBuffers,
        uint commandBufferCount,
        out bool commandBufferCompleted,
        VulkanSubmissionDiagnosticContext diagnosticContext = default)
        => SubmitAndWaitOpenXrCommandBuffers(
            commandBuffers,
            commandBufferCount,
            out commandBufferCompleted,
            out _,
            out _,
            diagnosticContext);

    private bool SubmitAndWaitOpenXrCommandBuffers(
        CommandBuffer* commandBuffers,
        uint commandBufferCount,
        out bool commandBufferCompleted,
        out EVulkanQueueSubmissionDisposition submissionDisposition,
        out EOpenXrStrictSpsFaultInjectionStage injectedFailureStage,
        VulkanSubmissionDiagnosticContext diagnosticContext = default)
    {
        commandBufferCompleted = false;
        submissionDisposition = EVulkanQueueSubmissionDisposition.NotSubmitted;
        injectedFailureStage = EOpenXrStrictSpsFaultInjectionStage.None;
        if (commandBuffers is null || commandBufferCount == 0)
            return false;

        FenceCreateInfo fenceCreateInfo = new()
        {
            SType = StructureType.FenceCreateInfo,
            Flags = 0,
        };

        if (Api!.CreateFence(device, ref fenceCreateInfo, null, out Fence fence) != Result.Success)
            throw new InvalidOperationException("Failed to create OpenXR Vulkan submit fence.");

        SetDebugObjectName(ObjectType.Fence, fence.Handle, "OpenXR.SubmitAndWaitFence");

        try
        {
            SubmitInfo submitInfo = new()
            {
                SType = StructureType.SubmitInfo,
                CommandBufferCount = commandBufferCount,
                PCommandBuffers = commandBuffers,
            };

            Result submitResult;
            long submitStart = Stopwatch.GetTimestamp();
            using (RuntimeRenderingHostServices.Current.StartProfileScope("OpenXR.Vulkan.QueueSubmit"))
            {
                long queueLockWaitStart = Stopwatch.GetTimestamp();
                bool queueLockTaken = false;
                try
                {
                    Monitor.Enter(_oneTimeSubmitLock, ref queueLockTaken);
                    LogOpenXrSerializedCriticalSectionWait("QueueSubmit", queueLockWaitStart, Stopwatch.GetTimestamp());
                    submitResult = SubmitToQueueTrackedWithDisposition(
                        graphicsQueue,
                        ref submitInfo,
                        fence,
                        diagnosticContext,
                        out bool queueDispatchAttempted,
                        out injectedFailureStage);
                    if (queueDispatchAttempted)
                    {
                        submissionDisposition =
                            EVulkanQueueSubmissionDisposition.SubmittedIncomplete;
                    }
                }
                finally
                {
                    if (queueLockTaken)
                        Monitor.Exit(_oneTimeSubmitLock);
                }
            }
            long submitEnd = Stopwatch.GetTimestamp();

            if (submitResult != Result.Success)
            {
                if (submitResult == Result.ErrorDeviceLost)
                    MarkDeviceLost("OpenXR Vulkan eye submit returned ErrorDeviceLost");

                Debug.VulkanWarning($"[OpenXR] Vulkan eye QueueSubmit failed: {submitResult}");
                return false;
            }

            long waitStart = Stopwatch.GetTimestamp();
            Result waitResult;
            using (RuntimeRenderingHostServices.Current.StartProfileScope("OpenXR.Vulkan.SubmitFenceWait"))
                waitResult = Api!.WaitForFences(device, 1, &fence, true, ulong.MaxValue);
            long waitEnd = Stopwatch.GetTimestamp();
            if (waitResult != Result.Success)
            {
                if (waitResult == Result.ErrorDeviceLost)
                {
                    RecordFirstFailingVulkanApi($"vkWaitForFences:OpenXR.Vulkan.SubmitFenceWait:{waitResult}");
                    MarkDeviceLost("OpenXR Vulkan eye fence wait returned ErrorDeviceLost");
                }

                Debug.VulkanWarning($"[OpenXR] Vulkan eye fence wait failed: {waitResult}");
                return false;
            }

            NotifyVulkanFenceCompleted(fence);
            submissionDisposition = EVulkanQueueSubmissionDisposition.Completed;

            if (OpenXrVulkanTraceEnabled)
            {
                double submitMs = (submitEnd - submitStart) * 1000.0 / Stopwatch.Frequency;
                double fenceWaitMs = (waitEnd - waitStart) * 1000.0 / Stopwatch.Frequency;
                Debug.Vulkan(
                    "[OpenXrVulkan] submitted commandBuffers={0} queueSubmitMs={1:F3} fenceWaitMs={2:F3}",
                    commandBufferCount,
                    submitMs,
                    fenceWaitMs);
            }

            commandBufferCompleted = true;
            return true;
        }
        finally
        {
            if (fence.Handle != 0)
                Api!.DestroyFence(device, fence, null);
        }
    }

    private static void LogOpenXrSerializedCriticalSectionWait(string sectionName, long waitStart, long waitEnd)
    {
        double waitMs = (waitEnd - waitStart) * 1000.0 / Stopwatch.Frequency;
        if (waitMs < 0.25)
            return;

        Debug.VulkanEvery(
            $"OpenXR.Vulkan.SerializedCriticalSection.{sectionName}",
            TimeSpan.FromSeconds(1),
            "[OpenXrVulkan] serialized critical section={0} waitMs={1:F3}",
            sectionName,
            waitMs);
    }
}
