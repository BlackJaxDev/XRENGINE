using System.Numerics;
using Silk.NET.Vulkan;
using XREngine.Rendering.Vulkan.RenderGraph;
using XREngine.Rendering.Diagnostics;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanCommandRuntime
{
    // Cache paths once; native dispatch instrumentation must not allocate per frame.
    private static readonly string[] NativeClassificationGpuProfilerPath = ["Advanced", "Classification", "ClassifyTiles"];
    private static readonly string[] NativeClassificationArgumentsGpuProfilerPath = ["Advanced", "Classification", "BuildArguments"];
    private static readonly string[] NativeAmbientOcclusionGpuProfilerPath = ["Advanced", "AmbientOcclusion", "GTAO"];
    private static readonly string[] NativeFroxelGpuProfilerPath = ["Advanced", "Lighting", "BuildFroxels"];
    private static readonly string[] NativeBackgroundGpuProfilerPath = ["Advanced", "Shading", "Background"];
    private static readonly string[] NativeOpaqueGpuProfilerPath = ["Advanced", "Shading", "NativeOpaque"];
    private static readonly string[] NativeOpaqueRepairGpuProfilerPath = ["Advanced", "Shading", "GpuOverflowRepair"];
    private static readonly string[] NativeOpaqueUberGpuProfilerPath = ["Advanced", "Shading", "NativeOpaqueUber"];
    private static readonly string[] OcclusionCulledBoundsGpuProfilerPath = ["Advanced", "Shading", "OcclusionCulledBounds"];
    // Matches XR_ADV_OVERLAY_GROUPS_PER_ROW in OcclusionCulledBounds.comp.
    private const uint OcclusionCulledBoundsGroupsPerRow = 4096u;
    // Matches XR_ADV_SHADING_UBER_FALLBACK in ShadeNativeOpaqueEvaluator.glslinc.
    private const uint NativeUberFallbackShadingFlag = 1u << 29;
    // Matches XR_ADV_SHADING_DIAGNOSTICS_DISABLED: binding 18 holds no diagnostics image, so the
    // shading, background and quad-fill passes do not store diagnostics.
    private const uint ShadingDiagnosticsDisabledFlag = 1u << 31;
    // Matches XR_ADV_SHADING_DECAL_BUFFER in ShadeNativeOpaqueEvaluator.glslinc.
    private const uint NativeDecalBufferShadingFlag = 1u << 16;
    // Matches XR_ADV_CLASSIFICATION_PIXEL_COUNTERS in ClassifyTiles.comp.
    private const uint ClassificationPixelCountersFlag = 1u << 30;

    /// <summary>
    /// Records one indirect dispatch for each dense shading kernel in <paramref name="kernels"/>.
    /// The other family's kernels are not dispatched.
    /// </summary>
    private void RecordNativeKernelDispatches(CommandBuffer commandBuffer, in VulkanAdvancedNativeComputeClosure closure,
        in VulkanAdvancedComputePipeline shade, ref VulkanAdvancedNativeShadingPushConstants push, ulong parameterAddress,
        UInt128 kernels)
    {
        for (uint kernel = 0; kernel < AdvancedRenderPipeline.DefaultMaxShadingKernels; ++kernel)
        {
            if (!VulkanAdvancedNativeShadingKernelFamilies.Contains(kernels, kernel))
                continue;
            push = push with { KernelIndex = kernel };
            PushNativeConstants(commandBuffer, shade, in push, parameterAddress);
            Api.CmdDispatchIndirect(commandBuffer, closure.DispatchArguments.NativeBuffer,
                closure.DispatchArguments.NativeOffset + kernel * 16UL);
        }
    }
    // Nest under the Advanced root: a separate root scope measured from the start of the frame.
    private static readonly string[] RvcShadeletTelemetryGpuProfilerPath = ["Advanced", "Rvc", "ShadeletTelemetry"];
    private static readonly string[] FoveatedQuadFillGpuProfilerPath = ["Advanced", "Rvc", "FoveatedQuadFill"];
    // Matches XR_ADV_SHADING_FOVEATED_QUADS in FoveatedQuadShading.glslinc.
    private const uint FoveatedQuadShadingFlag = 1u << 28;
    // Match XR_ADV_SHADING_EDGE_HALF_RATE, XR_ADV_SHADING_EDGE_HALF_RATE_TINT and the
    // radius field (5 bits, steps of 0.05) in FoveatedQuadShading.glslinc.
    private const uint EdgeHalfRateShadingFlag = 1u << 17;
    private const uint EdgeHalfRateTintFlag = 1u << 18;
    private const int EdgeHalfRateRadiusShift = 19;
    private const float EdgeHalfRateRadiusStep = 0.05f;

    /// <summary>
    /// Writes the hidden-area ellipse into the push fields that classification and the background
    /// pass do not read: depthSlices holds the center (two 16-bit UNORM values in eye-image UV),
    /// maxLightIndices and lightCount hold the inverse radii (float bits, in UV). Zero inverse radii
    /// turn the reject off. Only ClassifyTiles.comp and ShadeBackground.comp receive this push;
    /// the froxel and shading dispatches keep the real values. MSAA visibility shades with one
    /// full-screen dispatch that ignores classification, so it keeps every tile.
    /// </summary>
    private static VulkanAdvancedNativeShadingPushConstants WithHiddenAreaTiles(
        in VulkanAdvancedNativeShadingPushConstants push, Vector4 ellipse, bool multisampleVisibility)
    {
        // A center outside the image cannot be packed without moving the ellipse, which could
        // then exclude visible pixels, so such an ellipse turns the reject off.
        if (multisampleVisibility || !(ellipse.Z > 0f) || !(ellipse.W > 0f) ||
            !(ellipse.X >= 0f && ellipse.X <= 1f && ellipse.Y >= 0f && ellipse.Y <= 1f))
            return push with { DepthSlices = 0u, MaxLightIndices = 0u, LightCount = 0u };
        uint centerU = (uint)MathF.Round(ellipse.X * 65535f);
        uint centerV = (uint)MathF.Round(ellipse.Y * 65535f);
        return push with
        {
            DepthSlices = centerU | (centerV << 16),
            MaxLightIndices = BitConverter.SingleToUInt32Bits(1f / ellipse.Z),
            LightCount = BitConverter.SingleToUInt32Bits(1f / ellipse.W),
        };
    }

    /// <summary>
    /// Push flags for the shading and fill passes of one view. RVC foveated quads and
    /// Advanced edge half-rate shading share the follower rule and the fill pass; the
    /// edge mode adds its own bit and the quantized radius. MSAA visibility uses neither.
    /// </summary>
    private static uint QuadShadingFlags(
        bool rvcFoveatedQuads, bool edgeHalfRate, float edgeRadius, bool edgeTint, bool multisampleVisibility)
    {
        if (multisampleVisibility)
            return 0u;
        if (rvcFoveatedQuads)
            return FoveatedQuadShadingFlag;
        if (!edgeHalfRate)
            return 0u;
        uint radius = (uint)Math.Clamp((int)MathF.Round(edgeRadius / EdgeHalfRateRadiusStep), 1, 31);
        return FoveatedQuadShadingFlag | EdgeHalfRateShadingFlag |
            (edgeTint ? EdgeHalfRateTintFlag : 0u) |
            (radius << EdgeHalfRateRadiusShift);
    }
    private const string RvcTelemetryMissingClosureReason = "The native closure has no RVC telemetry counters or whole-array views.";
    private const string RvcTelemetryMissingKernelReason = "The RVC telemetry kernel is not current.";
    private int RecordAdvancedNativeComputePayload(
        scoped ref PrimaryCommandBufferRecordingState state,
        in VulkanAdvancedVisibilityOperationPayload payload,
        in VulkanPrimaryOperationRecordingInfo info)
    {
        if (payload.NativeComputeClosure is not { IsValid: true } closure ||
            !payload.NativeComputePipelines.IsCurrentFor(closure.UsesMultisampleVisibility) ||
            payload.NativeComputeDescriptorSet.Handle == 0 ||
            !payload.State.IsValid || !payload.SceneState.IsValid)
            throw new VulkanPlanPreconditionException("Advanced native compute reached recording without its immutable resources and pipeline generation.");
        if (state.RenderScope.IsActive) EndActiveRenderPass(ref state);

        uint width = closure.Identity.ResolvedExtent.Width, height = closure.Identity.ResolvedExtent.Height;
        uint tilesX = DivideRoundUp(width, 16), tilesY = DivideRoundUp(height, 16);
        uint totalTiles = checked(tilesX * tilesY * payload.State.ViewCount);
        uint depthSlices = checked((uint)(closure.FroxelGrid.NativeSize / 16UL / totalTiles));
        if (depthSlices == 0 || closure.ActiveTiles.NativeSize < checked(totalTiles * 16UL) ||
            closure.KernelCounts.NativeSize < AdvancedRenderPipeline.DefaultMaxShadingKernels * 4UL ||
            closure.DispatchArguments.NativeSize < AdvancedRenderPipeline.DefaultMaxShadingKernels * 16UL)
            throw new VulkanPlanPreconditionException("Advanced native output capacity does not match the admitted extent and view count.");
        var clear = RuntimeEngine.StartupPresentationClearColor;
        VulkanAdvancedNativeShadingPushConstants push = new(width, height, tilesX, tilesY,
            closure.ViewIndex, payload.State.ViewCount, 0,
            (payload.Request.RequireNativeOutput ? 2u : 0u) |
            (payload.Request.EnableBuiltInAmbientOcclusion ? 4u : 0u) |
            (payload.Request.EnableLightProbesAndIbl ? 8u : 0u) |
            (payload.Request.RequiresMaterialSurfaceExports ? 16u : 0u) |
            (payload.Request.SuppressBaselineDiffuse ? 32u : 0u) |
            (payload.Request.AmbientOcclusionMultiBounce ? 64u : 0u) |
            (payload.Request.AmbientOcclusionSpecularUsesVisibility ? 128u : 0u) |
            (((uint)payload.Request.ShadingDebugView & 0xFFu) << 8) |
            ((Math.Min(closure.MsaaSampleCount, 16u) & 0xFu) << 24) |
            QuadShadingFlags(payload.Request.RvcFoveatedQuadShading,
                payload.Request.AdvancedEdgeHalfRateShading, payload.Request.AdvancedEdgeHalfRateRadius,
                payload.Request.AdvancedEdgeHalfRateDebugTint, closure.UsesMultisampleVisibility) |
            (payload.Request.DecalBufferEnabled && closure.HasDecalBuffer ? NativeDecalBufferShadingFlag : 0u) |
            (closure.ShadingDiagnosticsAliased ? ShadingDiagnosticsDisabledFlag : 0u), depthSlices,
            checked((uint)(closure.LightIndices.NativeSize / sizeof(uint))),
            payload.SceneState.Lights.Length / 128u,
            checked((uint)(closure.KernelTiles.NativeSize / 16u)),
            payload.Request.MsaaSampleCount > 1u && payload.Request.HasAuthoredBackground
                ? Vector4.Zero
                : new Vector4(clear.R, clear.G, clear.B, clear.A));

        if (payload.Request.Stage != EAdvancedRenderStage.AmbientOcclusion &&
            !(state.AdvancedQueueOverlap is not null && payload.Request.Stage == EAdvancedRenderStage.WorkClassification))
        {
            TransitionNativeSharedInput(state.CommandBuffer, closure.Identity, closure.ViewIndex);
            TransitionNativeSharedInput(state.CommandBuffer, closure.Metadata, closure.ViewIndex);
        }
        if (payload.Request.Stage == EAdvancedRenderStage.WorkClassification)
        {
            // The shared compact tile list accumulates every eye. Only the
            // first immutable view initializes it; later views append disjoint
            // records carrying their own view ordinal.
            if (closure.ViewIndex == 0u)
            {
                FillNativeCounters(state.CommandBuffer, closure.ClassificationCounters);
                FillNativeCounters(state.CommandBuffer, closure.KernelCounts);
            }
            else
            {
                // A later view appends to the tile lists and counters of the earlier views and
                // writes the indirect arguments again. The barrier after the earlier view's
                // BuildArguments only makes those arguments visible to indirect reads, so this
                // barrier orders the earlier classification writes before this view's writes.
                EmitNativeComputeDependency(state.CommandBuffer);
            }
            // The classified and background pixel counters are read only by the diagnostic
            // readback, which instrumented strategies record after the last view.
            VulkanAdvancedNativeShadingPushConstants classifyPush = WithHiddenAreaTiles(
                GpuDiagnosticReadbackPlan.IsInstrumented(payload.Request.BackendPackage.Package.SubmissionResolution.Resolved)
                    ? push with { Flags = push.Flags | ClassificationPixelCountersFlag }
                    : push,
                payload.Request.HiddenAreaEllipses.ForView(closure.ViewIndex), closure.UsesMultisampleVisibility);
            RecordNativeDispatch(state.CommandBuffer, in payload, payload.NativeComputePipelines.Classify,
                in classifyPush, tilesX, tilesY, 1, "Advanced.ClassifyTiles", NativeClassificationGpuProfilerPath);
            EmitNativeBufferDependency(state.CommandBuffer, closure.KernelCounts,
                AccessFlags.ShaderWriteBit, AccessFlags.ShaderReadBit,
                PipelineStageFlags.ComputeShaderBit, PipelineStageFlags.ComputeShaderBit);
            EmitNativeBufferDependency(state.CommandBuffer, closure.ClassificationCounters,
                AccessFlags.ShaderWriteBit, AccessFlags.ShaderReadBit,
                PipelineStageFlags.ComputeShaderBit, PipelineStageFlags.ComputeShaderBit);
            RecordNativeDispatch(state.CommandBuffer, in payload, payload.NativeComputePipelines.BuildArguments,
                in push, 1, 1, 1, "Advanced.BuildClassificationIndirect", NativeClassificationArgumentsGpuProfilerPath);
            EmitNativeBufferDependency(state.CommandBuffer, closure.DispatchArguments,
                AccessFlags.ShaderWriteBit, AccessFlags.IndirectCommandReadBit,
                PipelineStageFlags.ComputeShaderBit, PipelineStageFlags.DrawIndirectBit);
            if (closure.ViewIndex + 1u == payload.State.ViewCount)
            {
                VulkanFrozenBufferBarrier classificationCounters = closure.ClassificationCounters;
                VulkanAdvancedVisibilityStageRequest request = payload.Request;
                _ = AdvancedCounterDiagnosticCopy?.Invoke(
                    state.CommandBuffer, in classificationCounters, in request,
                    checked((int)request.BackendPackage.CanonicalFrame.FrameGeneration),
                    uint.MaxValue, EGpuDiagnosticReadbackDecoder.AdvancedClassificationCounters);
            }
            return info.OperationIndex;
        }

        TransitionNativeInput(state.CommandBuffer, closure.Depth, closure.ViewIndex);
        if (payload.Request.Stage == EAdvancedRenderStage.AmbientOcclusion)
        {
            TransitionNativeOutput(state.CommandBuffer, closure.AmbientOcclusion, closure.ViewIndex);
            // The AO passes overwrite the whole image later in this frame, so the neutral fill
            // would be a dead write. The transition and the barrier still run.
            if (!payload.Request.SkipNeutralAmbientOcclusionFill)
                RecordNativeDispatch(state.CommandBuffer, in payload, payload.NativeComputePipelines.AmbientOcclusion,
                    in push, tilesX, tilesY, 1, "Advanced.GTAO", NativeAmbientOcclusionGpuProfilerPath);
            EmitMemoryBarrierMask(state.CommandBuffer, EMemoryBarrierMask.ShaderImageAccess | EMemoryBarrierMask.TextureFetch);
            return info.OperationIndex;
        }

        TransitionNativeInput(state.CommandBuffer, closure.AmbientOcclusion, closure.ViewIndex);
        TransitionNativeDecalBufferInputs(state.CommandBuffer, in closure);
        TransitionNativeOutput(state.CommandBuffer, closure.Hdr, closure.ViewIndex);
        TransitionNativeOutput(state.CommandBuffer, closure.Velocity, closure.ViewIndex);
        TransitionNativeOutput(state.CommandBuffer, closure.Reactive, closure.ViewIndex);
        TransitionNativeDiagnosticsOutput(state.CommandBuffer, in closure);
        TransitionNativeDdgiOutputs(state.CommandBuffer, in closure);
        FillNativeCounters(state.CommandBuffer, closure.LightingCounters);
        RecordNativeDispatch(state.CommandBuffer, in payload, payload.NativeComputePipelines.BuildFroxels,
            in push, DivideRoundUp(tilesX, 8), DivideRoundUp(tilesY, 8), DivideRoundUp(depthSlices, 4), "Advanced.BuildFroxels", NativeFroxelGpuProfilerPath);
        // Background reads only the visibility identity, so it can overlap the froxel build.
        // The global barrier after it makes the froxel, light index and decal buffers visible
        // to shading, together with the background image writes.
        VulkanAdvancedNativeShadingPushConstants backgroundPush = WithHiddenAreaTiles(
            push, payload.Request.HiddenAreaEllipses.ForView(closure.ViewIndex), closure.UsesMultisampleVisibility);
        RecordNativeDispatch(state.CommandBuffer, in payload, payload.NativeComputePipelines.Background,
            in backgroundPush, tilesX, tilesY, 1, "Advanced.Background", NativeBackgroundGpuProfilerPath);
        // Background initializes every pixel and shading then overwrites the covered pixels,
        // so this barrier orders a real write-after-write.
        EmitMemoryBarrierMask(state.CommandBuffer, EMemoryBarrierMask.ShaderImageAccess);

        // Classification and graphics preparation meet only at shading. The
        // final submit owns the Classified wait covering indirect argument reads.
        BeginAdvancedQueueOverlapShade(ref state);
        TrackAdvancedNativeShadeClosure(state.CommandBuffer, in closure);
        if (closure.UsesMultisampleVisibility)
        {
            TransitionNativeSharedInput(state.CommandBuffer, closure.IdentityMultisample!, closure.ViewIndex);
            TransitionNativeSharedInput(state.CommandBuffer, closure.MetadataMultisample!, closure.ViewIndex);
            TransitionNativeSharedInput(state.CommandBuffer, closure.SelectionMultisample!, closure.ViewIndex);
            TransitionNativeInput(state.CommandBuffer, closure.DepthMultisample!, closure.ViewIndex);
            TransitionNativeSharedInput(state.CommandBuffer, closure.SamplePositionMultisample!, closure.ViewIndex);
        }
        if (state.AdvancedQueueOverlap is not null)
        {
            // Final owns these real accesses; inherited entry layouts alone do
            // not publish its reads/writes into the completion journal.
            TransitionNativeSharedInput(state.CommandBuffer, closure.Identity, closure.ViewIndex);
            TransitionNativeSharedInput(state.CommandBuffer, closure.Metadata, closure.ViewIndex);
            TransitionNativeInput(state.CommandBuffer, closure.Depth, closure.ViewIndex);
            TransitionNativeInput(state.CommandBuffer, closure.AmbientOcclusion, closure.ViewIndex);
            TransitionNativeDecalBufferInputs(state.CommandBuffer, in closure);
            TransitionNativeOutput(state.CommandBuffer, closure.Hdr, closure.ViewIndex);
            TransitionNativeOutput(state.CommandBuffer, closure.Velocity, closure.ViewIndex);
            TransitionNativeOutput(state.CommandBuffer, closure.Reactive, closure.ViewIndex);
            TransitionNativeDiagnosticsOutput(state.CommandBuffer, in closure);
            TransitionNativeDdgiOutputs(state.CommandBuffer, in closure);
        }
        // Native shading families. The standard kernel shades standard materials and,
        // while the Uber kernel is not ready, Uber materials with a base-color fallback.
        // The Uber kernel then shades only Uber materials in its own pass. Multisample
        // shading resolves every sample of a pixel in one pass, so a scene with Uber
        // materials uses the combined multisample kernel when it is ready.
        VulkanAdvancedNativeShadingKernelFamilies families = payload.SceneState.KernelFamilies;
        VulkanAdvancedComputePipeline uberShade = payload.NativeComputePipelines.ShadeUber;
        VulkanAdvancedComputePipeline combinedMultisampleShade = payload.NativeComputePipelines.ShadeMultisampleCombined;
        bool uberPass = !closure.UsesMultisampleVisibility && families.HasUber && uberShade.IsCurrent;
        bool combinedMultisample = closure.UsesMultisampleVisibility && families.HasUber && combinedMultisampleShade.IsCurrent;
        bool uberFallback = families.HasUber && !uberPass && !combinedMultisample;
        if (uberFallback)
            push = push with { Flags = push.Flags | NativeUberFallbackShadingFlag };
        UInt128 standardKernels = families.Standard | (uberFallback ? families.Uber : UInt128.Zero);
        // A publication without family masks keeps the full kernel range.
        if ((families.Standard | families.Uber) == UInt128.Zero)
            standardKernels = UInt128.MaxValue;
        // Without a shading debug view and the Uber fallback, the lean standard kernel shades
        // the same image with fewer registers (XR_ADV_SHADE_LEAN).
        bool leanShade = !closure.UsesMultisampleVisibility && !uberFallback &&
            ((uint)payload.Request.ShadingDebugView & 0xFFu) == 0u &&
            payload.NativeComputePipelines.ShadeLean.IsCurrent;
        VulkanAdvancedComputePipeline shade = closure.UsesMultisampleVisibility
            ? combinedMultisample ? combinedMultisampleShade : payload.NativeComputePipelines.ShadeMultisample
            : leanShade ? payload.NativeComputePipelines.ShadeLean : payload.NativeComputePipelines.Shade;
        ulong parameterAddress = shade.UsesAddressRoot
            ? WriteNativeShadingAddressRoot(ref state, closure.ShadingAddressRoot, in push)
            : 0UL;
        using (TryBeginVulkanGpuProfilerScope(state.CommandBuffer, NativeOpaqueGpuProfilerPath))
        {
            CmdBeginLabel(state.CommandBuffer, closure.UsesMultisampleVisibility
                ? "Advanced.NativeOpaque.Msaa"
                : "Advanced.NativeOpaque.Indirect");
            BindPipelineTracked(state.CommandBuffer, PipelineBindPoint.Compute, shade.Pipeline);
            BindAdvancedVisibilityDescriptorSets(state.CommandBuffer, PipelineBindPoint.Compute,
                shade.Program.PipelineLayout, in payload, payload.NativeComputeDescriptorSet);
            if (closure.UsesMultisampleVisibility)
            {
                // MSAA must shade every covered sample at its own position.
                // Classification is pixel-granular and would omit edge materials,
                // so the MS variant performs one full-screen dispatch.
                push = push with { KernelIndex = 0u };
                PushNativeConstants(state.CommandBuffer, shade, in push, parameterAddress);
                Api.CmdDispatch(state.CommandBuffer, tilesX, tilesY, 1u);
            }
            else
                RecordNativeKernelDispatches(state.CommandBuffer, in closure, shade, ref push, parameterAddress, standardKernels);
            CmdEndLabel(state.CommandBuffer);
        }
        // No barrier before the repair dispatch: the main pass and the repair pass write
        // disjoint pixel sets. Both read the classification overflow flag. The main kernels
        // return when it is set, and the repair kernel returns when it is clear, so only one
        // of them writes any pixel in a frame.
        VulkanAdvancedNativeShadingPushConstants repairPush = push with { Flags = push.Flags | 1u };
        if (!closure.UsesMultisampleVisibility)
        {
            RecordNativeDispatch(state.CommandBuffer, in payload, shade,
                in repairPush, tilesX, tilesY, 1, "Advanced.NativeOpaque.GpuOverflowRepair", NativeOpaqueRepairGpuProfilerPath, parameterAddress);
            RecordNativeShadingRootUse(shade.UsesAddressRoot, checked((int)AdvancedRenderPipeline.DefaultMaxShadingKernels + 1));
        }
        else
            RecordNativeShadingRootUse(shade.UsesAddressRoot, 1);
        if (uberPass)
        {
            // No barrier before the Uber kernels: their pixel set is disjoint from the
            // standard pass. Without overflow, each pixel belongs to exactly one shading kernel,
            // and the Uber kernels shade only Uber kernel tiles. With overflow, the Uber main
            // kernels return at once and only the repair passes write.
            ulong uberParameterAddress = uberShade.UsesAddressRoot
                ? WriteNativeShadingAddressRoot(ref state, closure.ShadingAddressRoot, in push)
                : 0UL;
            using (TryBeginVulkanGpuProfilerScope(state.CommandBuffer, NativeOpaqueUberGpuProfilerPath))
            {
                CmdBeginLabel(state.CommandBuffer, "Advanced.NativeOpaque.Uber");
                BindPipelineTracked(state.CommandBuffer, PipelineBindPoint.Compute, uberShade.Pipeline);
                BindAdvancedVisibilityDescriptorSets(state.CommandBuffer, PipelineBindPoint.Compute,
                    uberShade.Program.PipelineLayout, in payload, payload.NativeComputeDescriptorSet);
                RecordNativeKernelDispatches(state.CommandBuffer, in closure, uberShade, ref push, uberParameterAddress, families.Uber);
                CmdEndLabel(state.CommandBuffer);
            }
            // The standard repair shades every pixel in an overflow frame, and the Uber repair
            // then overwrites the Uber pixels. This barrier keeps that order.
            EmitMemoryBarrierMask(state.CommandBuffer, EMemoryBarrierMask.ShaderImageAccess);
            RecordNativeDispatch(state.CommandBuffer, in payload, uberShade,
                in repairPush, tilesX, tilesY, 1, "Advanced.NativeOpaque.Uber.GpuOverflowRepair", NativeOpaqueRepairGpuProfilerPath, uberParameterAddress);
            RecordNativeShadingRootUse(uberShade.UsesAddressRoot, checked((int)AdvancedRenderPipeline.DefaultMaxShadingKernels + 1));
        }
        push = repairPush;
        EmitMemoryBarrierMask(state.CommandBuffer, EMemoryBarrierMask.ShaderImageAccess | EMemoryBarrierMask.TextureFetch);
        if ((push.Flags & FoveatedQuadShadingFlag) != 0u)
        {
            // Followers skipped shading; copy each block representative to them.
            RecordNativeDispatch(state.CommandBuffer, in payload, payload.NativeComputePipelines.FoveatedQuadFill,
                in push, tilesX, tilesY, 1, "Rvc.FoveatedQuadFill", FoveatedQuadFillGpuProfilerPath);
            EmitMemoryBarrierMask(state.CommandBuffer, EMemoryBarrierMask.ShaderImageAccess | EMemoryBarrierMask.TextureFetch);
        }
        if (payload.Request.ShowOcclusionCulledBounds && payload.NativeComputePipelines.OcclusionCulledBounds.IsCurrent)
            RecordOcclusionCulledBoundsOverlay(state.CommandBuffer, in payload, in push);
        VulkanFrozenBufferBarrier lightingCounters = closure.LightingCounters;
        VulkanAdvancedVisibilityStageRequest lightingRequest = payload.Request;
        _ = AdvancedCounterDiagnosticCopy?.Invoke(
            state.CommandBuffer, in lightingCounters, in lightingRequest,
            checked((int)lightingRequest.BackendPackage.CanonicalFrame.FrameGeneration),
            closure.ViewIndex, EGpuDiagnosticReadbackDecoder.AdvancedLightingCounters);
        if (payload.Request.RecordRvcShadeletTelemetry && closure.ViewIndex + 1u == payload.State.ViewCount)
            RecordRvcShadeletTelemetry(ref state, in payload, in closure, in push);
        return info.OperationIndex;
    }

    /// <summary>
    /// Runs the RVC shadelet telemetry kernel once, after the last view of the
    /// family has shaded. It reads every view layer and only writes its counters.
    /// </summary>
    private void RecordRvcShadeletTelemetry(
        scoped ref PrimaryCommandBufferRecordingState state,
        in VulkanAdvancedVisibilityOperationPayload payload,
        in VulkanAdvancedNativeComputeClosure closure,
        in VulkanAdvancedNativeShadingPushConstants push)
    {
        if (!closure.HasRvcTelemetry || !payload.NativeComputePipelines.RvcShadeletTelemetry.IsCurrent)
        {
            RuntimeEngine.Rendering.Stats.Rvc.RecordShadeletTelemetrySkipped(!closure.HasRvcTelemetry
                ? RvcTelemetryMissingClosureReason
                : RvcTelemetryMissingKernelReason);
            Debug.VulkanWarningEvery(
                "Vulkan.Rvc.ShadeletTelemetry.Unavailable",
                TimeSpan.FromSeconds(5),
                "[Vulkan] RVC shadelet telemetry was requested, but the family has no telemetry counters or kernel (closure={0}, kernel={1}).",
                closure.HasRvcTelemetry,
                payload.NativeComputePipelines.RvcShadeletTelemetry.IsCurrent);
            return;
        }

        uint viewCount = payload.State.ViewCount;
        for (uint view = 0u; view < viewCount; ++view)
        {
            TransitionNativeSharedInput(state.CommandBuffer, closure.Identity, view);
            TransitionNativeInput(state.CommandBuffer, closure.Depth, view);
        }
        TrackAdvancedNativeImageClosure(state.CommandBuffer, closure.RvcIdentityArrayResource);
        TrackAdvancedNativeImageClosure(state.CommandBuffer, closure.RvcDepthArrayResource);
        TrackAdvancedNativeBufferClosure(state.CommandBuffer, closure.RvcTelemetryCounters);
        FillNativeCounters(state.CommandBuffer, closure.RvcTelemetryCounters);
        VulkanAdvancedNativeShadingPushConstants telemetryPush = push with { ViewIndex = 0u, KernelIndex = 0u };
        RecordNativeDispatch(state.CommandBuffer, in payload, payload.NativeComputePipelines.RvcShadeletTelemetry,
            in telemetryPush, DivideRoundUp(push.Width, 8), DivideRoundUp(push.Height, 8), viewCount,
            "Rvc.ShadeletTelemetry", RvcShadeletTelemetryGpuProfilerPath);
        VulkanFrozenBufferBarrier counters = closure.RvcTelemetryCounters;
        EmitNativeBufferDependency(state.CommandBuffer, in counters,
            AccessFlags.ShaderWriteBit, AccessFlags.TransferReadBit,
            PipelineStageFlags.ComputeShaderBit, PipelineStageFlags.TransferBit);
        VulkanAdvancedVisibilityStageRequest request = payload.Request;
        bool readbackAttached = AdvancedCounterDiagnosticCopy?.Invoke(
            state.CommandBuffer, in counters, in request,
            checked((int)request.BackendPackage.CanonicalFrame.FrameGeneration),
            uint.MaxValue, EGpuDiagnosticReadbackDecoder.RvcShadeletTelemetry) == true;
        RuntimeEngine.Rendering.Stats.Rvc.RecordShadeletTelemetryDispatch(readbackAttached);
    }

    /// <summary>
    /// Draws the occlusion-culled bounds overlay into the shaded HDR color of this
    /// view. One workgroup tests one draw candidate. The kernel reads the late
    /// culling results: the per-draw persistent state (written by late compute) and
    /// the meshlet history (written by the late task stage), so a barrier first makes
    /// those writes visible to compute reads.
    /// </summary>
    private unsafe void RecordOcclusionCulledBoundsOverlay(CommandBuffer commandBuffer,
        in VulkanAdvancedVisibilityOperationPayload payload, in VulkanAdvancedNativeShadingPushConstants push)
    {
        uint candidates = payload.State.PayloadCapacity;
        if (candidates == 0u)
            return;
        MemoryBarrier barrier = new()
        {
            SType = StructureType.MemoryBarrier,
            SrcAccessMask = AccessFlags.ShaderWriteBit,
            DstAccessMask = AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit,
        };
        PipelineStageFlags sourceStages = PipelineStageFlags.ComputeShaderBit |
            (DeviceContext.SupportsMeshTaskIndirectCount ? PipelineStageFlags.TaskShaderBitExt : 0);
        CmdPipelineBarrierTracked(commandBuffer, sourceStages, PipelineStageFlags.ComputeShaderBit,
            DependencyFlags.None, 1, &barrier, 0, null, 0, null);
        // The kernel reads the payload capacity of one view from kernelIndex.
        VulkanAdvancedNativeShadingPushConstants overlayPush = push with { KernelIndex = candidates };
        uint groupsX = Math.Min(candidates, OcclusionCulledBoundsGroupsPerRow);
        uint groupsY = DivideRoundUp(candidates, OcclusionCulledBoundsGroupsPerRow);
        RecordNativeDispatch(commandBuffer, in payload, payload.NativeComputePipelines.OcclusionCulledBounds,
            in overlayPush, groupsX, groupsY, 1, "Advanced.OcclusionCulledBounds", OcclusionCulledBoundsGpuProfilerPath);
        EmitMemoryBarrierMask(commandBuffer, EMemoryBarrierMask.ShaderImageAccess | EMemoryBarrierMask.TextureFetch);
    }

    private void RecordNativeDispatch(CommandBuffer commandBuffer,
        in VulkanAdvancedVisibilityOperationPayload payload, in VulkanAdvancedComputePipeline pipeline,
        in VulkanAdvancedNativeShadingPushConstants push, uint x, uint y, uint z, string label, string[] profilerPath,
        ulong parameterAddress = 0)
    {
        using var profilerScope = TryBeginVulkanGpuProfilerScope(commandBuffer, profilerPath);
        CmdBeginLabel(commandBuffer, label);
        BindPipelineTracked(commandBuffer, PipelineBindPoint.Compute, pipeline.Pipeline);
        BindAdvancedVisibilityDescriptorSets(commandBuffer, PipelineBindPoint.Compute,
            pipeline.Program.PipelineLayout, in payload, payload.NativeComputeDescriptorSet);
        PushNativeConstants(commandBuffer, pipeline, in push, parameterAddress);
        Api.CmdDispatch(commandBuffer, x, y, z);
        CmdEndLabel(commandBuffer);
    }

    private void PushNativeConstants(CommandBuffer commandBuffer,
        in VulkanAdvancedComputePipeline pipeline, in VulkanAdvancedNativeShadingPushConstants push, ulong parameterAddress = 0)
    {
        if (pipeline.UsesAddressRoot)
        {
            if (pipeline.NativeShadingRootAbiVersion != VulkanNativeShadingRootPolicy.AbiVersion)
                throw new InvalidOperationException("Native shading address-root ABI does not match the admitted pipeline.");
            if (parameterAddress == 0)
                throw new VulkanPlanPreconditionException("The address-based shading pipeline has no prepared parameter root.");
            PushConstantsTracked(commandBuffer, pipeline.Program.PipelineLayout,
                VulkanMeshRenderingConventions.GetCommonPushConstantStageFlags(DeviceContext), 0,
                new VulkanAdvancedNativeShadingAddressRoot(parameterAddress, push.KernelIndex, push.Flags));
            return;
        }
        PushConstantsTracked(commandBuffer, pipeline.Program.PipelineLayout,
            VulkanMeshRenderingConventions.GetCommonPushConstantStageFlags(DeviceContext), 0, push);
    }

    private void TransitionNativeDdgiOutputs(CommandBuffer commandBuffer, in VulkanAdvancedNativeComputeClosure closure)
    {
        if (!closure.DdgiSurface.Enabled)
            return;
        TransitionNativeOutput(commandBuffer, closure.DdgiSurface.Emission, closure.ViewIndex);
        TransitionNativeOutput(commandBuffer, closure.DdgiSurface.Albedo, closure.ViewIndex);
        TransitionNativeOutput(commandBuffer, closure.DdgiSurface.Normal, closure.ViewIndex);
        TransitionNativeOutput(commandBuffer, closure.DdgiSurface.Rmse, closure.ViewIndex);
    }

    private void TransitionNativeInput(CommandBuffer commandBuffer, VulkanPhysicalImageGroup group, uint view)
        => EmitAdvancedVisibilityImageBarrier(commandBuffer, group, 0, view,
            ImageLayout.ShaderReadOnlyOptimal, AccessFlags.ShaderReadBit, PipelineStageFlags.ComputeShaderBit, false);

    // Identity and metadata are sampled/storage-capable graph resources. Keep
    // their sampled descriptors and both fork readers in the planner's GENERAL
    // layout; transitioning in either child would race the other child's reads.
    private void TransitionNativeSharedInput(CommandBuffer commandBuffer, VulkanPhysicalImageGroup group, uint view)
        => EmitAdvancedVisibilityImageBarrier(commandBuffer, group, 0, view,
            ImageLayout.General, AccessFlags.ShaderReadBit, PipelineStageFlags.ComputeShaderBit, false);

    /// <summary>Makes the mesh surface decal buffer layer of this view readable by shading.</summary>
    private void TransitionNativeDecalBufferInputs(CommandBuffer commandBuffer, in VulkanAdvancedNativeComputeClosure closure)
    {
        if (!closure.HasDecalBuffer)
            return;
        TransitionNativeInput(commandBuffer, closure.DecalBufferAlbedo!, closure.ViewIndex);
        TransitionNativeInput(commandBuffer, closure.DecalBufferSurface!, closure.ViewIndex);
    }

    /// <summary>
    /// Makes the shading diagnostics image a compute write target. When the frame does not
    /// declare that image, binding 18 holds the metadata image, which the shading passes only
    /// sample, so no write transition is emitted for it.
    /// </summary>
    private void TransitionNativeDiagnosticsOutput(CommandBuffer commandBuffer, in VulkanAdvancedNativeComputeClosure closure)
    {
        if (!closure.ShadingDiagnosticsAliased)
            TransitionNativeOutput(commandBuffer, closure.ShadingDiagnostics, closure.ViewIndex);
    }

    private void TransitionNativeOutput(CommandBuffer commandBuffer, VulkanPhysicalImageGroup group, uint view)
        => EmitAdvancedVisibilityImageBarrier(commandBuffer, group, 0, view,
            ImageLayout.General, AccessFlags.ShaderWriteBit, PipelineStageFlags.ComputeShaderBit, true);

    private void FillNativeCounters(CommandBuffer commandBuffer, in VulkanFrozenBufferBarrier buffer)
    {
        EmitNativeBufferDependency(commandBuffer, in buffer,
            AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit, AccessFlags.TransferWriteBit,
            PipelineStageFlags.AllCommandsBit, PipelineStageFlags.TransferBit);
        Api.CmdFillBuffer(commandBuffer, buffer.NativeBuffer, buffer.NativeOffset, buffer.NativeSize, 0);
        EmitNativeBufferDependency(commandBuffer, in buffer, AccessFlags.TransferWriteBit,
            AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit,
            PipelineStageFlags.TransferBit, PipelineStageFlags.ComputeShaderBit);
    }

    /// <summary>
    /// Orders all earlier compute shader reads and writes before later compute shader reads and
    /// writes, for every buffer and image.
    /// </summary>
    private unsafe void EmitNativeComputeDependency(CommandBuffer commandBuffer)
    {
        MemoryBarrier barrier = new()
        {
            SType = StructureType.MemoryBarrier,
            SrcAccessMask = AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit,
            DstAccessMask = AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit,
        };
        CmdPipelineBarrierTracked(commandBuffer, PipelineStageFlags.ComputeShaderBit, PipelineStageFlags.ComputeShaderBit,
            DependencyFlags.None, 1, &barrier, 0, null, 0, null);
    }

    private unsafe void EmitNativeBufferDependency(CommandBuffer commandBuffer,
        in VulkanFrozenBufferBarrier buffer, AccessFlags source, AccessFlags destination,
        PipelineStageFlags sourceStage, PipelineStageFlags destinationStage)
    {
        BufferMemoryBarrier barrier = new()
        {
            SType = StructureType.BufferMemoryBarrier,
            SrcAccessMask = source, DstAccessMask = destination,
            SrcQueueFamilyIndex = uint.MaxValue, DstQueueFamilyIndex = uint.MaxValue,
            Buffer = buffer.NativeBuffer, Offset = buffer.NativeOffset, Size = buffer.NativeSize,
        };
        CmdPipelineBarrierTracked(commandBuffer, sourceStage, destinationStage,
            DependencyFlags.None, 0, null, 1, &barrier, 0, null);
    }
}
