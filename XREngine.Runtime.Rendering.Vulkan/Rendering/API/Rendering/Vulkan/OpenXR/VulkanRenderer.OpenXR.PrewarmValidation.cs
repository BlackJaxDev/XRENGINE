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

        int openXrFrameDataSlotCount = ResolveOpenXrFrameDataSlotCount(OutputRuntime.Desktop.Images?.Length ?? 0);

        uint prewarmViewIndex = ResolveOpenXrExternalSwapchainViewIndex(resourcePlannerStateIndex);
        using IDisposable externalScope = EnterOpenXrExternalSwapchainRenderScope(
            extent.Width,
            extent.Height,
            BuildOpenXrExternalSwapchainPlannerTargetIdentity(prewarmViewIndex),
            ResolveOpenXrExternalSwapchainTargetName(prewarmViewIndex));
        using VulkanOpenXrThreadRenderStateScope renderStateScope =
            _commandRuntime.OpenXrRecording.EnterThreadRenderStateScope(
                CreateOpenXrThreadRenderStateData(),
                CreateOpenXrPrewarmRenderStateTracker(extent));
        OutputRuntime.OpenXrBackend.ExternalSwapchainPrewarmDepth++;

        try
        {
            EnsureOpenXrFrameDataSlotCapacity(openXrFrameDataSlotCount);
            _commandRuntime.EnsureOpenXrDescriptorFrameSlotFloor(
                openXrFrameDataSlotCount);
            DrainRetiredResourcesFromCompletedSubmittedFrameSlots();

            using (EnterOpenXrResourcePlannerThreadScope(
                resourcePlannerStateIndex,
                EVulkanOpenXrResourcePlannerPurpose.EyePrewarm))
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

                using (RuntimeRenderingHostServices.Profiling.StartProfileScope("OpenXR.Vulkan.PrewarmEye.Sort"))
                    ops = _frameOperationScheduler.SortFrameOpsCore(ops, CompiledRenderGraph);
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
                    ResolveOpenXrRecordImageIndex(resourcePlannerStateIndex, OutputRuntime.Desktop.Images?.Length ?? 0));
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
            OutputRuntime.OpenXrBackend.ExternalSwapchainPrewarmDepth--;
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
        OutputRuntime.OpenXrBackend.ExternalSwapchainPrewarmDepth++;
        int openXrFrameDataSlotCount = ResolveOpenXrFrameDataSlotCount(OutputRuntime.Desktop.Images?.Length ?? 0);

        try
        {
            EnsureOpenXrFrameDataSlotCapacity(openXrFrameDataSlotCount);
            _commandRuntime.EnsureOpenXrDescriptorFrameSlotFloor(
                openXrFrameDataSlotCount);
            DrainRetiredResourcesFromCompletedSubmittedFrameSlots();

            using (EnterOpenXrResourcePlannerThreadScope(
                resourcePlannerStateIndex,
                EVulkanOpenXrResourcePlannerPurpose.MirrorPrewarm))
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

                using (RuntimeRenderingHostServices.Profiling.StartProfileScope("OpenXR.Vulkan.PrewarmEyeMirror.Sort"))
                    ops = _frameOperationScheduler.SortFrameOpsCore(ops, CompiledRenderGraph);
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
                    ResolveOpenXrRecordImageIndex(resourcePlannerStateIndex, OutputRuntime.Desktop.Images?.Length ?? 0));
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
            OutputRuntime.OpenXrBackend.ExternalSwapchainPrewarmDepth--;
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
            capturedOp.Context = context;
            FrameOp op = RebaseFrameOpTargetsToActiveResourcePlan(capturedOp, activeRegistry);
            ops[opIndex] = op;

            ComputeDispatchSnapshot? snapshot = op switch
            {
                MeshDrawOp meshDraw => meshDraw.Draw.ProgramBindingSnapshot,
                ComputeDispatchOp compute => compute.Snapshot,
                ComputeDispatchIndirectOp computeIndirect => computeIndirect.Snapshot,
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
        switch (op)
        {
            case BlitOp blit:
                return RebaseBlitTargets(blit, activeRegistry);
            case PublishFramebufferForSamplingOp publish:
                return RebasePublishedFramebuffer(publish, activeRegistry);
            case ClearOp:
            case MeshDrawOp:
            case QueryOp:
            case IndirectDrawOp:
            case TransformFeedbackOp:
                op.Target = target;
                break;
        }

        return op;
    }

    private static BlitOp RebaseBlitTargets(BlitOp blit, RenderResourceRegistry activeRegistry)
    {
        XRFrameBuffer? inFbo = ResolveActiveFrameBuffer(blit.InFbo, activeRegistry);
        XRFrameBuffer? outFbo = ResolveActiveFrameBuffer(blit.OutFbo, activeRegistry);
        blit.InFbo = inFbo;
        blit.OutFbo = outFbo;
        blit.Target = outFbo;
        return blit;
    }

    private static PublishFramebufferForSamplingOp RebasePublishedFramebuffer(
        PublishFramebufferForSamplingOp publish,
        RenderResourceRegistry activeRegistry)
    {
        XRFrameBuffer frameBuffer = ResolveActiveFrameBuffer(publish.FrameBuffer, activeRegistry) ?? publish.FrameBuffer;
        publish.FrameBuffer = frameBuffer;
        publish.Target = frameBuffer;
        return publish;
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

        ResourcePlannerRuntimeState plannerState = CaptureResourcePlannerRuntimeState();
        VulkanResourceAllocator allocator = plannerState.ResourceAllocator;
        VulkanOpenXrResourceRegistryWrapperRefreshStamp refreshStamp = new(
            registry.InstanceRevision,
            registry.DescriptorRevision,
            plannerState.ResourcePlannerRevision,
            RuntimeHelpers.GetHashCode(allocator));
        if (OutputRuntime.OpenXrBackend.ResourceRegistryWrapperRefreshStamps.TryGetValue(registry, out VulkanOpenXrResourceRegistryWrapperRefreshStamp previousStamp) &&
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
                 !allocator.TryGetPhysicalGroupForResource(texture.Name, out VulkanPhysicalImageGroup? physicalGroup) ||
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

        OutputRuntime.OpenXrBackend.ResourceRegistryWrapperRefreshStamps[registry] = refreshStamp;
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
        if (!_commandRuntime.TryRegisterFrameWideMeshFrameDataRequirements(
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
            int drawUniformSlot = VulkanCommandRuntime.GetFrameWideMeshDrawUniformSlot(
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

    private VulkanOpenXrResourcePlannerThreadScope EnterOpenXrResourcePlannerThreadScope(
        int stateIndex,
        EVulkanOpenXrResourcePlannerPurpose purpose)
        => _commandRuntime.OpenXrRecording.EnterPlannerScope(
            CreateOpenXrResourcePlannerThreadData(),
            CreateLegacyOpenXrResourcePlannerContextKey(stateIndex, purpose));

    private VulkanOpenXrResourcePlannerThreadScope EnterOpenXrResourcePlannerThreadScope(in VulkanOpenXrViewResourcePlannerContextKey contextKey)
        => _commandRuntime.OpenXrRecording.EnterPlannerScope(
            CreateOpenXrResourcePlannerThreadData(),
            contextKey);

    private VulkanOpenXrResourcePlannerThreadData CreateOpenXrResourcePlannerThreadData()
        => new(
            OutputRuntime.OpenXrBackend,
            _deviceContext,
            OpenXrResourcePlannerStates,
            CommandThreadContext,
            _commandRuntime);

    private VulkanOpenXrThreadRenderStateData CreateOpenXrThreadRenderStateData()
        => new(CommandThreadContext, _commandRuntime);

    private static int NormalizeOpenXrResourcePlannerStateIndex(int stateIndex)
        => (uint)stateIndex < OpenXrEyeResourcePlannerStateCount ? stateIndex : 0;

    private static VulkanOpenXrViewResourcePlannerContextKey CreateLegacyOpenXrResourcePlannerContextKey(
        int stateIndex,
        EVulkanOpenXrResourcePlannerPurpose purpose)
    {
        int normalizedStateIndex = NormalizeOpenXrResourcePlannerStateIndex(stateIndex);
        uint legacyIndex = unchecked((uint)normalizedStateIndex);
        return new VulkanOpenXrViewResourcePlannerContextKey(
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

    private static string DescribeOpenXrResourcePlannerContextKey(in VulkanOpenXrViewResourcePlannerContextKey key)
        => $"purpose={key.Purpose} planner={key.ResourcePlannerStateIndex} eye={key.OpenXrViewIndex} imageIndex={key.OpenXrImageIndex} " +
           $"commandKey={key.CommandChainImageKey} frameSlot={key.FrameDataSlotIndex} foveationKey=0x{key.FoveationResourceKey:X} " +
           $"foveationAttachment={key.FoveationAttachmentKind} foveationOwned={key.FoveationAttachmentOwnedByResourcePlanner}";

}
