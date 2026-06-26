using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.Diagnostics;
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

    private readonly record struct OpenXrDepthTarget(
        Image Image,
        DeviceMemory Memory,
        ImageView View,
        Format Format,
        ImageAspectFlags Aspect);

    private readonly record struct OpenXrSwapchainImageViewCacheEntry(ImageView View, Format Format);

    internal readonly record struct OpenXrEyeSwapchainRenderRequest(
        Image Image,
        Format Format,
        Extent2D Extent,
        int ResourcePlannerStateIndex,
        uint OpenXrViewIndex,
        uint OpenXrImageIndex,
        Action EmitFrameOps);

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

    private Image[]? _openXrSingleSwapchainImages;
    private ImageView[]? _openXrSingleSwapchainImageViews;
    private bool[]? _openXrSingleSwapchainImageEverPresented;
    private readonly Dictionary<ulong, OpenXrSwapchainImageViewCacheEntry> _openXrSwapchainImageViews = new();
    private readonly Dictionary<ulong, List<CommandBufferCacheVariant>> _openXrPrimaryCommandBufferVariants = new();
    private readonly List<VulkanImportedTexturePendingUpload> _openXrRecordedTextureUploadsForSubmit = new();
    private OpenXrDepthTarget _openXrCachedDepthTarget;
    private Extent2D _openXrCachedDepthExtent;
    private int _openXrExternalSwapchainRenderDepth;
    private BoundingRectangle _openXrExternalSwapchainTargetRegion;
    private int _openXrExternalSwapchainPrewarmDepth;
    private int _synchronousResourceUploadBlockDepth;
    private readonly ResourcePlannerRuntimeState[] _openXrResourcePlannerStates = new ResourcePlannerRuntimeState[OpenXrEyeResourcePlannerStateCount];
    private readonly bool[] _hasOpenXrResourcePlannerStates = new bool[OpenXrEyeResourcePlannerStateCount];

    public override bool IsRenderingExternalSwapchainTarget => _openXrExternalSwapchainRenderDepth > 0;
    internal bool IsPrewarmingOpenXrExternalSwapchainTarget => _openXrExternalSwapchainPrewarmDepth > 0;
    public override bool AllowSynchronousResourceUploads
        => _synchronousResourceUploadBlockDepth == 0;

    internal IDisposable BlockSynchronousResourceUploads(string reason)
    {
        _synchronousResourceUploadBlockDepth++;
        if (OpenXrVulkanTraceEnabled || DescriptorTraceEnabled)
            Debug.VulkanWarningEvery(
                $"Vulkan.SyncUploads.Blocked.{reason}.{GetHashCode()}",
                TimeSpan.FromSeconds(2),
                "[VulkanDescriptor] syncUploads=blocked reason={0} depth={1}",
                reason,
                _synchronousResourceUploadBlockDepth);

        return new SynchronousResourceUploadBlockScope(this);
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
        foreach (List<CommandBufferCacheVariant> variants in _openXrPrimaryCommandBufferVariants.Values)
        {
            for (int i = 0; i < variants.Count; i++)
                variants[i].Dirty = true;
        }
    }

    public override bool TryGetExternalSwapchainTargetRegion(out BoundingRectangle region)
    {
        if (_openXrExternalSwapchainRenderDepth > 0 &&
            _openXrExternalSwapchainTargetRegion.Width > 0 &&
            _openXrExternalSwapchainTargetRegion.Height > 0)
        {
            region = _openXrExternalSwapchainTargetRegion;
            return true;
        }

        if (_openXrExternalSwapchainRenderDepth > 0 &&
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
        BoundingRectangle previousRegion = _openXrExternalSwapchainTargetRegion;
        _openXrExternalSwapchainRenderDepth++;
        _openXrExternalSwapchainTargetRegion = new BoundingRectangle(
            0,
            0,
            (int)Math.Min(width, (uint)int.MaxValue),
            (int)Math.Min(height, (uint)int.MaxValue));

        return new OpenXrExternalSwapchainRenderScope(this, previousRegion);
    }

    internal bool TryRenderOpenXrEyeSwapchain(
        Image image,
        Format format,
        Extent2D extent,
        int resourcePlannerStateIndex,
        uint openXrViewIndex,
        uint openXrImageIndex,
        Action emitFrameOps)
    {
        var request = new OpenXrEyeSwapchainRenderRequest(
            image,
            format,
            extent,
            resourcePlannerStateIndex,
            openXrViewIndex,
            openXrImageIndex,
            emitFrameOps);

        _openXrRecordedTextureUploadsForSubmit.Clear();
        if (!TryRecordOpenXrEyeSwapchainCommandBuffer(request, out OpenXrRecordedEyeCommandBuffer recorded))
            return false;

        bool submitted = false;
        bool commandBufferCompleted = false;
        try
        {
            submitted = SubmitAndWaitOpenXrCommandBuffer(recorded.CommandBuffer, out commandBufferCompleted);
            if (submitted)
            {
                PublishRecordedTextureUploadsAfterCompletedSubmit(_openXrRecordedTextureUploadsForSubmit, "OpenXR eye");
                ForceFlushCompletedNonImageRetiredResources();
            }
            else if (!commandBufferCompleted && !IsDeviceLost)
            {
                CancelRecordedTextureUploads(_openXrRecordedTextureUploadsForSubmit, "OpenXR eye command buffer did not complete");
            }

            return submitted;
        }
        finally
        {
            if (!submitted && !commandBufferCompleted && !IsDeviceLost)
                CancelRecordedTextureUploads(_openXrRecordedTextureUploadsForSubmit, "OpenXR eye command buffer submit failed");

            FreeOpenXrRecordedEyeCommandBuffer(recorded);
            _openXrRecordedTextureUploadsForSubmit.Clear();
        }
    }

    internal bool TryRenderOpenXrEyeSwapchains(
        in OpenXrEyeSwapchainRenderRequest firstEye,
        in OpenXrEyeSwapchainRenderRequest secondEye)
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
            hasFirst = TryRecordOpenXrEyeSwapchainCommandBuffer(firstEye, out firstRecorded);
            if (!hasFirst)
                return false;

            hasSecond = TryRecordOpenXrEyeSwapchainCommandBuffer(secondEye, out secondRecorded);
            if (!hasSecond)
                return false;

            submitted = SubmitAndWaitOpenXrCommandBuffers(
                firstRecorded.CommandBuffer,
                secondRecorded.CommandBuffer,
                out commandBuffersCompleted);

            if (submitted)
            {
                PublishRecordedTextureUploadsAfterCompletedSubmit(_openXrRecordedTextureUploadsForSubmit, "OpenXR eye batch");
                ForceFlushCompletedNonImageRetiredResources();
            }
            else if (!commandBuffersCompleted && !IsDeviceLost)
            {
                CancelRecordedTextureUploads(_openXrRecordedTextureUploadsForSubmit, "OpenXR eye batch command buffers did not complete");
            }

            return submitted;
        }
        finally
        {
            if (!submitted && !commandBuffersCompleted && !IsDeviceLost)
                CancelRecordedTextureUploads(_openXrRecordedTextureUploadsForSubmit, "OpenXR eye batch command buffer submit failed");

            if (hasSecond)
                FreeOpenXrRecordedEyeCommandBuffer(secondRecorded);
            if (hasFirst)
                FreeOpenXrRecordedEyeCommandBuffer(firstRecorded);

            _openXrRecordedTextureUploadsForSubmit.Clear();
        }
    }

    private bool TryRecordOpenXrEyeSwapchainCommandBuffer(
        in OpenXrEyeSwapchainRenderRequest request,
        out OpenXrRecordedEyeCommandBuffer recorded)
    {
        recorded = default;
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

        CommandBuffer commandBuffer = default;
        bool commandBufferAllocated = false;
        bool drainedFrameOps = false;

        Image[]? previousSwapChainImages = swapChainImages;
        ImageView[]? previousSwapChainImageViews = swapChainImageViews;
        Framebuffer[]? previousSwapChainFramebuffers = swapChainFramebuffers;
        bool[]? previousSwapchainImageEverPresented = _swapchainImageEverPresented;
        Format previousSwapChainImageFormat = swapChainImageFormat;
        Extent2D previousSwapChainExtent = swapChainExtent;
        Image previousDepthImage = _swapchainDepthImage;
        DeviceMemory previousDepthMemory = _swapchainDepthMemory;
        ImageView previousDepthView = _swapchainDepthView;
        Format previousDepthFormat = _swapchainDepthFormat;
        ImageAspectFlags previousDepthAspect = _swapchainDepthAspect;
        _openXrExternalSwapchainRenderDepth++;
        int openXrFrameDataSlotCount = ResolveOpenXrFrameDataSlotCount(previousSwapChainImages?.Length ?? 0);
        uint recordImageIndex = ResolveOpenXrRecordImageIndex(
            request.ResourcePlannerStateIndex,
            previousSwapChainImages?.Length ?? 0);

        try
        {
            EnsureOpenXrFrameDataSlotCapacity(openXrFrameDataSlotCount);
            EnsureDescriptorFrameSlotFrameCountFloor(openXrFrameDataSlotCount);
            WaitForOpenXrFrameDataSlot(recordImageIndex, "eye swapchain render");
            DrainRetiredResourcesIfSubmittedFrameSlotsCompleted();
            DrainCompletedRecordedTextureUploadPublications();

            ImageView openXrImageView = GetOrCreateOpenXrSwapchainImageView(request.Image, request.Format);
            OpenXrDepthTarget depthTarget = GetOrCreateOpenXrDepthTarget(request.Extent);

            EnsureOpenXrSingleSwapchainSlotCapacity(OpenXrExternalSwapchainTargetImageIndex);
            _openXrSingleSwapchainImages![OpenXrExternalSwapchainTargetImageIndex] = request.Image;
            _openXrSingleSwapchainImageViews![OpenXrExternalSwapchainTargetImageIndex] = openXrImageView;
            _openXrSingleSwapchainImageEverPresented![OpenXrExternalSwapchainTargetImageIndex] = false;

            swapChainImages = _openXrSingleSwapchainImages;
            swapChainImageViews = _openXrSingleSwapchainImageViews;
            swapChainFramebuffers = null;
            _swapchainImageEverPresented = _openXrSingleSwapchainImageEverPresented;
            swapChainImageFormat = request.Format;
            swapChainExtent = request.Extent;
            _swapchainDepthImage = depthTarget.Image;
            _swapchainDepthMemory = depthTarget.Memory;
            _swapchainDepthView = depthTarget.View;
            _swapchainDepthFormat = depthTarget.Format;
            _swapchainDepthAspect = depthTarget.Aspect;

            using (EnterOpenXrResourcePlannerScope(request.ResourcePlannerStateIndex))
            {
                ResetDynamicUniformRingBuffer(recordImageIndex);
                request.EmitFrameOps();

                FrameOp[] ops = DrainFrameOpsExcludingTextureUploads(out _);
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

                ops = VulkanRenderGraphCompiler.SortFrameOps(ops, CompiledRenderGraph);
                _ = PrepareResourcePlannerForFrameOps(ops);
                ulong plannerRevision = ResourcePlannerRevision;
                ulong frameOpsSignature = ComputeFrameOpsSignature(ops);
                uint openXrCommandChainImageIndex = BuildOpenXrCommandChainImageIndex(
                    request.OpenXrViewIndex,
                    request.OpenXrImageIndex,
                    request.Image);
                CommandChainSchedule? commandChainSchedule = TryBuildOpenXrEyeCommandChainSchedule(
                    openXrCommandChainImageIndex,
                    request.OpenXrViewIndex,
                    request.OpenXrImageIndex,
                    request.Image,
                    ops,
                    frameOpsSignature,
                    plannerRevision);

                bool reusedPrimary = TryReuseOpenXrPrimaryCommandBuffer(
                    recordImageIndex,
                    openXrCommandChainImageIndex,
                    request,
                    ops,
                    frameOpsSignature,
                    plannerRevision,
                    commandChainSchedule,
                    out commandBuffer);

                if (!reusedPrimary)
                {
                    commandBuffer = RecordOpenXrPrimaryCommandBuffer(
                        recordImageIndex,
                        openXrCommandChainImageIndex,
                        request,
                        ops,
                        frameOpsSignature,
                        plannerRevision,
                        commandChainSchedule);
                }

                MoveRecordedTextureUploadsForSubmitTo(_openXrRecordedTextureUploadsForSubmit);
                if (OpenXrVulkanTraceEnabled)
                {
                    Debug.Vulkan(
                        "[OpenXrVulkan] eye={0} swapchainImage={1} commandBuffer=0x{2:X} cached={3} pendingUploads={4}",
                        request.OpenXrViewIndex,
                        request.OpenXrImageIndex,
                        commandBuffer.Handle,
                        reusedPrimary,
                        _openXrRecordedTextureUploadsForSubmit.Count);
                }

                recorded = new OpenXrRecordedEyeCommandBuffer(
                    commandBuffer,
                    request.OpenXrViewIndex,
                    request.OpenXrImageIndex,
                    OwnedByOpenXrPrimaryCache: true);
                commandBufferAllocated = false;
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
                "[OpenXR] Vulkan eye render failed: {0}",
                ex.Message);
            return false;
        }
        finally
        {
            _openXrExternalSwapchainRenderDepth--;

            swapChainImages = previousSwapChainImages;
            swapChainImageViews = previousSwapChainImageViews;
            swapChainFramebuffers = previousSwapChainFramebuffers;
            _swapchainImageEverPresented = previousSwapchainImageEverPresented;
            swapChainImageFormat = previousSwapChainImageFormat;
            swapChainExtent = previousSwapChainExtent;
            _swapchainDepthImage = previousDepthImage;
            _swapchainDepthMemory = previousDepthMemory;
            _swapchainDepthView = previousDepthView;
            _swapchainDepthFormat = previousDepthFormat;
            _swapchainDepthAspect = previousDepthAspect;

            if (commandBufferAllocated && commandBuffer.Handle != 0)
                Api!.FreeCommandBuffers(device, commandPool, 1, ref commandBuffer);
        }
    }

    private void EnsureOpenXrSingleSwapchainSlotCapacity(uint requiredIndex)
    {
        int requiredLength = Math.Max((int)requiredIndex + 1, OpenXrEyeResourcePlannerStateCount);
        if (_openXrSingleSwapchainImages is { Length: var imageLength } &&
            imageLength >= requiredLength &&
            _openXrSingleSwapchainImageViews is { Length: var viewLength } &&
            viewLength >= requiredLength &&
            _openXrSingleSwapchainImageEverPresented is { Length: var presentedLength } &&
            presentedLength >= requiredLength)
        {
            return;
        }

        int newLength = requiredLength;
        Array.Resize(ref _openXrSingleSwapchainImages, newLength);
        Array.Resize(ref _openXrSingleSwapchainImageViews, newLength);
        Array.Resize(ref _openXrSingleSwapchainImageEverPresented, newLength);
    }

    private bool TryReuseOpenXrPrimaryCommandBuffer(
        uint recordImageIndex,
        uint commandChainImageIndex,
        in OpenXrEyeSwapchainRenderRequest request,
        FrameOp[] ops,
        ulong frameOpsSignature,
        ulong plannerRevision,
        CommandChainSchedule? commandChainSchedule,
        out CommandBuffer commandBuffer)
    {
        commandBuffer = default;
        if (!OpenXrVulkanPrimaryReuseEnabled)
            return false;

        ulong cacheKey = BuildOpenXrPrimaryCommandBufferCacheKey(commandChainImageIndex, request);
        if (!_openXrPrimaryCommandBufferVariants.TryGetValue(cacheKey, out List<CommandBufferCacheVariant>? variants))
            return false;

        bool gpuPipelineProfilingActive =
            IsVulkanGpuProfilerCommandBufferInstrumentationEnabled &&
            RenderPipelineGpuProfiler.Instance.IsProfilingActive;
        int commandBufferImageSlot = unchecked((int)Math.Min(recordImageIndex, int.MaxValue));
        ulong commandChainPrimaryGroupSignature = 0;
        int commandChainPrimaryGroupCount = 0;
        if (commandChainSchedule is not null)
        {
            Dictionary<CommandChainKey, CommandChain> commandChainCache = GetCommandChainCache(commandChainImageIndex);
            commandChainPrimaryGroupSignature = ComputeOpenXrPrimaryCommandBufferGroupHandleSignature(commandChainSchedule, commandChainCache);
            commandChainPrimaryGroupCount = commandChainSchedule.Groups.Length;
        }

        for (int i = 0; i < variants.Count; i++)
        {
            CommandBufferCacheVariant variant = variants[i];
            if (variant.Dirty ||
                variant.PrimaryCommandBuffer.Handle == 0 ||
                variant.FrameOpsSignature != frameOpsSignature ||
                variant.PlannerRevision != plannerRevision ||
                variant.CommandChainScheduleSignature != (commandChainSchedule?.StructuralSignature ?? ulong.MaxValue) ||
                variant.CommandChainPrimaryGroupSignature != (commandChainSchedule is null ? ulong.MaxValue : commandChainPrimaryGroupSignature) ||
                variant.CommandChainPrimaryGroupCount != (commandChainSchedule is null ? -1 : commandChainPrimaryGroupCount) ||
                IsCommandBufferVariantGpuProfilerStateDirty(variant, gpuPipelineProfilingActive, commandBufferImageSlot))
            {
                continue;
            }

            _lastReusableFrameDataRefreshFailureReason = null;
            if (!TryRefreshReusableCommandBufferFrameData(recordImageIndex, ops))
                return false;

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
                    request.OpenXrViewIndex,
                    request.OpenXrImageIndex,
                    commandChainImageIndex,
                    recordImageIndex,
                    commandBuffer.Handle);
            }

            return true;
        }

        return false;
    }

    private CommandBuffer RecordOpenXrPrimaryCommandBuffer(
        uint recordImageIndex,
        uint commandChainImageIndex,
        in OpenXrEyeSwapchainRenderRequest request,
        FrameOp[] ops,
        ulong frameOpsSignature,
        ulong plannerRevision,
        CommandChainSchedule? commandChainSchedule)
    {
        ulong cacheKey = BuildOpenXrPrimaryCommandBufferCacheKey(commandChainImageIndex, request);
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
        if (commandChainSchedule is not null)
        {
            Dictionary<CommandChainKey, CommandChain> commandChainCache = GetCommandChainCache(commandChainImageIndex);
            commandChainPrimaryGroupSignature = ComputeOpenXrPrimaryCommandBufferGroupHandleSignature(commandChainSchedule, commandChainCache);
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
                    "[OpenXrVulkan] record primary eye={0} swapchainImage={1} image=0x{2:X} commandKey={3} targetSlot={4} frameSlot={5} extent={6}x{7} ops={8}",
                    request.OpenXrViewIndex,
                    request.OpenXrImageIndex,
                    request.Image.Handle,
                    commandChainImageIndex,
                    OpenXrExternalSwapchainTargetImageIndex,
                    recordImageIndex,
                    request.Extent.Width,
                    request.Extent.Height,
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
                frameDataImageIndexOverride: recordImageIndex);
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
        variant.RecordedSwapchainImageEverPresented = IsSwapchainImageEverPresented(OpenXrExternalSwapchainTargetImageIndex);
        variant.RecordedSwapchainFinalLayout = swapchainLayoutAfterCommandBuffer;
        variant.RecordedSwapchainWriteCount = recordedSwapchainWriteCount;
        variant.CommandChainScheduleSignature = commandChainSchedule?.StructuralSignature ?? ulong.MaxValue;
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
                "[OpenXrVulkan] recorded primary eye={0} swapchainImage={1} commandKey={2} recorderSlot={3} commandBuffer=0x{4:X} recordMs={5:F3}",
                request.OpenXrViewIndex,
                request.OpenXrImageIndex,
                commandChainImageIndex,
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
    {
        if (!_openXrPrimaryCommandBufferVariants.TryGetValue(cacheKey, out List<CommandBufferCacheVariant>? variants))
        {
            variants = [];
            _openXrPrimaryCommandBufferVariants[cacheKey] = variants;
        }

        ulong scheduleSignature = commandChainSchedule?.StructuralSignature ?? ulong.MaxValue;
        ulong groupSignature = ulong.MaxValue;
        int groupCount = -1;
        if (commandChainSchedule is not null)
        {
            Dictionary<CommandChainKey, CommandChain> commandChainCache = GetCommandChainCache(commandChainImageIndex);
            groupSignature = ComputeOpenXrPrimaryCommandBufferGroupHandleSignature(commandChainSchedule, commandChainCache);
            groupCount = commandChainSchedule.Groups.Length;
        }

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

        CommandBuffer primary = AllocateCommandBuffer(CommandBufferLevel.Primary, "OpenXR eye primary command buffer variant");
        RegisterCommandBufferImageIndex(primary, recordImageIndex);
        CommandBufferCacheVariant created = new(
            primary,
            dynamicUiSecondaryCommandBuffer: default,
            ownsPrimaryCommandBuffer: true,
            ownsDynamicUiSecondaryCommandBuffer: false);
        variants.Add(created);
        return created;
    }

    private static ulong BuildOpenXrPrimaryCommandBufferCacheKey(
        uint commandChainImageIndex,
        in OpenXrEyeSwapchainRenderRequest request)
    {
        HashCode hash = new();
        hash.Add(0x53574150);
        hash.Add(commandChainImageIndex);
        hash.Add(request.Image.Handle);
        hash.Add((int)request.Format);
        hash.Add(request.Extent.Width);
        hash.Add(request.Extent.Height);
        hash.Add(request.OpenXrViewIndex);
        hash.Add(request.OpenXrImageIndex);
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
                hash.Add(chains.TryGetValue(key, out CommandChain? chain)
                    ? chain.SecondaryCommandBuffer.Handle
                    : 0UL);
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
            {
                return false;
            }

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

            using (EnterOpenXrResourcePlannerScope(request.ResourcePlannerStateIndex))
            {
                request.EmitFrameOps();

                FrameOp[] ops = DrainFrameOpsExcludingTextureUploads(out _);
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
            return false;

        ulong cacheKey = BuildOpenXrMirrorPrimaryCommandBufferCacheKey(commandChainImageIndex, request);
        if (!_openXrPrimaryCommandBufferVariants.TryGetValue(cacheKey, out List<CommandBufferCacheVariant>? variants))
            return false;

        bool gpuPipelineProfilingActive =
            IsVulkanGpuProfilerCommandBufferInstrumentationEnabled &&
            RenderPipelineGpuProfiler.Instance.IsProfilingActive;
        int commandBufferImageSlot = unchecked((int)Math.Min(recordImageIndex, int.MaxValue));
        ulong commandChainPrimaryGroupSignature = ulong.MaxValue;
        int commandChainPrimaryGroupCount = -1;
        if (commandChainSchedule is not null)
        {
            Dictionary<CommandChainKey, CommandChain> commandChainCache = GetCommandChainCache(commandChainImageIndex);
            commandChainPrimaryGroupSignature = ComputeOpenXrPrimaryCommandBufferGroupHandleSignature(commandChainSchedule, commandChainCache);
            commandChainPrimaryGroupCount = commandChainSchedule.Groups.Length;
        }

        for (int i = 0; i < variants.Count; i++)
        {
            CommandBufferCacheVariant variant = variants[i];
            if (variant.Dirty ||
                variant.PrimaryCommandBuffer.Handle == 0 ||
                variant.FrameOpsSignature != frameOpsSignature ||
                variant.PlannerRevision != plannerRevision ||
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

        return false;
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
        if (commandChainSchedule is not null)
        {
            Dictionary<CommandChainKey, CommandChain> commandChainCache = GetCommandChainCache(commandChainImageIndex);
            commandChainPrimaryGroupSignature = ComputeOpenXrPrimaryCommandBufferGroupHandleSignature(commandChainSchedule, commandChainCache);
            commandChainPrimaryGroupCount = commandChainSchedule.Groups.Length;
        }

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
            {
                return false;
            }

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

        Image[]? previousSwapChainImages = swapChainImages;
        ImageView[]? previousSwapChainImageViews = swapChainImageViews;
        Framebuffer[]? previousSwapChainFramebuffers = swapChainFramebuffers;
        bool[]? previousSwapchainImageEverPresented = _swapchainImageEverPresented;
        Format previousSwapChainImageFormat = swapChainImageFormat;
        Extent2D previousSwapChainExtent = swapChainExtent;
        Image previousDepthImage = _swapchainDepthImage;
        DeviceMemory previousDepthMemory = _swapchainDepthMemory;
        ImageView previousDepthView = _swapchainDepthView;
        Format previousDepthFormat = _swapchainDepthFormat;
        ImageAspectFlags previousDepthAspect = _swapchainDepthAspect;
        int openXrFrameDataSlotCount = ResolveOpenXrFrameDataSlotCount(previousSwapChainImages?.Length ?? 0);

        _openXrExternalSwapchainRenderDepth++;
        _openXrExternalSwapchainPrewarmDepth++;

        try
        {
            EnsureOpenXrFrameDataSlotCapacity(openXrFrameDataSlotCount);
            EnsureDescriptorFrameSlotFrameCountFloor(openXrFrameDataSlotCount);
            DrainRetiredResourcesIfSubmittedFrameSlotsCompleted();

            OpenXrDepthTarget depthTarget = GetOrCreateOpenXrDepthTarget(extent);

            _openXrSingleSwapchainImages ??= new Image[1];
            _openXrSingleSwapchainImageViews ??= new ImageView[1];
            _openXrSingleSwapchainImageEverPresented ??= new bool[1];
            _openXrSingleSwapchainImages[0] = default;
            _openXrSingleSwapchainImageViews[0] = default;
            _openXrSingleSwapchainImageEverPresented[0] = false;

            swapChainImages = _openXrSingleSwapchainImages;
            swapChainImageViews = _openXrSingleSwapchainImageViews;
            swapChainFramebuffers = null;
            _swapchainImageEverPresented = _openXrSingleSwapchainImageEverPresented;
            swapChainImageFormat = format;
            swapChainExtent = extent;
            _swapchainDepthImage = depthTarget.Image;
            _swapchainDepthMemory = depthTarget.Memory;
            _swapchainDepthView = depthTarget.View;
            _swapchainDepthFormat = depthTarget.Format;
            _swapchainDepthAspect = depthTarget.Aspect;

            using (EnterOpenXrResourcePlannerScope(resourcePlannerStateIndex))
            {
                emitFrameOps();

                FrameOp[] ops = DrainFrameOpsExcludingTextureUploads(out _);
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
            _openXrExternalSwapchainRenderDepth--;

            swapChainImages = previousSwapChainImages;
            swapChainImageViews = previousSwapChainImageViews;
            swapChainFramebuffers = previousSwapChainFramebuffers;
            _swapchainImageEverPresented = previousSwapchainImageEverPresented;
            swapChainImageFormat = previousSwapChainImageFormat;
            swapChainExtent = previousSwapChainExtent;
            _swapchainDepthImage = previousDepthImage;
            _swapchainDepthMemory = previousDepthMemory;
            _swapchainDepthView = previousDepthView;
            _swapchainDepthFormat = previousDepthFormat;
            _swapchainDepthAspect = previousDepthAspect;
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

            using (EnterOpenXrResourcePlannerScope(resourcePlannerStateIndex))
            {
                emitFrameOps();

                FrameOp[] ops = DrainFrameOpsExcludingTextureUploads(out _);
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

    private OpenXrResourcePlannerScope EnterOpenXrResourcePlannerScope(int stateIndex)
        => new(this, stateIndex);

    private static int NormalizeOpenXrResourcePlannerStateIndex(int stateIndex)
        => (uint)stateIndex < OpenXrEyeResourcePlannerStateCount ? stateIndex : 0;

    private readonly struct OpenXrResourcePlannerScope : IDisposable
    {
        private readonly VulkanRenderer _renderer;
        private readonly ResourcePlannerRuntimeState _previousState;
        private readonly int _stateIndex;

        public OpenXrResourcePlannerScope(VulkanRenderer renderer, int stateIndex)
        {
            _renderer = renderer;
            _stateIndex = NormalizeOpenXrResourcePlannerStateIndex(stateIndex);
            _previousState = renderer.CaptureResourcePlannerRuntimeState();
            ResourcePlannerRuntimeState openXrState = renderer._hasOpenXrResourcePlannerStates[_stateIndex]
                ? renderer._openXrResourcePlannerStates[_stateIndex]
                : ResourcePlannerRuntimeState.CreateEmpty();
            renderer.RestoreResourcePlannerRuntimeState(openXrState);
        }

        public void Dispose()
        {
            _renderer._openXrResourcePlannerStates[_stateIndex] = _renderer.CaptureResourcePlannerRuntimeState();
            _renderer._hasOpenXrResourcePlannerStates[_stateIndex] = true;
            _renderer.RestoreResourcePlannerRuntimeState(_previousState);
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
        {
            return;
        }

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

            if (cached.View.Handle != 0)
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

        return new OpenXrDepthTarget(depthImage, allocation.Memory, depthView, depthFormat, depthAspect);
    }

    private void DestroyOpenXrDepthTarget(OpenXrDepthTarget target)
    {
        if (target.View.Handle != 0)
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
        DestroyOpenXrPrimaryCommandBufferCache();
        DestroyOpenXrResourcePlannerState();

        foreach (OpenXrSwapchainImageViewCacheEntry entry in _openXrSwapchainImageViews.Values)
            if (entry.View.Handle != 0)
                Api!.DestroyImageView(device, entry.View, null);
        
        _openXrSwapchainImageViews.Clear();

        DestroyOpenXrDepthTarget(_openXrCachedDepthTarget);
        _openXrCachedDepthTarget = default;
        _openXrCachedDepthExtent = default;

        if (_openXrSingleSwapchainImages is not null)
            Array.Clear(_openXrSingleSwapchainImages);
        if (_openXrSingleSwapchainImageViews is not null)
            Array.Clear(_openXrSingleSwapchainImageViews);
        if (_openXrSingleSwapchainImageEverPresented is not null)
            Array.Clear(_openXrSingleSwapchainImageEverPresented);
    }

    private void DestroyOpenXrPrimaryCommandBufferCache()
    {
        if (_openXrPrimaryCommandBufferVariants.Count == 0)
            return;

        foreach (List<CommandBufferCacheVariant> variants in _openXrPrimaryCommandBufferVariants.Values)
        {
            for (int i = 0; i < variants.Count; i++)
            {
                CommandBufferCacheVariant variant = variants[i];
                CommandBuffer primary = variant.PrimaryCommandBuffer;
                if (primary.Handle != 0)
                {
                    if (variant.OwnsPrimaryCommandBuffer && !_deviceLost)
                        Api!.FreeCommandBuffers(device, commandPool, 1, ref primary);
                    RemoveCommandBufferBindState(variant.PrimaryCommandBuffer);
                }

                CommandBuffer dynamicSecondary = variant.DynamicUiSecondaryCommandBuffer;
                if (dynamicSecondary.Handle != 0)
                {
                    if (variant.OwnsDynamicUiSecondaryCommandBuffer && !_deviceLost)
                        Api!.FreeCommandBuffers(device, commandPool, 1, ref dynamicSecondary);
                    RemoveCommandBufferBindState(variant.DynamicUiSecondaryCommandBuffer);
                }
            }
        }

        _openXrPrimaryCommandBufferVariants.Clear();
    }

    private void DestroyOpenXrResourcePlannerState()
    {
        bool hasAnyState = false;
        for (int i = 0; i < _hasOpenXrResourcePlannerStates.Length; i++)
            hasAnyState |= _hasOpenXrResourcePlannerStates[i];

        if (!hasAnyState)
            return;

        ResourcePlannerRuntimeState previousState = CaptureResourcePlannerRuntimeState();
        WaitForAllInFlightWork();
        for (int i = 0; i < _openXrResourcePlannerStates.Length; i++)
        {
            if (!_hasOpenXrResourcePlannerStates[i])
                continue;

            RestoreResourcePlannerRuntimeState(_openXrResourcePlannerStates[i]);
            ReleaseDescriptorReferencesForPhysicalResourceDestruction($"OpenXrResourcePlannerStateDestroy.eye{i}");
            DrainAllRetiredDescriptorPools();
            _resourceAllocator.DestroyPhysicalImages(this);
            _resourceAllocator.DestroyPhysicalBuffers(this);
            _openXrResourcePlannerStates[i] = default;
            _hasOpenXrResourcePlannerStates[i] = false;
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
