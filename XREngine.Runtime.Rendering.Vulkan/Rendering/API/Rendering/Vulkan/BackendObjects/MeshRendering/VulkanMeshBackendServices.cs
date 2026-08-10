using Silk.NET.Vulkan;
using System.Runtime.CompilerServices;
using System.Threading;
using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.Vulkan.RenderGraph;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Narrow, generation-owned services consumed by mesh wrappers.  This keeps
/// descriptor publication diagnostics on concrete frame/output authorities
/// without letting a wrapper retain the renderer facade.
/// </summary>
internal sealed unsafe partial class VulkanMeshBackendServices(
    VulkanBackendObjectContext context,
    VulkanCommandRuntime commandRuntime,
    VulkanFramePlanner framePlanner,
    VulkanOutputRuntime outputRuntime,
    VulkanFrameLoop frameLoop,
    VulkanFrameOperationQueue operationQueue,
    VulkanFrameTelemetry telemetry)
{
    private ulong _materialUniformFrameId = ulong.MaxValue;
    private float _materialUniformUpdateDelta;
    private float _materialUniformSeconds;
    private float _materialUniformRenderDelta;

    internal float MaterialUniformUpdateDelta
    {
        get
        {
            CaptureMaterialUniformFrameTime();
            return _materialUniformUpdateDelta;
        }
    }

    internal float MaterialUniformSeconds
    {
        get
        {
            CaptureMaterialUniformFrameTime();
            return _materialUniformSeconds;
        }
    }

    internal float MaterialUniformRenderDelta
    {
        get
        {
            CaptureMaterialUniformFrameTime();
            return _materialUniformRenderDelta;
        }
    }

    private void CaptureMaterialUniformFrameTime()
    {
        ulong frameId = RuntimeEngine.Rendering.State.RenderFrameId;
        if (_materialUniformFrameId == frameId)
            return;

        _materialUniformUpdateDelta = RuntimeEngine.Time.Timer.Update.Delta;
        _materialUniformSeconds = RuntimeEngine.ElapsedTime;
        _materialUniformRenderDelta = RuntimeEngine.Time.Timer.Render.Delta;
        _materialUniformFrameId = frameId;
    }

    internal FrameOpContext? ActiveFrameOpContext
        => framePlanner
            .GetPublishedResourcePlannerGeneration<ResourcePlannerRuntimeGeneration>()
            .State
            .LastActiveFrameOpContext;

    /// <summary>
    /// Captures every mutable target, viewport, and fixed-function input needed to publish a
    /// mesh draw. The returned snapshot is frozen; command recording never reads these producer
    /// authorities again.
    /// </summary>
    internal VulkanMeshProducerSnapshot CaptureProducerSnapshot(XRFrameBuffer? explicitTarget = null)
    {
        ResourcePlannerRuntimeGeneration plannerGeneration = framePlanner
            .GetPublishedResourcePlannerGeneration<ResourcePlannerRuntimeGeneration>();
        XRFrameBuffer? target;
        FrameOpContext frameContext;
        if (plannerGeneration.HasActiveFrameOpContext)
        {
            ref readonly FrameOpContext activeContext =
                ref plannerGeneration.ActiveFrameOpContext;
            target = explicitTarget ??
                commandRuntime.CommandBuffers.BoundDrawFrameBuffer ??
                activeContext.OutputFrameBuffer;
            frameContext = activeContext;
        }
        else
        {
            target = explicitTarget ?? commandRuntime.CommandBuffers.BoundDrawFrameBuffer;
            frameContext = CaptureBootstrapFrameOpContext(target);
        }
        Extent2D targetExtent = target is null
            ? commandRuntime.StateTracker.GetCurrentTargetExtent()
            : ResolveFrameBufferDrawExtent(target);

        var pipelineState = RuntimeEngine.Rendering.State.RenderingPipelineState;
        var renderRegion = pipelineState?.CurrentRenderRegion ?? default;
        Viewport viewport = renderRegion.Width > 0 && renderRegion.Height > 0
            ? VulkanStateTracker.GetViewport(renderRegion, targetExtent)
            : commandRuntime.StateTracker.GetViewport(targetExtent);
        var cropRegion = pipelineState?.CurrentCropRegion ?? default;
        Rect2D scissor = cropRegion.Width > 0 && cropRegion.Height > 0
            ? VulkanStateTracker.GetScissor(cropRegion, targetExtent)
            : VulkanStateTracker.GetDefaultScissor(targetExtent);
        IndexedViewportScissorSnapshot indexedViewportScissors =
            commandRuntime.StateTracker.GetIndexedViewportScissorSnapshot(targetExtent);

        var openXr = outputRuntime.OpenXrBackend;
        bool externalTarget = openXr.CurrentThreadExecutionState.ExternalSwapchainDepth > 0;
        bool prewarmingExternalTarget = externalTarget &&
            Volatile.Read(ref openXr.ExternalSwapchainPrewarmDepth) > 0;

        return new VulkanMeshProducerSnapshot(
            frameContext,
            target,
            targetExtent,
            viewport,
            scissor,
            indexedViewportScissors,
            commandRuntime.StateTracker.CaptureFixedFunctionState(),
            externalTarget,
            prewarmingExternalTarget);
    }

    /// <summary>
    /// Captures the first context for pipeline resources, such as the BRDF
    /// precompute quad, which are materialized before the normal pipeline scope
    /// has published its context. This is producer-side capture; recording still
    /// consumes only the frozen context carried by the operation.
    /// </summary>
    private FrameOpContext CaptureBootstrapFrameOpContext(XRFrameBuffer? target)
    {
        XRRenderPipelineInstance? activePipeline = RuntimeEngine.Rendering.State.CurrentRenderingPipeline;
        XRRenderPipelineInstance? passPipeline = RuntimeEngine.Rendering.State.CurrentRenderGraphPassPipeline;
        XRRenderPipelineInstance? pipeline =
            RuntimeEngine.Rendering.State.CurrentRenderGraphPassIndex != int.MinValue && passPipeline is not null
                ? passPipeline
                : activePipeline;
        XRViewport? viewport = pipeline?.RenderState.WindowViewport
            ?? pipeline?.LastWindowViewport
            ?? RuntimeEngine.Rendering.State.RenderingViewport;
        Extent2D extent = target is null
            ? commandRuntime.StateTracker.GetCurrentTargetExtent()
            : ResolveFrameBufferDrawExtent(target);
        uint displayWidth = ResolvePositiveDimension(
            pipeline?.ResourceDisplayWidth,
            viewport?.Width,
            extent.Width);
        uint displayHeight = ResolvePositiveDimension(
            pipeline?.ResourceDisplayHeight,
            viewport?.Height,
            extent.Height);
        uint internalWidth = ResolvePositiveDimension(
            pipeline?.ResourceInternalWidth,
            viewport?.InternalWidth,
            displayWidth);
        uint internalHeight = ResolvePositiveDimension(
            pipeline?.ResourceInternalHeight,
            viewport?.InternalHeight,
            displayHeight);
        int targetIdentity = target is null ? 0 : RuntimeHelpers.GetHashCode(target);
        bool stereoEnabled = pipeline?.RenderState.StereoPass
            ?? RuntimeEngine.Rendering.State.IsStereoPass;
        string pipelineTypeName = pipeline?.AssignedPipeline?.GetType().Name ?? string.Empty;
        bool multiviewEnabled = stereoEnabled &&
            (pipelineTypeName.Contains("MultiView", StringComparison.OrdinalIgnoreCase) ||
             pipelineTypeName.Contains("Multiview", StringComparison.OrdinalIgnoreCase));
        EVulkanFrameOpContextKind contextKind = ResolveBootstrapContextKind(pipeline);
        int pipelineIdentity = pipeline?.InstanceId ?? 0;
        int viewportIdentity = viewport is null ? 0 : RuntimeHelpers.GetHashCode(viewport);
        int outputFrameBufferIdentity = targetIdentity;
        FrameOpSignatureHasher logicalViewHash = new();
        logicalViewHash.Add(0x4C4F474943564945UL);
        logicalViewHash.Add((int)contextKind);
        logicalViewHash.Add(pipelineIdentity);
        logicalViewHash.Add(viewportIdentity);
        logicalViewHash.Add(outputFrameBufferIdentity);
        ulong logicalViewId = logicalViewHash.ToHash();
        if (logicalViewId == 0UL)
            logicalViewId = 1UL;

        FrameOpContext captured = new(
            pipelineIdentity,
            viewportIdentity,
            pipeline,
            pipeline?.Resources,
            pipeline?.ActiveMeshRenderCommands.RenderingBackendReadyPackage.PassMetadata
                ?? pipeline?.Pipeline?.PassMetadata,
            displayWidth,
            displayHeight,
            internalWidth,
            internalHeight,
            target?.Name,
            OutputTargetIdentity: targetIdentity,
            OutputTargetName: target?.Name,
            OutputFrameBufferIdentity: outputFrameBufferIdentity,
            ContextKind: contextKind,
            ContextId: framePlanner.NextFrameContextId(),
            LogicalViewId: logicalViewId,
            SubmissionQueueFamily: context.DeviceContext.QueueFamilies.GraphicsFamilyIndex ?? 0u,
            StereoEnabled: stereoEnabled,
            MultiviewEnabled: multiviewEnabled,
            ResourceGeneration: unchecked((ulong)Math.Max(pipeline?.ResourceGeneration ?? 0, 0)),
            OutputFrameBuffer: target);
        captured = captured with
        {
            RecordingFingerprint = ComputeBootstrapRecordingFingerprint(captured),
        };

        ResourcePlannerRuntimeState state = framePlanner
            .GetPublishedResourcePlannerGeneration<ResourcePlannerRuntimeGeneration>()
            .State;
        state.LastActiveFrameOpContext = captured;
        framePlanner.PublishResourcePlannerGeneration(new ResourcePlannerRuntimeGeneration(state));
        return captured;
    }

    private static uint ResolvePositiveDimension(uint? preferred, int? secondary, uint fallback)
        => preferred is > 0u
            ? preferred.Value
            : secondary is > 0
                ? checked((uint)secondary.Value)
                : Math.Max(fallback, 1u);

    private static EVulkanFrameOpContextKind ResolveBootstrapContextKind(XRRenderPipelineInstance? pipeline)
    {
        if (RuntimeEngine.Rendering.State.IsLightProbePass)
            return EVulkanFrameOpContextKind.LightProbeCapture;
        if (pipeline?.RenderState.ShadowPass == true || RuntimeEngine.Rendering.State.IsShadowPass)
            return EVulkanFrameOpContextKind.Shadow;
        if (RuntimeEngine.Rendering.State.IsSceneCapturePass)
            return EVulkanFrameOpContextKind.SceneCapture;

        string pipelineTypeName = pipeline?.AssignedPipeline?.GetType().Name ?? string.Empty;
        if (pipelineTypeName.Contains("Diagnostic", StringComparison.OrdinalIgnoreCase))
            return EVulkanFrameOpContextKind.DiagnosticCapture;
        if (pipelineTypeName.Contains("UserInterface", StringComparison.OrdinalIgnoreCase) ||
            pipelineTypeName.Contains("UiPreview", StringComparison.OrdinalIgnoreCase))
            return EVulkanFrameOpContextKind.UiPreview;
        return EVulkanFrameOpContextKind.MainViewport;
    }

    private static ulong ComputeBootstrapRecordingFingerprint(in FrameOpContext context)
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
        hash.Add(context.ResourceGeneration);
        return hash.ToHash();
    }

    /// <summary>Publishes a fully captured mesh draw into the generation-owned operation queue.</summary>
    internal void EnqueueMeshDraw(
        int passIndex,
        in PendingMeshDraw draw,
        in VulkanMeshProducerSnapshot producer)
    {
        ref readonly FrameOpContext context =
            ref VulkanMeshProducerSnapshot.GetContextReference(in producer);
        int validatedPassIndex = ValidateMeshPassIndex(
            passIndex,
            context.PassMetadata);
        MeshDrawOp operation = MeshDrawOp.Rent(
            validatedPassIndex,
            producer.Target,
            draw,
            in context,
            operationQueue.CurrentThread.RenderQueryBracketDepth > 0);
        LowerMeshResourceUses(operation);
        PublishMeshDrawStats(draw, validatedPassIndex);

        FrameOpCapture? capture = operationQueue.CurrentThread.Capture;
        if (capture is not null)
        {
            capture.Add(operation);
            return;
        }

        using (operationQueue.SyncRoot.EnterScope())
            operationQueue.Pending.Add(operation);
    }

    /// <summary>
    /// Blocks synchronous uploads while an external swapchain draw snapshot is prepared.
    /// The returned scope is a no-op for ordinary desktop draws.
    /// </summary>
    internal IDisposable EnterExternalSnapshotUploadBlock(
        in VulkanMeshProducerSnapshot producer)
        => producer.IsExternalSwapchainTarget && !producer.IsPrewarmingExternalSwapchainTarget
            ? commandRuntime.OpenXrRecording.EnterSynchronousUploadBlockScope(outputRuntime.OpenXrBackend)
            : EmptyDisposable.Instance;

    private static Extent2D ResolveFrameBufferDrawExtent(XRFrameBuffer frameBuffer)
    {
        var targets = frameBuffer.Targets;
        if (targets is null || targets.Length == 0)
            return new Extent2D(Math.Max(frameBuffer.Width, 1u), Math.Max(frameBuffer.Height, 1u));

        uint minWidth = uint.MaxValue;
        uint minHeight = uint.MaxValue;
        bool found = false;
        for (int index = 0; index < targets.Length; index++)
        {
            var (target, _, mip, _) = targets[index];
            if (target is null)
                continue;

            uint width = Math.Max(target.Width, 1u);
            uint height = Math.Max(target.Height, 1u);
            int mipLevel = Math.Max(mip, 0);
            if (mipLevel > 0)
            {
                width = Math.Max(width >> mipLevel, 1u);
                height = Math.Max(height >> mipLevel, 1u);
            }

            minWidth = Math.Min(minWidth, width);
            minHeight = Math.Min(minHeight, height);
            found = true;
        }

        return found
            ? new Extent2D(minWidth, minHeight)
            : new Extent2D(Math.Max(frameBuffer.Width, 1u), Math.Max(frameBuffer.Height, 1u));
    }

    private static int ValidateMeshPassIndex(
        int passIndex,
        IReadOnlyCollection<RenderPassMetadata>? passMetadata)
    {
        if (passIndex != VulkanBarrierPlanner.SwapchainPassIndex &&
            passIndex != int.MinValue &&
            (Enum.IsDefined<EDefaultRenderPass>((EDefaultRenderPass)passIndex) ||
             passMetadata is null ||
             passMetadata.Count == 0 ||
             ContainsPass(passMetadata, passIndex)))
        {
            return passIndex;
        }

        int currentPassIndex = RuntimeEngine.Rendering.State.CurrentRenderGraphPassIndex;
        if (currentPassIndex != int.MinValue &&
            (passMetadata is null || passMetadata.Count == 0 || ContainsPass(passMetadata, currentPassIndex)))
        {
            return currentPassIndex;
        }

        const int preRenderPass = (int)EDefaultRenderPass.PreRender;
        if (passMetadata is not null && ContainsPass(passMetadata, preRenderPass))
            return preRenderPass;

        if (passMetadata is not null)
            foreach (RenderPassMetadata metadata in passMetadata)
                return metadata.PassIndex;

        return int.MinValue;
    }

    private static bool ContainsPass(
        IReadOnlyCollection<RenderPassMetadata> passMetadata,
        int passIndex)
    {
        foreach (RenderPassMetadata metadata in passMetadata)
            if (metadata.PassIndex == passIndex)
                return true;
        return false;
    }

    private static void LowerMeshResourceUses(MeshDrawOp operation)
    {
        ref FrameOpResourceUseList uses = ref operation.BeginResourceUseUpdate();
        ref readonly FrameOpContext context = ref operation.ContextReference;
        XRFrameBuffer? output = operation.Target ?? context.OutputFrameBuffer;
        if (output?.Targets is { Length: > 0 } targets)
        {
            for (int index = 0; index < targets.Length; index++)
                uses.Add(
                    ComputeResourceIdentity(targets[index].Target),
                    context.ResourceGeneration,
                    EFrameOpResourceAccess.Write);
        }

        ComputeDispatchSnapshot? bindings = operation.Draw.ProgramBindingSnapshot;
        if (bindings is not null)
        {
            foreach (XRTexture texture in bindings.Samplers.Values)
                AddDescriptorRead(ref uses, texture, context.ResourceGeneration);
            foreach (XRTexture texture in bindings.SamplersByName.Values)
                AddDescriptorRead(ref uses, texture, context.ResourceGeneration);
            foreach (ProgramImageBinding binding in bindings.Images.Values)
            {
                EFrameOpResourceAccess access = binding.Access switch
                {
                    XRRenderProgram.EImageAccess.ReadOnly => EFrameOpResourceAccess.Read,
                    XRRenderProgram.EImageAccess.WriteOnly => EFrameOpResourceAccess.Write,
                    _ => EFrameOpResourceAccess.Read | EFrameOpResourceAccess.Write,
                };
                uses.Add(
                    ComputeResourceIdentity(binding.Texture),
                    context.ResourceGeneration,
                    access | EFrameOpResourceAccess.Imported);
            }
            foreach (VulkanComputeBufferBinding binding in bindings.Buffers.Values)
                AddBufferRead(ref uses, binding, context.ResourceGeneration);
            foreach (VulkanComputeBufferBinding binding in bindings.BuffersByName.Values)
                AddBufferRead(ref uses, binding, context.ResourceGeneration);
        }
    }

    private static void AddDescriptorRead(
        ref FrameOpResourceUseList uses,
        XRTexture texture,
        ulong generation)
        => uses.Add(
            ComputeResourceIdentity(texture),
            generation,
            EFrameOpResourceAccess.Read | EFrameOpResourceAccess.Imported);

    private static void AddBufferRead(
        ref FrameOpResourceUseList uses,
        in VulkanComputeBufferBinding binding,
        ulong generation)
    {
        EFrameOpResourceAccess access =
            (binding.UsageFlags & BufferUsageFlags.StorageBufferBit) != 0
                ? EFrameOpResourceAccess.Read | EFrameOpResourceAccess.Write | EFrameOpResourceAccess.Imported
                : EFrameOpResourceAccess.Read | EFrameOpResourceAccess.Imported;
        uses.Add(ComputeResourceIdentity(binding.Data), generation, access);
    }

    private static ulong ComputeResourceIdentity(object resource)
    {
        FrameOpSignatureHasher hash = new();
        hash.Add(0x46524D4F50524553UL);
        hash.Add(RuntimeHelpers.GetHashCode(resource));
        hash.Add(resource.GetType().GetHashCode());
        ulong result = hash.ToHash();
        return result == 0UL ? 1UL : result;
    }

    private static void PublishMeshDrawStats(in PendingMeshDraw draw, int passIndex)
    {
        if (passIndex == int.MinValue)
            return;

        VulkanFrameDrawStats stats = draw.Renderer.EstimateFrameDrawStats(draw);
        if (stats.DrawCalls > 0)
            RuntimeEngine.Rendering.Stats.Frame.IncrementDrawCalls(stats.DrawCalls);
        if (stats.MultiDrawCalls > 0)
            RuntimeEngine.Rendering.Stats.Frame.IncrementMultiDrawCalls(stats.MultiDrawCalls);
        if (stats.TrianglesRendered > 0)
            RuntimeEngine.Rendering.Stats.Frame.AddTrianglesRendered(stats.TrianglesRendered);
    }

    private sealed class EmptyDisposable : IDisposable
    {
        internal static EmptyDisposable Instance { get; } = new();
        public void Dispose() { }
    }

    internal XRFrameBuffer? ResolveCurrentDrawTarget()
    {
        if (XRFrameBuffer.BoundForWriting is { } directlyBoundTarget)
            return directlyBoundTarget;

        XRRenderPipelineInstance.RenderingState.ScopedRenderTargetBinding? binding =
            RuntimeEngine.Rendering.State.CurrentRenderingPipeline?
                .RenderState
                .CurrentRenderTargetBinding;
        return binding is { Write: true, FrameBuffer: { } target }
            ? target
            : ActiveFrameOpContext?.OutputFrameBuffer;
    }

    internal int ResolveDescriptorViewFamilyIdentity()
        => framePlanner
            .GetPublishedResourcePlannerGeneration<ResourcePlannerRuntimeGeneration>()
            .DescriptorViewFamilyIdentity;

    internal ImageLayout ResolveDescriptorImageLayout(
        IVkImageDescriptorSource source,
        in VkImageDescriptorSnapshot snapshot,
        DescriptorType descriptorType)
    {
        if (descriptorType == DescriptorType.StorageImage)
            return ImageLayout.General;
        if ((snapshot.Usage & ImageUsageFlags.StorageBit) != 0 &&
            (snapshot.Usage & ImageUsageFlags.SampledBit) != 0)
        {
            return ImageLayout.General;
        }

        if (snapshot.TrackedLayout is ImageLayout.ShaderReadOnlyOptimal or
            ImageLayout.DepthStencilReadOnlyOptimal or
            ImageLayout.DepthReadOnlyOptimal or
            ImageLayout.StencilReadOnlyOptimal or
            ImageLayout.ReadOnlyOptimal)
        {
            return snapshot.TrackedLayout;
        }

        bool depthOrStencil =
            (snapshot.Aspect & (ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit)) != 0 ||
            snapshot.Format is Format.D24UnormS8Uint or Format.D32SfloatS8Uint or Format.D16UnormS8Uint;
        return depthOrStencil
            ? ImageLayout.DepthStencilReadOnlyOptimal
            : ImageLayout.ShaderReadOnlyOptimal;
    }

    internal void RecordDescriptorOwnershipDiagnostic(
        string programName,
        string materialName,
        ulong layoutFingerprint,
        int descriptorFrameSlotCount,
        int allocatedSetCount,
        bool sharedMaterialTier)
    {
        const int diagnosticLimit = 64;
        int diagnosticIndex = context.Descriptors.RecordMeshOwnershipDiagnostic();
        if (diagnosticIndex > diagnosticLimit)
            return;

        FrameOpContext? active = ActiveFrameOpContext;
        Debug.Vulkan(
            "[Vulkan.MeshOwnership] index={0}/{1} program='{2}' layout=0x{3:X16} material='{4}' output={5} outputTarget='{6}' pipeline={7} viewport={8} frameSlots={9} sets={10} sharedMaterial={11} planGeneration={12} descriptorGeneration={13}",
            diagnosticIndex,
            diagnosticLimit,
            programName,
            layoutFingerprint,
            materialName,
            active?.ContextKind ?? EVulkanFrameOpContextKind.Unknown,
            active?.OutputTargetName ?? active?.OutputFrameBufferName ?? "<unattributed>",
            active?.PipelineIdentity ?? 0,
            active?.ViewportIdentity ?? 0,
            descriptorFrameSlotCount,
            allocatedSetCount,
            sharedMaterialTier,
            active?.ResourceGeneration ?? 0,
            active?.DescriptorGeneration ?? 0);
    }

    internal void ObserveFinalPresentationDescriptor(
        int descriptorSlot,
        CommandBuffer commandBuffer,
        DescriptorSet descriptorSet,
        uint set,
        uint binding,
        string? bindingName,
        in DescriptorImageInfo imageInfo,
        ulong resourceSignature,
        bool writeMatched,
        bool writeSucceeded)
    {
        if (!string.Equals(bindingName, "SourceTexture", StringComparison.Ordinal))
            return;

        if (writeSucceeded)
        {
            VulkanPresentationSourcePublication publication =
                outputRuntime.PresentationSource.Publication;
            VulkanPresentationSourceTuple current = publication.CaptureLogical();
            _ = publication.TryBindDescriptor(
                current.LogicalEpoch,
                imageInfo,
                descriptorSet,
                context.GetResourceGeneration(ObjectType.DescriptorSet, descriptorSet.Handle),
                descriptorSlot,
                resourceSignature,
                commandBuffer,
                commandRuntime.ResolveCommandBufferRecordingGeneration(commandBuffer),
                out _);
        }

        if (!telemetry._finalPresentationLedger.Enabled)
            return;

        telemetry._finalPresentationLedger.ObserveDescriptor(
            frameLoop.AcceptedAttemptCount,
            descriptorSlot,
            unchecked((ulong)commandBuffer.Handle),
            descriptorSet.Handle,
            set,
            binding,
            bindingName,
            imageInfo,
            resourceSignature,
            writeMatched,
            writeSucceeded);
    }
}
