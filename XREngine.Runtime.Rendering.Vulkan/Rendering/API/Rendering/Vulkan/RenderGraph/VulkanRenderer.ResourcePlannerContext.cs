using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
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


    internal FrameOpContext CaptureFrameOpContext()
    {
        XRRenderPipelineInstance? pipeline = ResolveFrameOpContextPipeline(
            RuntimeEngine.Rendering.State.CurrentRenderingPipeline,
            RuntimeEngine.Rendering.State.CurrentRenderGraphPassPipeline,
            RuntimeEngine.Rendering.State.CurrentRenderGraphPassIndex);
        XRViewport? viewport = pipeline?.RenderState.WindowViewport
            ?? pipeline?.LastWindowViewport
            ?? RuntimeEngine.Rendering.State.RenderingViewport;
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
            pipeline?.ActiveMeshRenderCommands.RenderingBackendReadyPackage.PassMetadata
                ?? pipeline?.Pipeline?.PassMetadata,
            displayWidth,
            displayHeight,
            internalWidth,
            internalHeight,
            outputFrameBuffer?.Name,
            ShouldPreserveSubmissionOrderBlock(),
            outputTargetIdentity,
            outputTargetName);
        context = CompleteFrameOpContext(context with
        {
            OutputFrameBuffer = outputFrameBuffer,
            OperationWorkspace = GetCommandThreadFrameOpWorkspace(),
        });
        context = ApplyInteractiveResizePlannerFreeze(context);

        if (pipeline is not null)
            ActiveLastActiveFrameOpContext = context;

        return context;
    }

    /// <summary>
    /// Returns the immutable context installed by the active pipeline resource scope.
    /// Mesh command consumption calls this once per draw, so the fallback performs the
    /// full context capture only when no matching pipeline scope is active.
    /// </summary>
    internal FrameOpContext CaptureFrameOpContextForCurrentPipelineScope()
    {
        XRRenderPipelineInstance? pipeline = ResolveFrameOpContextPipeline(
            RuntimeEngine.Rendering.State.CurrentRenderingPipeline,
            RuntimeEngine.Rendering.State.CurrentRenderGraphPassPipeline,
            RuntimeEngine.Rendering.State.CurrentRenderGraphPassIndex);
        if (ActiveLastActiveFrameOpContext is { } active &&
            ReferenceEquals(active.PipelineInstance, pipeline))
        {
            return active;
        }

        return CaptureFrameOpContext();
    }

    /// <summary>
    /// Resolves the pipeline whose render-graph metadata owns the active pass.
    /// A nested pipeline may be active while a parent pass remains on the thread-local stack.
    /// </summary>
    internal static XRRenderPipelineInstance? ResolveFrameOpContextPipeline(
        XRRenderPipelineInstance? activePipeline,
        XRRenderPipelineInstance? passOwnerPipeline,
        int passIndex)
        => passIndex != int.MinValue && passOwnerPipeline is not null
            ? passOwnerPipeline
            : activePipeline;

    private FrameOpContext ApplyInteractiveResizePlannerFreeze(in FrameOpContext context)
    {
        if (!XRWindow.IsInteractiveResizeInProgress)
        {
            ResetInteractiveResizePlannerFreeze();
            return context;
        }

        if (TryResolveExternalSwapchainTargetExtent(out _))
            return context;

        CaptureInteractiveResizePlannerExtents(
            context,
            out VulkanInteractiveResizePlannerExtentSnapshot snapshot,
            out bool captured,
            out bool reportCapacityExceeded);
        VulkanInteractiveResizePlannerContextKey key = BuildInteractiveResizePlannerContextKey(context);
        if (reportCapacityExceeded)
        {
            Debug.VulkanWarning(
                "[VulkanResourcePlanner] Interactive-resize extent cache reached its {0}-context capacity. " +
                "Preserving existing frozen extents; additional context {1} pipeline={2} viewport={3} " +
                "outputFbo=0x{4:X8} output=0x{5:X8} will use live extents.",
                _framePlanner.InteractiveResizeExtentCache.Capacity,
                key.ContextKind,
                key.PipelineIdentity,
                key.ViewportIdentity,
                key.OutputFrameBufferIdentity,
                key.OutputTargetIdentity);
        }

        if (captured)
        {
            Debug.Vulkan(
                "[VulkanResourcePlanner] Freezing render-resource extents for context {0} pipeline={1} viewport={2} outputFbo=0x{3:X8} output=0x{4:X8} at {5}x{6}/{7}x{8}.",
                key.ContextKind,
                key.PipelineIdentity,
                key.ViewportIdentity,
                key.OutputFrameBufferIdentity,
                key.OutputTargetIdentity,
                snapshot.DisplayWidth,
                snapshot.DisplayHeight,
                snapshot.InternalWidth,
                snapshot.InternalHeight);
        }

        return RefreshFrameOpContextRecordingFingerprint(context with
        {
            DisplayWidth = snapshot.DisplayWidth,
            DisplayHeight = snapshot.DisplayHeight,
            InternalWidth = snapshot.InternalWidth,
            InternalHeight = snapshot.InternalHeight
        });
    }

    private void CaptureInteractiveResizePlannerExtents(
        in FrameOpContext context,
        out VulkanInteractiveResizePlannerExtentSnapshot snapshot,
        out bool captured,
        out bool reportCapacityExceeded)
    {
        VulkanInteractiveResizePlannerContextKey key = BuildInteractiveResizePlannerContextKey(context);
        VulkanInteractiveResizePlannerExtentSnapshot candidate = new(
            context.DisplayWidth,
            context.DisplayHeight,
            context.InternalWidth,
            context.InternalHeight);
        snapshot = _framePlanner.InteractiveResizeExtentCache.GetOrCapture(
            key,
            candidate,
            out captured,
            out reportCapacityExceeded);
    }

    private void ResetInteractiveResizePlannerFreeze()
        => _framePlanner.InteractiveResizeExtentCache.Clear();

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
        return !FrameOpContextHasPlannerResources(context)
            ? null
            : RentExternalResourcePlannerReadbackScope(context);
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
        if (!_deviceContext.IsOperational)
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
        VulkanPreparedResourceGenerationManifest? preparedManifest = null;
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
                UpdateResourcePlannerFromContext(context, deferReusedImageMetadataCommit: true);
                pendingState = scope.CaptureCurrent(this);
                pendingState.LastActiveFrameOpContext = context;

                if (!ValidatePreparedResourceAllocator(pendingState.ResourcePlanner, pendingState.ResourceAllocator, out failureReason))
                {
                    _ = pendingState.ResourceAllocator.TryRetirePhysicalResources(
                        this,
                        exceptImageGroups: pendingState.ResourceAllocator.CapturePendingReusedImageGroups(),
                        immediate: true);
                    return false;
                }

                if (!TryCapturePreparedResourceGenerationManifest(
                    generation,
                    pendingState,
                    previousState,
                    out preparedManifest,
                    out failureReason))
                {
                    RestorePreparedGenerationFramebufferWrappers(preparedManifest, previousState);
                    _ = pendingState.ResourceAllocator.TryRetirePhysicalResources(
                        this,
                        exceptImageGroups: pendingState.ResourceAllocator.CapturePendingReusedImageGroups(),
                        immediate: true);
                    return false;
                }

                pendingState = scope.CaptureCurrent(this);
                pendingState.LastActiveFrameOpContext = context;
                pendingState.PreparedGenerationManifest = preparedManifest;
                transaction = new VulkanRenderResourceGenerationTransaction(
                    this,
                    previousState,
                    pendingState,
                    BuildFrameOpPlannerStateKey(context),
                    preparedManifest!);
                return true;
            }
            catch (Exception ex)
            {
                RestorePreparedGenerationFramebufferWrappers(preparedManifest, previousState);
                pendingState = scope.CaptureCurrent(this);
                if (!pendingState.ResourceAllocator.IsRetired)
                    _ = pendingState.ResourceAllocator.TryRetirePhysicalResources(
                        this,
                        exceptImageGroups: pendingState.ResourceAllocator.CapturePendingReusedImageGroups(),
                        immediate: true);
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

    private bool TryCapturePreparedResourceGenerationManifest(
        RenderResourceGeneration generation,
        in ResourcePlannerRuntimeState pendingState,
        in ResourcePlannerRuntimeState previousState,
        out VulkanPreparedResourceGenerationManifest? manifest,
        out string? failureReason)
    {
        List<VulkanPreparedResourceGenerationManifest.ImageEntry> images = [];
        List<VulkanPreparedResourceGenerationManifest.FrameBufferEntry> frameBuffers = [];
        List<VulkanPreparedResourceGenerationManifest.BufferEntry> buffers = [];

        foreach ((string name, RenderTextureResource record) in generation.Registry.TextureRecords)
        {
            if (record.Instance is null ||
                !pendingState.ResourceAllocator.TryGetPhysicalGroupForResource(name, out VulkanPhysicalImageGroup? physicalGroup) ||
                physicalGroup?.IsAllocated != true)
            {
                continue;
            }

            if (GetOrCreateAPIRenderObject(record.Instance, generateNow: true) is not IVkImageDescriptorSource source ||
                !source.TryGetDescriptorSnapshot(
                    requestedViewType: null,
                    requestedAspectMask: null,
                    "pending Vulkan resource generation",
                    allowSynchronousUpload: true,
                    out VkImageDescriptorSnapshot snapshot) ||
                !snapshot.IsReady ||
                !snapshot.UsesAllocatorImage ||
                snapshot.Image.Handle != physicalGroup.Image.Handle)
            {
                manifest = null;
                failureReason = $"Vulkan image-view/descriptor payload for '{name}' was not ready for the pending generation.";
                return false;
            }

            images.Add(new(
                name,
                record.Instance,
                source,
                snapshot,
                GetCurrentVulkanResourceGeneration(ObjectType.Image, snapshot.Image.Handle),
                GetCurrentVulkanResourceGeneration(ObjectType.ImageView, snapshot.View.Handle),
                GetCurrentVulkanResourceGeneration(ObjectType.Sampler, snapshot.Sampler.Handle)));
        }

        foreach ((string name, RenderFrameBufferResource record) in generation.Registry.FrameBufferRecords)
        {
            if (record.Instance is null || !record.HasAttachments)
                continue;

            VkFrameBuffer? wrapper =
                GetOrCreateAPIRenderObject(record.Instance, generateNow: true) as VkFrameBuffer;
            if (wrapper is null ||
                !wrapper.TryCaptureRecordedRenderTargetSnapshot(out VulkanRecordedRenderTargetSnapshot snapshot))
            {
                // Every wrapper prepared before the failing entry has already
                // switched its cached native attachments to the pending planner
                // state. Restore the complete partial transaction immediately;
                // no manifest is published on this failure path.
                if (wrapper is not null || frameBuffers.Count != 0)
                {
                    using ThreadResourcePlannerRuntimeStateScope restoreScope =
                        EnterThreadResourcePlannerRuntimeStateScope(in previousState);
                    for (int preparedIndex = 0; preparedIndex < frameBuffers.Count; preparedIndex++)
                        frameBuffers[preparedIndex].Wrapper.EnsureCurrent();
                    wrapper?.EnsureCurrent();
                }
                manifest = null;
                failureReason = $"Vulkan framebuffer/dynamic-attachment snapshot for '{name}' was incomplete for the pending generation.";
                return false;
            }

            frameBuffers.Add(new(name, record.Instance, wrapper, snapshot));
        }

        foreach (VulkanPhysicalBufferGroup group in pendingState.ResourceAllocator.EnumeratePhysicalBufferGroups())
        {
            if (!group.IsAllocated || group.Buffer.Handle == 0)
                continue;

            buffers.Add(new(
                group.Buffer,
                GetCurrentVulkanResourceGeneration(ObjectType.Buffer, group.Buffer.Handle),
                group.SizeInBytes));
        }

        manifest = new VulkanPreparedResourceGenerationManifest(
            generation.Registry,
            generation.Registry.DescriptorSignature,
            images.ToArray(),
            frameBuffers.ToArray(),
            buffers.ToArray(),
            ResourceRuntime.Lifetime.Tracker.CaptureRetirementWatermark());
        failureReason = null;
        return true;
    }

    private bool TryValidatePreparedResourceGenerationManifest(
        VulkanPreparedResourceGenerationManifest manifest,
        out string? failureReason)
    {
        // A staged generation may have prepared descriptors while work from the
        // preceding generation was still in flight. Do not publish and retire the
        // previous allocator until the watermark captured with this manifest has
        // completed; otherwise the ticket would be a dead diagnostic capture.
        if (!IsVulkanRetirementReady(manifest.DependencyTicket))
        {
            failureReason = "The pending Vulkan resource generation still has in-flight preparation dependencies.";
            return false;
        }

        if (manifest.Registry.DescriptorSignature != manifest.DescriptorSignature)
        {
            failureReason = "The pending Vulkan resource registry descriptor payload changed before commit.";
            return false;
        }

        for (int i = 0; i < manifest.ImageCount; i++)
        {
            VulkanPreparedResourceGenerationManifest.ImageEntry entry = manifest.GetImage(i);
            if (!TryGetAPIRenderObject(entry.Texture, out var apiObject) ||
                !ReferenceEquals(apiObject, entry.Source) ||
                !entry.Source.TryGetDescriptorSnapshot(
                    requestedViewType: null,
                    requestedAspectMask: null,
                    "pending Vulkan resource generation commit",
                    allowSynchronousUpload: false,
                    out VkImageDescriptorSnapshot current) ||
                current != entry.Snapshot ||
                GetCurrentVulkanResourceGeneration(ObjectType.Image, current.Image.Handle) != entry.ImageGeneration ||
                GetCurrentVulkanResourceGeneration(ObjectType.ImageView, current.View.Handle) != entry.ViewGeneration ||
                GetCurrentVulkanResourceGeneration(ObjectType.Sampler, current.Sampler.Handle) != entry.SamplerGeneration)
            {
                failureReason = $"Vulkan image-view/descriptor payload for '{entry.Name}' changed before generation commit.";
                return false;
            }
        }

        for (int i = 0; i < manifest.FrameBufferCount; i++)
        {
            VulkanPreparedResourceGenerationManifest.FrameBufferEntry entry = manifest.GetFrameBuffer(i);
            if (!TryGetAPIRenderObject(entry.FrameBuffer, out var apiObject) ||
                !ReferenceEquals(apiObject, entry.Wrapper) ||
                !entry.Wrapper.TryCaptureRecordedRenderTargetSnapshot(out VulkanRecordedRenderTargetSnapshot current) ||
                current != entry.Snapshot)
            {
                failureReason = $"Vulkan framebuffer/dynamic-attachment payload for '{entry.Name}' changed before generation commit.";
                return false;
            }
        }

        for (int i = 0; i < manifest.BufferCount; i++)
        {
            VulkanPreparedResourceGenerationManifest.BufferEntry entry = manifest.GetBuffer(i);
            if (GetCurrentVulkanResourceGeneration(ObjectType.Buffer, entry.Buffer.Handle) != entry.Generation)
            {
                failureReason = $"Vulkan buffer 0x{entry.Buffer.Handle:X} changed before generation commit.";
                return false;
            }
        }

        failureReason = null;
        return true;
    }

    private void RestorePreparedGenerationFramebufferWrappers(
        VulkanPreparedResourceGenerationManifest? manifest,
        in ResourcePlannerRuntimeState previousState)
    {
        if (manifest is null || manifest.FrameBufferCount == 0)
            return;

        using ThreadResourcePlannerRuntimeStateScope scope =
            EnterThreadResourcePlannerRuntimeStateScope(in previousState);
        for (int i = 0; i < manifest.FrameBufferCount; i++)
            manifest.GetFrameBuffer(i).Wrapper.EnsureCurrent();
    }

    private sealed class VulkanRenderResourceGenerationTransaction(
        VulkanRenderer renderer,
        ResourcePlannerRuntimeState previousState,
        ResourcePlannerRuntimeState pendingState,
        VulkanFrameOpPlannerStateKey pendingKey,
        VulkanPreparedResourceGenerationManifest preparedManifest) : IRenderResourceGenerationTransaction
    {
        private bool _committed;

        public void Commit()
        {
            if (_committed)
                return;

            HashSet<VulkanPhysicalImageGroup>? reusedImageGroups =
                pendingState.ResourceAllocator.CapturePendingReusedImageGroups();
            using (ThreadResourcePlannerRuntimeStateScope validationScope =
                   renderer.EnterThreadResourcePlannerRuntimeStateScope(in pendingState))
            {
                if (!renderer.TryValidatePreparedResourceGenerationManifest(preparedManifest, out string? failureReason))
                    throw new InvalidOperationException(failureReason);
            }

            FrameOpResourcePlannerSwitchingState switchingState =
                CloneFrameOpResourcePlannerSwitchingState(
                    renderer.ActiveFrameOpResourcePlannerSwitchingState);
            pendingState.FrameOpResourcePlannerSwitchingState = switchingState;
            pendingState.PreparedGenerationManifest = preparedManifest;
            switchingState.States[pendingKey] = pendingState;
            renderer.MarkFrameOpResourcePlannerStateUsed(switchingState, pendingKey);
            renderer.PruneFrameOpResourcePlannerStatesToCapacity(switchingState);
            renderer.PublishResourcePlannerRuntimeState(pendingState, commitReusedImageMetadata: true);
            _committed = true;

            try
            {
                if (!ReferenceEquals(previousState.ResourceAllocator, pendingState.ResourceAllocator) &&
                    !IsAllocatorOwnedByFrameOpPlannerState(switchingState, previousState.ResourceAllocator))
                {
                    // Validation established that this exact preparation watermark
                    // is complete. Retire only after that dependency boundary.
                    if (!renderer.IsVulkanRetirementReady(preparedManifest.DependencyTicket))
                        throw new InvalidOperationException("Prepared Vulkan resource generation dependencies regressed before retirement.");

                    _ = previousState.ResourceAllocator.TryRetirePhysicalResources(
                        renderer,
                        exceptImageGroups: reusedImageGroups);
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
            {
                renderer.RestorePreparedGenerationFramebufferWrappers(preparedManifest, previousState);
                _ = pendingState.ResourceAllocator.TryRetirePhysicalResources(
                    renderer,
                    exceptImageGroups: pendingState.ResourceAllocator.CapturePendingReusedImageGroups(),
                    immediate: true);
            }
        }
    }

    internal ExternalResourcePlannerReadbackScope EnterFrameOpResourcePlannerReadbackScope(in FrameOpContext context)
        => new(this, context);

    // ExternalResourcePlannerReadbackScope temporarily swaps renderer-wide planner
    // state and updates the shared planner-state cache. Worker command recording may
    // run concurrently, but those swaps must remain an atomic scope until planner
    // snapshots become immutable and can be passed directly to RecordDraw.
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
            pipeline.ActiveMeshRenderCommands.RenderingBackendReadyPackage.PassMetadata
                ?? pipeline.Pipeline?.PassMetadata,
            displayWidth,
            displayHeight,
            internalWidth,
            internalHeight,
            outputFrameBuffer?.Name,
            ShouldPreserveSubmissionOrderBlock(),
            outputTargetIdentity,
            outputTargetName);

        context = CompleteFrameOpContext(context with { OutputFrameBuffer = outputFrameBuffer });
        return ApplyInteractiveResizePlannerFreeze(context);
    }

    private FrameOpContext CompleteFrameOpContext(in FrameOpContext context)
    {
        bool stereoEnabled = ResolveFrameOpContextStereoEnabled(context);
        EVulkanFrameOpContextKind contextKind = ResolveFrameOpContextKind(context);
        FrameOpContext complete = context with
        {
            OutputFrameBufferIdentity = ComputeOutputFrameBufferIdentity(context.OutputFrameBufferName),
            ContextKind = contextKind,
            ContextId = _framePlanner.NextFrameContextId(),
            LogicalViewId = ResolveFrameOpLogicalViewId(context, contextKind),
            SubmissionQueueFamily = ResolveFrameOpSubmissionQueueFamily(context.PassMetadata),
            StereoEnabled = stereoEnabled,
            MultiviewEnabled = ResolveFrameOpContextMultiviewEnabled(context, stereoEnabled),
            ResourceGeneration = ResolveFrameOpContextResourceGeneration(context.PipelineInstance),
            DescriptorGeneration = ResolveFrameOpContextDescriptorGeneration(context.ResourceRegistry),
            ResourceRegistrySignatureSnapshot = ComputeResourceRegistrySignature(context.ResourceRegistry),
        };

        return RefreshFrameOpContextRecordingFingerprint(complete);
    }

    private ulong ResolveFrameOpLogicalViewId(
        in FrameOpContext context,
        EVulkanFrameOpContextKind contextKind)
    {
        if (contextKind is EVulkanFrameOpContextKind.OpenXrEye or EVulkanFrameOpContextKind.OpenXrMirror)
        {
            uint openXrViewIndex = OutputRuntime.OpenXrBackend.CurrentThreadExecutionState.FrameContext.ViewIndex;
            ulong frameId = RuntimeRenderingHostServices.FrameTiming.CurrentRenderFrameId;
            if (frameId != 0UL &&
                RenderFrameViewSetPublication.TryGet(frameId, out RenderFrameViewSet views))
            {
                for (int index = 0; index < views.ViewCount; index++)
                {
                    RenderFrameViewDescriptor view = views.GetView(index);
                    if (view.OpenXrViewIndex == openXrViewIndex)
                        return view.EffectiveHistoryKey;
                }
            }
        }

        FrameOpSignatureHasher hash = new();
        hash.Add(0x4C4F474943564945UL);
        hash.Add((int)contextKind);
        hash.Add(context.PipelineIdentity);
        hash.Add(context.ViewportIdentity);
        hash.Add(context.OutputFrameBufferIdentity);
        ulong result = hash.ToHash();
        return result == 0UL ? 1UL : result;
    }

    private FrameOpContext RefreshFrameOpContextRecordingFingerprint(in FrameOpContext context)
        => context with { RecordingFingerprint = ComputeFrameOpContextRecordingFingerprint(context) };

    private EVulkanFrameOpContextKind ResolveFrameOpContextKind(in FrameOpContext context)
    {
        if (IsThreadOpenXrExternalSwapchainTarget)
        {
            EVulkanFrameOpContextKind contextKind =
                OutputRuntime.OpenXrBackend.CurrentThreadExecutionState.FrameContext.ContextKind;
            return contextKind == EVulkanFrameOpContextKind.Unknown
                ? EVulkanFrameOpContextKind.OpenXrEye
                : contextKind;
        }

        if (RuntimeEngine.Rendering.State.IsLightProbePass)
            return EVulkanFrameOpContextKind.LightProbeCapture;
        if (ResolveFrameOpContextShadowPass(context))
            return EVulkanFrameOpContextKind.Shadow;
        if (RuntimeEngine.Rendering.State.IsSceneCapturePass)
            return EVulkanFrameOpContextKind.SceneCapture;

        string pipelineTypeName = context.PipelineInstance?.AssignedPipeline?.GetType().Name ?? string.Empty;
        if (pipelineTypeName.Contains("Diagnostic", StringComparison.OrdinalIgnoreCase))
            return EVulkanFrameOpContextKind.DiagnosticCapture;
        if (pipelineTypeName.Contains("UserInterface", StringComparison.OrdinalIgnoreCase) ||
            pipelineTypeName.Contains("UiPreview", StringComparison.OrdinalIgnoreCase))
            return EVulkanFrameOpContextKind.UiPreview;

        return EVulkanFrameOpContextKind.MainViewport;
    }

    private uint ResolveFrameOpSubmissionQueueFamily(IReadOnlyCollection<RenderPassMetadata>? passMetadata)
        => BuildQueueOwnershipConfig(passMetadata).GraphicsQueueFamilyIndex;

    internal static bool ResolveFrameOpContextStereoEnabled(in FrameOpContext context)
        => context.PipelineInstance?.RenderState.StereoPass
            ?? RuntimeEngine.Rendering.State.IsStereoPass;

    internal static bool ResolveFrameOpContextShadowPass(in FrameOpContext context)
        => context.PipelineInstance?.RenderState.ShadowPass
            ?? RuntimeEngine.Rendering.State.IsShadowPass;

    internal static bool ResolveFrameOpContextMultiviewEnabled(
        in FrameOpContext context,
        bool stereoEnabled)
    {
        if (!stereoEnabled)
            return false;

        string pipelineTypeName = context.PipelineInstance?.AssignedPipeline?.GetType().Name ?? string.Empty;
        return pipelineTypeName.Contains("MultiView", StringComparison.OrdinalIgnoreCase) ||
            pipelineTypeName.Contains("Multiview", StringComparison.OrdinalIgnoreCase);
    }

    private static ulong ResolveFrameOpContextResourceGeneration(XRRenderPipelineInstance? pipeline)
        => unchecked((ulong)Math.Max(pipeline?.ResourceGeneration ?? 0, 0));

    private ulong ResolveFrameOpContextDescriptorGeneration(RenderResourceRegistry? registry)
    {
        // _frameTelemetry._vulkanDescriptorTableGeneration is a crash-breadcrumb counter. It advances for
        // descriptor content writes while the same frame is being prepared, so treating it
        // as a recording dependency makes otherwise identical frame ops acquire a different
        // primary-command-buffer key every frame. Descriptor-set identity and legal
        // completed-slot content refresh are validated per draw before a cached primary is
        // reused; the frame context only needs the immutable registry contract here.
        return unchecked((ulong)(uint)ComputeResourceRegistrySignature(registry));
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
        hash.Add(context.LogicalViewId);
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
        private readonly VulkanFrameOpPlannerStateKey _key;
        private readonly FrameOpContext _context;
        private readonly bool _active;

        public ExternalResourcePlannerReadbackScope(
            VulkanRenderer renderer,
            in FrameOpContext context)
        {
            _renderer = renderer;
            _context = context;
            _previousState = renderer.CaptureResourcePlannerRuntimeState();
            _active = renderer.DeviceContext.IsOperational &&
                FrameOpResourcePlannerSwitchingEnabled &&
                !renderer.ActiveFrameOpResourcePlannerSwitchingState.MergedPlanActive &&
                FrameOpContextHasPlannerResources(context);

            if (!_active)
            {
                _key = default;
                return;
            }

            FrameOpResourcePlannerSwitchingState switchingState = renderer.ActiveFrameOpResourcePlannerSwitchingState;
            VulkanFrameOpPlannerStateKey requestedKey = BuildFrameOpPlannerStateKey(context);
            bool canReusePreviousState = ResourcePlannerRuntimeStateMatchesPlannerStateKeyIgnoringRegistry(
                _previousState,
                requestedKey) &&
                IsFrameOpPlannerAllocatorExclusivelyOwnedByKey(
                    switchingState,
                    requestedKey,
                    _previousState.ResourceAllocator);
            bool foundCachedState = TryFindBestCompatibleFrameOpPlannerState(
                context,
                switchingState,
                out VulkanFrameOpPlannerStateKey cachedKey,
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
            bool canPublish = _active && _renderer.DeviceContext.IsOperational;
            if (canPublish)
            {
                currentState = _renderer.CaptureResourcePlannerRuntimeState();
                currentState.LastActiveFrameOpContext = _context;
                FrameOpResourcePlannerSwitchingState switchingState =
                    _renderer.ActiveFrameOpResourcePlannerSwitchingState;
                canPublish = IsFrameOpPlannerAllocatorExclusivelyOwnedByKey(
                    switchingState,
                    _key,
                    currentState.ResourceAllocator);
                if (canPublish)
                {
                    switchingState.States[_key] = currentState;
                    _renderer.MarkFrameOpResourcePlannerStateUsed(switchingState, _key);
                }
            }

            ResourcePlannerRuntimeState restoreState =
                _active && _previousState.ResourceAllocator is not null && _previousState.ResourceAllocator.IsRetired
                    ? canPublish
                        ? currentState
                        : ResourcePlannerRuntimeState.CreateEmpty()
                    : _previousState;
            _renderer.RestoreResourcePlannerRuntimeState(restoreState);
            if (_active &&
                !ReferenceEquals(currentState.ResourceAllocator, restoreState.ResourceAllocator) &&
                _context.ResourceRegistry is not null)
            {
                // Descriptor and attachment wrappers are shared even though planner
                // allocators are scoped. Force the next command-buffer preparation
                // for this registry to rebind wrappers to its restored allocator.
                _renderer.OutputRuntime.OpenXrBackend.ResourceRegistryWrapperRefreshStamps.Remove(
                    _context.ResourceRegistry);
            }
        }
    }

    /// <summary>
    /// Removes the interface-boxing allocation from the render-pipeline scope
    /// override. Nested pipelines rent distinct instances, and disposed instances
    /// remain thread-local so the steady-state render loop only resets their value
    /// state. The renderer owns its reusable instances, so worker lifecycle
    /// cannot retain scopes for a retired backend generation.
    /// </summary>
    internal sealed class PooledExternalResourcePlannerReadbackScope : IDisposable
    {
        private VulkanRenderer? _owner;
        private ExternalResourcePlannerReadbackScope _scope;
        private bool _leased;

        public void Lease(VulkanRenderer renderer, in FrameOpContext context)
        {
            _owner = renderer;
            _scope = new ExternalResourcePlannerReadbackScope(renderer, context);
            _leased = true;
        }

        public void Dispose()
        {
            if (!_leased)
                return;

            try
            {
                _scope.Dispose();
            }
            finally
            {
                _leased = false;
                _scope = default;
                VulkanRenderer? owner = _owner;
                _owner = null;
                owner?.ReturnExternalResourcePlannerReadbackScope(this);
            }
        }
    }

    private PooledExternalResourcePlannerReadbackScope RentExternalResourcePlannerReadbackScope(
        in FrameOpContext context)
    {
        if (!_framePlanner.FreeExternalResourcePlannerReadbackScopes.TryPop(out PooledExternalResourcePlannerReadbackScope? scope))
            scope = new PooledExternalResourcePlannerReadbackScope();
        scope.Lease(this, context);
        return scope;
    }

    private void ReturnExternalResourcePlannerReadbackScope(
        PooledExternalResourcePlannerReadbackScope scope)
        => _framePlanner.FreeExternalResourcePlannerReadbackScopes.Push(scope);

    private void ReleasePooledExternalResourcePlannerReadbackScopes()
        => _framePlanner.FreeExternalResourcePlannerReadbackScopes.Clear();

    private static bool TryFindBestCompatibleFrameOpPlannerState(
        in FrameOpContext context,
        FrameOpResourcePlannerSwitchingState switchingState,
        out VulkanFrameOpPlannerStateKey key,
        out ResourcePlannerRuntimeState state)
    {
        key = default;
        state = default;
        bool found = false;
        int bestScore = int.MinValue;

        foreach (KeyValuePair<VulkanFrameOpPlannerStateKey, ResourcePlannerRuntimeState> pair in switchingState.States)
        {
            if (!FrameOpContextMatchesPlannerStateKey(context, pair.Key) ||
                !IsFrameOpPlannerAllocatorExclusivelyOwnedByKey(
                    switchingState,
                    pair.Key,
                    pair.Value.ResourceAllocator))
            {
                continue;
            }

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

    /// <summary>
    /// Finds the prior state owned by the same output/view even when descriptor
    /// publication changed the registry or resource-generation input key. The
    /// caller still runs the planner against the current context, so a genuine
    /// physical-layout change replaces the allocator while a metadata-only change
    /// keeps the existing images and buffers alive.
    /// </summary>
    private static bool TryFindBestPhysicalOwnerFrameOpPlannerState(
        in VulkanFrameOpPlannerStateKey requestedKey,
        FrameOpResourcePlannerSwitchingState switchingState,
        out VulkanFrameOpPlannerStateKey key,
        out ResourcePlannerRuntimeState state)
    {
        key = default;
        state = default;
        bool found = false;
        int bestScore = int.MinValue;

        foreach (KeyValuePair<VulkanFrameOpPlannerStateKey, ResourcePlannerRuntimeState> pair in switchingState.States)
        {
            if (!FrameOpPlannerStateKeysSharePhysicalOwner(pair.Key, requestedKey) ||
                !IsReusableFrameOpResourcePlannerState(pair.Value) ||
                !IsFrameOpPlannerAllocatorExclusivelyOwnedByKey(
                    switchingState,
                    pair.Key,
                    pair.Value.ResourceAllocator))
            {
                continue;
            }

            int score = ScoreCompatibleFrameOpPlannerState(pair.Key, pair.Value);
            if (found && score <= bestScore)
                continue;

            found = true;
            bestScore = score;
            key = pair.Key;
            state = pair.Value;
        }

        return found;
    }

    private static bool FrameOpPlannerStateKeysSharePhysicalOwner(
        in VulkanFrameOpPlannerStateKey first,
        in VulkanFrameOpPlannerStateKey second)
        => first.ContextKind == second.ContextKind &&
           first.PipelineIdentity == second.PipelineIdentity &&
           first.ViewportIdentity == second.ViewportIdentity &&
           first.DisplayWidth == second.DisplayWidth &&
           first.DisplayHeight == second.DisplayHeight &&
           first.InternalWidth == second.InternalWidth &&
           first.InternalHeight == second.InternalHeight &&
           first.OutputFrameBufferIdentity == second.OutputFrameBufferIdentity &&
           first.OutputTargetIdentity == second.OutputTargetIdentity &&
           first.SubmissionQueueFamily == second.SubmissionQueueFamily;

    private static void RekeyFrameOpResourcePlannerState(
        FrameOpResourcePlannerSwitchingState switchingState,
        in VulkanFrameOpPlannerStateKey previousKey,
        in VulkanFrameOpPlannerStateKey currentKey,
        in ResourcePlannerRuntimeState state)
    {
        if (!previousKey.Equals(currentKey))
        {
            switchingState.States.Remove(previousKey);
            switchingState.LastUsedSerials.Remove(previousKey);
            switchingState.ActiveKeys.Remove(previousKey);
        }

        switchingState.States[currentKey] = state;
    }

    private static int ScoreCompatibleFrameOpPlannerState(
        in VulkanFrameOpPlannerStateKey key,
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
        in VulkanFrameOpPlannerStateKey key)
        => state.ResourceAllocator is not null &&
            !state.ResourceAllocator.IsRetired &&
            state.LastActiveFrameOpContext is FrameOpContext context &&
            FrameOpContextMatchesPlannerStateKeyIgnoringRegistry(context, key);


}
