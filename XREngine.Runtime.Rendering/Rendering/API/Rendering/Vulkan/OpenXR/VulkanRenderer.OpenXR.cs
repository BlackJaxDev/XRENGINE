using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Rendering;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private const int OpenXrEyeResourcePlannerStateCount = 2;
    private const uint OpenXrExternalSwapchainTargetImageIndex = 0;

    internal readonly record struct OpenXrDepthTarget(
        Image Image,
        DeviceMemory Memory,
        ImageView View,
        Format Format,
        ImageAspectFlags Aspect);

    private readonly record struct OpenXrSwapchainImageViewCacheEntry(ImageView View, Format Format);

    internal readonly record struct OpenXrEyeRenderTargetContext(
        uint OpenXrViewIndex,
        uint OpenXrImageIndex,
        Image Image,
        ImageView ImageView,
        Format ImageFormat,
        Extent2D Extent,
        Image DepthImage,
        DeviceMemory DepthMemory,
        ImageView DepthView,
        Format DepthFormat,
        ImageAspectFlags DepthAspect,
        BoundingRectangle ExternalTargetRegion,
        uint CommandChainImageKey,
        uint FrameDataSlotIndex,
        int ResourcePlannerStateIndex,
        ulong FoveationResourceKey,
        EVrFoveationAttachmentKind FoveationAttachmentKind,
        bool FoveationAttachmentOwnedByResourcePlanner)
    {
        public bool IsValid =>
            Image.Handle != 0 &&
            ImageView.Handle != 0 &&
            Extent.Width != 0 &&
            Extent.Height != 0 &&
            DepthImage.Handle != 0 &&
            DepthView.Handle != 0;
    }

    internal readonly record struct OpenXrViewResourcePlannerContextKey(
        int ResourcePlannerStateIndex,
        uint OpenXrViewIndex,
        uint OpenXrImageIndex,
        uint CommandChainImageKey,
        uint FrameDataSlotIndex,
        ulong FoveationResourceKey,
        EVrFoveationAttachmentKind FoveationAttachmentKind,
        bool FoveationAttachmentOwnedByResourcePlanner)
    {
        public static OpenXrViewResourcePlannerContextKey FromTarget(in OpenXrEyeRenderTargetContext target)
            => new(
                target.ResourcePlannerStateIndex,
                target.OpenXrViewIndex,
                target.OpenXrImageIndex,
                target.CommandChainImageKey,
                target.FrameDataSlotIndex,
                target.FoveationResourceKey,
                target.FoveationAttachmentKind,
                target.FoveationAttachmentOwnedByResourcePlanner);
    }

    internal readonly record struct OpenXrEyeSwapchainRenderRequest(
        Image Image,
        Format Format,
        Extent2D Extent,
        int ResourcePlannerStateIndex,
        uint OpenXrViewIndex,
        uint OpenXrImageIndex,
        ViewFoveationContext Foveation,
        Action EmitFrameOps);

    private readonly record struct OpenXrPreparedEyeCommandBufferInput(
        OpenXrEyeSwapchainRenderRequest Request,
        OpenXrEyeRenderTargetContext TargetContext,
        FrameOp[] Ops,
        FrameOpContext PlannerContext,
        ulong FrameOpsSignature,
        ulong PlannerRevision,
        CommandChainSchedule? CommandChainSchedule);

    internal readonly record struct OpenXrEyeMirrorRenderRequest(
        XRFrameBuffer TargetFrameBuffer,
        Extent2D Extent,
        int ResourcePlannerStateIndex,
        uint OpenXrViewIndex,
        uint OpenXrImageIndex,
        Action EmitFrameOps);

    internal readonly record struct OpenXrEyeMirrorPublishRequest(
        XRTexture2D? SourceTexture,
        Image SwapchainImage,
        Format SwapchainFormat,
        Extent2D Extent,
        XRTexture2D? PreviewTexture,
        string DestinationLabel,
        bool FlipPreviewY);

    internal readonly record struct OpenXrEyePreviewCopyRequest(
        Image SourceImage,
        Format SourceFormat,
        Extent2D SourceExtent,
        XRTexture2D? DestinationTexture,
        string DestinationLabel,
        bool FlipY);

    private readonly record struct OpenXrEyeMirrorPublishPlan(
        IVkImageDescriptorSource Source,
        Image SourceImage,
        Format SourceFormat,
        Extent2D SourceExtent,
        ImageLayout SourceOldLayout,
        ImageAspectFlags SourceAspect,
        Image SwapchainImage,
        Format SwapchainFormat,
        Extent2D SwapchainExtent,
        IVkImageDescriptorSource? PreviewSource,
        Image PreviewImage,
        Extent2D PreviewExtent,
        ImageLayout PreviewOldLayout,
        ImageAspectFlags PreviewAspect,
        string DestinationLabel,
        bool FlipPreviewY);

    private readonly record struct OpenXrRecordedEyeCommandBuffer(
        CommandBuffer CommandBuffer,
        uint OpenXrViewIndex,
        uint OpenXrImageIndex,
        uint FrameDataSlotIndex,
        bool OwnedByOpenXrPrimaryCache);

    private readonly record struct OpenXrEyePreviewCopyPlan(
        Image SourceImage,
        Format SourceFormat,
        Extent2D SourceExtent,
        IVkImageDescriptorSource DestinationSource,
        Image DestinationImage,
        Extent2D DestinationExtent,
        ImageLayout DestinationOldLayout,
        ImageAspectFlags DestinationAspect,
        string DestinationLabel,
        bool FlipY);

    private readonly Dictionary<ulong, OpenXrSwapchainImageViewCacheEntry> _openXrSwapchainImageViews = new();
    private readonly Dictionary<ulong, List<CommandBufferCacheVariant>> _openXrPrimaryCommandBufferVariants = new();
    private readonly object _openXrPrimaryCommandBufferVariantsLock = new();
    private readonly CommandPool[] _openXrEyeCommandPools = new CommandPool[OpenXrEyeResourcePlannerStateCount];
    private readonly object _openXrEyeCommandPoolsLock = new();
    private readonly List<VulkanImportedTexturePendingUpload>[] _openXrEyeRecordedTextureUploadsForSubmit = [new(), new()];
    private readonly List<VulkanImportedTexturePendingUpload> _openXrRecordedTextureUploadsForSubmit = new();
    private OpenXrDepthTarget _openXrCachedDepthTarget;
    private Extent2D _openXrCachedDepthExtent;
    private int _openXrExternalSwapchainRenderDepth;
    private BoundingRectangle _openXrExternalSwapchainTargetRegion;
    [ThreadStatic]
    private static VulkanRenderer? _threadOpenXrExternalSwapchainRenderer;
    [ThreadStatic]
    private static int _threadOpenXrExternalSwapchainRenderDepth;
    [ThreadStatic]
    private static BoundingRectangle _threadOpenXrExternalSwapchainTargetRegion;
    private int _openXrExternalSwapchainPrewarmDepth;
    private int _synchronousResourceUploadBlockDepth;
    [ThreadStatic]
    private static VulkanRenderer? _threadSynchronousResourceUploadBlockRenderer;
    [ThreadStatic]
    private static int _threadSynchronousResourceUploadBlockDepth;
    private readonly Dictionary<OpenXrViewResourcePlannerContextKey, ResourcePlannerRuntimeState> _openXrResourcePlannerStates = new();
    private readonly object _openXrResourcePlannerStatesLock = new();

    public override bool IsRenderingExternalSwapchainTarget =>
        IsThreadOpenXrExternalSwapchainTarget ||
        Volatile.Read(ref _openXrExternalSwapchainRenderDepth) > 0;
    internal bool IsPrewarmingOpenXrExternalSwapchainTarget => _openXrExternalSwapchainPrewarmDepth > 0;
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
           IsTruthyEnvironmentValue(Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.UnitTestUseOpenXr));

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

    public override bool TryGetExternalSwapchainTargetRegion(out BoundingRectangle region)
    {
        if (IsThreadOpenXrExternalSwapchainTarget &&
            _threadOpenXrExternalSwapchainTargetRegion.Width > 0 &&
            _threadOpenXrExternalSwapchainTargetRegion.Height > 0)
        {
            region = _threadOpenXrExternalSwapchainTargetRegion;
            return true;
        }

        if (Volatile.Read(ref _openXrExternalSwapchainRenderDepth) > 0 &&
            _openXrExternalSwapchainTargetRegion.Width > 0 &&
            _openXrExternalSwapchainTargetRegion.Height > 0)
        {
            region = _openXrExternalSwapchainTargetRegion;
            return true;
        }

        if (IsRenderingExternalSwapchainTarget &&
            swapChainExtent.Width > 0 &&
            swapChainExtent.Height > 0)
        {
            region = new BoundingRectangle(
                0,
                0,
                (int)Math.Min(swapChainExtent.Width, (uint)int.MaxValue),
                (int)Math.Min(swapChainExtent.Height, (uint)int.MaxValue));
            return true;
        }

        region = default;
        return false;
    }

    internal IDisposable EnterOpenXrExternalSwapchainRenderScope(uint width, uint height)
    {
        BoundingRectangle region = new(
            0,
            0,
            (int)Math.Min(width, (uint)int.MaxValue),
            (int)Math.Min(height, (uint)int.MaxValue));

        return new OpenXrExternalSwapchainRenderScope(this, region);
    }

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
            submitted = SubmitAndWaitOpenXrCommandBuffer(recorded.CommandBuffer, out commandBufferCompleted);
            if (submitted)
            {
                int publishCount = eyeUploads.Count;
                PublishRecordedTextureUploadsAfterCompletedSubmit(eyeUploads, "OpenXR eye");
                ForceFlushCompletedNonImageRetiredResources();
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
        bool hasFirst = false;
        bool hasSecond = false;
        bool submitted = false;
        bool commandBuffersCompleted = false;

        try
        {
            using (RuntimeRenderingHostServices.Current.StartProfileScope("OpenXR.Vulkan.Batch.RecordLeftEye"))
                hasFirst = TryRecordOpenXrEyeSwapchainCommandBuffer(firstEye, out firstRecorded);
            if (!hasFirst)
                return false;

            using (RuntimeRenderingHostServices.Current.StartProfileScope("OpenXR.Vulkan.Batch.RecordRightEye"))
                hasSecond = TryRecordOpenXrEyeSwapchainCommandBuffer(secondEye, out secondRecorded);
            if (!hasSecond)
                return false;

            using (RuntimeRenderingHostServices.Current.StartProfileScope("OpenXR.Vulkan.Batch.SubmitAndWait"))
            {
                submitted = SubmitAndWaitOpenXrCommandBuffers(
                    firstRecorded.CommandBuffer,
                    secondRecorded.CommandBuffer,
                    out commandBuffersCompleted);
            }

            if (submitted)
            {
                int publishCount = CountOpenXrEyeRecordedTextureUploads();
                using (RuntimeRenderingHostServices.Current.StartProfileScope("OpenXR.Vulkan.Batch.PublishUploads"))
                    PublishOpenXrEyeRecordedTextureUploadsAfterCompletedSubmit("OpenXR eye batch");
                using (RuntimeRenderingHostServices.Current.StartProfileScope("OpenXR.Vulkan.Batch.FlushRetired"))
                    ForceFlushCompletedNonImageRetiredResources();
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
        using IDisposable externalScope = EnterOpenXrExternalSwapchainRenderScope(
            request.Extent.Width,
            request.Extent.Height);
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
                DrainRetiredResourcesIfSubmittedFrameSlotsCompleted();
                DrainCompletedRecordedTextureUploadPublications();
            }

            using (RuntimeRenderingHostServices.Current.StartProfileScope("OpenXR.Vulkan.RecordEye.PrepareTargets"))
            {
                ImageView openXrImageView = GetOrCreateOpenXrSwapchainImageView(request.Image, request.Format);
                OpenXrDepthTarget depthTarget = GetOrCreateOpenXrDepthTarget(request.Extent);

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

                ulong plannerRevision;
                ulong frameOpsSignature;
                CommandChainSchedule? commandChainSchedule;
                FrameOpContext plannerContext;
                using (RuntimeRenderingHostServices.Current.StartProfileScope("OpenXR.Vulkan.RecordEye.PlanAndSchedule"))
                {
                    ops = VulkanRenderGraphCompiler.SortFrameOps(ops, CompiledRenderGraph);
                    plannerContext = PrepareResourcePlannerForFrameOps(ops);
                    RefreshFrameOpResourceWrappers(
                        ops,
                        plannerContext,
                        "OpenXR eye prepared frame-op resource refresh",
                        AllowSynchronousResourceUploads);
                    PrewarmOpenXrFrameOpResources(ops);
                    plannerRevision = ResourcePlannerRevision;
                    frameOpsSignature = ComputeFrameOpsSignature(ops);
                    commandChainSchedule = TryBuildOpenXrEyeCommandChainSchedule(
                        targetContext.CommandChainImageKey,
                        targetContext.OpenXrViewIndex,
                        targetContext.OpenXrImageIndex,
                        targetContext.Image,
                        ops,
                        frameOpsSignature,
                        plannerRevision);
                }

                prepared = new OpenXrPreparedEyeCommandBufferInput(
                    request,
                    targetContext,
                    ops,
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
            targetContext.Extent.Height);
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
                    reusedPrimary = TryReuseOpenXrPrimaryCommandBuffer(
                        targetContext.FrameDataSlotIndex,
                        targetContext.CommandChainImageKey,
                        targetContext,
                        prepared.Request,
                        prepared.Ops,
                        prepared.FrameOpsSignature,
                        prepared.PlannerRevision,
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
                            prepared.PlannerRevision,
                            prepared.CommandChainSchedule);
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
        ulong plannerRevision,
        CommandChainSchedule? commandChainSchedule,
        out CommandBuffer commandBuffer)
    {
        commandBuffer = default;
        if (!OpenXrVulkanPrimaryReuseEnabled)
        {
            RecordOpenXrPrimaryReuseMiss("openxr-primary-miss:disabled");
            return false;
        }

        ulong cacheKey = BuildOpenXrPrimaryCommandBufferCacheKey(commandChainImageIndex, targetContext);
        lock (_openXrPrimaryCommandBufferVariantsLock)
        {
            if (!_openXrPrimaryCommandBufferVariants.TryGetValue(cacheKey, out List<CommandBufferCacheVariant>? variants))
            {
                RecordOpenXrPrimaryReuseMiss($"openxr-primary-miss:no-variants key=0x{cacheKey:X16}");
                return false;
            }

            bool gpuPipelineProfilingActive =
                IsVulkanGpuProfilerCommandBufferInstrumentationEnabled &&
                RenderPipelineGpuProfiler.Instance.IsProfilingActive;
            int commandBufferImageSlot = unchecked((int)Math.Min(recordImageIndex, int.MaxValue));
            ulong commandChainPrimaryGroupSignature = 0;
            int commandChainPrimaryGroupCount = 0;
            bool usingCommandChains = commandChainSchedule is not null;
            bool requiresExactFrameOps = !usingCommandChains || HasTextureUploadFrameOps(ops);
            if (!TryComputeOpenXrPrimaryCommandBufferGroupSignature(
                    commandChainImageIndex,
                    commandChainSchedule,
                    requireReusableChains: true,
                    out commandChainPrimaryGroupSignature,
                    out commandChainPrimaryGroupCount))
            {
                RecordOpenXrPrimaryReuseMiss(
                    $"openxr-primary-miss:chains-not-reusable key=0x{cacheKey:X16} {DescribeOpenXrPrimaryReusableChainMiss(commandChainImageIndex, commandChainSchedule)}");
                return false;
            }

            for (int i = 0; i < variants.Count; i++)
            {
                CommandBufferCacheVariant variant = variants[i];
                if (variant.Dirty ||
                    variant.PrimaryCommandBuffer.Handle == 0 ||
                    (requiresExactFrameOps && variant.FrameOpsSignature != frameOpsSignature) ||
                    (!usingCommandChains && variant.PlannerRevision != plannerRevision) ||
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
                variant.LastUsedFrameId = VulkanFrameCounter;
                StoreFrameOpSignatureDebugParts(variant, ops);
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

            RecordOpenXrPrimaryReuseMiss(
                $"openxr-primary-miss:no-matching-variant key=0x{cacheKey:X16} variants={variants.Count} first={DescribeOpenXrPrimaryVariantMismatch(
                    variants,
                    requiresExactFrameOps,
                    usingCommandChains,
                    frameOpsSignature,
                    plannerRevision,
                    commandChainSchedule,
                    commandChainPrimaryGroupSignature,
                    commandChainPrimaryGroupCount,
                    gpuPipelineProfilingActive,
                    commandBufferImageSlot)}");
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
        ulong plannerRevision,
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

            swapchainLayoutAfterCommandBuffer = RecordCommandBuffer(
                imageIndex: OpenXrExternalSwapchainTargetImageIndex,
                variant.PrimaryCommandBuffer,
                dynamicUiBatchTextSecondaryCommandBuffer: default,
                ops,
                dynamicUiBatchTextOpCount: 0,
                commandChainSchedule,
                preserveSwapchainForOverlay: false,
                recordedSwapchainWriteCount: out recordedSwapchainWriteCount,
                transitionSwapchainToPresent: false,
                frameDataImageIndexOverride: recordImageIndex,
                openXrTargetContext: targetContext);
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
        variant.RecordedSwapchainImageEverPresented = false;
        variant.RecordedSwapchainFinalLayout = swapchainLayoutAfterCommandBuffer;
        variant.RecordedSwapchainWriteCount = recordedSwapchainWriteCount;
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
        if (!OpenXrVulkanTraceEnabled)
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

    private static string DescribeOpenXrPrimaryVariantMismatch(
        List<CommandBufferCacheVariant> variants,
        bool requiresExactFrameOps,
        bool usingCommandChains,
        ulong frameOpsSignature,
        ulong plannerRevision,
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
        if (!usingCommandChains && variant.PlannerRevision != plannerRevision)
            return $"planner recorded={variant.PlannerRevision} current={plannerRevision}";

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
            Api!.FreeCommandBuffers(device, commandPool, 1, ref commandBuffer);
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

            submitted = SubmitAndWaitOpenXrCommandBuffer(recorded.CommandBuffer, out commandBufferCompleted);
            if (submitted)
            {
                PublishRecordedTextureUploadsAfterCompletedSubmit(_openXrRecordedTextureUploadsForSubmit, "OpenXR eye mirror");
                ForceFlushCompletedNonImageRetiredResources();
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
                out commandBuffersCompleted);

            if (submitted)
            {
                PublishRecordedTextureUploadsAfterCompletedSubmit(_openXrRecordedTextureUploadsForSubmit, "OpenXR eye mirror batch");
                ForceFlushCompletedNonImageRetiredResources();
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
                out commandBuffersCompleted);

            if (submitted)
            {
                PublishRecordedTextureUploadsAfterCompletedSubmit(_openXrRecordedTextureUploadsForSubmit, "OpenXR eye mirror render+publish batch");
                ForceFlushCompletedNonImageRetiredResources();
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
                FreeOpenXrMirrorPublishCommandBuffer(publishCommandBuffer, commandBuffersCompleted);
            if (hasSecond)
                FreeOpenXrRecordedEyeCommandBuffer(secondRecorded);
            if (hasFirst)
                FreeOpenXrRecordedEyeCommandBuffer(firstRecorded);

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

        using IDisposable externalScope = EnterOpenXrExternalSwapchainRenderScope(
            request.Extent.Width,
            request.Extent.Height);

        try
        {
            EnsureOpenXrFrameDataSlotCapacity(openXrFrameDataSlotCount);
            EnsureDescriptorFrameSlotFrameCountFloor(openXrFrameDataSlotCount);
            WaitForOpenXrFrameDataSlot(recordImageIndex, "eye mirror render");
            DrainRetiredResourcesIfSubmittedFrameSlotsCompleted();
            DrainCompletedRecordedTextureUploadPublications();

            using (EnterOpenXrResourcePlannerThreadScope(request.ResourcePlannerStateIndex))
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

                ops = VulkanRenderGraphCompiler.SortFrameOps(ops, CompiledRenderGraph);
                _ = PrepareResourcePlannerForFrameOps(ops);
                ulong plannerRevision = ResourcePlannerRevision;
                ulong frameOpsSignature = ComputeFrameOpsSignature(ops);
                uint mirrorCommandChainImageIndex = recordImageIndex;

                CommandChainSchedule? commandChainSchedule = TryBuildOpenXrEyeCommandChainSchedule(
                    mirrorCommandChainImageIndex,
                    request.OpenXrViewIndex,
                    request.OpenXrImageIndex,
                    default,
                    ops,
                    frameOpsSignature,
                    plannerRevision);

                bool reusedPrimary = TryReuseOpenXrMirrorPrimaryCommandBuffer(
                    recordImageIndex,
                    mirrorCommandChainImageIndex,
                    request,
                    ops,
                    frameOpsSignature,
                    plannerRevision,
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
                        plannerRevision,
                        commandChainSchedule);
                }

                recorded = new OpenXrRecordedEyeCommandBuffer(
                    commandBuffer,
                    request.OpenXrViewIndex,
                    request.OpenXrImageIndex,
                    recordImageIndex,
                    OwnedByOpenXrPrimaryCache: true);
                return true;
            }
        }
        catch (Exception ex)
        {
            if (!drainedFrameOps)
                _ = DrainFrameOpsExcludingTextureUploads(out _);

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
        ulong plannerRevision,
        CommandChainSchedule? commandChainSchedule,
        out CommandBuffer commandBuffer)
    {
        commandBuffer = default;
        if (!OpenXrVulkanPrimaryReuseEnabled)
        {
            RecordOpenXrPrimaryReuseMiss("openxr-mirror-primary-miss:disabled");
            return false;
        }

        ulong cacheKey = BuildOpenXrMirrorPrimaryCommandBufferCacheKey(commandChainImageIndex, request);
        lock (_openXrPrimaryCommandBufferVariantsLock)
        {
            if (!_openXrPrimaryCommandBufferVariants.TryGetValue(cacheKey, out List<CommandBufferCacheVariant>? variants))
            {
                RecordOpenXrPrimaryReuseMiss($"openxr-mirror-primary-miss:no-variants key=0x{cacheKey:X16}");
                return false;
            }

            bool gpuPipelineProfilingActive =
                IsVulkanGpuProfilerCommandBufferInstrumentationEnabled &&
                RenderPipelineGpuProfiler.Instance.IsProfilingActive;
            int commandBufferImageSlot = unchecked((int)Math.Min(recordImageIndex, int.MaxValue));
            ulong commandChainPrimaryGroupSignature = ulong.MaxValue;
            int commandChainPrimaryGroupCount = -1;
            bool usingCommandChains = commandChainSchedule is not null;
            bool requiresExactFrameOps = !usingCommandChains || HasTextureUploadFrameOps(ops);
            if (!TryComputeOpenXrPrimaryCommandBufferGroupSignature(
                    commandChainImageIndex,
                    commandChainSchedule,
                    requireReusableChains: true,
                    out commandChainPrimaryGroupSignature,
                    out commandChainPrimaryGroupCount))
            {
                RecordOpenXrPrimaryReuseMiss(
                    $"openxr-mirror-primary-miss:chains-not-reusable key=0x{cacheKey:X16} {DescribeOpenXrPrimaryReusableChainMiss(commandChainImageIndex, commandChainSchedule)}");
                return false;
            }

            for (int i = 0; i < variants.Count; i++)
            {
                CommandBufferCacheVariant variant = variants[i];
                if (variant.Dirty ||
                    variant.PrimaryCommandBuffer.Handle == 0 ||
                    (requiresExactFrameOps && variant.FrameOpsSignature != frameOpsSignature) ||
                    (!usingCommandChains && variant.PlannerRevision != plannerRevision) ||
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
                variant.LastUsedFrameId = VulkanFrameCounter;
                StoreFrameOpSignatureDebugParts(variant, ops);
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

            RecordOpenXrPrimaryReuseMiss(
                $"openxr-mirror-primary-miss:no-matching-variant key=0x{cacheKey:X16} variants={variants.Count} first={DescribeOpenXrPrimaryVariantMismatch(
                    variants,
                    requiresExactFrameOps,
                    usingCommandChains,
                    frameOpsSignature,
                    plannerRevision,
                    commandChainSchedule,
                    commandChainPrimaryGroupSignature,
                    commandChainPrimaryGroupCount,
                    gpuPipelineProfilingActive,
                    commandBufferImageSlot)}");
            return false;
        }
    }

    private CommandBuffer RecordOpenXrMirrorPrimaryCommandBuffer(
        uint recordImageIndex,
        uint commandChainImageIndex,
        in OpenXrEyeMirrorRenderRequest request,
        FrameOp[] ops,
        ulong frameOpsSignature,
        ulong plannerRevision,
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

            _ = RecordCommandBuffer(
                OpenXrExternalSwapchainTargetImageIndex,
                variant.PrimaryCommandBuffer,
                dynamicUiBatchTextSecondaryCommandBuffer: default,
                ops,
                dynamicUiBatchTextOpCount: 0,
                commandChainSchedule,
                preserveSwapchainForOverlay: false,
                recordedSwapchainWriteCount: out int recordedSwapchainWriteCount,
                transitionSwapchainToPresent: false,
                frameDataImageIndexOverride: recordImageIndex);

            bool wasDirty = variant.Dirty;
            variant.Dirty = false;
            variant.FrameOpsSignature = frameOpsSignature;
            variant.DynamicUiSignature = 0;
            variant.DynamicUiOpCount = 0;
            variant.DynamicUiSecondaryRecorded = false;
            variant.PreserveSwapchainForOverlay = false;
            variant.RecordedSwapchainImageEverPresented = false;
            variant.RecordedSwapchainFinalLayout = ImageLayout.ShaderReadOnlyOptimal;
            variant.RecordedSwapchainWriteCount = recordedSwapchainWriteCount;
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

        CommandChainWorkerTiming workerTiming = DispatchCommandChainRecordingWorkers(schedule);
        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanCommandChainMetrics(
            chainWorkerRecordTime: workerTiming.WorkerRecordTime,
            renderThreadWaitForWorkersTime: workerTiming.WaitForWorkersTime);

        return schedule;
    }

    private static uint BuildOpenXrCommandChainImageIndex(uint viewIndex, uint imageIndex, Image image)
    {
        int hash = HashCode.Combine("OpenXR", viewIndex, imageIndex, image.Handle);
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
            if (!TryPrepareOpenXrEyeSwapchainPreviewCopy(in request, out OpenXrEyePreviewCopyPlan plan))
                return false;

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

        if (GetOrCreateAPIRenderObject(request.DestinationTexture, generateNow: true) is not IVkImageDescriptorSource destinationSource)
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
            ImageLayout.ColorAttachmentOptimal,
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

        Api!.CmdBlitImage(
            commandBuffer,
            plan.SourceImage,
            ImageLayout.TransferSrcOptimal,
            plan.DestinationImage,
            ImageLayout.TransferDstOptimal,
            1,
            ref blit,
            Filter.Linear);

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

        Result allocateResult = Api!.AllocateCommandBuffers(device, ref allocateInfo, out commandBuffer);
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
                FreeOpenXrMirrorPublishCommandBuffer(commandBuffer, commandBufferCompleted: true);
                commandBuffer = default;
                return false;
            }

            begun = true;
            ResetCommandBufferBindState(commandBuffer);
            RecordOpenXrEyeMirrorPublish(commandBuffer, in firstPlan, out firstPreviewCopied);
            RecordOpenXrEyeMirrorPublish(commandBuffer, in secondPlan, out secondPreviewCopied);

            Result endResult = Api.EndCommandBuffer(commandBuffer);
            if (endResult != Result.Success)
            {
                Debug.VulkanWarning($"[OpenXR] Failed to end eye mirror publish command buffer: {endResult}");
                FreeOpenXrMirrorPublishCommandBuffer(commandBuffer, commandBufferCompleted: true);
                commandBuffer = default;
                return false;
            }

            return true;
        }
        catch
        {
            if (begun)
                RemoveCommandBufferBindState(commandBuffer);
            FreeOpenXrMirrorPublishCommandBuffer(commandBuffer, commandBufferCompleted: true);
            commandBuffer = default;

            throw;
        }
    }

    private void FreeOpenXrMirrorPublishCommandBuffer(CommandBuffer commandBuffer, bool commandBufferCompleted)
    {
        if (commandBuffer.Handle == 0)
            return;

        if (!commandBufferCompleted)
        {
            RemoveCommandBufferBindState(commandBuffer);
            return;
        }

        Api!.FreeCommandBuffers(device, commandPool, 1, ref commandBuffer);
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

        Api!.CmdBlitImage(
            commandBuffer,
            plan.SourceImage,
            ImageLayout.TransferSrcOptimal,
            plan.SwapchainImage,
            ImageLayout.TransferDstOptimal,
            1,
            ref swapchainBlit,
            Filter.Linear);

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

            Api!.CmdBlitImage(
                commandBuffer,
                plan.SourceImage,
                ImageLayout.TransferSrcOptimal,
                plan.PreviewImage,
                ImageLayout.TransferDstOptimal,
                1,
                ref previewBlit,
                Filter.Linear);

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
        XRTexture2D? sourceTexture,
        XRTexture2D? destinationTexture,
        string destinationLabel,
        bool flipY = false)
    {
        if (sourceTexture is null || destinationTexture is null)
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

            Extent2D sourceExtent = ResolveOpenXrMirrorDestinationExtent(sourceTexture, source);
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

            using CommandScope scope = NewCommandScope();
            CommandBuffer commandBuffer = scope.CommandBuffer;

            TransitionOpenXrMirrorImage(
                commandBuffer,
                sourceImage,
                source.DescriptorFormat,
                sourceOldLayout,
                ImageLayout.TransferSrcOptimal,
                sourceAspect);

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
                Y = flipY ? destinationHeight : 0,
                Z = 0
            };
            blit.DstOffsets.Element1 = new Offset3D
            {
                X = destinationWidth,
                Y = flipY ? 0 : destinationHeight,
                Z = 1
            };

            Api!.CmdBlitImage(
                commandBuffer,
                sourceImage,
                ImageLayout.TransferSrcOptimal,
                destinationImage,
                ImageLayout.TransferDstOptimal,
                1,
                ref blit,
                Filter.Linear);

            TransitionOpenXrMirrorImage(
                commandBuffer,
                sourceImage,
                source.DescriptorFormat,
                ImageLayout.TransferSrcOptimal,
                sourceOldLayout,
                sourceAspect);

            TransitionOpenXrMirrorImage(
                commandBuffer,
                destinationImage,
                destination.DescriptorFormat,
                ImageLayout.TransferDstOptimal,
                ImageLayout.ShaderReadOnlyOptimal,
                destinationAspect);

            if (destination is IVkFrameBufferAttachmentSource destinationAttachmentSource)
                destinationAttachmentSource.UpdateAttachmentTrackedLayout(ImageLayout.ShaderReadOnlyOptimal, 0, 0);

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

            Api!.CmdBlitImage(
                commandBuffer,
                sourceImage,
                ImageLayout.TransferSrcOptimal,
                destinationImage,
                ImageLayout.TransferDstOptimal,
                1,
                ref blit,
                Filter.Linear);

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

    internal void PrewarmOpenXrEyeSwapchainResources(
        Format format,
        Extent2D extent,
        int resourcePlannerStateIndex,
        Action emitFrameOps)
    {
        if (extent.Width == 0 || extent.Height == 0)
            return;

        int openXrFrameDataSlotCount = ResolveOpenXrFrameDataSlotCount(swapChainImages?.Length ?? 0);

        using IDisposable externalScope = EnterOpenXrExternalSwapchainRenderScope(
            extent.Width,
            extent.Height);
        using ThreadRenderStateScope renderStateScope = EnterThreadRenderStateScope(
            CreateOpenXrPrewarmRenderStateTracker(extent));
        _openXrExternalSwapchainPrewarmDepth++;

        try
        {
            EnsureOpenXrFrameDataSlotCapacity(openXrFrameDataSlotCount);
            EnsureDescriptorFrameSlotFrameCountFloor(openXrFrameDataSlotCount);
            DrainRetiredResourcesIfSubmittedFrameSlotsCompleted();

            using (EnterOpenXrResourcePlannerThreadScope(resourcePlannerStateIndex))
            {
                FrameOp[] ops = CaptureFrameOpsExcludingTextureUploads(emitFrameOps, out _);
                ops = FilterDiagnosticSkippedFrameOps(ops);
                if (ops.Length == 0)
                    return;

                ops = VulkanRenderGraphCompiler.SortFrameOps(ops, CompiledRenderGraph);
                _ = PrepareResourcePlannerForFrameOps(ops);
                PrewarmOpenXrFrameOpResources(ops);
            }
        }
        catch (Exception ex)
        {
            _ = DrainFrameOpsExcludingTextureUploads(out _);
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

        using IDisposable externalScope = EnterOpenXrExternalSwapchainRenderScope(extent.Width, extent.Height);
        _openXrExternalSwapchainPrewarmDepth++;
        int openXrFrameDataSlotCount = ResolveOpenXrFrameDataSlotCount(swapChainImages?.Length ?? 0);

        try
        {
            EnsureOpenXrFrameDataSlotCapacity(openXrFrameDataSlotCount);
            EnsureDescriptorFrameSlotFrameCountFloor(openXrFrameDataSlotCount);
            DrainRetiredResourcesIfSubmittedFrameSlotsCompleted();

            using (EnterOpenXrResourcePlannerThreadScope(resourcePlannerStateIndex))
            {
                FrameOp[] ops = CaptureFrameOpsExcludingTextureUploads(emitFrameOps, out _);
                ops = FilterDiagnosticSkippedFrameOps(ops);
                if (ops.Length == 0)
                    return;

                ops = VulkanRenderGraphCompiler.SortFrameOps(ops, CompiledRenderGraph);
                _ = PrepareResourcePlannerForFrameOps(ops);
                PrewarmOpenXrFrameOpResources(ops);
            }
        }
        catch (Exception ex)
        {
            _ = DrainFrameOpsExcludingTextureUploads(out _);
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

    private void RefreshFrameOpResourceWrappers(
        FrameOp[] ops,
        FrameOpContext plannerContext,
        string reason,
        bool allowSynchronousUpload)
    {
        HashSet<object>? visitedRegistries = null;
        RefreshResourceRegistryWrappers(plannerContext.ResourceRegistry, ref visitedRegistries, reason, allowSynchronousUpload);

        foreach (FrameOp op in ops)
            RefreshResourceRegistryWrappers(op.Context.ResourceRegistry, ref visitedRegistries, reason, allowSynchronousUpload);
    }

    private void RefreshResourceRegistryWrappers(
        RenderResourceRegistry? registry,
        ref HashSet<object>? visitedRegistries,
        string reason,
        bool allowSynchronousUpload)
    {
        if (registry is null)
            return;

        visitedRegistries ??= new HashSet<object>(ReferenceEqualityComparer.Instance);
        if (!visitedRegistries.Add(registry))
            return;

        foreach (XRTexture texture in registry.EnumerateTextureInstances())
        {
            // The physical render graph allocator currently materializes graph textures as 2D/layered images.
            // Do not force-generate dormant 3D texture wrappers during frame-op resource refresh.
            if (texture is XRTexture3D)
                continue;

            if (GetOrCreateAPIRenderObject(texture, generateNow: true) is IVkImageDescriptorSource imageSource)
                _ = imageSource.TryEnsureDescriptorReadyForUse(reason, allowSynchronousUpload);
        }

        foreach (XRRenderBuffer renderBuffer in registry.EnumerateRenderBufferInstances())
        {
            if (GetOrCreateAPIRenderObject(renderBuffer, generateNow: true) is VkRenderBuffer vkRenderBuffer)
                vkRenderBuffer.RefreshIfStale();
        }
    }

    private void PrewarmOpenXrFrameOpResources(FrameOp[] ops)
    {
        if (ops.Length == 0)
            return;

        Dictionary<VkMeshRenderer, int> meshDrawSlotsByRenderer = _refreshMeshDrawSlotsByRendererScratch;
        meshDrawSlotsByRenderer.Clear();
        meshDrawSlotsByRenderer.EnsureCapacity(_refreshMeshDrawSlotCapacityHint);

        static int GetMeshDrawUniformSlot(Dictionary<VkMeshRenderer, int> slots, VkMeshRenderer renderer)
        {
            slots.TryGetValue(renderer, out int slot);
            slots[renderer] = slot + 1;
            return slot;
        }

        for (int i = 0; i < ops.Length; i++)
        {
            switch (ops[i])
            {
                case MeshDrawOp drawOp:
                    PrewarmDraw(drawOp.Draw.Renderer, drawOp.Draw);
                    break;
                case IndirectDrawOp indirectDrawOp:
                    PrewarmDraw(indirectDrawOp.MeshRenderer, indirectDrawOp.Draw);
                    break;
            }
        }

        _refreshMeshDrawSlotCapacityHint = Math.Max(1, meshDrawSlotsByRenderer.Count);

        void PrewarmDraw(VkMeshRenderer renderer, in PendingMeshDraw draw)
        {
            int drawUniformSlot = GetMeshDrawUniformSlot(meshDrawSlotsByRenderer, renderer);
            if (renderer.TryPrewarmFrameDataForRecording(draw, drawUniformSlot, out string reason))
                return;

            Debug.VulkanWarningEvery(
                $"OpenXR.Vulkan.PrewarmDrawResourcesFailed.{GetHashCode()}.{renderer.GetHashCode()}.{drawUniformSlot}",
                TimeSpan.FromSeconds(1),
                "[OpenXR] Vulkan eye prewarm could not prepare draw resources for mesh='{0}' material='{1}' slot={2}: {3}",
                renderer.MeshRenderer.Mesh?.Name ?? "<unnamed mesh>",
                (draw.MaterialOverride ?? renderer.MeshRenderer.Material)?.Name ?? "<unnamed material>",
                drawUniformSlot,
                reason);
        }
    }

    private OpenXrResourcePlannerThreadScope EnterOpenXrResourcePlannerThreadScope(int stateIndex)
        => new(this, CreateLegacyOpenXrResourcePlannerContextKey(stateIndex));

    private OpenXrResourcePlannerThreadScope EnterOpenXrResourcePlannerThreadScope(in OpenXrViewResourcePlannerContextKey contextKey)
        => new(this, contextKey);

    private static int NormalizeOpenXrResourcePlannerStateIndex(int stateIndex)
        => (uint)stateIndex < OpenXrEyeResourcePlannerStateCount ? stateIndex : 0;

    private static OpenXrViewResourcePlannerContextKey CreateLegacyOpenXrResourcePlannerContextKey(int stateIndex)
    {
        int normalizedStateIndex = NormalizeOpenXrResourcePlannerStateIndex(stateIndex);
        uint legacyIndex = unchecked((uint)normalizedStateIndex);
        return new OpenXrViewResourcePlannerContextKey(
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
        => $"planner={key.ResourcePlannerStateIndex} eye={key.OpenXrViewIndex} imageIndex={key.OpenXrImageIndex} " +
           $"commandKey={key.CommandChainImageKey} frameSlot={key.FrameDataSlotIndex} foveationKey=0x{key.FoveationResourceKey:X} " +
           $"foveationAttachment={key.FoveationAttachmentKind} foveationOwned={key.FoveationAttachmentOwnedByResourcePlanner}";

    private readonly struct OpenXrResourcePlannerThreadScope : IDisposable
    {
        private readonly VulkanRenderer _renderer;
        private readonly OpenXrViewResourcePlannerContextKey _contextKey;
        private readonly ThreadResourcePlannerRuntimeStateScope _threadScope;
        private readonly ThreadFrameOpResourcePlannerSwitchingStateScope _frameOpThreadScope;

        public OpenXrResourcePlannerThreadScope(
            VulkanRenderer renderer,
            in OpenXrViewResourcePlannerContextKey contextKey)
        {
            _renderer = renderer;
            _contextKey = contextKey;
            ResourcePlannerRuntimeState openXrState;
            lock (renderer._openXrResourcePlannerStatesLock)
            {
                openXrState = renderer._openXrResourcePlannerStates.TryGetValue(_contextKey, out ResourcePlannerRuntimeState existingState)
                    ? existingState
                    : ResourcePlannerRuntimeState.CreateEmpty();
            }
            openXrState.FrameOpResourcePlannerSwitchingState ??= new FrameOpResourcePlannerSwitchingState();
            _threadScope = renderer.EnterThreadResourcePlannerRuntimeStateScope(in openXrState);
            _frameOpThreadScope = renderer.EnterThreadFrameOpResourcePlannerSwitchingStateScope(
                openXrState.FrameOpResourcePlannerSwitchingState);
            if (OpenXrVulkanTraceEnabled)
            {
                Debug.Vulkan(
                    "[OpenXrVulkan] enter thread planner context {0}",
                    DescribeOpenXrResourcePlannerContextKey(in _contextKey));
            }
        }

        public void Dispose()
        {
            ResourcePlannerRuntimeState state = _threadScope.CaptureCurrent(_renderer);
            state.FrameOpResourcePlannerSwitchingState = _frameOpThreadScope.CaptureCurrent(_renderer);
            lock (_renderer._openXrResourcePlannerStatesLock)
                _renderer._openXrResourcePlannerStates[_contextKey] = state;
            if (OpenXrVulkanTraceEnabled)
            {
                Debug.Vulkan(
                    "[OpenXrVulkan] leave thread planner context {0}",
                    DescribeOpenXrResourcePlannerContextKey(in _contextKey));
            }
            _frameOpThreadScope.Dispose();
            _threadScope.Dispose();
        }
    }

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
            Api!.CmdClearColorImage(commandBuffer, image, ImageLayout.TransferDstOptimal, ref clearColor, 1, ref range);

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

    private static ImageLayout ResolveOpenXrMirrorDestinationLayout(IVkImageDescriptorSource destinationSource)
    {
        ImageLayout layout = ImageLayout.Undefined;
        if (destinationSource is IVkFrameBufferAttachmentSource attachmentSource)
            layout = attachmentSource.GetAttachmentTrackedLayout(0, 0);

        if (layout == ImageLayout.Undefined)
            layout = destinationSource.TrackedImageLayout;

        return layout;
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
                BaseArrayLayer = 0,
                LayerCount = 1
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

    private void DrainRetiredResourcesIfSubmittedFrameSlotsCompleted()
    {
        if (_frameSlotTimelineValues is null)
        {
            DrainCompletedRecordedTextureUploadPublications();
            return;
        }

        for (int i = 0; i < _frameSlotTimelineValues.Length; i++)
        {
            ulong value = _frameSlotTimelineValues[i];
            if (value == 0)
                continue;

            if (HasTimelineValueCompleted(_graphicsTimelineSemaphore, value))
                continue;

            Debug.VulkanEvery(
                $"OpenXR.Vulkan.PendingFrameSlotDrainSkipped.{GetHashCode()}.{i}",
                TimeSpan.FromSeconds(1),
                "[OpenXR] Vulkan skipped retired-resource drain before eye rendering because frame slot {0} is still pending at timeline value {1}.",
                i,
                value);
            return;
        }

        ForceFlushCompletedNonImageRetiredResources();
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

        TrackLiveImageView(imageView, "OpenXR.SwapchainImageView");
        return imageView;
    }

    private OpenXrDepthTarget GetOrCreateOpenXrDepthTarget(Extent2D extent)
    {
        if (_openXrCachedDepthTarget.Image.Handle != 0 &&
            _openXrCachedDepthExtent.Width == extent.Width &&
            _openXrCachedDepthExtent.Height == extent.Height)
            return _openXrCachedDepthTarget;

        DestroyOpenXrDepthTarget(_openXrCachedDepthTarget);
        _openXrCachedDepthTarget = CreateOpenXrDepthTarget(extent);
        _openXrCachedDepthExtent = extent;
        return _openXrCachedDepthTarget;
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

        if (Api!.CreateImage(device, ref imageInfo, null, out Image depthImage) != Result.Success)
            throw new InvalidOperationException("Failed to create OpenXR Vulkan depth image.");

        VulkanMemoryAllocation allocation = AllocateImageMemoryWithFallback(depthImage, MemoryPropertyFlags.DeviceLocalBit);
        _imageAllocations[depthImage.Handle] = allocation;

        if (Api!.BindImageMemory(device, depthImage, allocation.Memory, allocation.Offset) != Result.Success)
        {
            _imageAllocations.TryRemove(depthImage.Handle, out _);
            FreeMemoryAllocation(allocation);
            Api!.DestroyImage(device, depthImage, null);
            throw new InvalidOperationException("Failed to bind OpenXR Vulkan depth image memory.");
        }

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

        if (Api!.CreateImageView(device, ref viewInfo, null, out ImageView depthView) != Result.Success)
        {
            _imageAllocations.TryRemove(depthImage.Handle, out VulkanMemoryAllocation removed);
            FreeMemoryAllocation(removed);
            Api!.DestroyImage(device, depthImage, null);
            throw new InvalidOperationException("Failed to create OpenXR Vulkan depth image view.");
        }

        TrackLiveImageView(depthView, "OpenXR.DepthTarget");
        return new OpenXrDepthTarget(depthImage, allocation.Memory, depthView, depthFormat, depthAspect);
    }

    private void DestroyOpenXrDepthTarget(OpenXrDepthTarget target)
    {
        if (target.View.Handle != 0 && TryBeginDestroyImageView(target.View, "DestroyOpenXrDepthTarget"))
            Api!.DestroyImageView(device, target.View, null);

        if (target.Image.Handle == 0)
            return;

        Api!.DestroyImage(device, target.Image, null);
        if (_imageAllocations.TryRemove(target.Image.Handle, out VulkanMemoryAllocation allocation))
            FreeMemoryAllocation(allocation);
        else if (target.Memory.Handle != 0)
            Api!.FreeMemory(device, target.Memory, null);
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

        DestroyOpenXrDepthTarget(_openXrCachedDepthTarget);
        _openXrCachedDepthTarget = default;
        _openXrCachedDepthExtent = default;

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
                                Api!.FreeCommandBuffers(device, ownerPool, 1, ref primary);
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
                                Api!.FreeCommandBuffers(device, ownerPool, 1, ref dynamicSecondary);
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
        WaitForAllInFlightWork();
        foreach (KeyValuePair<OpenXrViewResourcePlannerContextKey, ResourcePlannerRuntimeState> pair in states)
        {
            RestoreResourcePlannerRuntimeState(pair.Value);
            ReleaseDescriptorReferencesForPhysicalResourceDestruction(
                $"OpenXrResourcePlannerStateDestroy.{DescribeOpenXrResourcePlannerContextKey(pair.Key)}");
            DrainAllRetiredDescriptorPools();
            ResourceAllocator.DestroyPhysicalImages(this);
            ResourceAllocator.DestroyPhysicalBuffers(this);
        }

        RestoreResourcePlannerRuntimeState(previousState);
    }

    private bool SubmitAndWaitOpenXrCommandBuffer(CommandBuffer commandBuffer, out bool commandBufferCompleted)
    {
        CommandBuffer* commandBuffers = stackalloc CommandBuffer[1];
        commandBuffers[0] = commandBuffer;
        return SubmitAndWaitOpenXrCommandBuffers(commandBuffers, 1, out commandBufferCompleted);
    }

    private bool SubmitAndWaitOpenXrCommandBuffers(
        CommandBuffer firstCommandBuffer,
        CommandBuffer secondCommandBuffer,
        out bool commandBuffersCompleted)
    {
        CommandBuffer* commandBuffers = stackalloc CommandBuffer[2];
        commandBuffers[0] = firstCommandBuffer;
        commandBuffers[1] = secondCommandBuffer;
        return SubmitAndWaitOpenXrCommandBuffers(commandBuffers, 2, out commandBuffersCompleted);
    }

    private bool SubmitAndWaitOpenXrCommandBuffers(
        CommandBuffer* commandBuffers,
        uint commandBufferCount,
        out bool commandBufferCompleted)
    {
        commandBufferCompleted = false;
        if (commandBuffers is null || commandBufferCount == 0)
            return false;

        FenceCreateInfo fenceCreateInfo = new()
        {
            SType = StructureType.FenceCreateInfo,
            Flags = 0,
        };

        if (Api!.CreateFence(device, ref fenceCreateInfo, null, out Fence fence) != Result.Success)
            throw new InvalidOperationException("Failed to create OpenXR Vulkan submit fence.");

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
                lock (_oneTimeSubmitLock)
                    submitResult = SubmitToQueueTracked(graphicsQueue, ref submitInfo, fence);
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
                    MarkDeviceLost("OpenXR Vulkan eye fence wait returned ErrorDeviceLost");

                Debug.VulkanWarning($"[OpenXR] Vulkan eye fence wait failed: {waitResult}");
                return false;
            }

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
}
