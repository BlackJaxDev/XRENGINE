using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Silk.NET.Vulkan;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.Shadows;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private void BuildCommandChainRenderPackets(
        FrameOp[] staticOps,
        FrameOp[] volatileOps,
        ulong resourcePlanRevision,
        bool excludeStaticQueryBrackets,
        List<RenderPacket> packets)
    {
        // Packet lowering is deliberately deterministic and allocation-free on a
        // schedule-cache hit. Parallelizing this cheap classification previously
        // allocated two exact-length arrays and captured two closures every time
        // visibility changed; actual Vulkan recording belongs on the persistent
        // command-chain workers instead.
        if (excludeStaticQueryBrackets)
            LowerFrameOpsToRenderPacketsExcludingQueryBrackets(staticOps, resourcePlanRevision, packets);
        else
            LowerFrameOpsToRenderPackets(staticOps, dynamicOverlay: false, resourcePlanRevision, packets);
        LowerFrameOpsToRenderPackets(volatileOps, dynamicOverlay: true, resourcePlanRevision, packets);
    }

    private void LowerFrameOpsToRenderPacketsExcludingQueryBrackets(
        FrameOp[] ops,
        ulong resourcePlanRevision,
        List<RenderPacket> packets)
    {
        int queryBracketDepth = 0;
        for (int i = 0; i < ops.Length; i++)
        {
            if (ops[i] is QueryOp queryOp)
            {
                if (queryOp.Operation == ERenderQueryOperation.Begin)
                    queryBracketDepth++;
                else if (queryOp.Operation == ERenderQueryOperation.End && queryBracketDepth > 0)
                    queryBracketDepth--;
                continue;
            }

            if (queryBracketDepth == 0)
            {
                int consumed = TryLowerCompatibleMeshPacket(
                    ops,
                    i,
                    dynamicOverlay: false,
                    resourcePlanRevision,
                    packets,
                    out DrawPacket preparedMeshDraw);
                if (consumed > 0)
                    i += consumed - 1;
                else if (IsSchedulableCommandChainFrameOp(ops[i], dynamicOverlay: false))
                    packets.Add(CreateRenderPacket(
                        ops[i], i, dynamicOverlay: false, resourcePlanRevision, preparedMeshDraw));
            }
        }

        if (queryBracketDepth != 0)
        {
            Debug.VulkanWarningEvery(
                $"Vulkan.CommandChains.UnbalancedQueryBracket.{GetHashCode()}",
                TimeSpan.FromSeconds(2),
                "[Vulkan.CommandChains] Found {0} unterminated occlusion query bracket(s); all operations after the unmatched begin remain inline.",
                queryBracketDepth);
        }
    }

    private void LowerFrameOpsToRenderPackets(
        FrameOp[] ops,
        bool dynamicOverlay,
        ulong resourcePlanRevision,
        List<RenderPacket> packets)
    {
        for (int i = 0; i < ops.Length; i++)
        {
            int consumed = TryLowerCompatibleMeshPacket(
                ops,
                i,
                dynamicOverlay,
                resourcePlanRevision,
                packets,
                out DrawPacket preparedMeshDraw);
            if (consumed > 0)
                i += consumed - 1;
            else if (IsSchedulableCommandChainFrameOp(ops[i], dynamicOverlay))
                packets.Add(CreateRenderPacket(
                    ops[i], i, dynamicOverlay, resourcePlanRevision, preparedMeshDraw));
        }
    }

    private int TryLowerCompatibleMeshPacket(
        FrameOp[] ops,
        int startIndex,
        bool dynamicOverlay,
        ulong resourcePlanRevision,
        List<RenderPacket> packets,
        out DrawPacket preparedMeshDraw)
    {
        preparedMeshDraw = default;
        if (!IsSchedulableCommandChainFrameOp(ops[startIndex], dynamicOverlay) ||
            ops[startIndex] is not MeshDrawOp first)
            return 0;

        DrawPacket firstDraw = CreateDrawPacket(startIndex, first);
        preparedMeshDraw = firstDraw;
        _commandChainDrawPacketScratch[0] = firstDraw;
        RenderViewKey viewKey = BuildRenderViewKey(first, dynamicOverlay: false);
        int targetIdentity = ResolveCommandChainTargetIdentity(first);
        DescriptorBindingSnapshot firstDescriptorSnapshot = CreateDescriptorSnapshot(first);
        int runCount = 1;
        int packetDrawLimit = viewKey.Kind == RenderViewKind.Shadow
            ? MaxShadowMeshDrawsPerRenderPacket
            : MaxMeshDrawsPerRenderPacket;
        int available = Math.Min(ops.Length - startIndex, packetDrawLimit);
        while (runCount < available &&
               ops[startIndex + runCount] is MeshDrawOp next &&
               IsMeshDrawPacketCompatible(
                   first,
                   firstDraw,
                   viewKey,
                   targetIdentity,
                   firstDescriptorSnapshot,
                   next,
                   startIndex + runCount,
                   out DrawPacket candidateDraw))
        {
            _commandChainDrawPacketScratch[runCount] = candidateDraw;
            runCount++;
        }

        if (runCount < MinMeshDrawsPerRenderPacket)
            return 0;

        Span<DrawPacket> draws = _commandChainDrawPacketScratch.AsSpan(0, runCount);
        FrameOpSignatureHasher structuralHash = new();
        FrameOpSignatureHasher frameDataHash = new();
        FrameOpSignatureHasher descriptorGenerationHash = new();
        FrameOpSignatureHasher descriptorSetHash = new();
        FrameOpSignatureHasher pipelineGenerationHash = new();
        int descriptorSetCount = 0;
        bool hasDescriptorBindings = false;
        for (int i = 0; i < runCount; i++)
        {
            MeshDrawOp drawOp = (MeshDrawOp)ops[startIndex + i];
            DrawPacket draw = draws[i];
            structuralHash.Add(draw.StructuralSignature);
            frameDataHash.Add(draw.FrameDataSignature);
            pipelineGenerationHash.Add(ResolvePipelineGeneration(drawOp));

            // A secondary command buffer may bind a different material descriptor
            // set and graphics program for every draw. Track the complete ordered
            // dependency set instead of splitting an otherwise compatible shadow
            // draw run into one secondary per material. Schema/identity and ordinary
            // descriptor publication changes require re-recording.
            DescriptorBindingSnapshot drawDescriptors = CreateDescriptorSnapshot(drawOp);
            descriptorGenerationHash.Add(drawDescriptors.DescriptorGeneration);
            descriptorSetHash.Add(drawDescriptors.DescriptorSetCount);
            descriptorSetHash.Add(drawDescriptors.DescriptorSetSignature);
            descriptorSetCount += drawDescriptors.DescriptorSetCount;
            hasDescriptorBindings |= drawDescriptors.DescriptorSetCount != 0 ||
                drawDescriptors.DescriptorGeneration != 0UL ||
                drawDescriptors.DescriptorSetSignature != 0UL;
        }

        DescriptorBindingSnapshot descriptorSnapshot = hasDescriptorBindings
            ? new DescriptorBindingSnapshot(
                descriptorGenerationHash.ToHash(),
                descriptorSetCount,
                descriptorSetHash.ToHash())
            : default;

        string targetName = ResolveCommandChainTargetName(first);
        ResourcePlanSnapshot resourceSnapshot = new(
            resourcePlanRevision,
            unchecked((ulong)targetIdentity),
            unchecked((ulong)targetName.GetHashCode(StringComparison.Ordinal)),
            pipelineGenerationHash.ToHash());
        RenderPacket packet = RentRenderPacket();
        packet.Reset(
            viewKey,
            first.PassIndex,
            targetIdentity,
            targetName,
            RenderPacketVolatility.FrameDataOnly,
            draws,
            ReadOnlySpan<DispatchPacket>.Empty,
            descriptorSnapshot,
            resourceSnapshot,
            structuralHash.ToHash(),
            frameDataHash.ToHash(),
            startIndex,
            runCount,
            dynamicOverlay: false);
        packets.Add(packet);
        return runCount;
    }

    private static bool IsMeshDrawPacketCompatible(
        MeshDrawOp first,
        DrawPacket firstDraw,
        RenderViewKey viewKey,
        int targetIdentity,
        DescriptorBindingSnapshot descriptorSnapshot,
        MeshDrawOp candidate,
        int candidateIndex,
        out DrawPacket candidateDraw)
    {
        candidateDraw = default;
        if (!IsSchedulableCommandChainFrameOp(candidate, dynamicOverlay: false) ||
            candidate.PassIndex != first.PassIndex ||
            ResolveCommandChainTargetIdentity(candidate) != targetIdentity ||
            BuildRenderViewKey(candidate, dynamicOverlay: false) != viewKey)
        {
            return false;
        }

        candidateDraw = CreateDrawPacket(candidateIndex, candidate);
        if (candidateDraw.Transparent != firstDraw.Transparent)
            return false;

        if (viewKey.Kind == RenderViewKind.Shadow &&
            ResolveShadowCommandChainBucket(
                candidateDraw.RendererIdentity,
                candidateDraw.MaterialIdentity) !=
            ResolveShadowCommandChainBucket(
                firstDraw.RendererIdentity,
                firstDraw.MaterialIdentity))
        {
            return false;
        }

        // Directional-shadow membership is stable while the camera moves, so a
        // packet can safely switch graphics programs and descriptor layouts per
        // draw and amortize secondary execution. Main-view membership churns with
        // frustum/occlusion results; retain its fine-grained program/descriptor
        // packets so adding one visible mesh does not re-record a whole draw run.
        if (viewKey.Kind != RenderViewKind.Shadow)
        {
            DescriptorBindingSnapshot candidateDescriptors = CreateDescriptorSnapshot(candidate);
            if (candidateDraw.ProgramIdentity != firstDraw.ProgramIdentity ||
                candidateDescriptors.DescriptorSetCount != descriptorSnapshot.DescriptorSetCount ||
                candidateDescriptors.DescriptorSetSignature != descriptorSnapshot.DescriptorSetSignature)
            {
                return false;
            }
        }

        return BuildFrameOpPlannerStateKey(candidate.Context) == BuildFrameOpPlannerStateKey(first.Context);
    }

    private RenderPacket CreateRenderPacket(
        FrameOp op,
        int opIndex,
        bool dynamicOverlay,
        ulong resourcePlanRevision,
        DrawPacket preparedMeshDraw)
    {
        RenderViewKey viewKey = BuildRenderViewKey(op, dynamicOverlay);
        RenderPacketVolatility volatility = ClassifyRenderPacketVolatility(op, dynamicOverlay);
        DrawPacket firstDraw = op switch
        {
            MeshDrawOp => preparedMeshDraw,
            IndirectDrawOp indirect => CreateIndirectDrawPacket(opIndex, indirect),
            MeshTaskDispatchIndirectCountOp meshTask => CreateMeshTaskDrawPacket(opIndex, meshTask),
            _ => default
        };
        int drawCount = op is MeshDrawOp or IndirectDrawOp or MeshTaskDispatchIndirectCountOp ? 1 : 0;
        DispatchPacket firstDispatch = op is ComputeDispatchOp compute
            ? CreateDispatchPacket(opIndex, compute)
            : default;
        int dispatchCount = op is ComputeDispatchOp ? 1 : 0;

        bool usePreparedMeshSignatures =
            op is MeshDrawOp && volatility == RenderPacketVolatility.FrameDataOnly;
        ulong structuralSignature = usePreparedMeshSignatures
            ? firstDraw.StructuralSignature
            : ComputeFrameOpStructuralSignature(op, opIndex, volatility);
        ulong frameDataSignature = op is MeshDrawOp
            ? firstDraw.FrameDataSignature
            : ComputeFrameOpFrameDataSignature(op, opIndex);
        int targetIdentity = ResolveCommandChainTargetIdentity(op);
        string targetName = ResolveCommandChainTargetName(op);
        DescriptorBindingSnapshot descriptorSnapshot = CreateDescriptorSnapshot(op);
        ResourcePlanSnapshot resourceSnapshot = new(
            resourcePlanRevision,
            unchecked((ulong)targetIdentity),
            unchecked((ulong)targetName.GetHashCode(StringComparison.Ordinal)),
            ResolvePipelineGeneration(op));

        RenderPacket packet = RentRenderPacket();
        packet.Reset(
            viewKey,
            op.PassIndex,
            targetIdentity,
            targetName,
            volatility,
            firstDraw,
            drawCount,
            firstDispatch,
            dispatchCount,
            descriptorSnapshot,
            resourceSnapshot,
            structuralSignature,
            frameDataSignature,
            opIndex,
            1,
            dynamicOverlay);
        return packet;
    }

    private RenderPacket RentRenderPacket()
    {
        int index = _commandChainPacketPoolCursor++;
        if ((uint)index < (uint)_commandChainPacketPool.Count)
            return _commandChainPacketPool[index];

        RenderPacket packet = new();
        _commandChainPacketPool.Add(packet);
        return packet;
    }

    private static int BuildCommandChainOrdinal(
        RenderPacket packet,
        Dictionary<ulong, int> structuralOccurrences)
    {
        if (packet.ViewKey.Kind == RenderViewKind.Shadow &&
            packet.DrawCount > 0)
        {
            int bucket = ResolveShadowCommandChainBucket(
                packet.FirstDraw.RendererIdentity,
                packet.FirstDraw.MaterialIdentity);
            ulong bucketSignature = unchecked(
                0x534841444F570000UL | (uint)bucket);
            structuralOccurrences.TryGetValue(
                bucketSignature,
                out int bucketOccurrence);
            structuralOccurrences[bucketSignature] = bucketOccurrence + 1;
            int bucketOrdinal = HashCode.Combine(
                unchecked((int)0x53484457),
                bucket,
                bucketOccurrence);
            return bucketOrdinal == -1
                ? int.MaxValue
                : bucketOrdinal;
        }

        ulong structuralSignature = packet.StructuralSignature;
        ulong descriptorBindingVariant =
            ResolveCommandChainDescriptorBindingVariant(
                packet.DescriptorSnapshot);
        ulong occurrenceSignature = MixSignature(
            structuralSignature,
            descriptorBindingVariant);
        structuralOccurrences.TryGetValue(
            occurrenceSignature,
            out int occurrence);
        structuralOccurrences[occurrenceSignature] = occurrence + 1;

        unchecked
        {
            int foldedStructuralSignature = (int)structuralSignature ^ (int)(structuralSignature >> 32);
            // Source indices shift whenever CpuQueryAsync changes the visible mesh
            // subset, which previously changed every key and defeated secondary
            // reuse. Structural identity plus only the duplicate occurrence remains
            // stable across those visibility changes; mutable draw data is refreshed
            // by the existing FrameDataOnly reuse path.
            int ordinal = HashCode.Combine(foldedStructuralSignature, occurrence);
            return ordinal == -1 ? int.MaxValue : ordinal;
        }
    }

    /// <summary>
    /// Identifies the immutable descriptor-set variant baked into a secondary
    /// command buffer. Captured frame-source resources use separate descriptor
    /// allocations, so retaining one chain per exact allocation avoids both an
    /// illegal same-handle descriptor rewrite and a full scene re-record when a
    /// temporal or shadow resource variant becomes active again.
    /// </summary>
    private static ulong ResolveCommandChainDescriptorBindingVariant(
        in DescriptorBindingSnapshot snapshot)
    {
        FrameOpSignatureHasher hash = new();
        hash.Add(snapshot.DescriptorGeneration);
        hash.Add(snapshot.DescriptorSetSignature);
        hash.Add(snapshot.DescriptorSetCount);
        return hash.ToHash();
    }

    private void ValidateParallelRenderPacketBuild(
        FrameOp[] staticOps,
        FrameOp[] volatileOps,
        ulong resourcePlanRevision,
        List<RenderPacket> parallelPackets)
    {
        List<RenderPacket> sequential = new(staticOps.Length + volatileOps.Length);
        LowerFrameOpsToRenderPackets(staticOps, dynamicOverlay: false, resourcePlanRevision, sequential);
        LowerFrameOpsToRenderPackets(volatileOps, dynamicOverlay: true, resourcePlanRevision, sequential);
        if (sequential.Count != parallelPackets.Count)
            throw new InvalidOperationException($"Parallel command-chain packet build produced {parallelPackets.Count} packets; sequential produced {sequential.Count}.");

        for (int i = 0; i < sequential.Count; i++)
            ValidateRenderPacketEquivalent(sequential[i], parallelPackets[i], i);
    }

    private static void ValidateRenderPacketEquivalent(RenderPacket expected, RenderPacket actual, int index)
    {
        if (expected.ViewKey != actual.ViewKey ||
            expected.PassIndex != actual.PassIndex ||
            expected.TargetIdentity != actual.TargetIdentity ||
            !string.Equals(expected.TargetName, actual.TargetName, StringComparison.Ordinal) ||
            expected.Volatility != actual.Volatility ||
            expected.StructuralSignature != actual.StructuralSignature ||
            expected.FrameDataSignature != actual.FrameDataSignature ||
            expected.SourceStartIndex != actual.SourceStartIndex ||
            expected.SourceCount != actual.SourceCount ||
            expected.DynamicOverlay != actual.DynamicOverlay ||
            expected.DrawCount != actual.DrawCount ||
            expected.DispatchCount != actual.DispatchCount)
        {
            throw new InvalidOperationException($"Parallel command-chain packet build mismatch at packet {index}.");
        }
    }

    internal static RenderPacketVolatility ClassifyRenderPacketVolatility(FrameOp op, bool dynamicOverlay)
    {
        if (dynamicOverlay || IsUiBatchTextDrawOp(op))
            return RenderPacketVolatility.DynamicCommand;

        // Late debug geometry uses ordinary mesh draws whose vertex/frame data may
        // change while their command topology remains cacheable. Keeping these two
        // draws inline made every camera update re-record the entire mixed primary,
        // and allowed their mutable state to contaminate unrelated render passes.
        if (IsReusableLateDebugOverlayDraw(op))
            return RenderPacketVolatility.FrameDataOnly;

        if (IsOverlayLikePass(op))
            return RenderPacketVolatility.DynamicCommand;

        return op switch
        {
            MeshDrawOp => RenderPacketVolatility.FrameDataOnly,
            ClearOp => RenderPacketVolatility.StaticStructural,
            BlitOp => RenderPacketVolatility.StaticStructural,
            IndirectDrawOp => RenderPacketVolatility.FrameDataOnly,
            MeshTaskDispatchIndirectCountOp => RenderPacketVolatility.FrameDataOnly,
            ComputeDispatchOp => RenderPacketVolatility.FrameDataOnly,
            ComputeDispatchIndirectOp => RenderPacketVolatility.DynamicCommand,
            BufferCopyOp => RenderPacketVolatility.DynamicCommand,
            SubmissionMarkerOp => RenderPacketVolatility.DynamicCommand,
            MemoryBarrierOp => RenderPacketVolatility.StaticStructural,
            PublishFramebufferForSamplingOp => RenderPacketVolatility.StaticStructural,
            TransformFeedbackOp => RenderPacketVolatility.DynamicCommand,
            DlssUpscaleOp => RenderPacketVolatility.DynamicCommand,
            DlssFrameGenerationOp => RenderPacketVolatility.DynamicCommand,
            TextureUploadFrameOp => RenderPacketVolatility.DynamicCommand,
            _ => RenderPacketVolatility.StructuralDirty,
        };
    }

    /// <summary>
    /// Returns whether the primary recorder can execute this frame op through a
    /// scheduled command-chain secondary. Other op kinds keep their existing
    /// inline or dedicated-secondary paths and must not occupy this cache.
    /// </summary>
    internal static bool IsSchedulableCommandChainFrameOp(FrameOp op, bool dynamicOverlay)
        => !dynamicOverlay &&
            op is MeshDrawOp draw &&
            draw.Context.PipelineInstance?.Pipeline is not UserInterfaceRenderPipeline &&
            ClassifyRenderPacketVolatility(op, dynamicOverlay) == RenderPacketVolatility.FrameDataOnly;

    private static bool IsReusableLateDebugOverlayDraw(FrameOp op)
        => op is MeshDrawOp &&
            string.Equals(
                TryGetPassName(op),
                "LateDebugOverlay",
                StringComparison.Ordinal);

    private static bool IsOverlayLikePass(FrameOp op)
    {
        string? name = TryGetPassName(op);
        return !string.IsNullOrWhiteSpace(name) &&
            (name.Contains("UI", StringComparison.OrdinalIgnoreCase) ||
             name.Contains("Overlay", StringComparison.OrdinalIgnoreCase) ||
             name.Contains("Profiler", StringComparison.OrdinalIgnoreCase) ||
             name.Contains("Gizmo", StringComparison.OrdinalIgnoreCase) ||
             name.Contains("ImGui", StringComparison.OrdinalIgnoreCase));
    }

    internal static RenderViewKey BuildRenderViewKey(FrameOp op, bool dynamicOverlay)
    {
        RenderViewKind kind = dynamicOverlay || IsOverlayLikePass(op)
            ? RenderViewKind.Overlay
            : ResolveRenderViewKind(op);
        int viewIndex = ResolveCommandChainViewIndex(op, kind);
        int lightIdentity = ResolveCommandChainLightIdentity(op, kind);
        int cascadeIndex = ResolveCommandChainCascadeIndex(op, kind);
        return new RenderViewKey(
            op.Context.PipelineIdentity,
            op.Context.ViewportIdentity,
            viewIndex,
            kind,
            lightIdentity,
            cascadeIndex);
    }

    private static RenderViewKind ResolveRenderViewKind(FrameOp op)
    {
        if (op is MeshDrawOp { Draw: var draw })
        {
            // Shadow render graphs often reuse generic pass names such as
            // DepthPrePass. The captured draw state is the authoritative signal;
            // classifying those draws as Main prevented stable cascade runs from
            // being grouped and conflated shadow and camera-view cache families.
            if (draw.ShadowUniformState.IsShadowPass)
                return RenderViewKind.Shadow;

            if (draw.IsStereoPass ||
                draw.Camera?.StereoEyeLeft.HasValue == true ||
                draw.StereoRightEyeCamera is not null)
            {
                return RenderViewKind.VREye;
            }
        }

        string? passName = TryGetPassName(op);
        if (passName is not null)
        {
            if (passName.Contains("Shadow", StringComparison.OrdinalIgnoreCase))
                return RenderViewKind.Shadow;
            if (passName.Contains("Reflection", StringComparison.OrdinalIgnoreCase))
                return RenderViewKind.Reflection;
            if (passName.Contains("Probe", StringComparison.OrdinalIgnoreCase))
                return RenderViewKind.Probe;
        }

        return RenderViewKind.Main;
    }

    private static int ResolveCommandChainViewIndex(FrameOp op, RenderViewKind kind)
    {
        if (kind == RenderViewKind.VREye && op is MeshDrawOp { Draw: var draw })
            return ResolveStereoViewIndex(draw);

        if (kind == RenderViewKind.Shadow && op is MeshDrawOp { Draw: { ShadowUniformState: var shadowState } })
        {
            if (shadowState.DirectionalCascadeInstancedLayeredShadowPass)
                return Math.Max(0, shadowState.DirectionalCascadeShadowLayerCount - 1);
            if (shadowState.PointLightInstancedLayeredShadowPass)
                return Math.Max(0, shadowState.PointLightShadowFaceCount - 1);
        }

        return 0;
    }

    private static int ResolveStereoViewIndex(in PendingMeshDraw draw)
    {
        if (draw.IsStereoPass)
            return CommandChainStereoMultiviewViewIndex;

        bool? cameraEyeLeft = draw.Camera?.StereoEyeLeft;
        if (cameraEyeLeft.HasValue)
            return cameraEyeLeft.Value ? CommandChainLeftEyeViewIndex : CommandChainRightEyeViewIndex;

        if (draw.StereoRightEyeCamera is not null && ReferenceEquals(draw.Camera, draw.StereoRightEyeCamera))
            return CommandChainRightEyeViewIndex;

        return CommandChainLeftEyeViewIndex;
    }

    private static int ResolveCommandChainLightIdentity(FrameOp op, RenderViewKind kind)
    {
        if (kind != RenderViewKind.Shadow)
            return 0;

        int identity = HashCode.Combine(
            op.Context.SchedulingIdentity,
            ResolveCommandChainTargetIdentity(op));
        return identity == 0 ? 1 : identity;
    }

    private static int ResolveCommandChainCascadeIndex(FrameOp op, RenderViewKind kind)
    {
        if (kind != RenderViewKind.Shadow)
            return -1;

        if (op is MeshDrawOp { Draw: { ShadowUniformState: var shadowState } })
        {
            if (shadowState.DirectionalCascadeInstancedLayeredShadowPass)
                return Math.Max(0, shadowState.DirectionalCascadeShadowLayerCount - 1);
            if (shadowState.PointLightInstancedLayeredShadowPass)
                return Math.Max(0, shadowState.PointLightShadowFaceCount - 1);
        }

        return Math.Max(0, op.PassIndex);
    }

    private static string? TryGetPassName(FrameOp op)
    {
        IReadOnlyCollection<RenderPassMetadata>? passMetadata = op.Context.PassMetadata;
        if (passMetadata is null)
            return null;

        if (passMetadata is IReadOnlyList<RenderPassMetadata> passList)
        {
            for (int i = 0; i < passList.Count; i++)
            {
                RenderPassMetadata pass = passList[i];
                if (pass.PassIndex == op.PassIndex)
                    return pass.Name;
            }

            return null;
        }

        foreach (RenderPassMetadata pass in passMetadata)
            if (pass.PassIndex == op.PassIndex)
                return pass.Name;

        return null;
    }

    private static string ResolvePassName(IReadOnlyCollection<RenderPassMetadata>? passMetadata, int passIndex)
    {
        if (passMetadata is null)
            return "<unknown>";

        if (passMetadata is IReadOnlyList<RenderPassMetadata> passList)
        {
            for (int i = 0; i < passList.Count; i++)
            {
                RenderPassMetadata pass = passList[i];
                if (pass.PassIndex == passIndex)
                    return pass.Name;
            }

            return "<unknown>";
        }

        foreach (RenderPassMetadata pass in passMetadata)
            if (pass.PassIndex == passIndex)
                return pass.Name;

        return "<unknown>";
    }

    internal static int ResolveCommandChainTargetIdentity(FrameOp op)
        => op switch
        {
            BlitOp blit => blit.OutFbo?.GetHashCode() ?? op.Context.OutputTargetIdentity,
            _ => op.Target?.GetHashCode() ?? op.Context.OutputTargetIdentity,
        };

    internal static string ResolveCommandChainTargetName(FrameOp op)
        => op switch
        {
            BlitOp blit => blit.OutFbo?.Name ?? op.Context.OutputTargetName ?? "<swapchain>",
            _ => op.Target?.Name ?? op.Context.OutputTargetName ?? "<swapchain>",
        };

    private static ulong ResolvePipelineGeneration(FrameOp op)
    {
        FrameOpSignatureHasher hash = new();
        hash.Add(op.Context.PipelineIdentity);

        switch (op)
        {
            case MeshDrawOp draw:
                AddProgramGeneration(ref hash, draw.Draw.PreparedProgram);
                break;
            case IndirectDrawOp indirect:
                AddProgramGeneration(ref hash, indirect.Draw.PreparedProgram);
                AddProgramGeneration(ref hash, indirect.BindlessMaterialTextures?.Program);
                break;
            case MeshTaskDispatchIndirectCountOp meshTask:
                AddProgramGeneration(ref hash, meshTask.BindlessMaterialTextures?.Program);
                break;
            case ComputeDispatchOp compute:
                AddProgramGeneration(ref hash, compute.Program);
                break;
            case ComputeDispatchIndirectOp computeIndirect:
                AddProgramGeneration(ref hash, computeIndirect.Program);
                break;
        }

        return hash.ToHash();
    }

    private static void AddProgramGeneration(
        ref FrameOpSignatureHasher hash,
        VkRenderProgram? program)
    {
        hash.Add(program?.BindingId ?? 0u);
        hash.Add(program?.LinkGeneration ?? 0UL);
    }

    private static DescriptorBindingSnapshot CreateDescriptorSnapshot(FrameOp op)
    {
        return op switch
        {
            MeshDrawOp draw => CreateMeshDrawDescriptorSnapshot(draw),
            ComputeDispatchOp compute => CreateComputeDispatchDescriptorSnapshot(compute),
            IndirectDrawOp indirect => CreateDescriptorSnapshotFromSignature(unchecked((ulong)(indirect.BindlessMaterialTextures?.Program.GetHashCode() ?? 0))),
            MeshTaskDispatchIndirectCountOp meshTask => CreateDescriptorSnapshotFromSignature(unchecked((ulong)(meshTask.BindlessMaterialTextures?.Program.GetHashCode() ?? 0))),
            _ => default,
        };
    }

    private static DescriptorBindingSnapshot CreateMeshDrawDescriptorSnapshot(MeshDrawOp draw)
    {
        bool hasMutableFrameSourceBindings =
            draw.Draw.ProgramBindingSnapshot is
                { HasMutableFrameSourceSamplerBindings: true };
        if (!hasMutableFrameSourceBindings &&
            draw.TryGetDescriptorBindingSnapshot(out DescriptorBindingSnapshot cached))
        {
            return cached;
        }

        ulong descriptorGeneration = 0UL;
        ulong descriptorSetSignature = 0UL;
        int setCount = 0;

        if (draw.Draw.ProgramBindingSnapshot is { } snapshot)
        {
            descriptorGeneration = ComputeDispatchSnapshotSignature(snapshot);
            descriptorSetSignature = ComputeDispatchSnapshotDescriptorSetSignature(snapshot);
            setCount = descriptorSetSignature == 0 ? 0 : 1;
        }

        XRMaterial? material = draw.Draw.MaterialOverride ?? draw.Draw.Renderer.MeshRenderer.Material;
        if (material is not null)
        {
            ulong descriptorResourceSignature = draw.Draw.Renderer.ComputeRecordedDescriptorResourceSignature(
                material,
                draw.Draw.PreparedProgram,
                draw.Draw.ProgramBindingSnapshot);
            if (descriptorResourceSignature != 0UL)
                descriptorGeneration = MixSignature(descriptorGeneration, descriptorResourceSignature);
        }

        ulong descriptorSchemaSignature = draw.Draw.Renderer.ComputeRecordedDescriptorSchemaSignature(draw.Draw.PreparedProgram);
        if (descriptorSchemaSignature != 0UL)
            descriptorSetSignature = MixSignature(descriptorSetSignature, descriptorSchemaSignature);

        if (draw.Draw.PreparedProgram is { } preparedProgram)
            descriptorSetSignature = MixSignature(descriptorSetSignature, preparedProgram.BindingId);

        int recordedSetCount = draw.Draw.Renderer.GetRecordedDescriptorSetCount(draw.Draw.PreparedProgram);
        if (recordedSetCount > setCount)
            setCount = recordedSetCount;

        DescriptorBindingSnapshot descriptorSnapshot = new(
            descriptorGeneration,
            setCount,
            descriptorSetSignature);
        if (!hasMutableFrameSourceBindings)
            draw.SetDescriptorBindingSnapshot(descriptorSnapshot);
        return descriptorSnapshot;
    }

    private static DescriptorBindingSnapshot CreateComputeDispatchDescriptorSnapshot(ComputeDispatchOp compute)
    {
        ulong descriptorGeneration = ComputeDispatchSnapshotSignature(compute.Snapshot);
        ulong descriptorSetSignature = ComputeDispatchSnapshotDescriptorSetSignature(compute.Snapshot);
        int setCount = descriptorSetSignature == 0 ? 0 : 1;
        return new DescriptorBindingSnapshot(descriptorGeneration, setCount, descriptorSetSignature);
    }

    private static DescriptorBindingSnapshot CreateDescriptorSnapshotFromSignature(ulong signature)
    {
        int setCount = signature == 0 ? 0 : 1;
        return new DescriptorBindingSnapshot(signature, setCount, signature);
    }

    private static DrawPacket CreateDrawPacket(int opIndex, MeshDrawOp op)
    {
        XRMaterial? material = op.Draw.MaterialOverride ?? op.Draw.Renderer.MeshRenderer.Material;
        int meshIdentity = op.Draw.Renderer.MeshRenderer.Mesh?.GetHashCode() ?? 0;
        int materialIdentity = material?.GetHashCode() ?? 0;
        int programIdentity = op.Draw.PreparedProgram is { } preparedProgram
            ? unchecked((int)preparedProgram.BindingId)
            : material?.RenderOptions?.GetHashCode() ?? materialIdentity;
        return new DrawPacket(
            opIndex,
            op.Draw.Renderer.GetHashCode(),
            meshIdentity,
            materialIdentity,
            programIdentity,
            op.Draw.Instances,
            op.Draw.BlendEnabled,
            ComputeFrameOpStructuralSignature(op, opIndex, RenderPacketVolatility.FrameDataOnly),
            ComputeFrameOpFrameDataSignature(op, opIndex));
    }

    private static DrawPacket CreateIndirectDrawPacket(int opIndex, IndirectDrawOp op)
        => new(
            opIndex,
            op.IndirectBuffer.GetHashCode(),
            unchecked((int)ComputeCommandBufferDataBufferSignature(op.IndirectBuffer)),
            unchecked((int)ComputeCommandBufferDataBufferSignature(op.ParameterBuffer)),
            op.BindlessMaterialTextures?.Program.GetHashCode() ?? 0,
            op.DrawCount,
            false,
            ComputeFrameOpStructuralSignature(op, opIndex, RenderPacketVolatility.FrameDataOnly),
            ComputeFrameOpFrameDataSignature(op, opIndex));

    private static DrawPacket CreateMeshTaskDrawPacket(int opIndex, MeshTaskDispatchIndirectCountOp op)
        => new(
            opIndex,
            op.IndirectBuffer.GetHashCode(),
            unchecked((int)ComputeCommandBufferDataBufferSignature(op.CountBuffer)),
            0,
            op.BindlessMaterialTextures?.Program.GetHashCode() ?? 0,
            op.MaxDrawCount,
            false,
            ComputeFrameOpStructuralSignature(op, opIndex, RenderPacketVolatility.FrameDataOnly),
            ComputeFrameOpFrameDataSignature(op, opIndex));

    private static DispatchPacket CreateDispatchPacket(int opIndex, ComputeDispatchOp op)
        => new(
            opIndex,
            op.Program.GetHashCode(),
            op.GroupsX,
            op.GroupsY,
            op.GroupsZ,
            ComputeFrameOpStructuralSignature(op, opIndex, RenderPacketVolatility.FrameDataOnly),
            ComputeFrameOpFrameDataSignature(op, opIndex));

    private static int ResolveCommandChainInlineOperationIndex(FrameOp[] ops, int sourceIndex)
    {
        int inlineOpIndex = 0;
        int queryBracketDepth = 0;
        int lastIndex = Math.Min(sourceIndex, ops.Length - 1);
        for (int opIndex = 0; opIndex <= lastIndex; opIndex++)
        {
            FrameOp op = ops[opIndex];
            bool isQuery = op is QueryOp;
            bool secondaryOwned =
                !isQuery &&
                queryBracketDepth == 0 &&
                IsSchedulableCommandChainFrameOp(op, dynamicOverlay: false);

            if (opIndex == sourceIndex)
                return inlineOpIndex;

            if (!secondaryOwned)
                inlineOpIndex++;

            if (op is QueryOp queryOp)
            {
                if (queryOp.Operation == ERenderQueryOperation.Begin)
                    queryBracketDepth++;
                else if (queryOp.Operation == ERenderQueryOperation.End && queryBracketDepth > 0)
                    queryBracketDepth--;
            }
        }

        return Math.Max(sourceIndex, 0);
    }

    private static ulong ComputeReusableComputeDescriptorBindingKey(
        ComputeDispatchOp op,
        int descriptorBindingOrdinal)
    {
        FrameOpSignatureHasher hash = new();
        hash.Add(0x434F4D5055444553UL);
        hash.Add(descriptorBindingOrdinal);
        hash.Add(op.PassIndex);
        hash.Add(ResolveCommandChainTargetIdentity(op));
        hash.Add(op.Context.PipelineIdentity);
        hash.Add(op.Context.ViewportIdentity);
        hash.Add(op.Program.BindingId);
        hash.Add(op.Program.LinkGeneration);
        hash.Add(op.GroupsX);
        hash.Add(op.GroupsY);
        hash.Add(op.GroupsZ);
        // Snapshot resources are descriptor contents, not command topology. A
        // stable per-dispatch set handle lets UPDATE_AFTER_BIND programs refresh
        // rotating render targets without rebuilding the thin primary. Ordinary
        // descriptor writes remain safe because exact dependency tracking dirties
        // every command buffer that recorded a non-update-after-bind set.
        return hash.ToHash();
    }

    private static ulong ComputeFrameOpStructuralSignature(FrameOp op, int opIndex, RenderPacketVolatility volatility)
    {
        FrameOpSignatureHasher hash = new();
        hash.Add(GetFrameOpKindId(op));
        hash.Add(op.PassIndex);
        hash.Add(ResolveCommandChainTargetIdentity(op));
        hash.Add(op.Context.PipelineIdentity);
        hash.Add(op.Context.ViewportIdentity);
        hash.Add((int)volatility);

        switch (op)
        {
            case MeshDrawOp draw:
                RenderViewKind drawKind = ResolveRenderViewKind(draw);
                hash.Add((int)drawKind);
                hash.Add(ResolveCommandChainViewIndex(draw, drawKind));
                hash.Add(ResolveCommandChainLightIdentity(draw, drawKind));
                hash.Add(ResolveCommandChainCascadeIndex(draw, drawKind));
                hash.Add(draw.Draw.Renderer.GetHashCode());
                hash.Add(draw.Draw.MaterialOverride?.GetHashCode() ?? 0);
                hash.Add(draw.Draw.Instances);
                hash.Add(draw.Draw.BlendEnabled);
                hash.Add(draw.Draw.AlphaToCoverageEnabled);
                hash.Add((int)draw.Draw.ColorBlendOp);
                hash.Add((int)draw.Draw.AlphaBlendOp);
                hash.Add((int)draw.Draw.SrcColorBlendFactor);
                hash.Add((int)draw.Draw.DstColorBlendFactor);
                hash.Add((int)draw.Draw.SrcAlphaBlendFactor);
                hash.Add((int)draw.Draw.DstAlphaBlendFactor);
                hash.Add((int)draw.Draw.ColorWriteMask);
                hash.Add((int)draw.Draw.CullMode);
                hash.Add((int)draw.Draw.FrontFace);
                hash.Add((int)draw.Draw.RasterizationSamples);
                hash.Add(draw.Draw.DepthTestEnabled);
                hash.Add(draw.Draw.DepthWriteEnabled);
                hash.Add((int)draw.Draw.DepthCompareOp);
                hash.Add(draw.Draw.StencilTestEnabled);
                hash.Add(draw.Draw.StencilWriteMask);
                AddViewportScissorSignature(ref hash, draw.Draw);
                hash.Add(draw.Draw.PreparedProgramIdentity);
                hash.Add(draw.Draw.PreparedProgram?.BindingId ?? 0u);
                hash.Add(ComputeShadowCommandChainStructuralSignature(draw.Draw.ShadowUniformState));
                break;
            case QueryOp query:
                hash.Add(query.Query.GetHashCode());
                hash.Add(query.Descriptor.GetHashCode());
                hash.Add(query.Query.Ticket.PoolIdentity);
                hash.Add(query.Query.Ticket.FirstQuery);
                hash.Add(query.Query.Ticket.QueryCount);
                hash.Add((int)query.Operation);
                hash.Add((ulong)query.TimestampStage);
                hash.Add(query.PointIndex);
                hash.Add(query.SourceHandles.Length);
                hash.Add(query.ResultDestination.Handle);
                hash.Add(query.ResultDestinationOffset);
                hash.Add(query.ResultStride);
                hash.Add(query.IncludeAvailability);
                break;
            case ClearOp clear:
                hash.Add(clear.ClearColor);
                hash.Add(clear.ClearDepth);
                hash.Add(clear.ClearStencil);
                break;
            case BlitOp blit:
                hash.Add(blit.InFbo?.GetHashCode() ?? 0);
                hash.Add(blit.OutFbo?.GetHashCode() ?? 0);
                hash.Add(blit.ColorBit);
                hash.Add(blit.DepthBit);
                hash.Add(blit.StencilBit);
                break;
            case PublishFramebufferForSamplingOp publish:
                hash.Add(publish.FrameBuffer.GetHashCode());
                break;
            case IndirectDrawOp indirect:
                hash.Add(ComputeCommandBufferDataBufferSignature(indirect.IndirectBuffer));
                hash.Add(ComputeCommandBufferDataBufferSignature(indirect.ParameterBuffer));
                hash.Add(indirect.DrawCount);
                hash.Add(indirect.Stride);
                hash.Add(indirect.ByteOffset);
                hash.Add(indirect.CountByteOffset);
                hash.Add(indirect.UseCount);
                hash.Add(
                    (int)indirect.SecondaryRecordingContract.Eligibility);
                break;
            case MeshTaskDispatchIndirectCountOp meshTask:
                hash.Add(ComputeCommandBufferDataBufferSignature(meshTask.IndirectBuffer));
                hash.Add(ComputeCommandBufferDataBufferSignature(meshTask.CountBuffer));
                hash.Add(meshTask.MaxDrawCount);
                hash.Add(meshTask.Stride);
                break;
            case ComputeDispatchOp compute:
                hash.Add(compute.Program.GetHashCode());
                hash.Add(compute.GroupsX);
                hash.Add(compute.GroupsY);
                hash.Add(compute.GroupsZ);
                break;
            case TextureUploadFrameOp upload:
                hash.Add(upload.Upload.PublicationToken);
                hash.Add(upload.Upload.Request.StreamingGeneration);
                hash.Add(upload.Upload.Image.Handle);
                hash.Add(upload.Upload.ImageView.Handle);
                hash.Add(upload.Upload.Sampler.Handle);
                hash.Add(upload.Upload.Extent.Width);
                hash.Add(upload.Upload.Extent.Height);
                hash.Add(upload.Upload.MipLevels);
                hash.Add((ulong)Math.Max(upload.Upload.CommittedBytes, 0L));
                hash.Add(upload.Upload.StagingResources.Length);
                break;
            default:
                hash.Add(opIndex);
                break;
        }

        return hash.ToHash();
    }
}
