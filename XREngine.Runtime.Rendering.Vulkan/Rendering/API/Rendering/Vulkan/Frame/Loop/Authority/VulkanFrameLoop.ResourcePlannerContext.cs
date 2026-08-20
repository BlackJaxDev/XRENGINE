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

internal sealed partial class VulkanFrameLoop
{
    private const int MaxFrameOpResourcePlannerSwitchingStates = 12;
    private static bool FrameOpResourcePlannerSwitchingEnabled => MaxFrameOpResourcePlannerSwitchingStates > 1;

    private void OnSwapchainExtentChanged(Extent2D extent)
    {
        _commandRuntime.ActiveState.SetSwapchainExtent(extent);
        if (_commandRuntime.ActiveBoundDrawFrameBuffer is null)
            _commandRuntime.ActiveState.SetCurrentTargetExtent(extent);
        _commandRuntime.MarkCommandBuffersDirty();
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
            var dimensions = VulkanFramePlanner.ResolveExternalFrameOpResourceDimensions(
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
            displayWidth = VulkanFramePlanner.ResolvePositiveDimension(
                pipeline?.ResourceDisplayWidth,
                viewport?.Width,
                fallbackExtent.Width,
                1u);
            displayHeight = VulkanFramePlanner.ResolvePositiveDimension(
                pipeline?.ResourceDisplayHeight,
                viewport?.Height,
                fallbackExtent.Height,
                1u);
            internalWidth = VulkanFramePlanner.ResolvePositiveDimension(
                pipeline?.ResourceInternalWidth,
                viewport?.InternalWidth,
                displayWidth,
                1u);
            internalHeight = VulkanFramePlanner.ResolvePositiveDimension(
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
            OutputSchedulingInstanceIdentity = viewport?.FrameOutputIdentity ?? 0UL,
            OutputSchedulingRequest = viewport?.CurrentFrameOutputRequest ?? default,
            OperationWorkspace = _commandRuntime.GetFrameOpWorkspace(),
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
        if (_resourcePlannerSessions.TryGetScopedFrameOpContext(out FrameOpContext active))
            return active;

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
        if (_targetDriver is not VulkanDesktopWsiTargetDriver { IsInteractiveResizeInProgress: true })
        {
            ResetInteractiveResizePlannerFreeze();
            return context;
        }

        // The window viewport and ImGui snapshot intentionally follow the live client
        // extent, but the scene source stays on its active resource generation until the
        // drag settles. Feeding live dimensions into the planner here reallocates that
        // source behind a still-valid command package and produces out-of-order recovery
        // presents.
        if (TryResolveExternalSwapchainTargetExtent(out _) ||
            context.PipelineInstance?.ActiveGeneration is not { } activeGeneration)
        {
            return context;
        }

        ResourceGenerationKey key = activeGeneration.Key;
        if (context.DisplayWidth == key.DisplayWidth &&
            context.DisplayHeight == key.DisplayHeight &&
            context.InternalWidth == key.InternalWidth &&
            context.InternalHeight == key.InternalHeight)
        {
            return context;
        }

        return VulkanFramePlanner.RefreshFrameOpContextRecordingFingerprint(context with
        {
            DisplayWidth = key.DisplayWidth,
            DisplayHeight = key.DisplayHeight,
            InternalWidth = key.InternalWidth,
            InternalHeight = key.InternalHeight,
        });
    }

    private void CaptureInteractiveResizePlannerExtents(
        in FrameOpContext context,
        out VulkanInteractiveResizePlannerExtentSnapshot snapshot,
        out bool captured,
        out bool reportCapacityExceeded)
    {
        VulkanInteractiveResizePlannerContextKey key = VulkanFramePlanner.BuildInteractiveResizePlannerContextKey(context);
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



    internal IDisposable EnterPipelineResourcePlannerReadbackScope(
        XRRenderPipelineInstance pipeline,
        XRViewport? viewport)
    {
        if (pipeline is null)
            throw new ArgumentNullException(nameof(pipeline));

        FrameOpContext context = CreateFrameOpContext(pipeline, viewport);
        return CreateExternalResourcePlannerReadbackScope(context);
    }

    internal IDisposable? EnterRenderPipelineFrameResourceScope(
        XRRenderPipelineInstance pipeline,
        XRViewport? viewport)
    {
        if (pipeline is null)
            return null;

        FrameOpContext context = CreateFrameOpContext(pipeline, viewport);
        return !VulkanFramePlanner.FrameOpContextHasPlannerResources(context)
            ? null
            : RentPipelineResourcePlannerScope(context);
    }

    internal bool TryPrepareRenderResourceGeneration(
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
            DescriptorGeneration = VulkanFramePlanner.ResolveFrameOpContextDescriptorGeneration(generation.Registry),
            ResourceRegistrySignatureSnapshot = VulkanFramePlanner.ComputeResourceRegistrySignature(generation.Registry),
        };
        context = VulkanFramePlanner.RefreshFrameOpContextRecordingFingerprint(context);

        using (VulkanResourcePlannerSessionService.RuntimeStateScope scope =
               _resourcePlannerSessions.EnterRuntimeStateScope(in pendingState))
        {
            try
            {
                UpdateResourcePlannerFromContext(context, deferReusedImageMetadataCommit: true);
                pendingState = scope.CaptureCurrent(CaptureResourcePlannerRuntimeState(), ActiveFrameOpResourcePlannerSwitchingState);
                pendingState.LastActiveFrameOpContext = context;

                if (!ValidatePreparedResourceAllocator(pendingState.ResourcePlanner, pendingState.ResourceAllocator, out failureReason))
                {
                    _ = pendingState.ResourceAllocator.TryRetirePhysicalResources(
                        BackendObjectContext,
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
                        BackendObjectContext,
                        exceptImageGroups: pendingState.ResourceAllocator.CapturePendingReusedImageGroups(),
                        immediate: true);
                    return false;
                }

                pendingState = scope.CaptureCurrent(CaptureResourcePlannerRuntimeState(), ActiveFrameOpResourcePlannerSwitchingState);
                pendingState.LastActiveFrameOpContext = context;
                pendingState.PreparedGenerationManifest = preparedManifest;

                if (!TryPreserveTrackedAutoExposureHistory(pendingState.ResourceAllocator))
                {
                    VulkanResourceAllocator? historyAllocator =
                        ResolveAutoExposureHistoryAllocator(
                            previousState.ResourceAllocator,
                            pendingState.ResourceAllocator);
                    if (historyAllocator is not null)
                    {
                        _ = PreserveAutoExposureHistory(
                            historyAllocator,
                            pendingState.ResourceAllocator);
                    }
                }

                transaction = _resourceGenerationTransactions.Create(
                    BackendObjectContext,
                    previousState,
                    pendingState,
                    VulkanFramePlanner.BuildFrameOpPlannerStateKey(context),
                    preparedManifest!);
                return true;
            }
            catch (Exception ex)
            {
                RestorePreparedGenerationFramebufferWrappers(preparedManifest, previousState);
                pendingState = scope.CaptureCurrent(CaptureResourcePlannerRuntimeState(), ActiveFrameOpResourcePlannerSwitchingState);
                if (!pendingState.ResourceAllocator.IsRetired)
                    _ = pendingState.ResourceAllocator.TryRetirePhysicalResources(
                        BackendObjectContext,
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

        // Materialize every planner-backed texture before framebuffer generation.
        // Framebuffers can create mip/layer attachment views (BloomBlurTexture is
        // the common case), which legitimately advances the texture descriptor
        // epoch. The immutable manifest must therefore be captured only after all
        // framebuffer attachment views have been created.
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
                RestorePreparedGenerationFramebufferWrappers(frameBuffers, wrapper, previousState);
                manifest = null;
                failureReason = $"Vulkan framebuffer/dynamic-attachment snapshot for '{name}' was incomplete for the pending generation.";
                return false;
            }

            frameBuffers.Add(new(name, record.Instance, wrapper, snapshot));
        }

        // Capture descriptor identity only after resource materialization is
        // complete. Any change observed by Commit from this point forward is a
        // real lifetime/identity violation rather than preparation creating the
        // attachment views it was asked to prepare.
        foreach ((string name, RenderTextureResource record) in generation.Registry.TextureRecords)
        {
            if (record.Instance is null ||
                !pendingState.ResourceAllocator.TryGetPhysicalGroupForResource(name, out VulkanPhysicalImageGroup? physicalGroup) ||
                physicalGroup?.IsAllocated != true)
            {
                continue;
            }

            if (!TryGetAPIRenderObject(record.Instance, out var apiObject) ||
                apiObject is not IVkImageDescriptorSource source ||
                !source.TryGetDescriptorSnapshot(
                    requestedViewType: null,
                    requestedAspectMask: null,
                    "pending Vulkan resource generation manifest",
                    allowSynchronousUpload: false,
                    out VkImageDescriptorSnapshot snapshot) ||
                !snapshot.IsReady ||
                !snapshot.UsesAllocatorImage ||
                snapshot.Image.Handle != physicalGroup.Image.Handle)
            {
                RestorePreparedGenerationFramebufferWrappers(
                    frameBuffers,
                    currentWrapper: null,
                    previousState: previousState);
                manifest = null;
                failureReason = $"Vulkan image-view/descriptor payload for '{name}' did not remain ready through pending-generation materialization.";
                return false;
            }

            images.Add(new(
                name,
                record.Instance,
                source,
                snapshot,
                _resourceRuntime.GetPublishedGeneration(ObjectType.Image, snapshot.Image.Handle),
                _resourceRuntime.GetPublishedGeneration(ObjectType.ImageView, snapshot.View.Handle),
                _resourceRuntime.GetPublishedGeneration(ObjectType.Sampler, snapshot.Sampler.Handle)));
        }

        foreach (VulkanPhysicalBufferGroup group in pendingState.ResourceAllocator.EnumeratePhysicalBufferGroups())
        {
            if (!group.IsAllocated || group.Buffer.Handle == 0)
                continue;

            buffers.Add(new(
                group.Buffer,
                _resourceRuntime.GetPublishedGeneration(ObjectType.Buffer, group.Buffer.Handle),
                group.SizeInBytes));
        }

        manifest = new VulkanPreparedResourceGenerationManifest(
            generation.Registry,
            generation.Registry.DescriptorSignature,
            images.ToArray(),
            frameBuffers.ToArray(),
            buffers.ToArray());
        failureReason = null;
        return true;
    }

    private bool TryValidatePreparedResourceGenerationManifest(
        VulkanPreparedResourceGenerationManifest manifest,
        out string? failureReason)
    {
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
                _resourceRuntime.GetPublishedGeneration(ObjectType.Image, current.Image.Handle) != entry.ImageGeneration ||
                _resourceRuntime.GetPublishedGeneration(ObjectType.ImageView, current.View.Handle) != entry.ViewGeneration ||
                _resourceRuntime.GetPublishedGeneration(ObjectType.Sampler, current.Sampler.Handle) != entry.SamplerGeneration)
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
            if (_resourceRuntime.GetPublishedGeneration(ObjectType.Buffer, entry.Buffer.Handle) != entry.Generation)
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

        using VulkanResourcePlannerSessionService.RuntimeStateScope scope =
            _resourcePlannerSessions.EnterRuntimeStateScope(in previousState);
        for (int i = 0; i < manifest.FrameBufferCount; i++)
            manifest.GetFrameBuffer(i).Wrapper.EnsureCurrent();
    }

    private void RestorePreparedGenerationFramebufferWrappers(
        List<VulkanPreparedResourceGenerationManifest.FrameBufferEntry> frameBuffers,
        VkFrameBuffer? currentWrapper,
        in ResourcePlannerRuntimeState previousState)
    {
        if (frameBuffers.Count == 0 && currentWrapper is null)
            return;

        using VulkanResourcePlannerSessionService.RuntimeStateScope scope =
            _resourcePlannerSessions.EnterRuntimeStateScope(in previousState);
        for (int i = 0; i < frameBuffers.Count; i++)
            frameBuffers[i].Wrapper.EnsureCurrent();
        currentWrapper?.EnsureCurrent();
    }




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
            var dimensions = VulkanFramePlanner.ResolveExternalFrameOpResourceDimensions(
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
            displayWidth = VulkanFramePlanner.ResolvePositiveDimension(
                pipeline.ResourceDisplayWidth, viewport?.Width, fallbackExtent.Width, 1u);
            displayHeight = VulkanFramePlanner.ResolvePositiveDimension(
                pipeline.ResourceDisplayHeight, viewport?.Height, fallbackExtent.Height, 1u);
            internalWidth = VulkanFramePlanner.ResolvePositiveDimension(
                pipeline.ResourceInternalWidth, viewport?.InternalWidth, displayWidth, 1u);
            internalHeight = VulkanFramePlanner.ResolvePositiveDimension(
                pipeline.ResourceInternalHeight, viewport?.InternalHeight, displayHeight, 1u);
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
            pipeline.ActiveMeshRenderCommands.RenderingBackendReadyPackage.PassMetadata ?? pipeline.Pipeline?.PassMetadata,
            displayWidth, displayHeight, internalWidth, internalHeight, outputFrameBuffer?.Name,
            ShouldPreserveSubmissionOrderBlock(), outputTargetIdentity, outputTargetName);
        return ApplyInteractiveResizePlannerFreeze(CompleteFrameOpContext(context with
        {
            OutputFrameBuffer = outputFrameBuffer,
            OutputSchedulingInstanceIdentity = viewport?.FrameOutputIdentity ?? 0UL,
            OutputSchedulingRequest = viewport?.CurrentFrameOutputRequest ?? default,
        }));
    }

    private FrameOpContext CompleteFrameOpContext(in FrameOpContext context)
    {
        bool stereoEnabled = VulkanFramePlanner.ResolveFrameOpContextStereoEnabled(context);
        EVulkanFrameOpContextKind contextKind = ResolveFrameOpContextKind(context);
        FrameOpContext complete = context with
        {
            OutputFrameBufferIdentity = VulkanFramePlanner.ComputeOutputFrameBufferIdentity(context.OutputFrameBufferName),
            ContextKind = contextKind,
            ContextId = _framePlanner.NextFrameContextId(),
            LogicalViewId = ResolveFrameOpLogicalViewId(context, contextKind),
            SubmissionQueueFamily = ResolveFrameOpSubmissionQueueFamily(context.PassMetadata),
            StereoEnabled = stereoEnabled,
            MultiviewEnabled = VulkanFramePlanner.ResolveFrameOpContextMultiviewEnabled(context, stereoEnabled),
            ResourceGeneration = ResolveFrameOpContextResourceGeneration(context.PipelineInstance),
            DescriptorGeneration = VulkanFramePlanner.ResolveFrameOpContextDescriptorGeneration(context.ResourceRegistry),
            ResourceRegistrySignatureSnapshot = VulkanFramePlanner.ComputeResourceRegistrySignature(context.ResourceRegistry),
        };

        return VulkanFramePlanner.RefreshFrameOpContextRecordingFingerprint(complete);
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

    private EVulkanFrameOpContextKind ResolveFrameOpContextKind(in FrameOpContext context)
    {
        if (IsRenderingExternalSwapchainTarget)
        {
            EVulkanFrameOpContextKind contextKind =
                OutputRuntime.OpenXrBackend.CurrentThreadExecutionState.FrameContext.ContextKind;
            return contextKind == EVulkanFrameOpContextKind.Unknown
                ? EVulkanFrameOpContextKind.OpenXrEye
                : contextKind;
        }

        if (RuntimeEngine.Rendering.State.IsLightProbePass)
            return EVulkanFrameOpContextKind.LightProbeCapture;
        if (VulkanFramePlanner.ResolveFrameOpContextShadowPass(context))
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
        => _framePlanner.BuildQueueOwnershipConfig(
            _deviceContext,
            passMetadata,
            VulkanFeatureProfile.ActiveProfile).GraphicsQueueFamilyIndex;

    private static ulong ResolveFrameOpContextResourceGeneration(XRRenderPipelineInstance? pipeline)
        => unchecked((ulong)Math.Max(pipeline?.ResourceGeneration ?? 0, 0));

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

    private ExternalResourcePlannerReadbackScope CreateExternalResourcePlannerReadbackScope(
        in FrameOpContext context)
    {
        ExternalResourcePlannerReadbackScope scope =
            _resourcePlannerSessions.CreateReadbackScope(
                _deviceContext,
                _outputRuntime,
                context);
        try
        {
            if (scope.RequiresPreparation)
            {
                UpdateResourcePlannerFromContext(context);
                scope.CompletePreparation();
            }

            return scope;
        }
        catch
        {
            scope.Dispose();
            throw;
        }
    }

    private PooledExternalResourcePlannerReadbackScope RentExternalResourcePlannerReadbackScope(
        in FrameOpContext context)
        => _resourcePlannerSessions.RentReadbackScope(
            CreateExternalResourcePlannerReadbackScope(context));

    private PooledExternalResourcePlannerReadbackScope RentPipelineResourcePlannerScope(
        in FrameOpContext context)
    {
        ExternalResourcePlannerReadbackScope readbackScope =
            CreateExternalResourcePlannerReadbackScope(context);
        try
        {
            ResourcePlannerRuntimeState scopedState =
                _resourcePlannerSessions.CaptureRuntimeState();
            scopedState.LastActiveFrameOpContext = context;
            return _resourcePlannerSessions.RentReadbackScope(
                readbackScope,
                _resourcePlannerSessions.EnterRuntimeStateScope(in scopedState));
        }
        catch
        {
            readbackScope.Dispose();
            throw;
        }
    }
}
