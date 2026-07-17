using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using Silk.NET.Vulkan;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.Resources;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private const int MaxFrameOpResourcePlannerSwitchingStates = 12;
    private static bool FrameOpResourcePlannerSwitchingEnabled => MaxFrameOpResourcePlannerSwitchingStates > 1;

    private void OnSwapchainExtentChanged(Extent2D extent)
    {
        ActiveState.SetSwapchainExtent(extent);
        if (ActiveBoundDrawFrameBuffer is null)
            ActiveState.SetCurrentTargetExtent(extent);
        MarkCommandBuffersDirty();
    }

    private void UpdateResourcePlannerFromPipeline()
    {
        UpdateResourcePlannerFromContext(CaptureFrameOpContext());
    }

    /// <summary>
    /// Represents the different kinds of frame operation contexts in the Vulkan renderer.
    /// </summary>
    internal enum EVulkanFrameOpContextKind
    {
        /// <summary>
        /// The context kind is unknown.
        /// </summary>
        Unknown = 0,
        /// <summary>
        /// The main viewport context.
        /// </summary>
        MainViewport = 1,
        /// <summary>
        /// The OpenXR eye context.
        /// </summary>
        OpenXrEye = 2,
        /// <summary>
        /// The OpenXR mirror context.
        /// </summary>
        OpenXrMirror = 3,
        /// <summary>
        /// The scene capture context.
        /// </summary>
        SceneCapture = 4,
        /// <summary>
        /// The light probe capture context.
        /// </summary>
        LightProbeCapture = 5,
        /// <summary>
        /// The shadow context.
        /// </summary>
        Shadow = 6,
        /// <summary>
        /// The UI preview context.
        /// </summary>
        UiPreview = 7,
        /// <summary>
        /// The diagnostic capture context.
        /// </summary>
        DiagnosticCapture = 8,
    }

    private long _frameOpContextId;

    /// <summary>
    /// Represents the context of a frame operation in the Vulkan renderer.
    /// Contains information about the current frame operation, including pipeline, viewport, and rendering targets.
    /// </summary>
    /// <param name="PipelineIdentity">The identity of the rendering pipeline.</param>
    /// <param name="ViewportIdentity">The identity of the viewport.</param>
    /// <param name="PipelineInstance">The instance of the rendering pipeline.</param>
    /// <param name="ResourceRegistry">The resource registry for the current frame operation.</param>
    /// <param name="PassMetadata">The metadata for the render passes.</param>
    /// <param name="DisplayWidth">The width of the display.</param>
    /// <param name="DisplayHeight">The height of the display.</param>
    /// <param name="InternalWidth">The internal width used for rendering.</param>
    /// <param name="InternalHeight">The internal height used for rendering.</param>
    /// <param name="OutputFrameBufferName">The name of the output frame buffer.</param>
    /// <param name="PreserveSubmissionOrderBlock">Indicates whether to preserve the submission order block.</param>
    /// <param name="OutputTargetIdentity">The identity of the output target.</param>
    /// <param name="OutputTargetName">The name of the output target.</param>
    /// <param name="OutputFrameBufferIdentity">The identity of the output frame buffer.</param>
    /// <param name="ContextKind">The kind of the frame operation context.</param>
    /// <param name="ContextId">The unique identifier for the context.</param>
    /// <param name="RecordingFingerprint">The recording fingerprint for the frame operation.</param>
    /// <param name="SubmissionQueueFamily">The submission queue family index.</param>
    /// <param name="StereoEnabled">Indicates whether stereo rendering is enabled.</param>
    /// <param name="MultiviewEnabled">Indicates whether multiview rendering is enabled.</param>
    /// <param name="ResourceGeneration">The resource generation number.</param>
    /// <param name="DescriptorGeneration">The descriptor generation number.</param>
    /// <param name="OutputFrameBuffer">The output frame buffer.</param>
    /// <param name="ResourceRegistrySignatureSnapshot">The immutable registry descriptor signature captured for this operation.</param>
    internal readonly record struct FrameOpContext(
        int PipelineIdentity,
        int ViewportIdentity,
        XRRenderPipelineInstance? PipelineInstance,
        RenderResourceRegistry? ResourceRegistry,
        IReadOnlyCollection<RenderPassMetadata>? PassMetadata,
        uint DisplayWidth = 1u,
        uint DisplayHeight = 1u,
        uint InternalWidth = 1u,
        uint InternalHeight = 1u,
        string? OutputFrameBufferName = null,
        bool PreserveSubmissionOrderBlock = false,
        int OutputTargetIdentity = 0,
        string? OutputTargetName = null,
        int OutputFrameBufferIdentity = 0,
        EVulkanFrameOpContextKind ContextKind = EVulkanFrameOpContextKind.Unknown,
        ulong ContextId = 0,
        ulong RecordingFingerprint = ulong.MaxValue,
        uint SubmissionQueueFamily = 0,
        bool StereoEnabled = false,
        bool MultiviewEnabled = false,
        ulong ResourceGeneration = 0,
        ulong DescriptorGeneration = 0,
        XRFrameBuffer? OutputFrameBuffer = null,
        int? ResourceRegistrySignatureSnapshot = null)
    {
        public int SchedulingIdentity => OutputTargetIdentity == 0
            ? HashCode.Combine(PipelineIdentity, ViewportIdentity)
            : HashCode.Combine(PipelineIdentity, ViewportIdentity, OutputTargetIdentity);
    }

    internal FrameOpContext CaptureFrameOpContext()
    {
        XRRenderPipelineInstance? pipeline = RuntimeEngine.Rendering.State.CurrentRenderingPipeline;
        XRViewport? viewport = RuntimeEngine.Rendering.State.RenderingViewport;
        uint displayWidth;
        uint displayHeight;
        uint internalWidth;
        uint internalHeight;
        int outputTargetIdentity = 0;
        string? outputTargetName = null;
        if (TryResolveExternalSwapchainTargetExtent(out Extent2D externalExtent))
        {
            var dimensions = ResolveExternalFrameOpResourceDimensions(
                externalExtent,
                pipeline?.ResourceInternalWidth,
                pipeline?.ResourceInternalHeight,
                viewport?.InternalWidth,
                viewport?.InternalHeight);
            displayWidth = dimensions.DisplayWidth;
            displayHeight = dimensions.DisplayHeight;
            internalWidth = dimensions.InternalWidth;
            internalHeight = dimensions.InternalHeight;
            TryGetExternalSwapchainTargetIdentity(out outputTargetIdentity, out outputTargetName);
        }
        else
        {
            Extent2D fallbackExtent = ResolveFrameOpContextFallbackExtent();
            displayWidth = ResolvePositiveDimension(
                pipeline?.ResourceDisplayWidth,
                viewport?.Width,
                fallbackExtent.Width,
                1u);
            displayHeight = ResolvePositiveDimension(
                pipeline?.ResourceDisplayHeight,
                viewport?.Height,
                fallbackExtent.Height,
                1u);
            internalWidth = ResolvePositiveDimension(
                pipeline?.ResourceInternalWidth,
                viewport?.InternalWidth,
                displayWidth,
                1u);
            internalHeight = ResolvePositiveDimension(
                pipeline?.ResourceInternalHeight,
                viewport?.InternalHeight,
                displayHeight,
                1u);
        }

        XRFrameBuffer? outputFrameBuffer = ResolveFrameOpOutputFrameBuffer(pipeline, viewport);
        ApplyOutputFrameBufferTargetIdentity(outputFrameBuffer, ref outputTargetIdentity, ref outputTargetName);

        FrameOpContext context = new(
            pipeline?.InstanceId ?? 0,
            viewport is null ? 0 : RuntimeHelpers.GetHashCode(viewport),
            pipeline,
            pipeline?.Resources,
            pipeline?.Pipeline?.PassMetadata,
            displayWidth,
            displayHeight,
            internalWidth,
            internalHeight,
            outputFrameBuffer?.Name,
            ShouldPreserveSubmissionOrderBlock(),
            outputTargetIdentity,
            outputTargetName);
        context = ApplyInteractiveResizePlannerFreeze(context) with { OutputFrameBuffer = outputFrameBuffer };
        context = CompleteFrameOpContext(context);

        if (pipeline is not null)
            ActiveLastActiveFrameOpContext = context;

        return context;
    }

    private FrameOpContext ApplyInteractiveResizePlannerFreeze(in FrameOpContext context)
    {
        if (TryResolveExternalSwapchainTargetExtent(out _))
            return context;

        if (!XRWindow.IsInteractiveResizeInProgress)
        {
            ResetInteractiveResizePlannerFreeze();
            return context;
        }

        if (!_interactiveResizePlannerFrozen)
        {
            CaptureInteractiveResizePlannerExtents(context);

            Debug.Vulkan(
                "[VulkanResourcePlanner] Freezing render-resource extents during interactive resize at {0}x{1}/{2}x{3}.",
                _interactiveResizeFrozenDisplayWidth,
                _interactiveResizeFrozenDisplayHeight,
                _interactiveResizeFrozenInternalWidth,
                _interactiveResizeFrozenInternalHeight);
        }
        return RefreshFrameOpContextRecordingFingerprint(context with
        {
            DisplayWidth = _interactiveResizeFrozenDisplayWidth,
            DisplayHeight = _interactiveResizeFrozenDisplayHeight,
            InternalWidth = _interactiveResizeFrozenInternalWidth,
            InternalHeight = _interactiveResizeFrozenInternalHeight
        });
    }

    private void CaptureInteractiveResizePlannerExtents(in FrameOpContext context)
    {
        _interactiveResizeFrozenDisplayWidth = context.DisplayWidth;
        _interactiveResizeFrozenDisplayHeight = context.DisplayHeight;
        _interactiveResizeFrozenInternalWidth = context.InternalWidth;
        _interactiveResizeFrozenInternalHeight = context.InternalHeight;
        _interactiveResizePlannerFrozen = true;
    }

    private void ResetInteractiveResizePlannerFreeze()
    {
        _interactiveResizePlannerFrozen = false;
        _interactiveResizeFrozenDisplayWidth = 0;
        _interactiveResizeFrozenDisplayHeight = 0;
        _interactiveResizeFrozenInternalWidth = 0;
        _interactiveResizeFrozenInternalHeight = 0;
    }

    internal FrameOpContext CaptureFrameOpContextOrLastActive()
    {
        FrameOpContext context = CaptureFrameOpContext();
        return context.PipelineInstance is not null || context.PassMetadata is { Count: > 0 }
            ? context
            : ActiveLastActiveFrameOpContext ?? context;
    }

    public IDisposable EnterPipelineResourcePlannerReadbackScope(
        XRRenderPipelineInstance pipeline,
        XRViewport? viewport)
    {
        if (pipeline is null)
            throw new ArgumentNullException(nameof(pipeline));

        FrameOpContext context = CreateFrameOpContext(pipeline, viewport);
        return new ExternalResourcePlannerReadbackScope(this, context);
    }

    internal override IDisposable? EnterRenderPipelineFrameResourceScope(
        XRRenderPipelineInstance pipeline,
        XRViewport? viewport)
    {
        if (pipeline is null)
            return null;

        FrameOpContext context = CreateFrameOpContext(pipeline, viewport);
        return !FrameOpContextHasPlannerResources(context) ? null : new ExternalResourcePlannerReadbackScope(this, context);
    }

    internal override bool TryPrepareRenderResourceGeneration(
        XRRenderPipelineInstance pipeline,
        RenderResourceGeneration generation,
        XRViewport? viewport,
        out IRenderResourceGenerationTransaction? transaction,
        out string? failureReason)
    {
        transaction = null;
        failureReason = null;
        if (!IsDeviceOperational)
        {
            failureReason = "Vulkan device is not operational.";
            return false;
        }

        if (generation.Registry.TextureRecords.Count == 0 &&
            generation.Registry.BufferRecords.Count == 0 &&
            generation.Registry.FrameBufferRecords.Count == 0)
        {
            return true;
        }

        ResourcePlannerRuntimeState previousState = CaptureResourcePlannerRuntimeState();
        ResourcePlannerRuntimeState pendingState = ResourcePlannerRuntimeState.CreateEmpty();
        FrameOpContext context = CreateFrameOpContext(pipeline, viewport) with
        {
            ResourceRegistry = generation.Registry,
            DisplayWidth = generation.Key.DisplayWidth,
            DisplayHeight = generation.Key.DisplayHeight,
            InternalWidth = generation.Key.InternalWidth,
            InternalHeight = generation.Key.InternalHeight,
            ResourceGeneration = unchecked((ulong)Math.Max(pipeline.ResourceGeneration + 1, 0)),
            DescriptorGeneration = ResolveFrameOpContextDescriptorGeneration(generation.Registry),
            ResourceRegistrySignatureSnapshot = ComputeResourceRegistrySignature(generation.Registry),
        };
        context = RefreshFrameOpContextRecordingFingerprint(context);

        using (ThreadResourcePlannerRuntimeStateScope scope = EnterThreadResourcePlannerRuntimeStateScope(in pendingState))
        {
            try
            {
                UpdateResourcePlannerFromContext(context);
                pendingState = scope.CaptureCurrent(this);
                pendingState.LastActiveFrameOpContext = context;

                if (!ValidatePreparedResourceAllocator(pendingState.ResourcePlanner, pendingState.ResourceAllocator, out failureReason))
                {
                    _ = pendingState.ResourceAllocator.TryRetirePhysicalResources(this, immediate: true);
                    return false;
                }

                foreach (RenderFrameBufferResource record in generation.Registry.FrameBufferRecords.Values)
                {
                    if (record.Instance is not null && record.HasAttachments)
                        GetOrCreateAPIRenderObject(record.Instance, generateNow: true);
                }

                pendingState = scope.CaptureCurrent(this);
                pendingState.LastActiveFrameOpContext = context;
                transaction = new VulkanRenderResourceGenerationTransaction(
                    this,
                    previousState,
                    pendingState,
                    BuildFrameOpPlannerStateKey(context));
                return true;
            }
            catch (Exception ex)
            {
                pendingState = scope.CaptureCurrent(this);
                if (!pendingState.ResourceAllocator.IsRetired)
                    _ = pendingState.ResourceAllocator.TryRetirePhysicalResources(this, immediate: true);
                failureReason = $"Vulkan generation preparation failed: {ex.Message}";
                return false;
            }
        }
    }

    private static bool ValidatePreparedResourceAllocator(
        VulkanResourcePlanner planner,
        VulkanResourceAllocator allocator,
        out string? failureReason)
    {
        foreach (VulkanAllocationRequest request in planner.CurrentPlan.AllTextures())
        {
            if (request.Lifetime == RenderResourceLifetime.External)
                continue;

            if (!allocator.TryGetPhysicalGroupForResource(request.Name, out VulkanPhysicalImageGroup? group) ||
                group?.IsAllocated != true)
            {
                failureReason = $"Vulkan image '{request.Name}' was not allocated for the pending generation.";
                return false;
            }
        }

        foreach (VulkanBufferAllocationRequest request in planner.CurrentPlan.AllBuffers())
        {
            if (request.Lifetime == RenderResourceLifetime.External)
                continue;

            if (!allocator.TryGetPhysicalBufferGroupForResource(request.Name, out VulkanPhysicalBufferGroup? group) ||
                group?.IsAllocated != true)
            {
                failureReason = $"Vulkan buffer '{request.Name}' was not allocated for the pending generation.";
                return false;
            }
        }

        failureReason = null;
        return true;
    }

    private sealed class VulkanRenderResourceGenerationTransaction(
        VulkanRenderer renderer,
        ResourcePlannerRuntimeState previousState,
        ResourcePlannerRuntimeState pendingState,
        FrameOpPlannerStateKey pendingKey) : IRenderResourceGenerationTransaction
    {
        private bool _committed;

        public void Commit()
        {
            if (_committed)
                return;

            pendingState.ResourceAllocator.CommitReusedPhysicalImageMetadata();
            renderer.RestoreResourcePlannerRuntimeState(pendingState);
            FrameOpResourcePlannerSwitchingState switchingState = renderer.ActiveFrameOpResourcePlannerSwitchingState;
            switchingState.States[pendingKey] = pendingState;
            renderer.MarkFrameOpResourcePlannerStateUsed(switchingState, pendingKey);
            _committed = true;

            try
            {
                renderer.PruneFrameOpResourcePlannerStatesToCapacity(switchingState);
                if (!ReferenceEquals(previousState.ResourceAllocator, pendingState.ResourceAllocator) &&
                    !IsAllocatorOwnedByFrameOpPlannerState(switchingState, previousState.ResourceAllocator))
                {
                    _ = previousState.ResourceAllocator.TryRetirePhysicalResources(renderer);
                }
            }
            catch (Exception ex)
            {
                Debug.VulkanWarning(
                    "[VulkanResourcePlanner] Generation {0} published, but post-commit retirement failed: {1}",
                    pendingKey.ResourceGeneration,
                    ex.Message);
            }
        }

        public void Dispose()
        {
            if (!_committed && !pendingState.ResourceAllocator.IsRetired)
                _ = pendingState.ResourceAllocator.TryRetirePhysicalResources(renderer, immediate: true);
        }
    }

    internal ExternalResourcePlannerReadbackScope EnterFrameOpResourcePlannerReadbackScope(in FrameOpContext context)
        => new ExternalResourcePlannerReadbackScope(this, context);

    private FrameOpContext CreateFrameOpContext(
        XRRenderPipelineInstance pipeline,
        XRViewport? viewport)
    {
        uint displayWidth;
        uint displayHeight;
        uint internalWidth;
        uint internalHeight;
        int outputTargetIdentity = 0;
        string? outputTargetName = null;
        if (TryResolveExternalSwapchainTargetExtent(out Extent2D externalExtent))
        {
            var dimensions = ResolveExternalFrameOpResourceDimensions(
                externalExtent,
                pipeline.ResourceInternalWidth,
                pipeline.ResourceInternalHeight,
                viewport?.InternalWidth,
                viewport?.InternalHeight);
            displayWidth = dimensions.DisplayWidth;
            displayHeight = dimensions.DisplayHeight;
            internalWidth = dimensions.InternalWidth;
            internalHeight = dimensions.InternalHeight;
            TryGetExternalSwapchainTargetIdentity(out outputTargetIdentity, out outputTargetName);
        }
        else
        {
            Extent2D fallbackExtent = ResolveFrameOpContextFallbackExtent();
            displayWidth = ResolvePositiveDimension(
                pipeline.ResourceDisplayWidth,
                viewport?.Width,
                fallbackExtent.Width,
                1u);
            displayHeight = ResolvePositiveDimension(
                pipeline.ResourceDisplayHeight,
                viewport?.Height,
                fallbackExtent.Height,
                1u);
            internalWidth = ResolvePositiveDimension(
                pipeline.ResourceInternalWidth,
                viewport?.InternalWidth,
                displayWidth,
                1u);
            internalHeight = ResolvePositiveDimension(
                pipeline.ResourceInternalHeight,
                viewport?.InternalHeight,
                displayHeight,
                1u);
        }

        XRFrameBuffer? outputFrameBuffer = ResolveFrameOpOutputFrameBuffer(pipeline, viewport);
        ApplyOutputFrameBufferTargetIdentity(outputFrameBuffer, ref outputTargetIdentity, ref outputTargetName);

        FrameOpContext context = new(
            pipeline.InstanceId,
            viewport is null
                ? (pipeline.LastWindowViewport is null ? 0 : RuntimeHelpers.GetHashCode(pipeline.LastWindowViewport))
                : RuntimeHelpers.GetHashCode(viewport),
            pipeline,
            pipeline.Resources,
            pipeline.Pipeline?.PassMetadata,
            displayWidth,
            displayHeight,
            internalWidth,
            internalHeight,
            outputFrameBuffer?.Name,
            ShouldPreserveSubmissionOrderBlock(),
            outputTargetIdentity,
            outputTargetName);

        context = ApplyInteractiveResizePlannerFreeze(context) with { OutputFrameBuffer = outputFrameBuffer };
        return CompleteFrameOpContext(context);
    }

    private FrameOpContext CompleteFrameOpContext(in FrameOpContext context)
    {
        FrameOpContext complete = context with
        {
            OutputFrameBufferIdentity = ComputeOutputFrameBufferIdentity(context.OutputFrameBufferName),
            ContextKind = ResolveFrameOpContextKind(context),
            ContextId = unchecked((ulong)Interlocked.Increment(ref _frameOpContextId)),
            SubmissionQueueFamily = ResolveFrameOpSubmissionQueueFamily(context.PassMetadata),
            StereoEnabled = RuntimeEngine.Rendering.State.IsStereoPass,
            MultiviewEnabled = ResolveFrameOpContextMultiviewEnabled(context),
            ResourceGeneration = ResolveFrameOpContextResourceGeneration(context.PipelineInstance),
            DescriptorGeneration = ResolveFrameOpContextDescriptorGeneration(context.ResourceRegistry),
            ResourceRegistrySignatureSnapshot = ComputeResourceRegistrySignature(context.ResourceRegistry),
        };

        return RefreshFrameOpContextRecordingFingerprint(complete);
    }

    private FrameOpContext RefreshFrameOpContextRecordingFingerprint(in FrameOpContext context)
        => context with { RecordingFingerprint = ComputeFrameOpContextRecordingFingerprint(context) };

    private EVulkanFrameOpContextKind ResolveFrameOpContextKind(in FrameOpContext context)
    {
        if (IsThreadOpenXrExternalSwapchainTarget)
        {
            return _threadOpenXrExternalSwapchainContextKind == EVulkanFrameOpContextKind.Unknown
                ? EVulkanFrameOpContextKind.OpenXrEye
                : _threadOpenXrExternalSwapchainContextKind;
        }

        if (RuntimeEngine.Rendering.State.IsLightProbePass)
            return EVulkanFrameOpContextKind.LightProbeCapture;
        if (RuntimeEngine.Rendering.State.IsShadowPass)
            return EVulkanFrameOpContextKind.Shadow;
        if (RuntimeEngine.Rendering.State.IsSceneCapturePass)
            return EVulkanFrameOpContextKind.SceneCapture;

        string pipelineTypeName = context.PipelineInstance?.Pipeline?.GetType().Name ?? string.Empty;
        if (pipelineTypeName.Contains("Diagnostic", StringComparison.OrdinalIgnoreCase))
            return EVulkanFrameOpContextKind.DiagnosticCapture;
        if (pipelineTypeName.Contains("UserInterface", StringComparison.OrdinalIgnoreCase) ||
            pipelineTypeName.Contains("UiPreview", StringComparison.OrdinalIgnoreCase))
            return EVulkanFrameOpContextKind.UiPreview;

        return EVulkanFrameOpContextKind.MainViewport;
    }

    private uint ResolveFrameOpSubmissionQueueFamily(IReadOnlyCollection<RenderPassMetadata>? passMetadata)
        => BuildQueueOwnershipConfig(passMetadata).GraphicsQueueFamilyIndex;

    private static bool ResolveFrameOpContextMultiviewEnabled(in FrameOpContext context)
    {
        if (!RuntimeEngine.Rendering.State.IsStereoPass)
            return false;

        string pipelineTypeName = context.PipelineInstance?.Pipeline?.GetType().Name ?? string.Empty;
        return pipelineTypeName.Contains("MultiView", StringComparison.OrdinalIgnoreCase) ||
            pipelineTypeName.Contains("Multiview", StringComparison.OrdinalIgnoreCase);
    }

    private static ulong ResolveFrameOpContextResourceGeneration(XRRenderPipelineInstance? pipeline)
        => unchecked((ulong)Math.Max(pipeline?.ResourceGeneration ?? 0, 0));

    private ulong ResolveFrameOpContextDescriptorGeneration(RenderResourceRegistry? registry)
    {
        ulong descriptorTableGeneration = unchecked((ulong)Math.Max(Volatile.Read(ref _vulkanDescriptorTableGeneration), 0L));
        return MixSignature(descriptorTableGeneration, unchecked((uint)ComputeResourceRegistrySignature(registry)));
    }

    internal static ulong ComputeFrameOpContextRecordingFingerprint(in FrameOpContext context)
    {
        FrameOpSignatureHasher hash = new();
        hash.Add(0x46524D4F50435458UL);
        hash.Add((int)context.ContextKind);
        hash.Add(context.PipelineIdentity);
        hash.Add(context.ViewportIdentity);
        hash.Add(context.OutputFrameBufferIdentity);
        hash.Add(context.OutputTargetIdentity);
        hash.Add(context.OutputTargetName);
        hash.Add(context.DisplayWidth);
        hash.Add(context.DisplayHeight);
        hash.Add(context.InternalWidth);
        hash.Add(context.InternalHeight);
        hash.Add(context.StereoEnabled);
        hash.Add(context.MultiviewEnabled);
        hash.Add(ResolveFrameOpContextResourceRegistrySignature(context));
        hash.Add(ComputePassMetadataSignature(context.PassMetadata));
        hash.Add(context.ResourceGeneration);
        hash.Add(context.DescriptorGeneration);
        hash.Add(context.SubmissionQueueFamily);
        return hash.ToHash();
    }

    /// <summary>
    /// Determines whether two frame-op contexts require the same Vulkan recording state.
    /// <see cref="FrameOpContext.ContextId"/> is diagnostic identity for an individual
    /// capture and must not split otherwise compatible render scopes.
    /// </summary>
    internal static bool AreFrameOpContextsRecordingCompatible(
        in FrameOpContext first,
        in FrameOpContext second)
    {
        if (first.Equals(second))
            return true;
        if (first.RecordingFingerprint != second.RecordingFingerprint)
            return false;

        // ContextId distinguishes diagnostic capture events, not recording state.
        // Keep record equality for every other field so future context additions
        // automatically participate in the compatibility decision.
        return (first with { ContextId = 0UL }).Equals(second with { ContextId = 0UL });
    }

    /// <summary>
    /// Determines whether an active inline query can remain inside the current Vulkan
    /// rendering scope while mesh descriptor state advances during draw preparation.
    /// </summary>
    internal static bool AreFrameOpContextsQueryScopeCompatible(
        in FrameOpContext first,
        in FrameOpContext second)
    {
        if (AreFrameOpContextsRecordingCompatible(first, second))
            return true;

        // Descriptor-table updates do not change dynamic-rendering compatibility.
        // Every other captured field remains part of exact record equality so changes
        // to target, dimensions, stereo/multiview, queue ownership, or resources split
        // the scope and invalidate the query instead of silently measuring nothing.
        FrameOpContext normalizedFirst = first with
        {
            ContextId = 0UL,
            RecordingFingerprint = 0UL,
            DescriptorGeneration = 0UL,
        };
        FrameOpContext normalizedSecond = second with
        {
            ContextId = 0UL,
            RecordingFingerprint = 0UL,
            DescriptorGeneration = 0UL,
        };
        return normalizedFirst.Equals(normalizedSecond);
    }

    private static XRFrameBuffer? ResolveFrameOpOutputFrameBuffer(
        XRRenderPipelineInstance? pipeline,
        XRViewport? viewport)
    {
        XRRenderPipelineInstance? activePipeline = RuntimeEngine.Rendering.State.CurrentRenderingPipeline;
        if (pipeline is null || ReferenceEquals(activePipeline, pipeline))
        {
            XRFrameBuffer? activeOutput = RuntimeEngine.Rendering.State.RenderingTargetOutputFBO;
            if (activeOutput is not null)
                return activeOutput;
        }

        return pipeline?.RenderState.OutputFBO ?? viewport?.LastRenderedTargetFBO;
    }

    private static void ApplyOutputFrameBufferTargetIdentity(
        XRFrameBuffer? frameBuffer,
        ref int outputTargetIdentity,
        ref string? outputTargetName)
    {
        if (frameBuffer is null || outputTargetIdentity != 0)
            return;

        outputTargetIdentity = RuntimeHelpers.GetHashCode(frameBuffer);
        outputTargetName = frameBuffer.Name;
    }

    private static bool ShouldPreserveSubmissionOrderBlock()
        => RuntimeEngine.Rendering.State.RenderingTargetOutputFBO is not null &&
           (RuntimeEngine.Rendering.State.IsSceneCapturePass ||
            RuntimeEngine.Rendering.State.IsLightProbePass);

    internal readonly struct ExternalResourcePlannerReadbackScope : IDisposable
    {
        private readonly VulkanRenderer _renderer;
        private readonly ResourcePlannerRuntimeState _previousState;
        private readonly FrameOpPlannerStateKey _key;
        private readonly FrameOpContext _context;
        private readonly bool _active;

        public ExternalResourcePlannerReadbackScope(
            VulkanRenderer renderer,
            in FrameOpContext context)
        {
            _renderer = renderer;
            _context = context;
            _previousState = renderer.CaptureResourcePlannerRuntimeState();
            _active = renderer.IsDeviceOperational &&
                FrameOpResourcePlannerSwitchingEnabled &&
                FrameOpContextHasPlannerResources(context);

            if (!_active)
            {
                _key = default;
                return;
            }

            FrameOpResourcePlannerSwitchingState switchingState = renderer.ActiveFrameOpResourcePlannerSwitchingState;
            FrameOpPlannerStateKey requestedKey = BuildFrameOpPlannerStateKey(context);
            bool canReusePreviousState = ResourcePlannerRuntimeStateMatchesPlannerStateKeyIgnoringRegistry(
                _previousState,
                requestedKey);
            bool foundCachedState = TryFindBestCompatibleFrameOpPlannerState(
                context,
                switchingState,
                out FrameOpPlannerStateKey cachedKey,
                out ResourcePlannerRuntimeState state);
            if (foundCachedState &&
                (!canReusePreviousState ||
                 ScoreCompatibleFrameOpPlannerState(cachedKey, state) >
                 ScoreCompatibleFrameOpPlannerState(requestedKey, _previousState)))
            {
                _key = cachedKey;
                if (VulkanFrameDiagnosticsTraceEnabled)
                {
                    Debug.Vulkan(
                        "[VulkanResourcePlanner] External readback cache hit registry=0x{0:X8} owner={1} revision={2} textures={3} buffers={4}.",
                        requestedKey.ResourceRegistrySignature,
                        state.AllocatorOwnershipId,
                        state.ResourcePlannerRevision,
                        state.ResourceAllocator.LogicalTextureAllocations.Count,
                        state.ResourceAllocator.LogicalBufferAllocations.Count);
                }
                renderer.RestoreResourcePlannerRuntimeState(state);
                renderer.MarkFrameOpResourcePlannerStateUsed(switchingState, _key);
                return;
            }

            if (canReusePreviousState)
            {
                _key = requestedKey;
                renderer.RestoreResourcePlannerRuntimeState(_previousState);
                switchingState.States[_key] = _previousState;
                renderer.MarkFrameOpResourcePlannerStateUsed(switchingState, _key);
                if (VulkanFrameDiagnosticsTraceEnabled)
                {
                    Debug.Vulkan(
                        "[VulkanResourcePlanner] External readback reused active state registry=0x{0:X8} owner={1} revision={2} textures={3} buffers={4}.",
                        requestedKey.ResourceRegistrySignature,
                        _previousState.AllocatorOwnershipId,
                        _previousState.ResourcePlannerRevision,
                        _previousState.ResourceAllocator.LogicalTextureAllocations.Count,
                        _previousState.ResourceAllocator.LogicalBufferAllocations.Count);
                }
                return;
            }

            _key = requestedKey;
            renderer.RestoreResourcePlannerRuntimeState(ResourcePlannerRuntimeState.CreateEmpty());
            renderer.UpdateResourcePlannerFromContext(context);
            ResourcePlannerRuntimeState preparedState = renderer.CaptureResourcePlannerRuntimeState();
            preparedState.LastActiveFrameOpContext = context;
            if (VulkanFrameDiagnosticsTraceEnabled)
            {
                Debug.Vulkan(
                    "[VulkanResourcePlanner] External readback cache miss prepared registry=0x{0:X8} owner={1} revision={2} textures={3} buffers={4}.",
                    requestedKey.ResourceRegistrySignature,
                    preparedState.AllocatorOwnershipId,
                    preparedState.ResourcePlannerRevision,
                    preparedState.ResourceAllocator.LogicalTextureAllocations.Count,
                    preparedState.ResourceAllocator.LogicalBufferAllocations.Count);
            }
            switchingState.States[_key] = preparedState;
            renderer.MarkFrameOpResourcePlannerStateUsed(switchingState, _key);
        }

        public void Dispose()
        {
            ResourcePlannerRuntimeState currentState = default;
            bool canPublish = _active && _renderer.IsDeviceOperational;
            if (canPublish)
            {
                currentState = _renderer.CaptureResourcePlannerRuntimeState();
                currentState.LastActiveFrameOpContext = _context;
                _renderer.ActiveFrameOpResourcePlannerSwitchingState.States[_key] =
                    currentState;
                _renderer.MarkFrameOpResourcePlannerStateUsed(
                    _renderer.ActiveFrameOpResourcePlannerSwitchingState,
                    _key);
            }

            ResourcePlannerRuntimeState restoreState =
                _active && _previousState.ResourceAllocator is not null && _previousState.ResourceAllocator.IsRetired
                    ? canPublish
                        ? currentState
                        : ResourcePlannerRuntimeState.CreateEmpty()
                    : _previousState;
            _renderer.RestoreResourcePlannerRuntimeState(restoreState);
        }
    }

    private static bool TryFindBestCompatibleFrameOpPlannerState(
        in FrameOpContext context,
        FrameOpResourcePlannerSwitchingState switchingState,
        out FrameOpPlannerStateKey key,
        out ResourcePlannerRuntimeState state)
    {
        key = default;
        state = default;
        bool found = false;
        int bestScore = int.MinValue;

        foreach (KeyValuePair<FrameOpPlannerStateKey, ResourcePlannerRuntimeState> pair in switchingState.States)
        {
            if (!FrameOpContextMatchesPlannerStateKey(context, pair.Key))
                continue;

            int score = ScoreCompatibleFrameOpPlannerState(pair.Key, pair.Value);
            if (!found || score > bestScore)
            {
                found = true;
                bestScore = score;
                key = pair.Key;
                state = pair.Value;
            }
        }

        return found;
    }

    private static int ScoreCompatibleFrameOpPlannerState(
        in FrameOpPlannerStateKey key,
        in ResourcePlannerRuntimeState state)
    {
        int score = 0;
        if (state.ResourcePlannerRevision != 0)
            score += 10_000;
        if (state.ResourcePlannerSignature != ulong.MaxValue)
            score += 1_000;
        if (state.ResourceAllocationSignature != ulong.MaxValue)
            score += 1_000;

        score += Math.Min(state.ResourceAllocator.LogicalTextureAllocations.Count, 4096) * 4;
        score += Math.Min(state.ResourceAllocator.LogicalBufferAllocations.Count, 4096);

        return score;
    }

    private static bool ResourcePlannerRuntimeStateMatchesPlannerStateKeyIgnoringRegistry(
        in ResourcePlannerRuntimeState state,
        in FrameOpPlannerStateKey key)
        => state.ResourceAllocator is not null &&
            !state.ResourceAllocator.IsRetired &&
            state.LastActiveFrameOpContext is FrameOpContext context &&
            FrameOpContextMatchesPlannerStateKeyIgnoringRegistry(context, key);

    internal bool TryEnsurePhysicalImageForTextureResource(
        string? resourceName,
        out VulkanPhysicalImageGroup? group)
        => TryEnsurePhysicalImageForTextureResource(resourceName, out group, out _);

    internal bool TryEnsurePhysicalImageForTextureResource(
        string? resourceName,
        out VulkanPhysicalImageGroup? group,
        out string? failureReason)
    {
        group = null;
        failureReason = null;
        if (string.IsNullOrWhiteSpace(resourceName))
            return false;

        if (ResourceAllocator.TryGetPhysicalGroupForResource(resourceName, out group) &&
            group?.IsAllocated == true)
        {
            return true;
        }

        FrameOpContext context = CaptureFrameOpContextOrLastActive();
        if (context.ResourceRegistry is null ||
            !context.ResourceRegistry.TextureRecords.ContainsKey(resourceName))
        {
            group = null;
            return false;
        }

        if (_isRecordingCommandBuffer)
        {
            Debug.VulkanWarningEvery(
                $"Vulkan.ResourcePlanner.LazyRebuildDuringRecord.{resourceName}",
                TimeSpan.FromSeconds(2),
                "[VulkanResourcePlanner] Deferring lazy physical-image plan rebuild for '{0}' during command-buffer recording.",
                resourceName);
            failureReason = "resource planner rebuild is deferred during command-buffer recording";
            group = null;
            return false;
        }

        if (IsCommandChainResourcePlanFrozen)
        {
            Debug.VulkanWarningEvery(
                $"Vulkan.ResourcePlanner.LazyRebuildDuringFrozenCommandChainPlan.{resourceName}",
                TimeSpan.FromSeconds(2),
                "[VulkanResourcePlanner] Refusing lazy physical-image plan rebuild for '{0}' while command-chain readers are using frozen plan revision {1}.",
                resourceName,
                _commandChainFrozenResourcePlanRevision);
            failureReason = $"resource planner rebuild is deferred while command-chain readers are using frozen plan revision {_commandChainFrozenResourcePlanRevision}";
            group = null;
            return false;
        }

        if (VulkanFrameDiagnosticsTraceEnabled)
        {
            Debug.Vulkan(
                "[VulkanResourcePlanner] Lazy physical-image rebuild resource='{0}' registry=0x{1:X8} owner={2} revision={3} textures={4} buffers={5}.",
                resourceName,
                ResolveFrameOpContextResourceRegistrySignature(context),
                ResourceAllocator.OwnershipId,
                ResourcePlannerRevision,
                ResourceAllocator.LogicalTextureAllocations.Count,
                ResourceAllocator.LogicalBufferAllocations.Count);
        }

        UpdateResourcePlannerFromContext(context);

        if (ResourceAllocator.TryGetPhysicalGroupForResource(resourceName, out group) &&
            group is not null)
        {
            if (!group.TryEnsureAllocated(this, out string allocationFailureReason))
            {
                Debug.VulkanWarningEvery(
                    $"Vulkan.ResourcePlanner.LazyPhysicalImageAllocationFailed.{resourceName}",
                    TimeSpan.FromSeconds(2),
                    "[VulkanResourcePlanner] Lazy physical-image allocation failed for '{0}': {1}",
                    resourceName,
                    allocationFailureReason);
                failureReason = allocationFailureReason;
                group = null;
                return false;
            }

            return group.IsAllocated;
        }

        group = null;
        return false;
    }

    private FrameOpContext PrepareResourcePlannerForFrameOps(FrameOp[] ops, ulong frameOpsSignature = 0)
    {
        if (ops.Length == 0)
        {
            FrameOpContext context = CaptureFrameOpContext();
            if (context.ResourceRegistry is null && context.PassMetadata is null)
                return context;

            UpdateResourcePlannerFromContext(context);
            return context;
        }

        FrameOpContext primary = SelectPrimaryPlannerContext(ops);
        RenderResourceRegistry? mergedRegistry = BuildMergedFrameOpRegistry(ops, primary, frameOpsSignature);
        FrameOpContext plannerContext = mergedRegistry is null
            ? primary
            : RefreshFrameOpContextRecordingFingerprint(primary with
            {
                ResourceRegistry = mergedRegistry,
                DescriptorGeneration = ResolveFrameOpContextDescriptorGeneration(mergedRegistry),
                ResourceRegistrySignatureSnapshot = ComputeResourceRegistrySignature(mergedRegistry),
            });

        plannerContext = RefreshPlannerExtentsFromLiveContext(plannerContext, ops);

        // Descriptor snapshots are captured against the full pipeline resource
        // plan before the command buffer is recorded. Keep frame-op recording on
        // that same plan so FBO writes, sampled descriptors, and readback all
        // resolve the same physical image groups.
        UpdateResourcePlannerFromContext(plannerContext);

        return plannerContext;
    }

    private ulong PrepareFrameOpResourcePlannerStatesForFrameOps(FrameOp[] ops, ulong frameOpsSignature = 0)
    {
        if (!IsDeviceOperational)
            return ResourcePlannerRevision;

        FrameOpResourcePlannerSwitchingState switchingState = ActiveFrameOpResourcePlannerSwitchingState;
        switchingState.SwitchingActive = false;
        switchingState.RecordingScopeActive = false;
        switchingState.HasActiveKey = false;
        switchingState.HasActiveContext = false;
        switchingState.ActiveKeys.Clear();

        if (!FrameOpResourcePlannerSwitchingEnabled)
        {
            DestroyFrameOpResourcePlannerStates();
            return ResourcePlannerRevision;
        }

        if (ops.Length == 0)
            return ResourcePlannerRevision;

        List<FrameOpPlannerStateKey> keys = _frameOpPlannerStateKeyScratch;
        keys.Clear();
        CollectFrameOpPlannerStateKeys(ops, keys);
        if (keys.Count == 0)
        {
            keys.Clear();
            PruneFrameOpResourcePlannerStatesToCapacity(switchingState);
            return ResourcePlannerRevision;
        }

        if (keys.Count > MaxFrameOpResourcePlannerSwitchingStates)
        {
            Debug.VulkanWarningEvery(
                $"Vulkan.ResourcePlanner.FrameOpContextStateCap.{GetHashCode()}",
                TimeSpan.FromSeconds(1),
                "[VulkanResourcePlanner] Collapsing {0} frame-op planner contexts into the merged planner to avoid duplicating physical render resources. Cap={1} Revision={2}",
                keys.Count,
                MaxFrameOpResourcePlannerSwitchingStates,
                ResourcePlannerRevision);
            DestroyFrameOpResourcePlannerStates();
            keys.Clear();
            return ResourcePlannerRevision;
        }

        ResourcePlannerRuntimeState previousState = CaptureResourcePlannerRuntimeState();
        try
        {
            ResetActiveFrameOpResourcePlannerState(switchingState);
            for (int i = 0; i < keys.Count; i++)
            {
                FrameOpPlannerStateKey key = keys[i];
                bool cached = switchingState.States.TryGetValue(key, out ResourcePlannerRuntimeState existingState);
                ResourcePlannerRuntimeState state = cached
                    ? existingState
                    : ResourcePlannerRuntimeState.CreateEmpty();

                if (VulkanFrameDiagnosticsTraceEnabled)
                {
                    Debug.VulkanEvery(
                        $"Vulkan.ResourcePlanner.KeyedStatePrepare.{key.GetHashCode()}",
                        TimeSpan.FromSeconds(1),
                        "[VulkanResourcePlanner] Preparing keyed state registry=0x{0:X8} generation={1} cached={2} owner={3} revision={4} signature=0x{5:X16}.",
                        key.ResourceRegistrySignature,
                        key.ResourceGeneration,
                        cached,
                        state.AllocatorOwnershipId,
                        state.ResourcePlannerRevision,
                        state.ResourcePlannerSignature);
                }

                ResetActiveFrameOpResourcePlannerState(switchingState);
                RestoreResourcePlannerRuntimeState(state);
                FrameOpContext keyContext = PrepareResourcePlannerForFrameOps(ops, key, frameOpsSignature);
                ResourcePlannerRuntimeState preparedState = CaptureResourcePlannerRuntimeState();
                preparedState.LastActiveFrameOpContext = keyContext;
                switchingState.States[key] = preparedState;
                switchingState.ActiveKeys.Add(key);
                MarkFrameOpResourcePlannerStateUsed(switchingState, key);
            }
        }
        finally
        {
            ResetActiveFrameOpResourcePlannerState(switchingState);
            RestoreResourcePlannerRuntimeState(previousState);
        }

        keys.Clear();
        switchingState.SwitchingActive = switchingState.ActiveKeys.Count > 1;
        if (!switchingState.SwitchingActive)
        {
            PruneFrameOpResourcePlannerStatesToCapacity(switchingState);
            return ResourcePlannerRevision;
        }

        PruneFrameOpResourcePlannerStatesToCapacity(switchingState);
        AssertFrameOpPlannerAllocatorOwnership(switchingState);

        ulong signature = ComputeActiveFrameOpResourcePlannerStatesSignature();
        if (VulkanFrameDiagnosticsTraceEnabled)
        {
            Debug.VulkanEvery(
                $"Vulkan.ResourcePlanner.FrameOpContextStates.{GetHashCode()}",
                TimeSpan.FromSeconds(1),
                "[VulkanResourcePlanner] Prepared {0} frame-op context resource planner states. Signature=0x{1:X16}.",
                switchingState.ActiveKeys.Count,
                signature);
        }
        return signature;
    }

    private FrameOpContext PrepareResourcePlannerForFrameOps(
        FrameOp[] ops,
        in FrameOpPlannerStateKey key,
        ulong frameOpsSignature = 0)
    {
        FrameOpContext plannerContext = SelectPrimaryPlannerContext(ops, key);
        RenderResourceRegistry? mergedRegistry = BuildMergedFrameOpRegistry(ops, plannerContext, frameOpsSignature);
        if (mergedRegistry is not null)
        {
            plannerContext = RefreshFrameOpContextRecordingFingerprint(plannerContext with
            {
                ResourceRegistry = mergedRegistry,
                DescriptorGeneration = ResolveFrameOpContextDescriptorGeneration(mergedRegistry),
                ResourceRegistrySignatureSnapshot = ComputeResourceRegistrySignature(mergedRegistry),
            });
        }

        plannerContext = RefreshPlannerExtentsFromLiveContext(plannerContext, ops, filterByPlannerKey: true, plannerKey: key);
        UpdateResourcePlannerFromContext(plannerContext);

        return plannerContext;
    }

    private static void ResetActiveFrameOpResourcePlannerState(FrameOpResourcePlannerSwitchingState switchingState)
    {
        switchingState.HasActiveKey = false;
        switchingState.HasActiveContext = false;
        switchingState.ActiveKey = default;
    }

    private void MarkFrameOpResourcePlannerStateUsed(
        FrameOpResourcePlannerSwitchingState switchingState,
        in FrameOpPlannerStateKey key)
    {
        switchingState.LastUsedSerials[key] = ++switchingState.UsageSerial;
    }

    private void PruneFrameOpResourcePlannerStatesToCapacity(FrameOpResourcePlannerSwitchingState switchingState)
    {
        if (switchingState.States.Count <= MaxFrameOpResourcePlannerSwitchingStates)
            return;

        List<FrameOpPlannerStateKey> staleKeys = _frameOpPlannerStateEvictionScratch;
        staleKeys.Clear();
        foreach (FrameOpPlannerStateKey key in switchingState.States.Keys)
        {
            if (switchingState.ActiveKeys.Contains(key))
                continue;

            staleKeys.Add(key);
        }

        int pruneCount = Math.Min(
            staleKeys.Count,
            switchingState.States.Count - MaxFrameOpResourcePlannerSwitchingStates);
        if (pruneCount <= 0)
        {
            staleKeys.Clear();
            return;
        }

        RuntimeRenderingHostServices.Current.RecordRenderFrameOutputWork(
            new FrameOutputWorkTelemetry(PlannerPrunes: pruneCount));
        ResourcePlannerRuntimeState previousState = CaptureResourcePlannerRuntimeState();
        WaitForAllInFlightWork();
        int prunedCount = 0;
        for (int i = 0; i < pruneCount; i++)
        {
            if (!TryPopOldestFrameOpResourcePlannerStateKey(switchingState, staleKeys, out FrameOpPlannerStateKey key))
                break;

            if (!switchingState.States.Remove(key, out ResourcePlannerRuntimeState state))
                continue;

            switchingState.LastUsedSerials.Remove(key);
            if (!IsAllocatorOwnedByFrameOpPlannerState(switchingState, state.ResourceAllocator))
            {
                RestoreResourcePlannerRuntimeState(state);
                _ = ResourceAllocator.TryRetirePhysicalResources(this);
            }
            prunedCount++;
        }

        if (previousState.ResourceAllocator is not null && previousState.ResourceAllocator.IsRetired)
            previousState = ResourcePlannerRuntimeState.CreateEmpty();
        RestoreResourcePlannerRuntimeState(previousState);
        if (prunedCount > 0 && !IsDeviceLost)
            ForceFlushAllRetiredResourcesAfterWaiting("FrameOpResourcePlannerStatePrune");

        staleKeys.Clear();
        if (prunedCount == 0)
            return;

        Debug.VulkanEvery(
            $"Vulkan.ResourcePlanner.FrameOpContextStatePruned.{GetHashCode()}",
            TimeSpan.FromSeconds(1),
            "[VulkanResourcePlanner] Pruned {0} cached frame-op planner state(s) to stay under capacity. Remaining={1} Cap={2}",
            prunedCount,
            switchingState.States.Count,
            MaxFrameOpResourcePlannerSwitchingStates);
    }

    private static ulong GetFrameOpResourcePlannerStateLastUsedSerial(
        FrameOpResourcePlannerSwitchingState switchingState,
        in FrameOpPlannerStateKey key)
        => switchingState.LastUsedSerials.TryGetValue(key, out ulong serial)
            ? serial
            : 0UL;

    private static bool TryPopOldestFrameOpResourcePlannerStateKey(
        FrameOpResourcePlannerSwitchingState switchingState,
        List<FrameOpPlannerStateKey> keys,
        out FrameOpPlannerStateKey key)
    {
        int oldestIndex = -1;
        ulong oldestSerial = ulong.MaxValue;
        for (int i = 0; i < keys.Count; i++)
        {
            ulong serial = GetFrameOpResourcePlannerStateLastUsedSerial(switchingState, keys[i]);
            if (oldestIndex >= 0 && serial >= oldestSerial)
                continue;

            oldestIndex = i;
            oldestSerial = serial;
        }

        if (oldestIndex < 0)
        {
            key = default;
            return false;
        }

        key = keys[oldestIndex];
        keys.RemoveAt(oldestIndex);
        return true;
    }

    private void CollectFrameOpPlannerStateKeys(FrameOp[] ops, List<FrameOpPlannerStateKey> keys)
    {
        for (int i = 0; i < ops.Length; i++)
        {
            FrameOpContext context = ops[i].Context;
            if (!FrameOpContextHasPlannerResources(context))
                continue;

            FrameOpPlannerStateKey key = BuildFrameOpPlannerStateKey(context);
            if (!keys.Contains(key))
                keys.Add(key);
        }

        keys.Sort(static (left, right) =>
        {
            int compare = left.PipelineIdentity.CompareTo(right.PipelineIdentity);
            if (compare != 0)
                return compare;

            compare = left.ContextKind.CompareTo(right.ContextKind);
            if (compare != 0)
                return compare;

            compare = left.ViewportIdentity.CompareTo(right.ViewportIdentity);
            if (compare != 0)
                return compare;

            compare = left.DisplayWidth.CompareTo(right.DisplayWidth);
            if (compare != 0)
                return compare;

            compare = left.DisplayHeight.CompareTo(right.DisplayHeight);
            if (compare != 0)
                return compare;

            compare = left.InternalWidth.CompareTo(right.InternalWidth);
            if (compare != 0)
                return compare;

            compare = left.InternalHeight.CompareTo(right.InternalHeight);
            if (compare != 0)
                return compare;

            compare = left.OutputFrameBufferIdentity.CompareTo(right.OutputFrameBufferIdentity);
            if (compare != 0)
                return compare;

            compare = left.OutputTargetIdentity.CompareTo(right.OutputTargetIdentity);
            if (compare != 0)
                return compare;

            compare = left.ResourceRegistrySignature.CompareTo(right.ResourceRegistrySignature);
            if (compare != 0)
                return compare;

            compare = left.PassMetadataSignature.CompareTo(right.PassMetadataSignature);
            if (compare != 0)
                return compare;

            compare = left.ResourceGeneration.CompareTo(right.ResourceGeneration);
            if (compare != 0)
                return compare;

            compare = left.SubmissionQueueFamily.CompareTo(right.SubmissionQueueFamily);
            return compare;
        });
    }

    private ulong ComputeActiveFrameOpResourcePlannerStatesSignature()
    {
        FrameOpResourcePlannerSwitchingState switchingState = ActiveFrameOpResourcePlannerSwitchingState;
        FrameOpSignatureHasher hash = new();
        List<FrameOpPlannerStateKey> keys = _frameOpPlannerStateKeyScratch;
        keys.Clear();
        foreach (FrameOpPlannerStateKey key in switchingState.ActiveKeys)
            keys.Add(key);
        keys.Sort(static (left, right) =>
        {
            int compare = left.PipelineIdentity.CompareTo(right.PipelineIdentity);
            if (compare != 0)
                return compare;

            compare = left.ContextKind.CompareTo(right.ContextKind);
            if (compare != 0)
                return compare;

            compare = left.ViewportIdentity.CompareTo(right.ViewportIdentity);
            if (compare != 0)
                return compare;

            compare = left.DisplayWidth.CompareTo(right.DisplayWidth);
            if (compare != 0)
                return compare;

            compare = left.DisplayHeight.CompareTo(right.DisplayHeight);
            if (compare != 0)
                return compare;

            compare = left.InternalWidth.CompareTo(right.InternalWidth);
            if (compare != 0)
                return compare;

            compare = left.InternalHeight.CompareTo(right.InternalHeight);
            if (compare != 0)
                return compare;

            compare = left.OutputFrameBufferIdentity.CompareTo(right.OutputFrameBufferIdentity);
            if (compare != 0)
                return compare;

            compare = left.OutputTargetIdentity.CompareTo(right.OutputTargetIdentity);
            if (compare != 0)
                return compare;

            compare = left.ResourceRegistrySignature.CompareTo(right.ResourceRegistrySignature);
            if (compare != 0)
                return compare;

            compare = left.PassMetadataSignature.CompareTo(right.PassMetadataSignature);
            if (compare != 0)
                return compare;

            compare = left.ResourceGeneration.CompareTo(right.ResourceGeneration);
            if (compare != 0)
                return compare;

            compare = left.SubmissionQueueFamily.CompareTo(right.SubmissionQueueFamily);
            return compare;
        });

        hash.Add(keys.Count);

        for (int i = 0; i < keys.Count; i++)
        {
            FrameOpPlannerStateKey key = keys[i];
            hash.Add((int)key.ContextKind);
            hash.Add(key.PipelineIdentity);
            hash.Add(key.ViewportIdentity);
            hash.Add(key.DisplayWidth);
            hash.Add(key.DisplayHeight);
            hash.Add(key.InternalWidth);
            hash.Add(key.InternalHeight);
            hash.Add(key.OutputFrameBufferIdentity);
            hash.Add(key.OutputTargetIdentity);
            hash.Add(key.ResourceRegistrySignature);
            hash.Add(key.PassMetadataSignature);
            hash.Add(key.ResourceGeneration);
            hash.Add(key.SubmissionQueueFamily);

            if (!switchingState.States.TryGetValue(key, out ResourcePlannerRuntimeState state))
            {
                hash.Add(0);
                continue;
            }

            hash.Add(state.ResourcePlannerRevision);
            hash.Add(state.ResourcePlannerSignature);
            hash.Add(state.ResourceAllocationSignature);
        }

        keys.Clear();
        return hash.ToHash();
    }

    private FrameOpResourcePlannerRecordingScope EnterFrameOpResourcePlannerRecordingScope()
        => new(this);

    private bool TryActivateFrameOpResourcePlannerState(in FrameOpContext context)
    {
        if (!IsDeviceOperational)
            return false;

        FrameOpResourcePlannerSwitchingState switchingState = ActiveFrameOpResourcePlannerSwitchingState;
        if (switchingState.ActiveKeys.Count == 0 ||
            !FrameOpContextHasPlannerResources(context))
        {
            return false;
        }

        if (!TryFindActiveFrameOpPlannerStateKey(context, switchingState, out FrameOpPlannerStateKey key))
            return false;

        if (switchingState.HasActiveKey &&
            key.Equals(switchingState.ActiveKey))
        {
            switchingState.ActiveContext = context;
            switchingState.HasActiveContext = true;
            MarkFrameOpResourcePlannerStateUsed(switchingState, key);
            return true;
        }

        SaveActiveFrameOpResourcePlannerState();

        if (!switchingState.States.TryGetValue(key, out ResourcePlannerRuntimeState state))
            return false;

        AssertFrameOpPlannerStateMatchesContext(state, key, context);
        RestoreResourcePlannerRuntimeState(state);
        switchingState.ActiveKey = key;
        switchingState.HasActiveKey = true;
        switchingState.ActiveContext = context;
        switchingState.HasActiveContext = true;
        MarkFrameOpResourcePlannerStateUsed(switchingState, key);
        return true;
    }

    private static bool TryFindActiveFrameOpPlannerStateKey(
        in FrameOpContext context,
        FrameOpResourcePlannerSwitchingState switchingState,
        out FrameOpPlannerStateKey key)
    {
        foreach (FrameOpPlannerStateKey activeKey in switchingState.ActiveKeys)
        {
            if (!FrameOpContextMatchesPlannerStateKey(context, activeKey))
                continue;

            key = activeKey;
            return true;
        }

        key = default;
        return false;
    }

    private void SaveActiveFrameOpResourcePlannerState()
    {
        if (!IsDeviceOperational)
            return;

        FrameOpResourcePlannerSwitchingState switchingState = ActiveFrameOpResourcePlannerSwitchingState;
        if (!switchingState.RecordingScopeActive ||
            !switchingState.HasActiveKey ||
            !switchingState.HasActiveContext)
        {
            return;
        }

        ResourcePlannerRuntimeState state = CaptureResourcePlannerRuntimeState();
        state.LastActiveFrameOpContext = switchingState.ActiveContext;
        switchingState.States[switchingState.ActiveKey] = state;
        MarkFrameOpResourcePlannerStateUsed(switchingState, switchingState.ActiveKey);
    }

    private void DestroyFrameOpResourcePlannerStates()
    {
        FrameOpResourcePlannerSwitchingState switchingState = ActiveFrameOpResourcePlannerSwitchingState;
        if (switchingState.States.Count == 0 && !switchingState.HasPreparationState)
            return;

        ResourcePlannerRuntimeState previousState = CaptureResourcePlannerRuntimeState();
        WaitForAllInFlightWork();
        HashSet<VulkanResourceAllocator> retiredAllocators = new(ReferenceEqualityComparer.Instance);
        foreach (KeyValuePair<FrameOpPlannerStateKey, ResourcePlannerRuntimeState> pair in switchingState.States)
        {
            RetireResourcePlannerRuntimeStateAllocator(
                pair.Value,
                retiredAllocators,
                $"FrameOpResourcePlannerStateDestroy.pipe{pair.Key.PipelineIdentity}.vp{pair.Key.ViewportIdentity}");
        }

        if (switchingState.HasPreparationState)
        {
            RetireResourcePlannerRuntimeStateAllocator(
                switchingState.PreparationState,
                retiredAllocators,
                "FrameOpResourcePlannerPreparationStateDestroy");
        }

        switchingState.States.Clear();
        switchingState.LastUsedSerials.Clear();
        switchingState.ActiveKeys.Clear();
        switchingState.SwitchingActive = false;
        switchingState.RecordingScopeActive = false;
        switchingState.HasActiveKey = false;
        switchingState.HasActiveContext = false;
        switchingState.PreparationState = default;
        switchingState.HasPreparationState = false;
        if (previousState.ResourceAllocator is not null && previousState.ResourceAllocator.IsRetired)
            previousState = ResourcePlannerRuntimeState.CreateEmpty();
        RestoreResourcePlannerRuntimeState(previousState);
        if (!IsDeviceLost)
            ForceFlushAllRetiredResourcesAfterWaiting("FrameOpResourcePlannerStateDestroy");
    }

    private static bool IsAllocatorOwnedByFrameOpPlannerState(
        FrameOpResourcePlannerSwitchingState switchingState,
        VulkanResourceAllocator allocator)
    {
        foreach (ResourcePlannerRuntimeState state in switchingState.States.Values)
        {
            if (ReferenceEquals(state.ResourceAllocator, allocator))
                return true;
        }

        return switchingState.HasPreparationState &&
            ReferenceEquals(switchingState.PreparationState.ResourceAllocator, allocator);
    }

    private void RetireResourcePlannerRuntimeStateAllocator(
        in ResourcePlannerRuntimeState state,
        HashSet<VulkanResourceAllocator> retiredAllocators,
        string reason)
    {
        VulkanResourceAllocator allocator = state.ResourceAllocator;
        if (allocator is null || !retiredAllocators.Add(allocator) || allocator.IsRetired)
            return;

        RestoreResourcePlannerRuntimeState(state);
        _ = allocator.TryRetirePhysicalResources(this);
    }

    private void RetireResourcePlannerRuntimeStateAllocators(
        in ResourcePlannerRuntimeState state,
        HashSet<VulkanResourceAllocator> retiredAllocators,
        string reason)
    {
        RetireResourcePlannerRuntimeStateAllocator(state, retiredAllocators, reason);

        FrameOpResourcePlannerSwitchingState? switchingState = state.FrameOpResourcePlannerSwitchingState;
        if (switchingState is null)
            return;

        foreach (ResourcePlannerRuntimeState nestedState in switchingState.States.Values)
            RetireResourcePlannerRuntimeStateAllocator(nestedState, retiredAllocators, reason);

        if (switchingState.HasPreparationState)
            RetireResourcePlannerRuntimeStateAllocator(switchingState.PreparationState, retiredAllocators, reason);
    }

    [Conditional("DEBUG")]
    private static void AssertResourcePlannerRuntimeStateCanBeRestored(in ResourcePlannerRuntimeState state)
    {
        if (state.ResourcePlanner is null)
            throw new InvalidOperationException("A cached frame-op planner state has no resource planner.");
        if (state.ResourceAllocator is null)
            throw new InvalidOperationException("A cached frame-op planner state has no resource allocator.");
        if (state.BarrierPlanner is null)
            throw new InvalidOperationException("A cached frame-op planner state has no barrier planner.");
        if (state.ResourceAllocator.OwnershipId != state.AllocatorOwnershipId)
        {
            throw new InvalidOperationException(
                $"Cached frame-op planner allocator ownership changed from {state.AllocatorOwnershipId} to {state.ResourceAllocator.OwnershipId}.");
        }
        if (state.ResourceAllocator.IsRetired)
            throw new InvalidOperationException($"Cached frame-op planner allocator owner {state.AllocatorOwnershipId} is retired.");
    }

    [Conditional("DEBUG")]
    private void AssertFrameOpPlannerAllocatorOwnership(FrameOpResourcePlannerSwitchingState switchingState)
    {
        foreach (KeyValuePair<FrameOpPlannerStateKey, ResourcePlannerRuntimeState> first in switchingState.States)
        {
            AssertResourcePlannerRuntimeStateCanBeRestored(first.Value);
            foreach (KeyValuePair<FrameOpPlannerStateKey, ResourcePlannerRuntimeState> second in switchingState.States)
            {
                if (first.Key.Equals(second.Key))
                    continue;

                if (ReferenceEquals(first.Value.ResourceAllocator, second.Value.ResourceAllocator))
                {
                    throw new InvalidOperationException(
                        $"Frame-op planner states {first.Key} and {second.Key} share allocator owner {first.Value.AllocatorOwnershipId} without an explicit sharing policy.");
                }
            }

            if (switchingState.HasPreparationState)
            {
                if (ReferenceEquals(first.Value.ResourceAllocator, switchingState.PreparationState.ResourceAllocator))
                {
                    throw new InvalidOperationException(
                        $"Frame-op planner state {first.Key} shares allocator owner {first.Value.AllocatorOwnershipId} with the merged preparation state.");
                }
            }
        }

        if (switchingState.HasPreparationState)
            AssertResourcePlannerRuntimeStateCanBeRestored(switchingState.PreparationState);
    }

    [Conditional("DEBUG")]
    private static void AssertFrameOpPlannerStateMatchesContext(
        in ResourcePlannerRuntimeState state,
        in FrameOpPlannerStateKey key,
        in FrameOpContext context)
    {
        AssertResourcePlannerRuntimeStateCanBeRestored(state);
        if (context.ResourceGeneration != key.ResourceGeneration)
        {
            throw new InvalidOperationException(
                $"Frame-op planner context generation {context.ResourceGeneration} does not match key generation {key.ResourceGeneration} for {key}.");
        }
        if (state.LastActiveFrameOpContext is not FrameOpContext lastContext)
            return;

        // Keyed allocators intentionally retain the merged registry used to build
        // the physical plan. Its registry signature is therefore a superset of
        // the original frame-op key even though the stable execution identity
        // must continue to match that key.
        if (!FrameOpContextMatchesPlannerStateKeyIgnoringRegistry(lastContext, key))
            throw new InvalidOperationException($"Cached frame-op planner context does not match key {key}.");
        if (!FrameOpContextMatchesPlannerStateKey(context, key))
            throw new InvalidOperationException($"Active frame-op planner context does not match key {key}.");
    }

    private static HashSet<int>? BuildActiveFrameOpPassSet(FrameOp[] ops)
    {
        HashSet<int> passIndices = [];
        foreach (FrameOp op in ops)
        {
            if (op.PassIndex != int.MinValue)
                passIndices.Add(op.PassIndex);
        }

        return passIndices.Count > 0 ? passIndices : null;
    }

    private static HashSet<int>? BuildActiveFrameOpPassSet(FrameOp[] ops, in FrameOpPlannerStateKey key)
    {
        HashSet<int> passIndices = [];
        foreach (FrameOp op in ops)
        {
            if (!FrameOpMatchesPlannerStateKey(op, key))
                continue;

            if (op.PassIndex != int.MinValue)
                passIndices.Add(op.PassIndex);
        }

        return passIndices.Count > 0 ? passIndices : null;
    }

    private static HashSet<string>? BuildActiveFrameOpFrameBufferSet(FrameOp[] ops)
    {
        HashSet<string> frameBufferNames = new(StringComparer.OrdinalIgnoreCase);
        foreach (FrameOp op in ops)
        {
            AddFrameBufferName(frameBufferNames, op.Target);

            if (op is BlitOp blit)
            {
                AddFrameBufferName(frameBufferNames, blit.InFbo);
                AddFrameBufferName(frameBufferNames, blit.OutFbo);
            }
        }

        return frameBufferNames.Count > 0 ? frameBufferNames : null;
    }

    private static HashSet<string>? BuildActiveFrameOpFrameBufferSet(FrameOp[] ops, in FrameOpPlannerStateKey key)
    {
        HashSet<string> frameBufferNames = new(StringComparer.OrdinalIgnoreCase);
        foreach (FrameOp op in ops)
        {
            if (!FrameOpMatchesPlannerStateKey(op, key))
                continue;

            AddFrameBufferName(frameBufferNames, op.Target);

            if (op is BlitOp blit)
            {
                AddFrameBufferName(frameBufferNames, blit.InFbo);
                AddFrameBufferName(frameBufferNames, blit.OutFbo);
            }
        }

        return frameBufferNames.Count > 0 ? frameBufferNames : null;
    }

    private static void AddFrameBufferName(HashSet<string> frameBufferNames, XRFrameBuffer? frameBuffer)
    {
        string? name = frameBuffer?.Name;
        if (!string.IsNullOrWhiteSpace(name))
            frameBufferNames.Add(name);
    }

    private static bool FrameOpContextHasPlannerResources(in FrameOpContext context)
        => context.ResourceRegistry is not null ||
            context.PassMetadata is { Count: > 0 };

    internal static FrameOpPlannerStateKey BuildFrameOpPlannerStateKey(in FrameOpContext context)
        => new(
            context.ContextKind,
            context.PipelineIdentity,
            context.ViewportIdentity,
            context.DisplayWidth,
            context.DisplayHeight,
            context.InternalWidth,
            context.InternalHeight,
            context.OutputFrameBufferIdentity,
            ResolveResourcePlanOutputTargetIdentity(context),
            ResolveFrameOpContextResourceRegistrySignature(context),
            ComputePassMetadataSignature(context.PassMetadata),
            context.ResourceGeneration,
            context.SubmissionQueueFamily);

    /// <summary>
    /// Returns the physical-plan identity for an output. Command recording continues to use the
    /// concrete target identity, but rotating desktop target/FBO instances must not manufacture a
    /// new allocator owner when their pipeline, named attachment contract, and extent are compatible.
    /// </summary>
    internal static int ResolveResourcePlanOutputTargetIdentity(in FrameOpContext context)
    {
        if (context.ContextKind != EVulkanFrameOpContextKind.MainViewport)
            return context.OutputTargetIdentity;

        if (context.OutputFrameBufferIdentity != 0)
            return context.OutputFrameBufferIdentity;

        return HashCode.Combine(
            (int)context.ContextKind,
            context.PipelineIdentity,
            context.ViewportIdentity);
    }

    private static bool FrameOpMatchesPlannerStateKey(FrameOp op, in FrameOpPlannerStateKey key)
        => FrameOpContextHasPlannerResources(op.Context) &&
            FrameOpContextMatchesPlannerStateKey(op.Context, key);

    private static bool FrameOpContextMatchesPlannerStateKey(in FrameOpContext context, in FrameOpPlannerStateKey key)
        => context.ContextKind == key.ContextKind &&
            context.PipelineIdentity == key.PipelineIdentity &&
            context.ViewportIdentity == key.ViewportIdentity &&
            context.DisplayWidth == key.DisplayWidth &&
            context.DisplayHeight == key.DisplayHeight &&
            context.InternalWidth == key.InternalWidth &&
            context.InternalHeight == key.InternalHeight &&
            context.OutputFrameBufferIdentity == key.OutputFrameBufferIdentity &&
            ResolveResourcePlanOutputTargetIdentity(context) == key.OutputTargetIdentity &&
            ResolveFrameOpContextResourceRegistrySignature(context) == key.ResourceRegistrySignature &&
            ComputePassMetadataSignature(context.PassMetadata) == key.PassMetadataSignature &&
            context.ResourceGeneration == key.ResourceGeneration &&
            context.SubmissionQueueFamily == key.SubmissionQueueFamily;

    private static bool FrameOpContextMatchesPlannerStateKeyIgnoringRegistry(
        in FrameOpContext context,
        in FrameOpPlannerStateKey key)
        => context.ContextKind == key.ContextKind &&
            context.PipelineIdentity == key.PipelineIdentity &&
            context.ViewportIdentity == key.ViewportIdentity &&
            context.DisplayWidth == key.DisplayWidth &&
            context.DisplayHeight == key.DisplayHeight &&
            context.InternalWidth == key.InternalWidth &&
            context.InternalHeight == key.InternalHeight &&
            context.OutputFrameBufferIdentity == key.OutputFrameBufferIdentity &&
            ResolveResourcePlanOutputTargetIdentity(context) == key.OutputTargetIdentity &&
            ComputePassMetadataSignature(context.PassMetadata) == key.PassMetadataSignature &&
            context.ResourceGeneration == key.ResourceGeneration &&
            context.SubmissionQueueFamily == key.SubmissionQueueFamily;

    private static int ComputeOutputFrameBufferIdentity(string? outputFrameBufferName)
        => string.IsNullOrWhiteSpace(outputFrameBufferName)
            ? 0
            : StringComparer.OrdinalIgnoreCase.GetHashCode(outputFrameBufferName!);

    private static int ResolveFrameOpContextResourceRegistrySignature(in FrameOpContext context)
        => context.ResourceRegistrySignatureSnapshot ?? ComputeResourceRegistrySignature(context.ResourceRegistry);

    private FrameOpContext RefreshPlannerExtentsFromLiveContext(FrameOpContext context, FrameOp[] ops)
    {
        FrameOpPlannerStateKey ignoredKey = default;
        return RefreshPlannerExtentsFromLiveContext(context, ops, filterByPlannerKey: false, ignoredKey);
    }

    private FrameOpContext RefreshPlannerExtentsFromLiveContext(
        FrameOpContext context,
        FrameOp[] ops,
        bool filterByPlannerKey,
        in FrameOpPlannerStateKey plannerKey)
    {
        if (TryResolveExternalSwapchainTargetExtent(out Extent2D externalExtent))
        {
            var dimensions = ResolveExternalFrameOpResourceDimensions(
                externalExtent,
                context.PipelineInstance?.ResourceInternalWidth,
                context.PipelineInstance?.ResourceInternalHeight,
                viewportInternalWidth: null,
                viewportInternalHeight: null,
                contextInternalWidth: context.InternalWidth,
                contextInternalHeight: context.InternalHeight);
            if (context.DisplayWidth == dimensions.DisplayWidth &&
                context.DisplayHeight == dimensions.DisplayHeight &&
                context.InternalWidth == dimensions.InternalWidth &&
                context.InternalHeight == dimensions.InternalHeight)
            {
                return context;
            }

            Debug.VulkanEvery(
                $"Vulkan.ResourcePlanner.ExternalFrameOpExtents.{context.PipelineIdentity}.{context.ViewportIdentity}",
                TimeSpan.FromSeconds(1),
                "[VulkanResourcePlanner] Refreshing external swapchain frame-op planner extents. Old={0}x{1}/{2}x{3} New={4}x{5}/{6}x{7}.",
                context.DisplayWidth,
                context.DisplayHeight,
                context.InternalWidth,
                context.InternalHeight,
                dimensions.DisplayWidth,
                dimensions.DisplayHeight,
                dimensions.InternalWidth,
                dimensions.InternalHeight);

            return RefreshFrameOpContextRecordingFingerprint(context with
            {
                DisplayWidth = dimensions.DisplayWidth,
                DisplayHeight = dimensions.DisplayHeight,
                InternalWidth = dimensions.InternalWidth,
                InternalHeight = dimensions.InternalHeight
            });
        }

        if (XRWindow.IsInteractiveResizeInProgress)
            return ApplyInteractiveResizePlannerFreeze(context);

        if (IsRenderingExternalSwapchainTarget)
            return context;

        FrameOpContext live = CaptureFrameOpContextOrLastActive();
        bool refreshExtents =
            ReferenceEquals(context.PipelineInstance, live.PipelineInstance) ||
            ReferenceEquals(context.ResourceRegistry, live.ResourceRegistry);

        if (!refreshExtents)
        {
            foreach (FrameOp op in ops)
            {
                if (filterByPlannerKey && !FrameOpMatchesPlannerStateKey(op, plannerKey))
                    continue;

                if (VulkanRenderGraphCompiler.OpTargetsSwapchain(op))
                {
                    refreshExtents = true;
                    break;
                }
            }
        }

        if (!refreshExtents)
            return context;

        uint displayWidth = live.DisplayWidth > 0 ? live.DisplayWidth : context.DisplayWidth;
        uint displayHeight = live.DisplayHeight > 0 ? live.DisplayHeight : context.DisplayHeight;
        uint internalWidth = live.InternalWidth > 0 ? live.InternalWidth : context.InternalWidth;
        uint internalHeight = live.InternalHeight > 0 ? live.InternalHeight : context.InternalHeight;

        if (displayWidth == context.DisplayWidth &&
            displayHeight == context.DisplayHeight &&
            internalWidth == context.InternalWidth &&
            internalHeight == context.InternalHeight)
        {
            return context;
        }

        if (VulkanFrameDiagnosticsTraceEnabled)
        {
            Debug.VulkanEvery(
                $"Vulkan.ResourcePlanner.RefreshFrameOpExtents.{context.PipelineIdentity}.{context.ViewportIdentity}",
                TimeSpan.FromSeconds(1),
                "[VulkanResourcePlanner] Refreshing frame-op planner extents from live viewport. Old={0}x{1}/{2}x{3} Live={4}x{5}/{6}x{7}.",
                context.DisplayWidth,
                context.DisplayHeight,
                context.InternalWidth,
                context.InternalHeight,
                displayWidth,
                displayHeight,
                internalWidth,
                internalHeight);
        }

        return RefreshFrameOpContextRecordingFingerprint(context with
        {
            DisplayWidth = displayWidth,
            DisplayHeight = displayHeight,
            InternalWidth = internalWidth,
            InternalHeight = internalHeight
        });
    }

    private static FrameOpContext SelectPrimaryPlannerContext(FrameOp[] ops)
    {
        FrameOpContext fallback = ops[0].Context;
        FrameOpContext best = fallback;
        int bestScore = int.MinValue;

        foreach (FrameOp op in ops)
        {
            FrameOpContext context = op.Context;
            if (context.ResourceRegistry is null)
                continue;

            int score = 1;
            score += Math.Min(context.ResourceRegistry.TextureRecords.Count, 128);
            score += Math.Min(context.ResourceRegistry.FrameBufferRecords.Count, 128) * 2;
            score += (context.PassMetadata?.Count ?? 0) * 4;
            if (VulkanRenderGraphCompiler.OpTargetsSwapchain(op))
                score += 16;

            score += ScoreFrameOpFrameBufferTargets(op, context.ResourceRegistry);

            if (score > bestScore ||
                (score == bestScore && ComparePlannerContextTieBreak(context, best) < 0))
            {
                bestScore = score;
                best = context;
            }
        }

        return best;
    }

    private static FrameOpContext SelectPrimaryPlannerContext(FrameOp[] ops, in FrameOpPlannerStateKey key)
    {
        FrameOpContext best = default;
        bool hasBest = false;
        int bestScore = int.MinValue;

        foreach (FrameOp op in ops)
        {
            if (!FrameOpMatchesPlannerStateKey(op, key))
                continue;

            FrameOpContext context = op.Context;
            if (!hasBest)
            {
                best = context;
                hasBest = true;
            }

            if (context.ResourceRegistry is null)
                continue;

            int score = 1;
            score += Math.Min(context.ResourceRegistry.TextureRecords.Count, 128);
            score += Math.Min(context.ResourceRegistry.FrameBufferRecords.Count, 128) * 2;
            score += (context.PassMetadata?.Count ?? 0) * 4;
            if (VulkanRenderGraphCompiler.OpTargetsSwapchain(op))
                score += 16;

            score += ScoreFrameOpFrameBufferTargets(op, context.ResourceRegistry);

            if (score > bestScore ||
                (score == bestScore && ComparePlannerContextTieBreak(context, best) < 0))
            {
                bestScore = score;
                best = context;
            }
        }

        return hasBest ? best : SelectPrimaryPlannerContext(ops);
    }

    private static int ScoreFrameOpFrameBufferTargets(FrameOp op, RenderResourceRegistry registry)
    {
        int score = ScoreFrameOpFrameBufferTarget(op.Context.OutputFrameBuffer, registry);
        score += ScoreFrameOpFrameBufferTarget(op.Target, registry);
        if (op is BlitOp blit)
        {
            score += ScoreFrameOpFrameBufferTarget(blit.InFbo, registry);
            score += ScoreFrameOpFrameBufferTarget(blit.OutFbo, registry);
        }

        return score;
    }

    private static int ScoreFrameOpFrameBufferTarget(XRFrameBuffer? target, RenderResourceRegistry registry)
    {
        if (target is null)
            return 0;

        return !string.IsNullOrWhiteSpace(target.Name) &&
            registry.FrameBufferRecords.ContainsKey(target.Name)
                ? 256
                : 32;
    }

    private static int ComparePlannerContextTieBreak(in FrameOpContext left, in FrameOpContext right)
    {
        int compare = left.PipelineIdentity.CompareTo(right.PipelineIdentity);
        if (compare != 0)
            return compare;

        compare = ((int)left.ContextKind).CompareTo((int)right.ContextKind);
        if (compare != 0)
            return compare;

        compare = left.ViewportIdentity.CompareTo(right.ViewportIdentity);
        if (compare != 0)
            return compare;

        compare = ResolveFrameOpContextResourceRegistrySignature(left)
            .CompareTo(ResolveFrameOpContextResourceRegistrySignature(right));
        if (compare != 0)
            return compare;

        compare = left.OutputFrameBufferIdentity.CompareTo(right.OutputFrameBufferIdentity);
        if (compare != 0)
            return compare;

        compare = left.OutputTargetIdentity.CompareTo(right.OutputTargetIdentity);
        if (compare != 0)
            return compare;

        compare = left.ResourceGeneration.CompareTo(right.ResourceGeneration);
        if (compare != 0)
            return compare;

        compare = left.DescriptorGeneration.CompareTo(right.DescriptorGeneration);
        if (compare != 0)
            return compare;

        return ComputePassMetadataSignature(left.PassMetadata).CompareTo(ComputePassMetadataSignature(right.PassMetadata));
    }

    private static uint ResolvePositiveDimension(uint? primary, int? secondary, uint tertiary, uint fallback)
    {
        if (primary.HasValue && primary.Value > 0)
            return primary.Value;

        if (secondary.HasValue && secondary.Value > 0)
            return (uint)secondary.Value;

        return tertiary > 0 ? tertiary : fallback;
    }

    internal static (uint DisplayWidth, uint DisplayHeight, uint InternalWidth, uint InternalHeight) ResolveExternalFrameOpResourceDimensions(
        in Extent2D externalExtent,
        uint? pipelineInternalWidth,
        uint? pipelineInternalHeight,
        int? viewportInternalWidth,
        int? viewportInternalHeight,
        uint contextInternalWidth = 0u,
        uint contextInternalHeight = 0u)
    {
        uint displayWidth = Math.Max(externalExtent.Width, 1u);
        uint displayHeight = Math.Max(externalExtent.Height, 1u);
        uint internalWidth = ResolvePositiveDimension(
            pipelineInternalWidth,
            viewportInternalWidth,
            contextInternalWidth,
            displayWidth);
        uint internalHeight = ResolvePositiveDimension(
            pipelineInternalHeight,
            viewportInternalHeight,
            contextInternalHeight,
            displayHeight);

        return (displayWidth, displayHeight, internalWidth, internalHeight);
    }

    private Extent2D ResolveFrameOpContextFallbackExtent()
    {
        if (TryResolveExternalSwapchainTargetExtent(out Extent2D externalExtent))
            return externalExtent;

        return swapChainExtent;
    }

    private VulkanResourceExtentContext BuildResourceExtentContext(in FrameOpContext context)
    {
        if (TryResolveExternalSwapchainTargetExtent(out Extent2D externalExtent))
        {
            var dimensions = ResolveExternalFrameOpResourceDimensions(
                externalExtent,
                context.PipelineInstance?.ResourceInternalWidth,
                context.PipelineInstance?.ResourceInternalHeight,
                viewportInternalWidth: null,
                viewportInternalHeight: null,
                contextInternalWidth: context.InternalWidth,
                contextInternalHeight: context.InternalHeight);
            return new VulkanResourceExtentContext(
                dimensions.DisplayWidth,
                dimensions.DisplayHeight,
                dimensions.InternalWidth,
                dimensions.InternalHeight);
        }

        Extent2D fallbackExtent = ResolveFrameOpContextFallbackExtent();
        uint displayWidth = context.DisplayWidth > 0
            ? context.DisplayWidth
            : Math.Max(fallbackExtent.Width, 1u);
        uint displayHeight = context.DisplayHeight > 0
            ? context.DisplayHeight
            : Math.Max(fallbackExtent.Height, 1u);
        uint internalWidth = context.InternalWidth > 0
            ? context.InternalWidth
            : displayWidth;
        uint internalHeight = context.InternalHeight > 0
            ? context.InternalHeight
            : displayHeight;

        return new VulkanResourceExtentContext(
            displayWidth,
            displayHeight,
            internalWidth,
            internalHeight);
    }

    private bool TryResolveExternalSwapchainTargetExtent(out Extent2D extent)
    {
        if (TryGetExternalSwapchainTargetRegion(out BoundingRectangle region) &&
            region.Width > 0 &&
            region.Height > 0)
        {
            extent = new Extent2D(
                (uint)region.Width,
                (uint)region.Height);
            return true;
        }

        if (IsRenderingExternalSwapchainTarget)
            throw new InvalidOperationException("OpenXR external swapchain rendering is active, but no valid external target extent is bound.");

        extent = default;
        return false;
    }

    private RenderResourceRegistry? BuildMergedFrameOpRegistry(
        FrameOp[] ops,
        in FrameOpContext primaryContext,
        ulong frameOpsSignature = 0)
    {
        RenderResourceRegistry? primaryRegistry = primaryContext.ResourceRegistry;
        FrameOpPlannerStateKey ownerKey = BuildFrameOpPlannerStateKey(primaryContext);
        if (frameOpsSignature != 0)
        {
            for (int cacheIndex = 0; cacheIndex < _mergedFrameOpRegistryCache.Count; cacheIndex++)
            {
                MergedFrameOpRegistryCacheEntry entry = _mergedFrameOpRegistryCache[cacheIndex];
                if (entry.FrameOpsSignature != frameOpsSignature || !entry.OwnerKey.Equals(ownerKey))
                    continue;

                entry.LastUsedFrameId = RuntimeEngine.Rendering.State.RenderFrameId;
                return entry.MergedRegistry;
            }
        }

        List<RenderResourceRegistry> registries = CollectUniqueFrameOpRegistries(ops);
        int frameBufferDescriptorSignature = ComputeFrameOpFrameBufferDescriptorSignature(ops);
        bool hasFrameBufferDescriptors = frameBufferDescriptorSignature != 0;

        // Shadow command collections are conditional, but their logical resources are structural
        // for the owning pipeline generation. Once a source registry participates in this owner's
        // plan, retain its descriptors until the compatibility key changes. Owner scoping prevents
        // a desktop shadow/source registry from mutating an eye, mirror, or capture plan.
        List<FrameOpRegistryCacheSource> cacheSources = BuildFrameOpRegistryCacheSources(registries);
        if (TryGetCachedMergedFrameOpRegistry(ownerKey, primaryRegistry, cacheSources, frameBufferDescriptorSignature, ops, out RenderResourceRegistry? cachedRegistry))
            return cachedRegistry;

        if (registries.Count == 0 && !hasFrameBufferDescriptors)
        {
            RememberResolvedFrameOpRegistry(
                ownerKey,
                primaryRegistry,
                cacheSources,
                frameBufferDescriptorSignature,
                frameOpsSignature,
                primaryRegistry);
            return primaryRegistry;
        }

        if (registries.Count == 1 && !hasFrameBufferDescriptors)
        {
            RenderResourceRegistry resolvedRegistry = registries[0];
            RememberResolvedFrameOpRegistry(
                ownerKey,
                primaryRegistry,
                cacheSources,
                frameBufferDescriptorSignature,
                frameOpsSignature,
                resolvedRegistry);
            return resolvedRegistry;
        }

        if (!hasFrameBufferDescriptors && primaryRegistry is not null && RegistriesCoveredByPrimary(registries, primaryRegistry))
        {
            RememberResolvedFrameOpRegistry(
                ownerKey,
                primaryRegistry,
                cacheSources,
                frameBufferDescriptorSignature,
                frameOpsSignature,
                primaryRegistry);
            return primaryRegistry;
        }

        RenderResourceRegistry merged = new();
        if (primaryRegistry is not null)
            AddRegistryDescriptors(merged, primaryRegistry, overwrite: true);

        for (int i = 0; i < registries.Count; i++)
        {
            RenderResourceRegistry registry = registries[i];
            if (ReferenceEquals(registry, primaryRegistry))
                continue;

            AddRegistryDescriptors(merged, registry, overwrite: false);
        }

        AddFrameOpFrameBufferDescriptors(merged, ops);

        RememberMergedFrameOpRegistry(
            ownerKey,
            primaryRegistry,
            cacheSources,
            frameBufferDescriptorSignature,
            frameOpsSignature,
            merged);
        return merged;
    }

    private void RememberResolvedFrameOpRegistry(
        in FrameOpPlannerStateKey ownerKey,
        RenderResourceRegistry? primaryRegistry,
        List<FrameOpRegistryCacheSource> cacheSources,
        int frameBufferDescriptorSignature,
        ulong frameOpsSignature,
        RenderResourceRegistry? resolvedRegistry)
    {
        if (frameOpsSignature == 0 || resolvedRegistry is null)
            return;

        RememberMergedFrameOpRegistry(
            ownerKey,
            primaryRegistry,
            cacheSources,
            frameBufferDescriptorSignature,
            frameOpsSignature,
            resolvedRegistry);
    }

    private List<RenderResourceRegistry> CollectUniqueFrameOpRegistries(FrameOp[] ops)
    {
        List<RenderResourceRegistry> registries = _frameOpRegistryScratch;
        registries.Clear();
        registries.EnsureCapacity(Math.Min(ops.Length, MaxFrameOpResourcePlannerSwitchingStates));
        for (int opIndex = 0; opIndex < ops.Length; opIndex++)
        {
            FrameOp op = ops[opIndex];
            if (op.Context.ResourceRegistry is not { } registry)
                continue;

            bool exists = false;
            for (int i = 0; i < registries.Count; i++)
            {
                if (ReferenceEquals(registries[i], registry))
                {
                    exists = true;
                    break;
                }
            }

            if (!exists)
                registries.Add(registry);
        }

        // List<T>.Sort(Comparison<T>) materializes a comparer wrapper on every call.
        // Registry lists are normally tiny, so insertion sort is both allocation-free
        // and cheaper than constructing sorting infrastructure in this per-frame path.
        for (int i = 1; i < registries.Count; i++)
        {
            RenderResourceRegistry value = registries[i];
            int valueIdentity = RuntimeHelpers.GetHashCode(value);
            int insertIndex = i;
            while (insertIndex > 0 &&
                   RuntimeHelpers.GetHashCode(registries[insertIndex - 1]) > valueIdentity)
            {
                registries[insertIndex] = registries[insertIndex - 1];
                insertIndex--;
            }

            registries[insertIndex] = value;
        }
        return registries;
    }

    private List<FrameOpRegistryCacheSource> BuildFrameOpRegistryCacheSources(
        List<RenderResourceRegistry> registries)
    {
        List<FrameOpRegistryCacheSource> sources = _frameOpRegistryCacheSourceScratch;
        sources.Clear();
        sources.EnsureCapacity(registries.Count);
        for (int i = 0; i < registries.Count; i++)
        {
            RenderResourceRegistry registry = registries[i];
            sources.Add(new FrameOpRegistryCacheSource(
                registry,
                ComputeResourceRegistrySignature(registry)));
        }

        return sources;
    }

    private bool TryGetCachedMergedFrameOpRegistry(
        in FrameOpPlannerStateKey ownerKey,
        RenderResourceRegistry? primaryRegistry,
        List<FrameOpRegistryCacheSource> sources,
        int frameBufferDescriptorSignature,
        FrameOp[] ops,
        out RenderResourceRegistry? mergedRegistry)
    {
        for (int i = 0; i < _mergedFrameOpRegistryCache.Count; i++)
        {
            MergedFrameOpRegistryCacheEntry entry = _mergedFrameOpRegistryCache[i];
            if (!entry.OwnerKey.Equals(ownerKey))
                continue;

            bool descriptorsChanged = false;
            FrameOpRegistryCacheSource[] accumulatedSources = entry.Sources;
            List<FrameOpRegistryCacheSource>? updatedSources = null;
            for (int sourceIndex = 0; sourceIndex < sources.Count; sourceIndex++)
            {
                FrameOpRegistryCacheSource current = sources[sourceIndex];
                int accumulatedIndex = IndexOfFrameOpRegistryCacheSource(accumulatedSources, current);
                if (accumulatedIndex >= 0 &&
                    accumulatedSources[accumulatedIndex].DescriptorSignature == current.DescriptorSignature)
                {
                    continue;
                }

                updatedSources ??= [.. accumulatedSources];
                if (accumulatedIndex >= 0)
                    updatedSources[accumulatedIndex] = current;
                else
                    updatedSources.Add(current);
                descriptorsChanged = true;
            }

            if (updatedSources is not null)
            {
                accumulatedSources = [.. updatedSources];
                entry.Sources = accumulatedSources;
            }

            int primaryDescriptorSignature = primaryRegistry?.DescriptorSignature ?? 0;
            if (entry.PrimaryDescriptorSignature != primaryDescriptorSignature)
            {
                entry.PrimaryDescriptorSignature = primaryDescriptorSignature;
                descriptorsChanged = true;
            }

            if (descriptorsChanged)
            {
                for (int sourceIndex = 0; sourceIndex < accumulatedSources.Length; sourceIndex++)
                {
                    RenderResourceRegistry source = accumulatedSources[sourceIndex].Registry;
                    AddRegistryDescriptors(entry.MergedRegistry, source, overwrite: true);
                }

                // The current primary wins any same-name descriptor conflict while descriptors
                // from conditional sources remain resident until the owner key changes.
                if (primaryRegistry is not null)
                    AddRegistryDescriptors(entry.MergedRegistry, primaryRegistry, overwrite: true);
            }

            if (entry.FrameBufferDescriptorSignature != frameBufferDescriptorSignature)
            {
                AddFrameOpFrameBufferDescriptors(entry.MergedRegistry, ops, overwrite: true);
                entry.FrameBufferDescriptorSignature = frameBufferDescriptorSignature;
            }

            entry.LastUsedFrameId = RuntimeEngine.Rendering.State.RenderFrameId;
            mergedRegistry = entry.MergedRegistry;
            return true;
        }

        mergedRegistry = null;
        return false;
    }

    private static int IndexOfFrameOpRegistryCacheSource(
        FrameOpRegistryCacheSource[] sources,
        in FrameOpRegistryCacheSource current)
    {
        for (int i = 0; i < sources.Length; i++)
        {
            if (ReferenceEquals(sources[i].Registry, current.Registry))
                return i;
        }

        // Frame commands may produce short-lived registry wrappers for the same
        // immutable descriptor set. Treat those as one structural cache source;
        // retaining every wrapper would grow the source array and allocate on
        // every otherwise-stable frame.
        for (int i = 0; i < sources.Length; i++)
        {
            FrameOpRegistryCacheSource existing = sources[i];
            if (existing.DescriptorSignature == current.DescriptorSignature &&
                RegistryDescriptorsEquivalent(existing.Registry, current.Registry))
            {
                return i;
            }
        }

        return -1;
    }

    private static bool RegistryDescriptorsEquivalent(
        RenderResourceRegistry left,
        RenderResourceRegistry right)
        => left.TextureRecords.Count == right.TextureRecords.Count &&
            left.FrameBufferRecords.Count == right.FrameBufferRecords.Count &&
            left.BufferRecords.Count == right.BufferRecords.Count &&
            TextureDescriptorsCoveredByPrimary(left, right) &&
            FrameBufferDescriptorsCoveredByPrimary(left, right) &&
            BufferDescriptorsCoveredByPrimary(left, right);

    private void RememberMergedFrameOpRegistry(
        in FrameOpPlannerStateKey ownerKey,
        RenderResourceRegistry? primaryRegistry,
        List<FrameOpRegistryCacheSource> sources,
        int frameBufferDescriptorSignature,
        ulong frameOpsSignature,
        RenderResourceRegistry mergedRegistry)
    {
        ulong frameId = RuntimeEngine.Rendering.State.RenderFrameId;
        _mergedFrameOpRegistryCache.Add(new MergedFrameOpRegistryCacheEntry(
            ownerKey,
            primaryRegistry,
            sources.ToArray(),
            frameBufferDescriptorSignature,
            frameOpsSignature,
            mergedRegistry,
            frameId));

        if (_mergedFrameOpRegistryCache.Count <= MaxMergedFrameOpRegistryCacheEntries)
            return;

        int oldestIndex = 0;
        ulong oldestFrameId = _mergedFrameOpRegistryCache[0].LastUsedFrameId;
        for (int i = 1; i < _mergedFrameOpRegistryCache.Count; i++)
        {
            ulong candidateFrameId = _mergedFrameOpRegistryCache[i].LastUsedFrameId;
            if (candidateFrameId < oldestFrameId)
            {
                oldestIndex = i;
                oldestFrameId = candidateFrameId;
            }
        }

        _mergedFrameOpRegistryCache.RemoveAt(oldestIndex);
    }

    private static bool RegistriesCoveredByPrimary(
        IEnumerable<RenderResourceRegistry> registries,
        RenderResourceRegistry primaryRegistry)
    {
        foreach (RenderResourceRegistry registry in registries)
        {
            if (ReferenceEquals(registry, primaryRegistry))
                continue;

            if (!TextureDescriptorsCoveredByPrimary(registry, primaryRegistry) ||
                !FrameBufferDescriptorsCoveredByPrimary(registry, primaryRegistry) ||
                !BufferDescriptorsCoveredByPrimary(registry, primaryRegistry))
                return false;
        }

        return true;
    }

    private static bool TextureDescriptorsCoveredByPrimary(
        RenderResourceRegistry source,
        RenderResourceRegistry primary)
    {
        foreach (KeyValuePair<string, RenderTextureResource> pair in source.TextureRecords)
            if (!primary.TextureRecords.TryGetValue(pair.Key, out RenderTextureResource? primaryRecord) ||
                !EqualityComparer<TextureResourceDescriptor>.Default.Equals(primaryRecord.Descriptor, pair.Value.Descriptor))
                return false;

        return true;
    }

    private static bool FrameBufferDescriptorsCoveredByPrimary(
        RenderResourceRegistry source,
        RenderResourceRegistry primary)
    {
        foreach (KeyValuePair<string, RenderFrameBufferResource> pair in source.FrameBufferRecords)
            if (!primary.FrameBufferRecords.TryGetValue(pair.Key, out RenderFrameBufferResource? primaryRecord) ||
                !FrameBufferDescriptorsEquivalent(primaryRecord.Descriptor, pair.Value.Descriptor))
                return false;
        
        return true;
    }

    private static bool BufferDescriptorsCoveredByPrimary(
        RenderResourceRegistry source,
        RenderResourceRegistry primary)
    {
        foreach (KeyValuePair<string, RenderBufferResource> pair in source.BufferRecords)
            if (!primary.BufferRecords.TryGetValue(pair.Key, out RenderBufferResource? primaryRecord) ||
                !EqualityComparer<BufferResourceDescriptor>.Default.Equals(primaryRecord.Descriptor, pair.Value.Descriptor))
                return false;
        
        return true;
    }

    private static bool FrameBufferDescriptorsEquivalent(
        FrameBufferResourceDescriptor left,
        FrameBufferResourceDescriptor right)
    {
        if (!string.Equals(left.Name, right.Name, StringComparison.OrdinalIgnoreCase) ||
            left.Lifetime != right.Lifetime ||
            left.SizePolicy != right.SizePolicy ||
            left.Attachments.Count != right.Attachments.Count)
            return false;

        for (int i = 0; i < left.Attachments.Count; i++)
        {
            FrameBufferAttachmentDescriptor leftAttachment = left.Attachments[i];
            FrameBufferAttachmentDescriptor rightAttachment = right.Attachments[i];
            if (!string.Equals(leftAttachment.ResourceName, rightAttachment.ResourceName, StringComparison.OrdinalIgnoreCase) ||
                leftAttachment.Attachment != rightAttachment.Attachment ||
                leftAttachment.MipLevel != rightAttachment.MipLevel ||
                leftAttachment.LayerIndex != rightAttachment.LayerIndex)
                return false;
        }

        return true;
    }

    internal static void AddRegistryDescriptors(
        RenderResourceRegistry destination,
        RenderResourceRegistry source,
        bool overwrite)
    {
        foreach (KeyValuePair<string, RenderTextureResource> pair in source.TextureRecords)
            if (overwrite || !destination.TextureRecords.ContainsKey(pair.Key))
                destination.RegisterTextureDescriptor(pair.Value.Descriptor);

        foreach (KeyValuePair<string, RenderFrameBufferResource> pair in source.FrameBufferRecords)
            if (overwrite || !destination.FrameBufferRecords.ContainsKey(pair.Key))
                destination.RegisterFrameBufferDescriptor(pair.Value.Descriptor);

        foreach (KeyValuePair<string, RenderBufferResource> pair in source.BufferRecords)
            if (overwrite || !destination.BufferRecords.ContainsKey(pair.Key))
                destination.RegisterBufferDescriptor(pair.Value.Descriptor);
    }

    private void AddFrameOpFrameBufferDescriptors(
        RenderResourceRegistry destination,
        FrameOp[] ops,
        bool overwrite = false)
    {
        List<XRFrameBuffer> frameBuffers = CollectUniqueFrameOpFrameBuffers(ops);
        for (int frameBufferIndex = 0; frameBufferIndex < frameBuffers.Count; frameBufferIndex++)
        {
            XRFrameBuffer frameBuffer = frameBuffers[frameBufferIndex];
            if (string.IsNullOrWhiteSpace(frameBuffer.Name))
                continue;

            if (frameBuffer.Targets is not null)
            {
                foreach (var (target, attachment, mipLevel, layerIndex) in frameBuffer.Targets)
                {
                    if (target is not XRTexture texture || string.IsNullOrWhiteSpace(texture.Name))
                        continue;

                    if (overwrite || !destination.TextureRecords.ContainsKey(texture.Name))
                    {
                        TextureResourceDescriptor textureDescriptor = RenderResourceDescriptorFactory.FromTexture(texture, RenderResourceLifetime.External);
                        destination.RegisterTextureDescriptor(EnrichTextureDescriptorForFrameBufferAttachment(textureDescriptor, texture, attachment, mipLevel, layerIndex));
                    }

                    if (texture is XRTextureViewBase view)
                    {
                        XRTexture viewedTexture = view.GetViewedTexture();
                        if (!string.IsNullOrWhiteSpace(viewedTexture.Name) &&
                            (overwrite || !destination.TextureRecords.ContainsKey(viewedTexture.Name)))
                        {
                            int sourceMipLevel = mipLevel >= 0 ? SaturatingAddToInt32(view.MinLevel, (uint)mipLevel) : mipLevel;
                            int sourceLayerIndex = layerIndex >= 0 ? SaturatingAddToInt32(view.MinLayer, (uint)layerIndex) : layerIndex;
                            TextureResourceDescriptor viewedDescriptor = RenderResourceDescriptorFactory.FromTexture(viewedTexture, RenderResourceLifetime.External);
                            destination.RegisterTextureDescriptor(EnrichTextureDescriptorForFrameBufferAttachment(viewedDescriptor, viewedTexture, attachment, sourceMipLevel, sourceLayerIndex));
                        }
                    }
                }
            }

            if (overwrite || !destination.FrameBufferRecords.ContainsKey(frameBuffer.Name))
                destination.RegisterFrameBufferDescriptor(RenderResourceDescriptorFactory.FromFrameBuffer(frameBuffer, RenderResourceLifetime.External));
        }
    }

    private int ComputeFrameOpFrameBufferDescriptorSignature(FrameOp[] ops)
    {
        HashCode hash = new();
        List<XRFrameBuffer> frameBuffers = CollectUniqueFrameOpFrameBuffers(ops);
        int namedFrameBufferCount = 0;
        for (int frameBufferIndex = 0; frameBufferIndex < frameBuffers.Count; frameBufferIndex++)
        {
            XRFrameBuffer frameBuffer = frameBuffers[frameBufferIndex];
            if (string.IsNullOrWhiteSpace(frameBuffer.Name))
                continue;

            hash.Add(frameBuffer.Name, StringComparer.OrdinalIgnoreCase);
            hash.Add(RenderResourceSizePolicy.Absolute(
                Math.Max(frameBuffer.Width, 1u),
                Math.Max(frameBuffer.Height, 1u)));
            if (frameBuffer.Targets is not null)
            {
                foreach (var (target, attachment, mipLevel, layerIndex) in frameBuffer.Targets)
                {
                    string resourceName = target switch
                    {
                        XRTexture texture => texture.Name ?? texture.GetDescribingName(),
                        _ => target?.GetType().Name ?? string.Empty
                    };
                    hash.Add(resourceName, StringComparer.OrdinalIgnoreCase);
                    hash.Add(attachment);
                    hash.Add(mipLevel);
                    hash.Add(layerIndex);
                }
            }
            namedFrameBufferCount++;
        }

        return namedFrameBufferCount == 0 ? 0 : hash.ToHashCode();
    }

    private List<XRFrameBuffer> CollectUniqueFrameOpFrameBuffers(FrameOp[] ops)
    {
        List<XRFrameBuffer> frameBuffers = _frameOpFrameBufferScratch;
        frameBuffers.Clear();
        frameBuffers.EnsureCapacity(Math.Min(ops.Length * 4, 256));
        for (int opIndex = 0; opIndex < ops.Length; opIndex++)
        {
            FrameOp op = ops[opIndex];
            AddUniqueFrameBuffer(frameBuffers, op.Context.OutputFrameBuffer);
            AddUniqueFrameBuffer(frameBuffers, op.Target);
            if (op is not BlitOp blit)
                continue;

            AddUniqueFrameBuffer(frameBuffers, blit.InFbo);
            AddUniqueFrameBuffer(frameBuffers, blit.OutFbo);
        }

        return frameBuffers;
    }

    private static void AddUniqueFrameBuffer(List<XRFrameBuffer> frameBuffers, XRFrameBuffer? candidate)
    {
        if (candidate is null)
            return;

        for (int i = 0; i < frameBuffers.Count; i++)
            if (ReferenceEquals(frameBuffers[i], candidate))
                return;

        frameBuffers.Add(candidate);
    }

    internal static bool RequiresResourcePlannerRebuild(in FrameOpContext previous, in FrameOpContext next)
    {
        if (!ReferenceEquals(previous.PipelineInstance, next.PipelineInstance))
            return true;

        if (!ReferenceEquals(previous.ResourceRegistry, next.ResourceRegistry))
            return true;

        if (!ReferenceEquals(previous.PassMetadata, next.PassMetadata))
            return true;

        return !string.Equals(
            previous.OutputFrameBufferName,
            next.OutputFrameBufferName,
            StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateResourcePlannerFromContext(
        in FrameOpContext context,
        HashSet<int>? activePassIndices = null,
        HashSet<string>? activeFrameBufferNames = null,
        int activeResourceSetSignature = 0,
        bool constrainToActivePassSet = false)
    {
        if (!IsDeviceOperational)
            return;

        if (IsCommandChainResourcePlanFrozen)
            throw new InvalidOperationException(
                $"Resource planner cannot be replaced while command-chain readers are using frozen plan revision {_commandChainFrozenResourcePlanRevision}.");
        
        int activePassSetSignature = ComputeActivePassSetSignature(activePassIndices);
        ResourcePlanningInputs planningInputs = PrepareResourcePlanningInputs(
            context,
            activePassIndices,
            activePassSetSignature,
            activeFrameBufferNames,
            activeResourceSetSignature,
            constrainToActivePassSet);

        if (CanReuseResourcePlannerFastPath(planningInputs.FastPathKey))
            return;

        ulong plannerSignature = ComputeResourcePlannerSignature(
            context,
            planningInputs.QueueOwnership,
            planningInputs.CompiledGraph,
            planningInputs.ActivePassMetadata);
        if (plannerSignature == ActiveResourcePlannerSignature)
        {
            RememberResourcePlannerFastPath(planningInputs.FastPathKey);
            return;
        }

        ResourcePlannerSignatureBreakdown signatureBreakdown = ComputeResourcePlannerSignatureBreakdown(
            context,
            planningInputs.QueueOwnership,
            planningInputs.CompiledGraph,
            planningInputs.ActivePassMetadata);
        Debug.VulkanEvery(
            $"Vulkan.ResourcePlanner.SignatureChange.{context.ContextKind}.{context.PipelineIdentity}.{context.ViewportIdentity}.{context.OutputTargetIdentity}",
            TimeSpan.FromSeconds(1),
            "[VulkanResourcePlanner] Signature changed for context kind={0} id={1} fingerprint=0x{2:X16}. Revision={3} Old=0x{4:X16} New=0x{5:X16} ChangedFields=[{6}] OldComponents=[{7}] NewComponents=[{8}]",
            context.ContextKind,
            context.ContextId,
            signatureBreakdown.CompatibilityFingerprint,
            ActiveResourcePlannerRevision,
            ActiveResourcePlannerSignature,
            plannerSignature,
            signatureBreakdown.DescribeDelta(ActiveResourcePlannerSignatureBreakdown),
            ActiveResourcePlannerSignatureBreakdown,
            signatureBreakdown);

        VulkanResourcePlanner pendingPlanner = BuildResourceDescriptorPlan(context, planningInputs.ActivePassMetadata);
        PhysicalAllocationPlan allocationPlan = BuildPhysicalAllocationPlan(
            context,
            pendingPlanner,
            planningInputs.ActivePassMetadata);
        LogPhysicalAllocationPlanStatus(context, pendingPlanner, allocationPlan, planningInputs.ActivePassMetadata);

        VulkanResourceAllocator oldAllocator = ResourceAllocator;
        VulkanResourceAllocator? pendingAllocator = null;
        HashSet<VulkanPhysicalImageGroup>? reusedImageGroups = null;
        int retiredImageCount = 0;
        int retiredBufferCount = 0;
        if (allocationPlan.Changed)
        {
            if (TryDescribeRecentResourceAllocationFailure(out string recentAllocationFailureReason))
            {
                Debug.VulkanEvery(
                    $"Vulkan.ResourcePlanner.DeferRecentAllocationRetry.{context.PipelineIdentity}.{context.ViewportIdentity}",
                    TimeSpan.FromSeconds(1),
                    "[VulkanResourcePlanner] Deferring physical resource plan after recent allocation failure. Planner=0x{0:X16} Allocation=0x{1:X16}. Reason={2}",
                    plannerSignature,
                    allocationPlan.Signature,
                    recentAllocationFailureReason);
                return;
            }

            if (!TryBuildPhysicalAllocator(
                context,
                pendingPlanner,
                allocationPlan.ExtentContext,
                planningInputs.ActivePassMetadata,
                out pendingAllocator,
                out reusedImageGroups,
                out retiredImageCount,
                out retiredBufferCount))
            {
                RecordResourceAllocationPlanFailure(plannerSignature, allocationPlan.Signature);
                return;
            }

            ClearResourceAllocationPlanFailure(plannerSignature, allocationPlan.Signature);
        }

        ActiveResourcePlanner = pendingPlanner;
        if (pendingAllocator is not null)
        {
            ActiveResourceAllocator = pendingAllocator;
            pendingAllocator.CommitReusedPhysicalImageMetadata();
        }

        CommitPhysicalAllocatorPlan(
            allocationPlan.Changed,
            oldAllocator,
            reusedImageGroups,
            retiredImageCount,
            retiredBufferCount,
            plannerSignature,
            allocationPlan.Signature);
        RebuildRenderGraphAndBarriers(planningInputs, plannerSignature, allocationPlan.Signature);

        ActiveResourcePlannerSignature = plannerSignature;
        ActiveResourceAllocationSignature = allocationPlan.Signature;
        ActiveResourcePlannerSignatureBreakdown = signatureBreakdown;
        ActiveResourcePlannerRevision++;
        RememberResourcePlannerFastPath(planningInputs.FastPathKey);
    }

    private ResourcePlanningInputs PrepareResourcePlanningInputs(
        in FrameOpContext context,
        HashSet<int>? activePassIndices,
        int activePassSetSignature,
        HashSet<string>? activeFrameBufferNames,
        int activeResourceSetSignature,
        bool constrainToActivePassSet)
    {
        IReadOnlyCollection<RenderPassMetadata>? activePassMetadata = FilterActivePassMetadata(
            context.PassMetadata,
            context.ResourceRegistry,
            context.ResourceRegistry?.DescriptorRevision ?? 0,
            activePassIndices,
            activePassSetSignature,
            activeFrameBufferNames,
            activeResourceSetSignature,
            constrainToActivePassSet);
        VulkanCompiledRenderGraph compiledGraph = _renderGraphCompiler.Compile(activePassMetadata);
        VulkanBarrierPlanner.QueueOwnershipConfig queueOwnership = BuildQueueOwnershipConfig(activePassMetadata);
        ResourcePlannerFastPathKey fastPathKey = new(
            context.ResourceRegistry,
            context.ResourceRegistry?.DescriptorRevision ?? 0,
            activePassMetadata,
            ComputePassMetadataRevisionStamp(activePassMetadata),
            activePassSetSignature,
            activeResourceSetSignature,
            ComputeOutputFrameBufferIdentity(context.OutputFrameBufferName),
            ResolveResourcePlanOutputTargetIdentity(context),
            context.DisplayWidth,
            context.DisplayHeight,
            context.InternalWidth,
            context.InternalHeight,
            queueOwnership,
            SupportsTransformFeedback);

        return new ResourcePlanningInputs(activePassMetadata, compiledGraph, queueOwnership, fastPathKey);
    }

    private bool CanReuseResourcePlannerFastPath(in ResourcePlannerFastPathKey key)
        => ActiveHasResourcePlannerFastPathKey
            && ActiveResourcePlannerSignature != ulong.MaxValue
            && key.Matches(ActiveResourcePlannerFastPathKey);

    private void RememberResourcePlannerFastPath(in ResourcePlannerFastPathKey key)
    {
        ActiveResourcePlannerFastPathKey = key;
        ActiveHasResourcePlannerFastPathKey = true;
    }

    private static VulkanResourcePlanner BuildResourceDescriptorPlan(
        in FrameOpContext context,
        IReadOnlyCollection<RenderPassMetadata>? activePassMetadata)
    {
        VulkanResourcePlanner pendingPlanner = new();
        pendingPlanner.Sync(context.ResourceRegistry, context.OutputFrameBufferName);
        ValidateVulkanResourcePlanMetadata(activePassMetadata, pendingPlanner);
        return pendingPlanner;
    }

    private PhysicalAllocationPlan BuildPhysicalAllocationPlan(
        in FrameOpContext context,
        VulkanResourcePlanner pendingPlanner,
        IReadOnlyCollection<RenderPassMetadata>? passMetadata)
    {
        VulkanResourceExtentContext extentContext = BuildResourceExtentContext(context);
        ulong allocationSignature = ComputeResourceAllocationSignature(
            context,
            pendingPlanner,
            passMetadata,
            extentContext,
            SupportsTransformFeedback);
        return new PhysicalAllocationPlan(
            extentContext,
            allocationSignature,
            allocationSignature != ActiveResourceAllocationSignature);
    }

    private void LogPhysicalAllocationPlanStatus(
        in FrameOpContext context,
        VulkanResourcePlanner pendingPlanner,
        in PhysicalAllocationPlan allocationPlan,
        IReadOnlyCollection<RenderPassMetadata>? activePassMetadata)
    {
        if (allocationPlan.Changed)
        {
            ResourceAllocationSignatureBreakdown allocationBreakdown = ComputeResourceAllocationSignatureBreakdown(
                context,
                pendingPlanner,
                activePassMetadata,
                allocationPlan.ExtentContext,
                SupportsTransformFeedback);
            Debug.VulkanEvery(
                $"Vulkan.ResourcePlanner.PhysicalPlanChange.{context.ContextKind}.{context.PipelineIdentity}.{context.ViewportIdentity}.{context.OutputTargetIdentity}",
                TimeSpan.FromSeconds(1),
                "[VulkanResourcePlanner] Physical resource plan changed for context kind={0} id={1} fingerprint=0x{2:X16}. Revision={3} Old=0x{4:X16} New=0x{5:X16} Components=[{6}]",
                context.ContextKind,
                context.ContextId,
                context.RecordingFingerprint,
                ActiveResourcePlannerRevision,
                ActiveResourceAllocationSignature,
                allocationPlan.Signature,
                allocationBreakdown);
            return;
        }

        Debug.VulkanEvery(
            $"Vulkan.ResourcePlanner.PhysicalPlanReuse.{context.ContextKind}.{context.PipelineIdentity}.{context.ViewportIdentity}.{context.OutputTargetIdentity}",
            TimeSpan.FromSeconds(1),
            "[VulkanResourcePlanner] Reusing physical resource plan for metadata-only graph change in context kind={0} id={1} fingerprint=0x{2:X16}. Revision={3} AllocationSignature=0x{4:X16}",
            context.ContextKind,
            context.ContextId,
            context.RecordingFingerprint,
            ActiveResourcePlannerRevision,
            allocationPlan.Signature);
    }

    private bool ShouldDeferFailedResourceAllocationRetry(
        ulong plannerSignature,
        ulong allocationSignature)
    {
        if (ActiveFailedResourcePlannerSignature != plannerSignature ||
            ActiveFailedResourceAllocationSignature != allocationSignature ||
            ActiveFailedResourceAllocationTimestamp == 0)
        {
            return false;
        }

        return Stopwatch.GetElapsedTime(ActiveFailedResourceAllocationTimestamp) <
            ResolveResourceAllocationFailureRetryDelay();
    }

    internal bool TryDescribeRecentResourceAllocationFailure(out string reason)
    {
        reason = string.Empty;

        long failureTimestamp = ActiveFailedResourceAllocationTimestamp;
        if (failureTimestamp == 0)
            return false;

        TimeSpan elapsed = Stopwatch.GetElapsedTime(failureTimestamp);
        TimeSpan retryDelay = ResolveResourceAllocationFailureRetryDelay();
        if (elapsed >= retryDelay)
            return false;

        reason =
            $"Vulkan resource planner is backing off after a failed physical allocation ({elapsed.TotalMilliseconds:F0}/{retryDelay.TotalMilliseconds:F0} ms, planner=0x{ActiveFailedResourcePlannerSignature:X16}, allocation=0x{ActiveFailedResourceAllocationSignature:X16})";
        return true;
    }

    private static TimeSpan ResolveResourceAllocationFailureRetryDelay()
    {
        IRuntimeRenderingHostServices host = RuntimeRenderingHostServices.Current;
        return host.IsOpenXRActive || host.IsInVR
            ? OpenXrResourceAllocationFailureRetryDelay
            : ResourceAllocationFailureRetryDelay;
    }

    private void RecordResourceAllocationPlanFailure(
        ulong plannerSignature,
        ulong allocationSignature)
    {
        ActiveFailedResourcePlannerSignature = plannerSignature;
        ActiveFailedResourceAllocationSignature = allocationSignature;
        ActiveFailedResourceAllocationTimestamp = Stopwatch.GetTimestamp();
    }

    private void ClearResourceAllocationPlanFailure(
        ulong plannerSignature,
        ulong allocationSignature)
    {
        if (ActiveFailedResourcePlannerSignature != plannerSignature ||
            ActiveFailedResourceAllocationSignature != allocationSignature)
        {
            return;
        }

        ActiveFailedResourcePlannerSignature = ulong.MaxValue;
        ActiveFailedResourceAllocationSignature = ulong.MaxValue;
        ActiveFailedResourceAllocationTimestamp = 0;
    }

    private bool TryBuildPhysicalAllocator(
        in FrameOpContext context,
        VulkanResourcePlanner pendingPlanner,
        VulkanResourceExtentContext extentContext,
        IReadOnlyCollection<RenderPassMetadata>? activePassMetadata,
        out VulkanResourceAllocator? pendingAllocator,
        out HashSet<VulkanPhysicalImageGroup>? reusedImageGroups,
        out int retiredImageCount,
        out int retiredBufferCount)
    {
        pendingAllocator = new();
        reusedImageGroups = null;
        retiredImageCount = 0;
        retiredBufferCount = 0;

        try
        {
            pendingAllocator.UpdatePlan(pendingPlanner.CurrentPlan);
            pendingAllocator.RebuildPhysicalPlan(
                this,
                activePassMetadata,
                pendingPlanner,
                extentContext);
            int reusedImageCount = pendingAllocator.ReuseCompatiblePhysicalImagesFrom(
                ResourceAllocator,
                out reusedImageGroups);
            if (reusedImageCount > 0)
            {
                Debug.VulkanEvery(
                    "Vulkan.ResourcePlanner.PhysicalImageReuse",
                    TimeSpan.FromSeconds(1),
                    "[VulkanResourcePlanner] Reused {0} compatible physical image groups from active plan before allocating pending plan.",
                    reusedImageCount);
            }

            if (!pendingAllocator.TryAllocatePhysicalImages(this, out string imageAllocationFailureReason))
            {
                if (IsExpectedVulkanImageAllocationDeferral(imageAllocationFailureReason))
                {
                    Debug.VulkanEvery(
                        "Vulkan.ResourcePlanner.PhysicalImageAllocationDeferred",
                        TimeSpan.FromSeconds(1),
                        "[VulkanResourcePlanner] Deferred pending physical image allocation. Keeping active plan revision={0}. Reason={1}",
                        ActiveResourcePlannerRevision,
                        imageAllocationFailureReason);
                }
                else
                {
                    Debug.VulkanWarning(
                        "[VulkanResourcePlanner] Pending physical image allocation failed. Keeping active plan revision={0}. Reason={1}",
                        ActiveResourcePlannerRevision,
                        imageAllocationFailureReason);
                }

                pendingAllocator.DestroyPhysicalImagesImmediate(this, reusedImageGroups);
                pendingAllocator.DestroyPhysicalBuffersImmediate(this);
                pendingAllocator = null;
                return false;
            }

            pendingAllocator.AllocatePhysicalBuffers(this);
        }
        catch (Exception ex)
        {
            pendingAllocator?.DestroyPhysicalImagesImmediate(this, reusedImageGroups);
            pendingAllocator?.DestroyPhysicalBuffersImmediate(this);
            pendingAllocator = null;
            Debug.VulkanWarning(
                "[VulkanResourcePlanner] Pending physical resource plan failed. Keeping active plan revision={0}. Reason={1}",
                ActiveResourcePlannerRevision,
                ex.Message);
            return false;
        }

        HashSet<VulkanPhysicalImageGroup>? reusedGroups = reusedImageGroups;
        retiredImageCount = ResourceAllocator
            .EnumeratePhysicalGroups()
            .Count(g => g.IsAllocated && (reusedGroups is null || !reusedGroups.Contains(g)));
        retiredBufferCount = ResourceAllocator.EnumeratePhysicalBufferGroups().Count(static g => g.IsAllocated);
        return true;
    }

    internal static bool IsExpectedVulkanImageAllocationDeferral(Exception exception)
        => IsExpectedVulkanImageAllocationDeferral(exception.Message);

    internal static bool IsExpectedVulkanImageAllocationDeferral(string failureReason)
        => failureReason.Contains("Vulkan image allocation deferred under", StringComparison.OrdinalIgnoreCase) ||
            failureReason.Contains("allocation deferred under allocator pressure", StringComparison.OrdinalIgnoreCase);

    private void CommitPhysicalAllocatorPlan(
        bool physicalPlanChanged,
        VulkanResourceAllocator oldAllocator,
        HashSet<VulkanPhysicalImageGroup>? reusedImageGroups,
        int retiredImageCount,
        int retiredBufferCount,
        ulong plannerSignature,
        ulong allocationSignature)
    {
        if (!physicalPlanChanged)
            return;

        if (retiredImageCount > 0 || retiredBufferCount > 0)
        {
            _lastResourcePlanReplacementRevision = ActiveResourcePlannerRevision + 1;
            _lastResourcePlanReplacementSignature = plannerSignature;
            _lastResourcePlanReplacementAllocationSignature = allocationSignature;
            _lastResourcePlanReplacementRetiredImageCount = retiredImageCount;
            _lastResourcePlanReplacementRetiredBufferCount = retiredBufferCount;
            LogDeferredResourcePlanReplacementRetirement(
                retiredImageCount,
                retiredBufferCount,
                plannerSignature,
                allocationSignature);
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanRetiredResourcePlanReplacement(retiredImageCount, retiredBufferCount);
        }

        if (IsDeviceLost)
            return;

        VulkanPhysicalImageGroup? retainedAutoExposureGroup = PreserveAutoExposureHistory(oldAllocator);

        EvictFrameOpResourcePlannerStatesReferencingAllocator(oldAllocator);
        _ = oldAllocator.TryRetirePhysicalResources(this, retainedAutoExposureGroup, reusedImageGroups);
    }

    private void EvictFrameOpResourcePlannerStatesReferencingAllocator(VulkanResourceAllocator allocator)
    {
        FrameOpResourcePlannerSwitchingState switchingState = ActiveFrameOpResourcePlannerSwitchingState;
        List<FrameOpPlannerStateKey> staleKeys = _frameOpPlannerStateEvictionScratch;
        staleKeys.Clear();
        foreach (KeyValuePair<FrameOpPlannerStateKey, ResourcePlannerRuntimeState> pair in switchingState.States)
        {
            if (ReferenceEquals(pair.Value.ResourceAllocator, allocator))
                staleKeys.Add(pair.Key);
        }

        for (int i = 0; i < staleKeys.Count; i++)
        {
            FrameOpPlannerStateKey key = staleKeys[i];
            switchingState.States.Remove(key);
            switchingState.LastUsedSerials.Remove(key);
            switchingState.ActiveKeys.Remove(key);
            if (switchingState.HasActiveKey && switchingState.ActiveKey.Equals(key))
            {
                switchingState.HasActiveKey = false;
                switchingState.HasActiveContext = false;
                switchingState.ActiveKey = default;
            }
        }

        bool preparationReferencedAllocator = switchingState.HasPreparationState &&
            ReferenceEquals(switchingState.PreparationState.ResourceAllocator, allocator);
        if (preparationReferencedAllocator)
        {
            switchingState.PreparationState = default;
            switchingState.HasPreparationState = false;
        }

        switchingState.SwitchingActive = switchingState.ActiveKeys.Count > 1;
        if (staleKeys.Count > 0)
        {
            Debug.VulkanEvery(
                $"Vulkan.ResourcePlanner.RetiredAllocatorCacheEviction.{allocator.OwnershipId}",
                TimeSpan.FromSeconds(1),
                "[VulkanResourcePlanner] Evicted {0} cached frame-op planner state(s) before retiring allocator owner {1}. PreparationReferenced={2} FirstRegistry=0x{3:X8}.",
                staleKeys.Count,
                allocator.OwnershipId,
                preparationReferencedAllocator,
                staleKeys[0].ResourceRegistrySignature);
        }

        staleKeys.Clear();
    }

    private VulkanPhysicalImageGroup? PreserveAutoExposureHistory(VulkanResourceAllocator oldAllocator)
    {
        if (ShouldSkipAutoExposureHistoryPreserve())
            return null;

        bool hasOldGroup = TryGetAutoExposurePhysicalGroup(oldAllocator, out VulkanPhysicalImageGroup? oldGroup);
        bool hasNewGroup = TryGetAutoExposurePhysicalGroup(ResourceAllocator, out VulkanPhysicalImageGroup? newGroup);
        if (hasOldGroup && hasNewGroup && ReferenceEquals(oldGroup, newGroup))
            return null;

        if (hasNewGroup && newGroup is not null)
        {
            if (TryCopyAutoExposureHistory(oldGroup, newGroup, "active-plan"))
            {
                DestroyRetainedAutoExposureHistory("superseded by active-plan copy");
                return null;
            }

            if (TryCopyAutoExposureHistory(_retainedAutoExposureHistoryGroup, newGroup, "retained-plan-gap"))
            {
                DestroyRetainedAutoExposureHistory("restored into active plan");
                return null;
            }

            DestroyRetainedAutoExposureHistory("new active plan could not use retained history");
            return null;
        }

        if (hasOldGroup && IsUsableAutoExposureHistoryGroup(oldGroup))
            return RetainAutoExposureHistory(oldGroup!);

        return null;
    }

    private static bool TryGetAutoExposurePhysicalGroup(
        VulkanResourceAllocator allocator,
        out VulkanPhysicalImageGroup? group)
        => allocator.TryGetPhysicalGroupForResource(DefaultRenderPipeline.AutoExposureTextureName, out group) &&
           group is not null;

    private bool TryCopyAutoExposureHistory(
        VulkanPhysicalImageGroup? oldGroup,
        VulkanPhysicalImageGroup newGroup,
        string sourceLabel)
    {
        if (!IsUsableAutoExposureHistoryGroup(oldGroup) ||
            !IsUsableAutoExposureTargetGroup(newGroup) ||
            ReferenceEquals(oldGroup, newGroup) ||
            oldGroup!.Format != newGroup.Format ||
            oldGroup.ResolvedExtent.Width != newGroup.ResolvedExtent.Width ||
            oldGroup.ResolvedExtent.Height != newGroup.ResolvedExtent.Height ||
            oldGroup.ResolvedExtent.Depth != newGroup.ResolvedExtent.Depth)
        {
            return false;
        }

        ImageLayout oldLayout = oldGroup.LastKnownLayout;
        ImageLayout newCurrentLayout = newGroup.LastKnownLayout;
        ImageLayout newRestoreLayout = newCurrentLayout == ImageLayout.Undefined
            ? ResolveInitialPhysicalGroupLayout(newGroup.Usage, VulkanResourceAllocator.IsDepthStencilFormat(newGroup.Format))
            : newCurrentLayout;

        using var scope = NewCommandScope();

        // The auto-exposure texture is only touched by compute (storage writes/reads)
        // and fragment sampling, so those stages fully cover prior access without
        // resorting to AllCommands.
        const PipelineStageFlags autoExposureStages =
            PipelineStageFlags.ComputeShaderBit | PipelineStageFlags.FragmentShaderBit;

        TransitionPhysicalGroupForCopy(
            scope.CommandBuffer,
            oldGroup,
            oldLayout,
            ImageLayout.TransferSrcOptimal,
            AccessFlags.ShaderWriteBit,
            AccessFlags.TransferReadBit,
            autoExposureStages,
            PipelineStageFlags.TransferBit);

        TransitionPhysicalGroupForCopy(
            scope.CommandBuffer,
            newGroup,
            newCurrentLayout,
            ImageLayout.TransferDstOptimal,
            newCurrentLayout == ImageLayout.Undefined ? AccessFlags.None : AccessFlags.ShaderWriteBit,
            AccessFlags.TransferWriteBit,
            newCurrentLayout == ImageLayout.Undefined ? PipelineStageFlags.TopOfPipeBit : autoExposureStages,
            PipelineStageFlags.TransferBit);

        ImageCopy copy = new()
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
                AspectMask = ImageAspectFlags.ColorBit,
                MipLevel = 0,
                BaseArrayLayer = 0,
                LayerCount = 1
            },
            Extent = new Extent3D(
                Math.Max(1u, oldGroup.ResolvedExtent.Width),
                Math.Max(1u, oldGroup.ResolvedExtent.Height),
                Math.Max(1u, oldGroup.ResolvedExtent.Depth))
        };

        CmdCopyImageTracked(
            scope.CommandBuffer,
            oldGroup.Image,
            ImageLayout.TransferSrcOptimal,
            newGroup.Image,
            ImageLayout.TransferDstOptimal,
            1,
            &copy);

        TransitionPhysicalGroupForCopy(
            scope.CommandBuffer,
            newGroup,
            ImageLayout.TransferDstOptimal,
            newRestoreLayout,
            AccessFlags.TransferWriteBit,
            AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit,
            PipelineStageFlags.TransferBit,
            autoExposureStages);

        oldGroup.LastKnownLayout = ImageLayout.TransferSrcOptimal;
        newGroup.LastKnownLayout = newRestoreLayout;
        Debug.VulkanEvery(
            $"Vulkan.AutoExposure.HistoryPreserve.{sourceLabel}",
            TimeSpan.FromSeconds(2),
            "[Vulkan] Preserved auto exposure history via {0}: src=0x{1:X} dst=0x{2:X} layout={3}->{4}.",
            sourceLabel,
            oldGroup.Image.Handle,
            newGroup.Image.Handle,
            oldLayout,
            newRestoreLayout);
        return true;
    }

    private VulkanPhysicalImageGroup RetainAutoExposureHistory(VulkanPhysicalImageGroup oldGroup)
    {
        if (!ReferenceEquals(_retainedAutoExposureHistoryGroup, oldGroup))
            DestroyRetainedAutoExposureHistory("replaced by newer active history");

        _retainedAutoExposureHistoryGroup = oldGroup;
        Debug.VulkanEvery(
            "Vulkan.AutoExposure.HistoryRetain",
            TimeSpan.FromSeconds(2),
            "[Vulkan] Retained auto exposure history while switching to a planner context without AutoExposureTex: image=0x{0:X} layout={1}.",
            oldGroup.Image.Handle,
            oldGroup.LastKnownLayout);
        return oldGroup;
    }

    private void DestroyRetainedAutoExposureHistory(string reason)
    {
        VulkanPhysicalImageGroup? group = _retainedAutoExposureHistoryGroup;
        if (group is null)
            return;

        group.Destroy(this);
        _retainedAutoExposureHistoryGroup = null;
        Debug.VulkanEvery(
            "Vulkan.AutoExposure.HistoryRetainedDestroy",
            TimeSpan.FromSeconds(2),
            "[Vulkan] Destroyed retained auto exposure history ({0}).",
            reason);
    }

    private bool ShouldSkipAutoExposureHistoryPreserve()
        => IsDeviceLost ||
           ActiveResourcePlannerRevision == 0 ||
           RuntimeRenderingHostServices.Current.IsInVR;

    private static bool IsUsableAutoExposureHistoryGroup(VulkanPhysicalImageGroup? group)
        => group is not null &&
           group.IsAllocated &&
           group.Image.Handle != 0 &&
           group.LastKnownLayout != ImageLayout.Undefined;

    private static bool IsUsableAutoExposureTargetGroup(VulkanPhysicalImageGroup? group)
        => group is not null &&
           group.IsAllocated &&
           group.Image.Handle != 0;

    private void TransitionPhysicalGroupForCopy(
        CommandBuffer commandBuffer,
        VulkanPhysicalImageGroup group,
        ImageLayout oldLayout,
        ImageLayout newLayout,
        AccessFlags srcAccess,
        AccessFlags dstAccess,
        PipelineStageFlags srcStage,
        PipelineStageFlags dstStage)
    {
        if (oldLayout == newLayout)
            return;

        ImageMemoryBarrier barrier = new()
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = oldLayout,
            NewLayout = newLayout,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = group.Image,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = Math.Max(1u, group.MipLevels),
                BaseArrayLayer = 0,
                LayerCount = Math.Max(1u, group.Template.Layers),
            },
            SrcAccessMask = srcAccess,
            DstAccessMask = dstAccess,
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

    private void RebuildRenderGraphAndBarriers(
        in ResourcePlanningInputs planningInputs,
        ulong resourcePlannerSignature,
        ulong resourceAllocationSignature)
    {
        ActiveCompiledRenderGraph = planningInputs.CompiledGraph;

        BarrierPlanFastPathKey barrierKey = new(
            planningInputs.CompiledGraph,
            resourcePlannerSignature,
            resourceAllocationSignature,
            planningInputs.QueueOwnership);
        if (ActiveHasBarrierPlanFastPathKey && barrierKey.Matches(ActiveBarrierPlanFastPathKey))
            return;

        BarrierPlanner.Rebuild(
            planningInputs.ActivePassMetadata,
            ResourcePlanner,
            ResourceAllocator,
            CompiledRenderGraph.Synchronization,
            planningInputs.QueueOwnership);
        ActiveBarrierPlanFastPathKey = barrierKey;
        ActiveHasBarrierPlanFastPathKey = true;
    }

    private IReadOnlyCollection<RenderPassMetadata>? FilterActivePassMetadata(
        IReadOnlyCollection<RenderPassMetadata>? passMetadata,
        RenderResourceRegistry? resourceRegistry,
        int resourceRegistryRevision,
        HashSet<int>? activePassIndices,
        int activePassSetSignature,
        HashSet<string>? activeFrameBufferNames,
        int activeResourceSetSignature,
        bool constrainToActivePassSet)
    {
        if (passMetadata is null || passMetadata.Count == 0)
            return passMetadata;

        if (activePassIndices is null)
        {
            if (constrainToActivePassSet)
                return Array.Empty<RenderPassMetadata>();

            if (resourceRegistry is null)
                return passMetadata;
        }

        if (activePassIndices is { Count: 0 })
            return Array.Empty<RenderPassMetadata>();

        if (ReferenceEquals(passMetadata, _lastActiveFilterSourcePassMetadata) &&
            ReferenceEquals(resourceRegistry, _lastActiveFilterResourceRegistry) &&
            resourceRegistryRevision == _lastActiveFilterResourceRegistryRevision &&
            activePassSetSignature == _lastActiveFilterPassSetSignature &&
            activeResourceSetSignature == _lastActiveFilterResourceSetSignature &&
            constrainToActivePassSet == _lastActiveFilterConstrainToActivePassSet)
        {
            return _lastActiveFilterResult;
        }

        for (int cacheIndex = 0; cacheIndex < _activePassMetadataFilterCache.Count; cacheIndex++)
        {
            ActivePassMetadataFilterCacheEntry entry = _activePassMetadataFilterCache[cacheIndex];
            if (!entry.Matches(
                passMetadata,
                resourceRegistry,
                resourceRegistryRevision,
                activePassSetSignature,
                activeResourceSetSignature,
                constrainToActivePassSet))
            {
                continue;
            }

            RememberLastActivePassMetadataFilter(
                passMetadata,
                resourceRegistry,
                resourceRegistryRevision,
                activePassSetSignature,
                activeResourceSetSignature,
                constrainToActivePassSet,
                entry.Result);
            return entry.Result;
        }

        int filteredCapacity = activePassIndices is null
            ? passMetadata.Count
            : Math.Min(passMetadata.Count, activePassIndices.Count);
        List<RenderPassMetadata> filtered = new(filteredCapacity);
        bool removedResourceUsages = false;
        foreach (RenderPassMetadata pass in passMetadata)
        {
            if (activePassIndices is not null && !activePassIndices.Contains(pass.PassIndex))
                continue;

            RenderPassMetadata activePass = FilterActivePassResourceUsages(
                pass,
                activePassIndices,
                activeFrameBufferNames,
                resourceRegistry,
                ref removedResourceUsages);
            filtered.Add(activePass);
        }

        IReadOnlyCollection<RenderPassMetadata> result;
        if (filtered.Count == passMetadata.Count && !removedResourceUsages)
        {
            result = passMetadata;
        }
        else if (filtered.Count == 0)
        {
            result = Array.Empty<RenderPassMetadata>();
        }
        else
        {
            filtered.Sort(static (left, right) => left.PassIndex.CompareTo(right.PassIndex));
            result = filtered.ToArray();
        }

        var cacheEntry = new ActivePassMetadataFilterCacheEntry(
            passMetadata,
            resourceRegistry,
            resourceRegistryRevision,
            activePassSetSignature,
            activeResourceSetSignature,
            constrainToActivePassSet,
            result);
        if (_activePassMetadataFilterCache.Count < MaxActivePassMetadataFilterCacheEntries)
        {
            _activePassMetadataFilterCache.Add(cacheEntry);
        }
        else
        {
            _activePassMetadataFilterCache[_activePassMetadataFilterCacheReplacementIndex] = cacheEntry;
            _activePassMetadataFilterCacheReplacementIndex =
                (_activePassMetadataFilterCacheReplacementIndex + 1) % MaxActivePassMetadataFilterCacheEntries;
        }

        RememberLastActivePassMetadataFilter(
            passMetadata,
            resourceRegistry,
            resourceRegistryRevision,
            activePassSetSignature,
            activeResourceSetSignature,
            constrainToActivePassSet,
            result);
        return result;
    }

    private void RememberLastActivePassMetadataFilter(
        IReadOnlyCollection<RenderPassMetadata> passMetadata,
        RenderResourceRegistry? resourceRegistry,
        int resourceRegistryRevision,
        int activePassSetSignature,
        int activeResourceSetSignature,
        bool constrainToActivePassSet,
        IReadOnlyCollection<RenderPassMetadata> result)
    {
        _lastActiveFilterSourcePassMetadata = passMetadata;
        _lastActiveFilterResourceRegistry = resourceRegistry;
        _lastActiveFilterResourceRegistryRevision = resourceRegistryRevision;
        _lastActiveFilterPassSetSignature = activePassSetSignature;
        _lastActiveFilterResourceSetSignature = activeResourceSetSignature;
        _lastActiveFilterConstrainToActivePassSet = constrainToActivePassSet;
        _lastActiveFilterResult = result;
    }

    private static RenderPassMetadata FilterActivePassResourceUsages(
        RenderPassMetadata pass,
        HashSet<int>? activePassIndices,
        HashSet<string>? activeFrameBufferNames,
        RenderResourceRegistry? resourceRegistry,
        ref bool removedResourceUsages)
    {
        bool hasActiveFrameBufferSet = activeFrameBufferNames is { Count: > 0 };
        bool hasResourceRegistry = resourceRegistry is not null;
        if (!hasActiveFrameBufferSet && !hasResourceRegistry)
            return pass;

        List<RenderPassResourceUsage>? activeUsages = null;
        for (int i = 0; i < pass.ResourceUsages.Count; i++)
        {
            RenderPassResourceUsage usage = pass.ResourceUsages[i];
            if ((hasActiveFrameBufferSet && IsInactiveFrameBufferUsage(usage, activeFrameBufferNames!)) ||
                (hasResourceRegistry && IsMissingDeclaredResourceUsage(usage, resourceRegistry!)))
            {
                removedResourceUsages = true;
                if (activeUsages is null)
                {
                    activeUsages = new List<RenderPassResourceUsage>(pass.ResourceUsages.Count);
                    for (int previous = 0; previous < i; previous++)
                        activeUsages.Add(pass.ResourceUsages[previous]);
                }
                continue;
            }

            activeUsages?.Add(usage);
        }

        if (activeUsages is null)
            return pass;

        RenderPassMetadata filtered = new(pass.PassIndex, pass.Name, pass.Stage, pass.DeclarationOrder);
        foreach (RenderPassResourceUsage usage in activeUsages)
            filtered.AddUsage(usage);

        foreach (int dependency in pass.ExplicitDependencies)
            if (activePassIndices is null || activePassIndices.Contains(dependency))
                filtered.AddDependency(dependency);

        foreach (string schema in pass.DescriptorSchemas)
            filtered.AddDescriptorSchema(schema);

        return filtered;
    }

    private static bool IsMissingDeclaredResourceUsage(
        RenderPassResourceUsage usage,
        RenderResourceRegistry resourceRegistry)
    {
        string resourceName = usage.ResourceName;
        if (string.IsNullOrWhiteSpace(resourceName) ||
            resourceName.Equals(RenderGraphResourceNames.OutputRenderTarget, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (TryExtractRenderGraphResourceName(resourceName, "fbo::", out string frameBufferName))
        {
            return !IsVulkanExternalOutputName(frameBufferName) &&
                !resourceRegistry.FrameBufferRecords.ContainsKey(frameBufferName);
        }

        if (TryExtractRenderGraphResourceName(resourceName, "tex::", out string textureName))
        {
            return !resourceRegistry.TextureRecords.ContainsKey(textureName);
        }

        if (TryExtractRenderGraphResourceName(resourceName, "buf::", out string bufferName))
        {
            return !resourceRegistry.BufferRecords.ContainsKey(bufferName);
        }

        return false;
    }

    private static bool IsInactiveFrameBufferUsage(
        RenderPassResourceUsage usage,
        HashSet<string> activeFrameBufferNames)
    {
        if (!TryExtractRenderGraphResourceName(usage.ResourceName, "fbo::", out string frameBufferName))
            return false;

        return !IsVulkanExternalOutputName(frameBufferName) &&
            !activeFrameBufferNames.Contains(frameBufferName);
    }

    private static bool TryExtractRenderGraphResourceName(
        string resourceName,
        string prefix,
        out string name)
    {
        if (!resourceName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            name = string.Empty;
            return false;
        }

        int start = prefix.Length;
        int end = resourceName.IndexOf("::", start, StringComparison.Ordinal);
        if (end < 0)
            end = resourceName.Length;

        if (end <= start)
        {
            name = string.Empty;
            return false;
        }

        name = resourceName[start..end];
        return true;
    }

    private static int ComputeActivePassSetSignature(HashSet<int>? activePassIndices)
    {
        if (activePassIndices is not { Count: > 0 })
            return 0;

        HashCode hash = new();
        hash.Add(activePassIndices.Count);
        long sum = 0;
        long squaredSum = 0;
        int xor = 0;
        foreach (int passIndex in activePassIndices)
        {
            sum += passIndex;
            squaredSum += (long)passIndex * passIndex;
            xor ^= HashCode.Combine(passIndex);
        }

        hash.Add(sum);
        hash.Add(squaredSum);
        hash.Add(xor);
        return hash.ToHashCode();
    }

    private static int ComputeActiveFrameBufferSetSignature(HashSet<string>? activeFrameBufferNames)
    {
        if (activeFrameBufferNames is not { Count: > 0 })
            return 0;

        HashCode hash = new();
        hash.Add(activeFrameBufferNames.Count);
        foreach (string frameBufferName in activeFrameBufferNames.OrderBy(static name => name, StringComparer.OrdinalIgnoreCase))
            hash.Add(frameBufferName, StringComparer.OrdinalIgnoreCase);

        return hash.ToHashCode();
    }

    private void LogDeferredResourcePlanReplacementRetirement(
        int imageCount,
        int bufferCount,
        ulong plannerSignature,
        ulong allocationSignature)
    {
        if (IsDeviceLost)
            return;

        Debug.VulkanEvery(
            "Vulkan.ResourcePlanner.PlanReplacementDeferredRetirement",
            TimeSpan.FromSeconds(2),
            "[VulkanResourcePlanner] Deferring replaced physical resource plan retirement through frame-slot/timeline completion. revision={0} oldPlan=0x{1:X16} newPlan=0x{2:X16} oldAllocation=0x{3:X16} newAllocation=0x{4:X16} images={5} buffers={6}",
            ActiveResourcePlannerRevision + 1,
            ActiveResourcePlannerSignature,
            plannerSignature,
            ActiveResourceAllocationSignature,
            allocationSignature,
            imageCount,
            bufferCount);
    }

    private static void ValidateVulkanResourcePlanMetadata(
        IReadOnlyCollection<RenderPassMetadata>? passMetadata,
        VulkanResourcePlanner planner,
        HashSet<int>? activePassIndices = null)
    {
        if (passMetadata is null || passMetadata.Count == 0)
            return;

        foreach (RenderPassMetadata pass in passMetadata)
        {
            if (activePassIndices is { Count: > 0 } && !activePassIndices.Contains(pass.PassIndex))
                continue;

            foreach (RenderPassResourceUsage usage in pass.ResourceUsages)
            {
                string resourceName = usage.ResourceName;
                if (string.IsNullOrWhiteSpace(resourceName)
                    || IsVulkanExternalOutputResourceBinding(resourceName, planner))
                {
                    continue;
                }

                if (resourceName.StartsWith("fbo::", StringComparison.OrdinalIgnoreCase))
                {
                    ValidateVulkanFrameBufferBinding(pass, usage, resourceName, planner);
                    continue;
                }

                if (resourceName.StartsWith("tex::", StringComparison.OrdinalIgnoreCase))
                {
                    string textureName = resourceName["tex::".Length..];
                    if (!string.IsNullOrWhiteSpace(textureName)
                        && !IsVulkanPlannerOptionalResource(textureName)
                        && !planner.TryGetTextureDescriptor(textureName, out _))
                    {
                        Debug.VulkanWarningEvery(
                            $"VulkanResourcePlanner.MissingTexture.{pass.PassIndex}.{textureName}",
                            TimeSpan.FromSeconds(2),
                            "[VulkanResourcePlanner] Pass '{0}' references missing declared texture '{1}'.",
                            pass.Name,
                            textureName);
                    }
                    continue;
                }

                if (resourceName.StartsWith("buf::", StringComparison.OrdinalIgnoreCase))
                {
                    string bufferName = resourceName["buf::".Length..];
                    if (!string.IsNullOrWhiteSpace(bufferName)
                        && !IsVulkanPlannerOptionalResource(bufferName)
                        && !planner.TryGetBufferDescriptor(bufferName, out _))
                    {
                        Debug.VulkanWarningEvery(
                            $"VulkanResourcePlanner.MissingBuffer.{pass.PassIndex}.{bufferName}",
                            TimeSpan.FromSeconds(2),
                            "[VulkanResourcePlanner] Pass '{0}' references missing declared buffer '{1}'.",
                            pass.Name,
                            bufferName);
                    }
                }
            }
        }
    }

    private static void ValidateVulkanFrameBufferBinding(
        RenderPassMetadata pass,
        RenderPassResourceUsage usage,
        string resourceName,
        VulkanResourcePlanner planner)
    {
        string[] segments = resourceName.Split("::", StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
            return;

        string frameBufferName = segments[1];
        if (IsVulkanExternalOutputName(frameBufferName) || IsVulkanPlannerOptionalResource(frameBufferName))
            return;

        string slot = segments.Length >= 3 ? segments[2] : "color";
        if (!planner.TryGetFrameBufferDescriptor(frameBufferName, out FrameBufferResourceDescriptor? descriptor)
            || descriptor is null)
        {
            Debug.VulkanWarningEvery(
                $"VulkanResourcePlanner.MissingFBO.{pass.PassIndex}.{frameBufferName}",
                TimeSpan.FromSeconds(2),
                "[VulkanResourcePlanner] Pass '{0}' references missing declared framebuffer '{1}'.",
                pass.Name,
                frameBufferName);
            return;
        }

        foreach (FrameBufferAttachmentDescriptor attachment in descriptor.Attachments)
        {
            if (!MatchesVulkanFrameBufferSlot(attachment.Attachment, slot))
                continue;

            if (!planner.TryGetTextureDescriptor(attachment.ResourceName, out _))
            {
                if (IsVulkanPlannerOptionalResource(attachment.ResourceName))
                    return;

                Debug.VulkanWarningEvery(
                    $"VulkanResourcePlanner.MissingFBOAttachment.{pass.PassIndex}.{frameBufferName}.{attachment.ResourceName}",
                    TimeSpan.FromSeconds(2),
                    "[VulkanResourcePlanner] Pass '{0}' framebuffer '{1}' references attachment '{2}' that is missing from declared textures.",
                    pass.Name,
                    frameBufferName,
                    attachment.ResourceName);
            }
            return;
        }

        Debug.VulkanWarningEvery(
            $"VulkanResourcePlanner.MissingFBOSlot.{pass.PassIndex}.{frameBufferName}.{slot}",
            TimeSpan.FromSeconds(2),
            "[VulkanResourcePlanner] Pass '{0}' framebuffer '{1}' has no attachment matching slot '{2}' for usage {3}.",
            pass.Name,
            frameBufferName,
            slot,
            usage.ResourceType);
    }

    private static bool IsVulkanExternalOutputResourceBinding(string resourceName, VulkanResourcePlanner planner)
    {
        if (resourceName.Equals(RenderGraphResourceNames.OutputRenderTarget, StringComparison.OrdinalIgnoreCase))
            return !planner.TryGetOutputFrameBufferDescriptor(out _);

        if (planner.TryGetOutputFrameBufferDescriptor(out _) &&
            resourceName.StartsWith("fbo::", StringComparison.OrdinalIgnoreCase))
        {
            string[] outputSegments = resourceName.Split("::", StringSplitOptions.RemoveEmptyEntries);
            if (outputSegments.Length >= 2 && IsVulkanExternalOutputName(outputSegments[1]))
                return false;
        }

        if (!resourceName.StartsWith("fbo::", StringComparison.OrdinalIgnoreCase))
            return false;

        string[] segments = resourceName.Split("::", StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 2 && IsVulkanExternalOutputName(segments[1]);
    }

    private static bool IsVulkanExternalOutputName(string resourceName)
        => resourceName.Equals(RenderGraphResourceNames.OutputRenderTarget, StringComparison.OrdinalIgnoreCase);

    private static bool IsVulkanPlannerOptionalResource(string resourceName)
        => VulkanPlannerOptionalResourceNames.Contains(resourceName);

    private static bool MatchesVulkanFrameBufferSlot(EFrameBufferAttachment attachment, string slot)
    {
        if (slot.StartsWith("color", StringComparison.OrdinalIgnoreCase))
        {
            if (slot.Length > 5 && int.TryParse(slot.AsSpan(5), out int colorIndex))
            {
                EFrameBufferAttachment expected = (EFrameBufferAttachment)((int)EFrameBufferAttachment.ColorAttachment0 + colorIndex);
                return attachment == expected;
            }

            return attachment is >= EFrameBufferAttachment.ColorAttachment0 and <= EFrameBufferAttachment.ColorAttachment31;
        }

        if (slot.Equals("depth", StringComparison.OrdinalIgnoreCase))
            return attachment is EFrameBufferAttachment.DepthAttachment or EFrameBufferAttachment.DepthStencilAttachment;

        if (slot.Equals("stencil", StringComparison.OrdinalIgnoreCase))
            return attachment is EFrameBufferAttachment.StencilAttachment or EFrameBufferAttachment.DepthStencilAttachment;

        return false;
    }

    private static ulong ComputeResourceAllocationSignature(
        in FrameOpContext context,
        VulkanResourcePlanner planner,
        IReadOnlyCollection<RenderPassMetadata>? passMetadata,
        VulkanResourceExtentContext extentContext,
        bool supportsTransformFeedback)
    {
        ResourceAllocationSignatureBreakdown breakdown = ComputeResourceAllocationSignatureBreakdown(
            context,
            planner,
            passMetadata,
            extentContext,
            supportsTransformFeedback);
        HashCode hash = new();
        hash.Add(breakdown.AllocationDescriptors);
        hash.Add(breakdown.DisplayWidth);
        hash.Add(breakdown.DisplayHeight);
        hash.Add(breakdown.InternalWidth);
        hash.Add(breakdown.InternalHeight);
        hash.Add(breakdown.PhysicalUsage);
        hash.Add(breakdown.SupportsTransformFeedback);
        return unchecked((ulong)hash.ToHashCode());
    }

    private static ResourceAllocationSignatureBreakdown ComputeResourceAllocationSignatureBreakdown(
        in FrameOpContext context,
        VulkanResourcePlanner planner,
        IReadOnlyCollection<RenderPassMetadata>? passMetadata,
        VulkanResourceExtentContext extentContext,
        bool supportsTransformFeedback)
        => new(
            ComputePhysicalResourceDescriptorSignature(context.ResourceRegistry),
            extentContext.WindowWidth,
            extentContext.WindowHeight,
            extentContext.InternalWidth,
            extentContext.InternalHeight,
            VulkanResourceAllocator.ComputePhysicalPlanUsageSignature(planner, passMetadata),
            supportsTransformFeedback);

    private static ulong ComputeResourcePlannerSignature(
        in FrameOpContext context,
        in VulkanBarrierPlanner.QueueOwnershipConfig queueOwnership,
        VulkanCompiledRenderGraph compiledGraph,
        IReadOnlyCollection<RenderPassMetadata>? passMetadata)
    {
        HashCode hash = new();
        hash.Add(ComputeResourcePlanCompatibilityFingerprint(context));
        hash.Add(ComputePassMetadataSignature(passMetadata));

        hash.Add(compiledGraph.Batches.Count);
        foreach (VulkanCompiledPassBatch batch in compiledGraph.Batches)
        {
            hash.Add(batch.BatchIndex);
            hash.Add((int)batch.Stage);
            hash.Add(batch.AttachmentSignature, StringComparer.Ordinal);
            hash.Add(batch.PassIndices.Count);
            for (int i = 0; i < batch.PassIndices.Count; i++)
                hash.Add(batch.PassIndices[i]);
        }

        hash.Add(compiledGraph.Synchronization.Edges.Count);
        foreach (RenderGraphSynchronizationEdge edge in compiledGraph.Synchronization.Edges)
        {
            hash.Add(edge.ProducerPassIndex);
            hash.Add(edge.ConsumerPassIndex);
            hash.Add(edge.ResourceName, StringComparer.OrdinalIgnoreCase);
            hash.Add((int)edge.ResourceType);
            AddSubresourceRangeToHash(ref hash, edge.SubresourceRange);
            hash.Add((int)edge.ProducerState.StageMask);
            hash.Add((int)edge.ProducerState.AccessMask);
            hash.Add((int)(edge.ProducerState.Layout ?? RenderGraphImageLayout.Undefined));
            hash.Add((int)edge.ConsumerState.StageMask);
            hash.Add((int)edge.ConsumerState.AccessMask);
            hash.Add((int)(edge.ConsumerState.Layout ?? RenderGraphImageLayout.Undefined));
            hash.Add(edge.DependencyOnly);
        }

        hash.Add(queueOwnership.GraphicsQueueFamilyIndex);
        hash.Add(queueOwnership.ComputeQueueFamilyIndex ?? queueOwnership.GraphicsQueueFamilyIndex);
        hash.Add(queueOwnership.TransferQueueFamilyIndex ?? queueOwnership.GraphicsQueueFamilyIndex);

        return unchecked((ulong)hash.ToHashCode());
    }

    private static ulong ComputeResourcePlanCompatibilityFingerprint(in FrameOpContext context)
    {
        FrameOpSignatureHasher hash = new();
        hash.Add(0x56554C4B504C414EUL);
        hash.Add((int)context.ContextKind);
        hash.Add(context.PipelineIdentity);
        hash.Add(context.ViewportIdentity);
        hash.Add(context.OutputFrameBufferIdentity);
        hash.Add(ResolveResourcePlanOutputTargetIdentity(context));
        hash.Add(context.DisplayWidth);
        hash.Add(context.DisplayHeight);
        hash.Add(context.InternalWidth);
        hash.Add(context.InternalHeight);
        hash.Add(context.StereoEnabled);
        hash.Add(context.MultiviewEnabled);
        hash.Add(ComputeResourceRegistrySignature(context.ResourceRegistry));
        hash.Add(ComputePassMetadataSignature(context.PassMetadata));
        hash.Add(context.ResourceGeneration);
        hash.Add(context.SubmissionQueueFamily);
        return hash.ToHash();
    }

    private static ResourcePlannerSignatureBreakdown ComputeResourcePlannerSignatureBreakdown(
        in FrameOpContext context,
        in VulkanBarrierPlanner.QueueOwnershipConfig queueOwnership,
        VulkanCompiledRenderGraph compiledGraph,
        IReadOnlyCollection<RenderPassMetadata>? passMetadata)
        => new(
            context.ContextKind,
            context.ContextId,
            ComputeResourcePlanCompatibilityFingerprint(context),
            ComputeResourceRegistrySignature(context.ResourceRegistry),
            context.OutputFrameBufferIdentity,
            ResolveResourcePlanOutputTargetIdentity(context),
            context.DisplayWidth,
            context.DisplayHeight,
            context.InternalWidth,
            context.InternalHeight,
            ComputePassMetadataSignature(passMetadata),
            ComputeCompiledGraphBatchSignature(compiledGraph),
            ComputeCompiledGraphEdgeSignature(compiledGraph),
            context.ResourceGeneration,
            context.DescriptorGeneration,
            context.SubmissionQueueFamily,
            queueOwnership.GraphicsQueueFamilyIndex,
            queueOwnership.ComputeQueueFamilyIndex ?? queueOwnership.GraphicsQueueFamilyIndex,
            queueOwnership.TransferQueueFamilyIndex ?? queueOwnership.GraphicsQueueFamilyIndex);

    private static int ComputeCompiledGraphBatchSignature(VulkanCompiledRenderGraph compiledGraph)
    {
        HashCode hash = new();
        hash.Add(compiledGraph.Batches.Count);
        foreach (VulkanCompiledPassBatch batch in compiledGraph.Batches)
        {
            hash.Add(batch.BatchIndex);
            hash.Add((int)batch.Stage);
            hash.Add(batch.AttachmentSignature, StringComparer.Ordinal);
            hash.Add(batch.PassIndices.Count);
            for (int i = 0; i < batch.PassIndices.Count; i++)
                hash.Add(batch.PassIndices[i]);
        }

        return hash.ToHashCode();
    }

    private static int ComputeCompiledGraphEdgeSignature(VulkanCompiledRenderGraph compiledGraph)
    {
        HashCode hash = new();
        hash.Add(compiledGraph.Synchronization.Edges.Count);
        foreach (RenderGraphSynchronizationEdge edge in compiledGraph.Synchronization.Edges)
        {
            hash.Add(edge.ProducerPassIndex);
            hash.Add(edge.ConsumerPassIndex);
            hash.Add(edge.ResourceName, StringComparer.OrdinalIgnoreCase);
            hash.Add((int)edge.ResourceType);
            AddSubresourceRangeToHash(ref hash, edge.SubresourceRange);
            hash.Add((int)edge.ProducerState.StageMask);
            hash.Add((int)edge.ProducerState.AccessMask);
            hash.Add((int)(edge.ProducerState.Layout ?? RenderGraphImageLayout.Undefined));
            hash.Add((int)edge.ConsumerState.StageMask);
            hash.Add((int)edge.ConsumerState.AccessMask);
            hash.Add((int)(edge.ConsumerState.Layout ?? RenderGraphImageLayout.Undefined));
            hash.Add(edge.DependencyOnly);
        }

        return hash.ToHashCode();
    }

    private static int ComputeResourceRegistrySignature(RenderResourceRegistry? registry)
        => registry?.DescriptorSignature ?? 0;

    private static int ComputePhysicalResourceDescriptorSignature(RenderResourceRegistry? registry)
    {
        if (registry is null)
            return 0;

        HashCode hash = new();

        foreach (KeyValuePair<string, RenderTextureResource> pair in registry.TextureRecords.OrderBy(static p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            TextureResourceDescriptor descriptor = pair.Value.Descriptor;
            hash.Add(pair.Key, StringComparer.OrdinalIgnoreCase);
            hash.Add((int)descriptor.Lifetime);
            hash.Add((int)descriptor.SizePolicy.SizeClass);
            hash.Add(descriptor.SizePolicy.ScaleX);
            hash.Add(descriptor.SizePolicy.ScaleY);
            hash.Add(descriptor.SizePolicy.Width);
            hash.Add(descriptor.SizePolicy.Height);
            hash.Add(descriptor.FormatLabel, StringComparer.OrdinalIgnoreCase);
            hash.Add(descriptor.ArrayLayers);
            hash.Add(descriptor.StereoCompatible);
            hash.Add(descriptor.SupportsAliasing);
            hash.Add(descriptor.RequiresStorageUsage);
            hash.Add((int)descriptor.Kind);
            hash.Add((int)descriptor.Usage);
            hash.Add(descriptor.InternalFormat.HasValue ? (int)descriptor.InternalFormat.Value : -1);
            hash.Add(descriptor.PixelFormat.HasValue ? (int)descriptor.PixelFormat.Value : -1);
            hash.Add(descriptor.PixelType.HasValue ? (int)descriptor.PixelType.Value : -1);
            hash.Add(descriptor.SizedInternalFormat.HasValue ? (int)descriptor.SizedInternalFormat.Value : -1);
            hash.Add(descriptor.Samples);
            hash.Add(descriptor.MipPolicy.BaseMipLevel);
            hash.Add(descriptor.MipPolicy.MipLevelCount);
            hash.Add(descriptor.MipPolicy.AutoGenerateMipmaps);
            hash.Add(descriptor.MipPolicy.RequireImmutableStorage);
            hash.Add(descriptor.SourceTextureName, StringComparer.OrdinalIgnoreCase);
            hash.Add(descriptor.BaseMipLevel);
            hash.Add(descriptor.MipLevelCount);
            hash.Add(descriptor.BaseLayer);
            hash.Add(descriptor.LayerCount);
            hash.Add((int)descriptor.DepthStencilAspect);
            hash.Add(descriptor.ArrayTarget);
            hash.Add(descriptor.Multisample);
        }

        foreach (KeyValuePair<string, RenderBufferResource> pair in registry.BufferRecords.OrderBy(static p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            BufferResourceDescriptor descriptor = pair.Value.Descriptor;
            hash.Add(pair.Key, StringComparer.OrdinalIgnoreCase);
            hash.Add((int)descriptor.Lifetime);
            hash.Add(descriptor.SizeInBytes);
            hash.Add((int)descriptor.Target);
            hash.Add((int)descriptor.Usage);
            hash.Add(descriptor.SupportsAliasing);
            hash.Add(descriptor.ElementStride);
            hash.Add(descriptor.ElementCount);
            hash.Add((int)descriptor.AccessPattern);
        }

        return hash.ToHashCode();
    }

    private static int ComputePassMetadataSignature(IReadOnlyCollection<RenderPassMetadata>? passMetadata)
    {
        if (passMetadata is null || passMetadata.Count == 0)
            return 0;

        HashCode hash = new();
        hash.Add(passMetadata.Count);

        if (passMetadata is IReadOnlyList<RenderPassMetadata> passList)
        {
            for (int passIndex = 0; passIndex < passList.Count; passIndex++)
                AddPassMetadataToHash(ref hash, passList[passIndex]);
        }
        else
        {
            foreach (RenderPassMetadata pass in passMetadata)
                AddPassMetadataToHash(ref hash, pass);
        }

        return hash.ToHashCode();
    }

    private static void AddPassMetadataToHash(ref HashCode hash, RenderPassMetadata pass)
    {
        hash.Add(pass.PassIndex);
        hash.Add(pass.DeclarationOrder);
        hash.Add((int)pass.Stage);
        hash.Add(pass.Name, StringComparer.Ordinal);
        hash.Add(pass.Revision);

        for (int usageIndex = 0; usageIndex < pass.ResourceUsages.Count; usageIndex++)
        {
            RenderPassResourceUsage usage = pass.ResourceUsages[usageIndex];
            hash.Add(usage.ResourceName, StringComparer.Ordinal);
            hash.Add((int)usage.ResourceType);
            hash.Add((int)usage.Access);
            hash.Add((int)usage.LoadOp);
            hash.Add((int)usage.StoreOp);
            AddSubresourceRangeToHash(ref hash, usage.SubresourceRange);
        }

        for (int dependencyIndex = 0; dependencyIndex < pass.ExplicitDependencies.Count; dependencyIndex++)
            hash.Add(pass.ExplicitDependencies[dependencyIndex]);

        for (int schemaIndex = 0; schemaIndex < pass.DescriptorSchemas.Count; schemaIndex++)
            hash.Add(pass.DescriptorSchemas[schemaIndex], StringComparer.Ordinal);
    }

    private static int ComputePassMetadataRevisionStamp(IReadOnlyCollection<RenderPassMetadata>? passMetadata)
    {
        if (passMetadata is null || passMetadata.Count == 0)
            return 0;

        HashCode hash = new();
        hash.Add(passMetadata.Count);
        if (passMetadata is IReadOnlyList<RenderPassMetadata> passList)
        {
            for (int passIndex = 0; passIndex < passList.Count; passIndex++)
            {
                RenderPassMetadata pass = passList[passIndex];
                hash.Add(pass.PassIndex);
                hash.Add(pass.DeclarationOrder);
                hash.Add(pass.Revision);
            }
        }
        else
        {
            foreach (RenderPassMetadata pass in passMetadata)
            {
                hash.Add(pass.PassIndex);
                hash.Add(pass.DeclarationOrder);
                hash.Add(pass.Revision);
            }
        }

        return hash.ToHashCode();
    }

    private int ComputeResourcePlanningSignature(IReadOnlyCollection<RenderPassMetadata>? passMetadata)
    {
        Extent2D fallbackExtent = ResolveFrameOpContextFallbackExtent();
        HashCode hash = new();
        hash.Add(fallbackExtent.Width);
        hash.Add(fallbackExtent.Height);

        foreach (VulkanAllocationRequest request in ResourcePlanner.CurrentPlan.AllTextures())
        {
            hash.Add(request.Name, StringComparer.OrdinalIgnoreCase);
            hash.Add((int)request.Lifetime);
            hash.Add(request.AliasKey);
        }

        foreach (VulkanBufferAllocationRequest request in ResourcePlanner.CurrentPlan.AllBuffers())
        {
            hash.Add(request.Name, StringComparer.OrdinalIgnoreCase);
            hash.Add((int)request.Lifetime);
            hash.Add(request.AliasKey);
        }

        if (passMetadata is not null)
        {
            hash.Add(passMetadata.Count);
            foreach (RenderPassMetadata pass in passMetadata.OrderBy(static p => p.PassIndex))
            {
                hash.Add(pass.PassIndex);
                hash.Add(pass.DeclarationOrder);
                hash.Add((int)pass.Stage);
                hash.Add(pass.Name, StringComparer.Ordinal);

                foreach (RenderPassResourceUsage usage in pass.ResourceUsages)
                {
                    hash.Add(usage.ResourceName, StringComparer.Ordinal);
                    hash.Add((int)usage.ResourceType);
                    hash.Add((int)usage.Access);
                    hash.Add((int)usage.LoadOp);
                    hash.Add((int)usage.StoreOp);
                    AddSubresourceRangeToHash(ref hash, usage.SubresourceRange);
                }
            }
        }

        return hash.ToHashCode();
    }

    private static void AddSubresourceRangeToHash(ref HashCode hash, RenderGraphSubresourceRange range)
    {
        hash.Add(range.BaseMipLevel);
        hash.Add(range.MipLevelCount);
        hash.Add(range.BaseArrayLayer);
        hash.Add(range.ArrayLayerCount);
    }
    internal int EnsureValidPassIndex(
        int passIndex,
        string opName,
        IReadOnlyCollection<RenderPassMetadata>? passMetadata = null)
    {
        passMetadata ??= RuntimeEngine.Rendering.State.CurrentRenderingPipeline?.Pipeline?.PassMetadata;

        if (passIndex == VulkanBarrierPlanner.SwapchainPassIndex)
        {
            Debug.VulkanWarningEvery(
                $"Vulkan.InvalidPass.{opName}.SwapchainPseudoPass",
                TimeSpan.FromSeconds(1),
                "[Vulkan] '{0}' attempted to use the reserved swapchain pseudo-pass as a render-graph pass. Treating it as unresolved.",
                opName);
            passIndex = int.MinValue;
        }

        // Short-circuit: well-known EDefaultRenderPass values are always valid.
        // Metadata may lag behind runtime enqueues (conditional pipeline paths,
        // hot-reload) — accept standard passes without warning.
        if (passIndex != int.MinValue && Enum.IsDefined(typeof(EDefaultRenderPass), passIndex))
            return passIndex;

        bool hasMetadata = passMetadata is { Count: > 0 };
        bool passDefinedInMetadata = hasMetadata && passMetadata!.Any(m => m.PassIndex == passIndex);

        if (passIndex != int.MinValue && (!hasMetadata || passDefinedInMetadata))
            return passIndex;

        int currentPassIndex = RuntimeEngine.Rendering.State.CurrentRenderGraphPassIndex;
        if (passIndex != int.MinValue && passIndex == currentPassIndex)
            return passIndex;

        if (passIndex == int.MinValue)
        {
            bool currentPassDefined = currentPassIndex != int.MinValue &&
                (!hasMetadata || passMetadata!.Any(m => m.PassIndex == currentPassIndex));

            if (currentPassDefined)
                return currentPassIndex;

            if (hasMetadata && !opName.Contains("Compute", StringComparison.OrdinalIgnoreCase))
            {
                const int preRenderPass = (int)EDefaultRenderPass.PreRender;
                if (passMetadata!.Any(m => m.PassIndex == preRenderPass))
                    return preRenderPass;
            }
        }

        int fallback = ResolveFallbackPassIndex(opName, passMetadata);

        string reason = passIndex == int.MinValue
            ? "invalid sentinel value"
            : $"pass {passIndex} is missing from metadata";

        int? firstKnownBarrierPass = BarrierPlanner.GetFirstKnownPassIndex();

        Debug.VulkanWarningEvery(
            $"Vulkan.InvalidPass.{opName}.{passIndex}",
            TimeSpan.FromSeconds(1),
            "[Vulkan] '{0}' emitted with invalid render-graph pass index ({1}). Falling back to pass {2}. " +
            "MetadataCount={3} BarrierPlannerFirstPass={4} CurrentPipeline={5}",
            opName,
            reason,
            fallback,
            passMetadata?.Count ?? -1,
            firstKnownBarrierPass?.ToString() ?? "none",
            RuntimeEngine.Rendering.State.CurrentRenderingPipeline?.GetType().Name ?? "null");

        return fallback;
    }

    private static int ResolveFallbackPassIndex(string opName, IReadOnlyCollection<RenderPassMetadata>? passMetadata)
    {
        if (passMetadata is null || passMetadata.Count == 0)
            return int.MinValue;

        ERenderGraphPassStage? preferredStage = ResolvePreferredFallbackStage(opName, passMetadata);
        if (preferredStage.HasValue)
        {
            RenderPassMetadata? preferredPass = passMetadata
                .Where(m => m.Stage == preferredStage.Value)
                .OrderBy(m => m.PassIndex)
                .FirstOrDefault();

            if (preferredPass is not null)
                return preferredPass.PassIndex;
        }

        return passMetadata.OrderBy(m => m.PassIndex).First().PassIndex;
    }

    private static ERenderGraphPassStage? ResolvePreferredFallbackStage(string opName, IReadOnlyCollection<RenderPassMetadata> passMetadata)
    {
        if (opName.Contains("Compute", StringComparison.OrdinalIgnoreCase))
            return ERenderGraphPassStage.Compute;

        if (opName.Contains("Blit", StringComparison.OrdinalIgnoreCase))
        {
            bool hasTransferPass = passMetadata.Any(m => m.Stage == ERenderGraphPassStage.Transfer);
            return hasTransferPass ? ERenderGraphPassStage.Transfer : ERenderGraphPassStage.Graphics;
        }

        return ERenderGraphPassStage.Graphics;
    }

}
