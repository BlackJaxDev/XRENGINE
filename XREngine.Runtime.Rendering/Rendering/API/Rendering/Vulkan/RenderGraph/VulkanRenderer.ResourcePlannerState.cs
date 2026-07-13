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

    internal enum EVulkanFrameOpContextKind
    {
        Unknown = 0,
        MainViewport = 1,
        OpenXrEye = 2,
        OpenXrMirror = 3,
        SceneCapture = 4,
        LightProbeCapture = 5,
        Shadow = 6,
        UiPreview = 7,
        DiagnosticCapture = 8,
    }

    private long _frameOpContextId;

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
        ulong DescriptorGeneration = 0)
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
        RegisterFrameOpOutputFrameBuffer(outputFrameBuffer, pipeline?.Resources);
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
        context = ApplyInteractiveResizePlannerFreeze(context);
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
        if (context.PipelineInstance is not null || context.PassMetadata is { Count: > 0 })
            return context;

        return ActiveLastActiveFrameOpContext ?? context;
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
        if (!FrameOpContextHasPlannerResources(context))
            return null;

        return new ExternalResourcePlannerReadbackScope(this, context);
    }

    internal IDisposable EnterFrameOpResourcePlannerReadbackScope(in FrameOpContext context)
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
        RegisterFrameOpOutputFrameBuffer(outputFrameBuffer, pipeline.Resources);
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

        context = ApplyInteractiveResizePlannerFreeze(context);
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
        {
            return EVulkanFrameOpContextKind.UiPreview;
        }

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
        hash.Add(ComputeResourceRegistrySignature(context.ResourceRegistry));
        hash.Add(ComputePassMetadataSignature(context.PassMetadata));
        hash.Add(context.ResourceGeneration);
        hash.Add(context.DescriptorGeneration);
        hash.Add(context.SubmissionQueueFamily);
        return hash.ToHash();
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

    private void RegisterFrameOpOutputFrameBuffer(XRFrameBuffer? frameBuffer, RenderResourceRegistry? registry)
    {
        if (frameBuffer is null || registry is null)
            return;

        if (IsFrameOpOutputFrameBufferRegistered(frameBuffer, registry))
            return;

        EnsureFrameBufferRegistered(frameBuffer, registry);
        EnsureFrameBufferAttachmentsRegistered(frameBuffer, registry);
    }

    private static bool IsFrameOpOutputFrameBufferRegistered(
        XRFrameBuffer frameBuffer,
        RenderResourceRegistry registry)
    {
        string? name = frameBuffer.Name;
        if (string.IsNullOrWhiteSpace(name) ||
            !registry.FrameBufferRecords.TryGetValue(name, out RenderFrameBufferResource? record) ||
            !ReferenceEquals(record.Instance, frameBuffer))
        {
            return false;
        }

        FrameBufferResourceDescriptor descriptor = record.Descriptor;
        uint width = Math.Max(frameBuffer.Width, 1u);
        uint height = Math.Max(frameBuffer.Height, 1u);
        if (descriptor.SizePolicy.SizeClass != RenderResourceSizeClass.AbsolutePixels ||
            descriptor.SizePolicy.Width != width ||
            descriptor.SizePolicy.Height != height)
        {
            return false;
        }

        var targets = frameBuffer.Targets;
        if (targets is null)
            return descriptor.Attachments.Count == 0;

        if (descriptor.Attachments.Count != targets.Length)
            return false;

        for (int i = 0; i < targets.Length; i++)
        {
            var (target, attachment, mipLevel, layerIndex) = targets[i];
            if (target is not XRTexture texture)
                return false;

            string? textureName = texture.Name;
            if (string.IsNullOrWhiteSpace(textureName) ||
                !registry.TextureRecords.TryGetValue(textureName, out RenderTextureResource? textureRecord) ||
                !ReferenceEquals(textureRecord.Instance, texture))
            {
                return false;
            }

            FrameBufferAttachmentDescriptor attachmentDescriptor = descriptor.Attachments[i];
            if (!string.Equals(attachmentDescriptor.ResourceName, textureName, StringComparison.OrdinalIgnoreCase) ||
                attachmentDescriptor.Attachment != attachment ||
                attachmentDescriptor.MipLevel != mipLevel ||
                attachmentDescriptor.LayerIndex != layerIndex)
            {
                return false;
            }

            if (texture is XRTextureViewBase view)
            {
                XRTexture viewedTexture = view.GetViewedTexture();
                string? viewedTextureName = viewedTexture.Name;
                if (string.IsNullOrWhiteSpace(viewedTextureName) ||
                    !registry.TextureRecords.TryGetValue(viewedTextureName, out RenderTextureResource? viewedTextureRecord) ||
                    !ReferenceEquals(viewedTextureRecord.Instance, viewedTexture))
                {
                    return false;
                }
            }
        }

        return true;
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

    private readonly struct ExternalResourcePlannerReadbackScope : IDisposable
    {
        private readonly VulkanRenderer _renderer;
        private readonly ResourcePlannerRuntimeState _previousState;
        private readonly FrameOpPlannerStateKey _key;
        private readonly bool _active;

        public ExternalResourcePlannerReadbackScope(
            VulkanRenderer renderer,
            in FrameOpContext context)
        {
            _renderer = renderer;
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
            if (TryFindBestCompatibleFrameOpPlannerState(context, switchingState, out _key, out ResourcePlannerRuntimeState state))
            {
                renderer.RestoreResourcePlannerRuntimeState(state);
                renderer.MarkFrameOpResourcePlannerStateUsed(switchingState, _key);
                return;
            }

            _key = requestedKey;
            renderer.RestoreResourcePlannerRuntimeState(ResourcePlannerRuntimeState.CreateEmpty());
            renderer.UpdateResourcePlannerFromContext(context);
            switchingState.States[_key] = renderer.CaptureResourcePlannerRuntimeState();
            renderer.MarkFrameOpResourcePlannerStateUsed(switchingState, _key);
        }

        public void Dispose()
        {
            ResourcePlannerRuntimeState currentState = default;
            bool canPublish = _active && _renderer.IsDeviceOperational;
            if (canPublish)
            {
                currentState = _renderer.CaptureResourcePlannerRuntimeState();
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

    private FrameOpContext PrepareResourcePlannerForFrameOps(FrameOp[] ops)
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
        RenderResourceRegistry? mergedRegistry = BuildMergedFrameOpRegistry(ops, primary);
        FrameOpContext plannerContext = mergedRegistry is null
            ? primary
            : primary with { ResourceRegistry = mergedRegistry };

        plannerContext = RefreshPlannerExtentsFromLiveContext(plannerContext, ops);

        // Descriptor snapshots are captured against the full pipeline resource
        // plan before the command buffer is recorded. Keep frame-op recording on
        // that same plan so FBO writes, sampled descriptors, and readback all
        // resolve the same physical image groups.
        UpdateResourcePlannerFromContext(plannerContext);

        return plannerContext;
    }

    private ulong PrepareFrameOpResourcePlannerStatesForFrameOps(FrameOp[] ops)
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

        if (keys.Count == 1)
        {
            FrameOpPlannerStateKey key = keys[0];
            switchingState.States[key] = CaptureResourcePlannerRuntimeState();
            switchingState.ActiveKeys.Add(key);
            MarkFrameOpResourcePlannerStateUsed(switchingState, key);
            AssertFrameOpPlannerAllocatorOwnership(switchingState);
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
                ResourcePlannerRuntimeState state = switchingState.States.TryGetValue(key, out ResourcePlannerRuntimeState existingState)
                    ? existingState
                    : ResourcePlannerRuntimeState.CreateEmpty();

                ResetActiveFrameOpResourcePlannerState(switchingState);
                RestoreResourcePlannerRuntimeState(state);
                _ = PrepareResourcePlannerForFrameOps(ops, key);
                switchingState.States[key] = CaptureResourcePlannerRuntimeState();
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

    private FrameOpContext PrepareResourcePlannerForFrameOps(FrameOp[] ops, in FrameOpPlannerStateKey key)
    {
        FrameOpContext plannerContext = SelectPrimaryPlannerContext(ops, key);
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
        System.Diagnostics.Debug.Assert(state.ResourcePlanner is not null);
        System.Diagnostics.Debug.Assert(state.ResourceAllocator is not null);
        System.Diagnostics.Debug.Assert(state.BarrierPlanner is not null);
        System.Diagnostics.Debug.Assert(state.ResourceAllocator.OwnershipId == state.AllocatorOwnershipId);
        System.Diagnostics.Debug.Assert(!state.ResourceAllocator.IsRetired);
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

                System.Diagnostics.Debug.Assert(
                    !ReferenceEquals(first.Value.ResourceAllocator, second.Value.ResourceAllocator),
                    $"Frame-op planner states {first.Key} and {second.Key} share allocator owner {first.Value.AllocatorOwnershipId} without an explicit sharing policy.");
            }

            if (switchingState.HasPreparationState)
            {
                System.Diagnostics.Debug.Assert(
                    !ReferenceEquals(first.Value.ResourceAllocator, switchingState.PreparationState.ResourceAllocator),
                    $"Frame-op planner state {first.Key} shares allocator owner {first.Value.AllocatorOwnershipId} with the merged preparation state.");
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
        System.Diagnostics.Debug.Assert(context.ResourceGeneration == key.ResourceGeneration);
        if (state.LastActiveFrameOpContext is not FrameOpContext lastContext)
            return;

        System.Diagnostics.Debug.Assert(FrameOpContextMatchesPlannerStateKey(lastContext, key));
        System.Diagnostics.Debug.Assert(FrameOpContextMatchesPlannerStateKey(context, key));
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
            ComputePassMetadataSignature(context.PassMetadata) == key.PassMetadataSignature &&
            context.ResourceGeneration == key.ResourceGeneration &&
            context.SubmissionQueueFamily == key.SubmissionQueueFamily;

    private static int ComputeOutputFrameBufferIdentity(string? outputFrameBufferName)
        => string.IsNullOrWhiteSpace(outputFrameBufferName)
            ? 0
            : StringComparer.OrdinalIgnoreCase.GetHashCode(outputFrameBufferName!);

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

            foreach (XRFrameBuffer target in EnumerateFrameOpFrameBuffers(op))
            {
                if (!string.IsNullOrWhiteSpace(target.Name) &&
                    context.ResourceRegistry.FrameBufferRecords.ContainsKey(target.Name))
                {
                    score += 256;
                }
                else
                {
                    score += 32;
                }
            }

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

            foreach (XRFrameBuffer target in EnumerateFrameOpFrameBuffers(op))
            {
                if (!string.IsNullOrWhiteSpace(target.Name) &&
                    context.ResourceRegistry.FrameBufferRecords.ContainsKey(target.Name))
                {
                    score += 256;
                }
                else
                {
                    score += 32;
                }
            }

            if (score > bestScore ||
                (score == bestScore && ComparePlannerContextTieBreak(context, best) < 0))
            {
                bestScore = score;
                best = context;
            }
        }

        return hasBest ? best : SelectPrimaryPlannerContext(ops);
    }

    private static int ComparePlannerContextTieBreak(in FrameOpContext left, in FrameOpContext right)
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

        compare = (left.ResourceRegistry?.DescriptorSignature ?? 0)
            .CompareTo(right.ResourceRegistry?.DescriptorSignature ?? 0);
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
        in FrameOpContext primaryContext)
    {
        RenderResourceRegistry? primaryRegistry = primaryContext.ResourceRegistry;
        FrameOpPlannerStateKey ownerKey = BuildFrameOpPlannerStateKey(primaryContext);
        RenderResourceRegistry[] registries = CollectUniqueFrameOpRegistries(ops);

        // Shadow command collections are conditional, but their logical resources are structural
        // for the owning pipeline generation. Once a source registry participates in this owner's
        // plan, retain its descriptors until the compatibility key changes. Owner scoping prevents
        // a desktop shadow/source registry from mutating an eye, mirror, or capture plan.
        FrameOpRegistryCacheSource[] cacheSources = BuildFrameOpRegistryCacheSources(registries);
        if (TryGetCachedMergedFrameOpRegistry(ownerKey, primaryRegistry, cacheSources, out RenderResourceRegistry? cachedRegistry))
            return cachedRegistry;

        if (registries.Length == 0)
            return primaryRegistry;

        if (registries.Length == 1)
            return registries[0];

        if (primaryRegistry is not null && RegistriesCoveredByPrimary(registries, primaryRegistry))
            return primaryRegistry;

        RenderResourceRegistry merged = new();
        if (primaryRegistry is not null)
            AddRegistryDescriptors(merged, primaryRegistry, overwrite: true);

        foreach (RenderResourceRegistry registry in registries)
        {
            if (ReferenceEquals(registry, primaryRegistry))
                continue;

            AddRegistryDescriptors(merged, registry, overwrite: false);
        }

        RememberMergedFrameOpRegistry(ownerKey, primaryRegistry, cacheSources, merged);
        return merged;
    }

    private static RenderResourceRegistry[] CollectUniqueFrameOpRegistries(FrameOp[] ops)
    {
        List<RenderResourceRegistry>? registries = null;
        foreach (FrameOp op in ops)
        {
            if (op.Context.ResourceRegistry is not { } registry)
                continue;

            registries ??= new();
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

        if (registries is null || registries.Count == 0)
            return [];

        registries.Sort(static (left, right) =>
            RuntimeHelpers.GetHashCode(left).CompareTo(RuntimeHelpers.GetHashCode(right)));
        return registries.ToArray();
    }

    private static FrameOpRegistryCacheSource[] BuildFrameOpRegistryCacheSources(
        RenderResourceRegistry[] registries)
    {
        FrameOpRegistryCacheSource[] sources = new FrameOpRegistryCacheSource[registries.Length];
        for (int i = 0; i < registries.Length; i++)
        {
            RenderResourceRegistry registry = registries[i];
            sources[i] = new FrameOpRegistryCacheSource(
                registry,
                ComputeResourceRegistrySignature(registry));
        }

        return sources;
    }

    private bool TryGetCachedMergedFrameOpRegistry(
        in FrameOpPlannerStateKey ownerKey,
        RenderResourceRegistry? primaryRegistry,
        FrameOpRegistryCacheSource[] sources,
        out RenderResourceRegistry? mergedRegistry)
    {
        for (int i = 0; i < _mergedFrameOpRegistryCache.Count; i++)
        {
            MergedFrameOpRegistryCacheEntry entry = _mergedFrameOpRegistryCache[i];
            if (!entry.OwnerKey.Equals(ownerKey) ||
                !ReferenceEquals(entry.PrimaryRegistry, primaryRegistry))
                continue;

            if (primaryRegistry is null)
            {
                if (!FrameOpRegistryCacheSourcesMatch(entry.Sources, sources))
                    continue;

                entry.LastUsedFrameId = RuntimeEngine.Rendering.State.RenderFrameId;
                mergedRegistry = entry.MergedRegistry;
                return true;
            }

            bool rebuild = entry.PrimaryDescriptorSignature != primaryRegistry.DescriptorSignature;
            FrameOpRegistryCacheSource[] accumulatedSources = entry.Sources;
            List<FrameOpRegistryCacheSource>? updatedSources = null;
            for (int sourceIndex = 0; sourceIndex < sources.Length; sourceIndex++)
            {
                FrameOpRegistryCacheSource current = sources[sourceIndex];
                int accumulatedIndex = IndexOfFrameOpRegistryCacheSource(accumulatedSources, current.Registry);
                if (accumulatedIndex >= 0 &&
                    accumulatedSources[accumulatedIndex].DescriptorSignature == current.DescriptorSignature)
                {
                    continue;
                }

                updatedSources ??= new List<FrameOpRegistryCacheSource>(accumulatedSources);
                if (accumulatedIndex >= 0)
                    updatedSources[accumulatedIndex] = current;
                else
                    updatedSources.Add(current);
                rebuild = true;
            }

            if (updatedSources is not null)
            {
                accumulatedSources = [.. updatedSources];
                entry.Sources = accumulatedSources;
            }

            if (rebuild)
            {
                RenderResourceRegistry persistentMerged = new();
                AddRegistryDescriptors(persistentMerged, primaryRegistry, overwrite: true);
                for (int sourceIndex = 0; sourceIndex < accumulatedSources.Length; sourceIndex++)
                {
                    RenderResourceRegistry source = accumulatedSources[sourceIndex].Registry;
                    if (!ReferenceEquals(source, primaryRegistry))
                        AddRegistryDescriptors(persistentMerged, source, overwrite: false);
                }

                entry.PrimaryDescriptorSignature = primaryRegistry.DescriptorSignature;
                entry.MergedRegistry = persistentMerged;
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
        RenderResourceRegistry registry)
    {
        for (int i = 0; i < sources.Length; i++)
        {
            if (ReferenceEquals(sources[i].Registry, registry))
                return i;
        }

        return -1;
    }

    private void RememberMergedFrameOpRegistry(
        in FrameOpPlannerStateKey ownerKey,
        RenderResourceRegistry? primaryRegistry,
        FrameOpRegistryCacheSource[] sources,
        RenderResourceRegistry mergedRegistry)
    {
        ulong frameId = RuntimeEngine.Rendering.State.RenderFrameId;
        _mergedFrameOpRegistryCache.Add(new MergedFrameOpRegistryCacheEntry(
            ownerKey,
            primaryRegistry,
            sources,
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

    private static bool FrameOpRegistryCacheSourcesMatch(
        FrameOpRegistryCacheSource[] cached,
        FrameOpRegistryCacheSource[] current)
    {
        if (cached.Length != current.Length)
            return false;

        for (int i = 0; i < cached.Length; i++)
        {
            if (!ReferenceEquals(cached[i].Registry, current[i].Registry) ||
                cached[i].DescriptorSignature != current[i].DescriptorSignature)
            {
                return false;
            }
        }

        return true;
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
            {
                return false;
            }
        }

        return true;
    }

    private static bool TextureDescriptorsCoveredByPrimary(
        RenderResourceRegistry source,
        RenderResourceRegistry primary)
    {
        foreach (KeyValuePair<string, RenderTextureResource> pair in source.TextureRecords)
        {
            if (!primary.TextureRecords.TryGetValue(pair.Key, out RenderTextureResource? primaryRecord) ||
                !EqualityComparer<TextureResourceDescriptor>.Default.Equals(primaryRecord.Descriptor, pair.Value.Descriptor))
            {
                return false;
            }
        }

        return true;
    }

    private static bool FrameBufferDescriptorsCoveredByPrimary(
        RenderResourceRegistry source,
        RenderResourceRegistry primary)
    {
        foreach (KeyValuePair<string, RenderFrameBufferResource> pair in source.FrameBufferRecords)
        {
            if (!primary.FrameBufferRecords.TryGetValue(pair.Key, out RenderFrameBufferResource? primaryRecord) ||
                !FrameBufferDescriptorsEquivalent(primaryRecord.Descriptor, pair.Value.Descriptor))
            {
                return false;
            }
        }

        return true;
    }

    private static bool BufferDescriptorsCoveredByPrimary(
        RenderResourceRegistry source,
        RenderResourceRegistry primary)
    {
        foreach (KeyValuePair<string, RenderBufferResource> pair in source.BufferRecords)
        {
            if (!primary.BufferRecords.TryGetValue(pair.Key, out RenderBufferResource? primaryRecord) ||
                !EqualityComparer<BufferResourceDescriptor>.Default.Equals(primaryRecord.Descriptor, pair.Value.Descriptor))
            {
                return false;
            }
        }

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
        {
            return false;
        }

        for (int i = 0; i < left.Attachments.Count; i++)
        {
            FrameBufferAttachmentDescriptor leftAttachment = left.Attachments[i];
            FrameBufferAttachmentDescriptor rightAttachment = right.Attachments[i];
            if (!string.Equals(leftAttachment.ResourceName, rightAttachment.ResourceName, StringComparison.OrdinalIgnoreCase) ||
                leftAttachment.Attachment != rightAttachment.Attachment ||
                leftAttachment.MipLevel != rightAttachment.MipLevel ||
                leftAttachment.LayerIndex != rightAttachment.LayerIndex)
            {
                return false;
            }
        }

        return true;
    }

    internal static void AddRegistryDescriptors(
        RenderResourceRegistry destination,
        RenderResourceRegistry source,
        bool overwrite)
    {
        foreach (KeyValuePair<string, RenderTextureResource> pair in source.TextureRecords)
        {
            if (overwrite || !destination.TextureRecords.ContainsKey(pair.Key))
                destination.RegisterTextureDescriptor(pair.Value.Descriptor);
        }

        foreach (KeyValuePair<string, RenderFrameBufferResource> pair in source.FrameBufferRecords)
        {
            if (overwrite || !destination.FrameBufferRecords.ContainsKey(pair.Key))
                destination.RegisterFrameBufferDescriptor(pair.Value.Descriptor);
        }

        foreach (KeyValuePair<string, RenderBufferResource> pair in source.BufferRecords)
        {
            if (overwrite || !destination.BufferRecords.ContainsKey(pair.Key))
                destination.RegisterBufferDescriptor(pair.Value.Descriptor);
        }
    }

    private static IEnumerable<XRFrameBuffer> EnumerateFrameOpFrameBuffers(FrameOp op)
    {
        if (op.Target is not null)
            yield return op.Target;

        if (op is BlitOp blit)
        {
            if (blit.InFbo is not null)
                yield return blit.InFbo;
            if (blit.OutFbo is not null)
                yield return blit.OutFbo;
        }
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
        {
            throw new InvalidOperationException(
                $"Resource planner cannot be replaced while command-chain readers are using frozen plan revision {_commandChainFrozenResourcePlanRevision}.");
        }

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
            ActiveResourceAllocator = pendingAllocator;

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

        _ = oldAllocator.TryRetirePhysicalResources(this, retainedAutoExposureGroup, reusedImageGroups);
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

        _lastActiveFilterSourcePassMetadata = passMetadata;
        _lastActiveFilterResourceRegistry = resourceRegistry;
        _lastActiveFilterResourceRegistryRevision = resourceRegistryRevision;
        _lastActiveFilterPassSetSignature = activePassSetSignature;
        _lastActiveFilterResourceSetSignature = activeResourceSetSignature;
        _lastActiveFilterConstrainToActivePassSet = constrainToActivePassSet;
        _lastActiveFilterResult = result;
        return result;
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

        foreach (RenderPassMetadata pass in passMetadata)
        {
            hash.Add(pass.PassIndex);
            hash.Add(pass.DeclarationOrder);
            hash.Add((int)pass.Stage);
            hash.Add(pass.Name, StringComparer.Ordinal);
            hash.Add(pass.Revision);

            foreach (RenderPassResourceUsage usage in pass.ResourceUsages)
            {
                hash.Add(usage.ResourceName, StringComparer.Ordinal);
                hash.Add((int)usage.ResourceType);
                hash.Add((int)usage.Access);
                hash.Add((int)usage.LoadOp);
                hash.Add((int)usage.StoreOp);
                AddSubresourceRangeToHash(ref hash, usage.SubresourceRange);
            }

            foreach (int dependency in pass.ExplicitDependencies)
                hash.Add(dependency);

            foreach (string schema in pass.DescriptorSchemas)
                hash.Add(schema, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }

    private static int ComputePassMetadataRevisionStamp(IReadOnlyCollection<RenderPassMetadata>? passMetadata)
    {
        if (passMetadata is null || passMetadata.Count == 0)
            return 0;

        HashCode hash = new();
        hash.Add(passMetadata.Count);
        foreach (RenderPassMetadata pass in passMetadata)
        {
            hash.Add(pass.PassIndex);
            hash.Add(pass.DeclarationOrder);
            hash.Add(pass.Revision);
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
