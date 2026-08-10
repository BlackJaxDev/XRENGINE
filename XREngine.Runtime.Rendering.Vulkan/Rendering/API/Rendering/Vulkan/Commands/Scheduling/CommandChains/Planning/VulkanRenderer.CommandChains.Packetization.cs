using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Silk.NET.Vulkan;
using XREngine.Components.Lights;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.Shadows;

namespace XREngine.Rendering.Vulkan;

internal sealed unsafe partial class VulkanCommandRuntime
{
    private void BuildCommandChainRenderPackets(
        uint targetImageIndex,
        FrameOperationStream staticOps,
        FrameOperationStream volatileOps,
        ulong resourcePlanRevision,
        bool excludeStaticQueryBrackets,
        List<RenderPacket> packets,
        in VulkanRecordedRenderTargetSnapshot preparedRecordingTarget)
    {
        bool profileDetail =
            VulkanMeshRenderingConventions.CommandRecordingDetailProfilingEnabled;
        // Packet lowering is deliberately deterministic and allocation-free on a
        // schedule-cache hit. Parallelizing this cheap classification previously
        // allocated two exact-length arrays and captured two closures every time
        // visibility changed; actual Vulkan recording belongs on the persistent
        // command-chain workers instead.
        if (excludeStaticQueryBrackets)
            LowerFrameOpsToRenderPacketsExcludingQueryBrackets(targetImageIndex, staticOps, resourcePlanRevision, packets, preparedRecordingTarget, profileDetail);
        else
            LowerFrameOpsToRenderPackets(targetImageIndex, staticOps, dynamicOverlay: false, resourcePlanRevision, packets, preparedRecordingTarget, profileDetail);
        LowerFrameOpsToRenderPackets(targetImageIndex, volatileOps, dynamicOverlay: true, resourcePlanRevision, packets, preparedRecordingTarget, profileDetail);
    }

    private void LowerFrameOpsToRenderPacketsExcludingQueryBrackets(
        uint targetImageIndex,
        FrameOperationStream ops,
        ulong resourcePlanRevision,
        List<RenderPacket> packets,
        in VulkanRecordedRenderTargetSnapshot preparedRecordingTarget,
        bool profileDetail)
    {
        int queryBracketDepth = 0;
        for (int i = 0; i < ops.Count; i++)
        {
            ref readonly FrameOperationHeader header = ref ops.GetHeader(i);
            if (header.OpCode == EVulkanPrimaryPlanNodeKind.Query)
            {
                QueryOp queryOp = (QueryOp)ops.GetPayloadForPrimaryDispatch(i);
                if (queryOp.Operation == ERenderQueryOperation.Begin)
                    queryBracketDepth++;
                else if (queryOp.Operation == ERenderQueryOperation.End && queryBracketDepth > 0)
                    queryBracketDepth--;
                continue;
            }

            if (queryBracketDepth == 0)
            {
                int consumed = TryLowerCompatibleMeshPacket(
                    targetImageIndex,
                    ops,
                    i,
                    dynamicOverlay: false,
                    resourcePlanRevision,
                    packets,
                    preparedRecordingTarget,
                    profileDetail,
                    out DrawPacket preparedMeshDraw);
                if (consumed > 0)
                    i += consumed - 1;
                else if (IsSchedulableCommandChainFrameOp(ops, i, dynamicOverlay: false))
                    packets.Add(CreateRenderPacket(
                        targetImageIndex, ops, i, dynamicOverlay: false, resourcePlanRevision, preparedMeshDraw, preparedRecordingTarget));
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
        uint targetImageIndex,
        FrameOperationStream ops,
        bool dynamicOverlay,
        ulong resourcePlanRevision,
        List<RenderPacket> packets,
        in VulkanRecordedRenderTargetSnapshot preparedRecordingTarget,
        bool profileDetail)
    {
        for (int i = 0; i < ops.Count; i++)
        {
            int consumed = TryLowerCompatibleMeshPacket(
                targetImageIndex,
                ops,
                i,
                dynamicOverlay,
                resourcePlanRevision,
                packets,
                preparedRecordingTarget,
                profileDetail,
                out DrawPacket preparedMeshDraw);
            if (consumed > 0)
                i += consumed - 1;
            else if (IsSchedulableCommandChainFrameOp(ops, i, dynamicOverlay))
                packets.Add(CreateRenderPacket(
                    targetImageIndex, ops, i, dynamicOverlay, resourcePlanRevision, preparedMeshDraw, preparedRecordingTarget));
        }
    }

    private int TryLowerCompatibleMeshPacket(
        uint targetImageIndex,
        FrameOperationStream ops,
        int startIndex,
        bool dynamicOverlay,
        ulong resourcePlanRevision,
        List<RenderPacket> packets,
        in VulkanRecordedRenderTargetSnapshot preparedRecordingTarget,
        bool profileDetail,
        out DrawPacket preparedMeshDraw)
    {
        preparedMeshDraw = default;
        if (!IsSchedulableCommandChainFrameOp(ops, startIndex, dynamicOverlay) ||
            ops.GetHeader(startIndex).OpCode != EVulkanPrimaryPlanNodeKind.MeshDraw)
            return 0;
        MeshDrawOp first = (MeshDrawOp)ops.GetPayloadForPrimaryDispatch(startIndex);

        DrawPacket firstDraw;
        RenderViewKey viewKey;
        int targetIdentity;
        int runCount;
        using (VulkanCpuStageScope compatibilityStage = new(
                   _frameTelemetry,
                   EVulkanCpuStage.CommandChainCompatibilityScan,
                   profileDetail))
        {
            firstDraw = CreateDrawPacket(startIndex, first);
            preparedMeshDraw = firstDraw;
            _commandChainDrawPacketScratch[0] = firstDraw;
            viewKey = BuildRenderViewKey(first, dynamicOverlay: false);
            targetIdentity = ResolveCommandChainTargetIdentity(first);
            DescriptorBindingSnapshot firstDescriptorSnapshot =
                CreateDescriptorSnapshot(first);
            runCount = 1;
            int packetDrawLimit = viewKey.Kind == RenderViewKind.Shadow
                ? MaxShadowMeshDrawsPerRenderPacket
                : MaxMeshDrawsPerRenderPacket;
            int available = Math.Min(ops.Count - startIndex, packetDrawLimit);
            while (runCount < available &&
                   ops.GetHeader(startIndex + runCount).OpCode == EVulkanPrimaryPlanNodeKind.MeshDraw &&
                   IsMeshDrawPacketCompatible(
                       first,
                       firstDraw,
                       viewKey,
                       targetIdentity,
                       firstDescriptorSnapshot,
                       (MeshDrawOp)ops.GetPayloadForPrimaryDispatch(startIndex + runCount),
                       startIndex + runCount,
                       out DrawPacket candidateDraw))
            {
                _commandChainDrawPacketScratch[runCount] = candidateDraw;
                runCount++;
            }
        }

        int compatibleRunCount = runCount;
        using (VulkanCpuStageScope capacityStage = new(
                   _frameTelemetry,
                   EVulkanCpuStage.CommandChainCapacityPlanning,
                   profileDetail))
        {
            runCount = LimitMeshPacketToRecordedIdentityCapacity(
                ops,
                startIndex,
                compatibleRunCount);
        }
        bool identityCapacityLimited = runCount < compatibleRunCount;
        if (runCount < MinMeshDrawsPerRenderPacket &&
            (!identityCapacityLimited || runCount <= 1))
            return 0;

        Span<DrawPacket> draws = _commandChainDrawPacketScratch.AsSpan(0, runCount);
        FrameOpSignatureHasher structuralHash = new();
        FrameOpSignatureHasher frameDataHash = new();
        FrameOpSignatureHasher descriptorGenerationHash = new();
        FrameOpSignatureHasher descriptorSetHash = new();
        FrameOpSignatureHasher pipelineGenerationHash = new();
        int descriptorSetCount = 0;
        bool hasDescriptorBindings = false;
        using (VulkanCpuStageScope dependencyStage = new(
                   _frameTelemetry,
                   EVulkanCpuStage.CommandChainDependencyAggregation,
                   profileDetail))
        {
            for (int i = 0; i < runCount; i++)
            {
                MeshDrawOp drawOp = (MeshDrawOp)ops.GetPayloadForPrimaryDispatch(startIndex + i);
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
        }

        DescriptorBindingSnapshot descriptorSnapshot = hasDescriptorBindings
            ? new DescriptorBindingSnapshot(
                descriptorGenerationHash.ToHash(),
                descriptorSetCount,
                descriptorSetHash.ToHash())
            : default;

        string targetName = ResolveCommandChainTargetName(first);
        VulkanRecordedRenderTargetSnapshot nativeTarget =
            CaptureRecordedRenderTargetSnapshot(first, preparedRecordingTarget);
        ResourcePlanSnapshot resourceSnapshot = new(
            resourcePlanRevision,
            nativeTarget.AttachmentCount > 0
                ? nativeTarget.GetAttachment(0).ImageGeneration
                : 0UL,
            nativeTarget.FramebufferGeneration,
            pipelineGenerationHash.ToHash(),
            ResourcePlanSnapshot.PackRenderArea(
                nativeTarget.Width,
                nativeTarget.Height),
            first.ContextReference.SubmissionQueueFamily,
            nativeTarget);
        RenderPacket packet = RentRenderPacket();
        packet.Reset(
            GetActiveCommandChainPacketPayloadArena(),
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
        using (VulkanCpuStageScope recordedKeyStage = new(
                   _frameTelemetry,
                   EVulkanCpuStage.CommandChainRecordedKeyCapture,
                   profileDetail))
        {
            packet.SetRecordedPacketKey(CaptureRecordedPacketKey(
                ops,
                startIndex,
                runCount,
                nativeTarget,
                descriptorSnapshot,
                resourceSnapshot));
        }
        packet.Seal();
        packets.Add(packet);
        return runCount;
    }

    /// <summary>
    /// Limits a compatible mesh run before its exact inline native identities
    /// overflow. A capacity-limited prefix remains worth grouping even when it
    /// is smaller than the ordinary batching threshold; accepting an oversized
    /// packet would make its prepared key permanently incomplete and force the
    /// whole run back into primary inline recording every frame.
    /// </summary>
    private int LimitMeshPacketToRecordedIdentityCapacity(
        FrameOperationStream ops,
        int startIndex,
        int compatibleRunCount)
    {
        int vertexIdentityCount = 0;
        int auxiliaryIdentityCount = 0;
        int descriptorSetIdentityCount = 0;

        int programLimitedCount = Math.Min(
            compatibleRunCount,
            VulkanRecordedProgramIdentityBuffer.Capacity);
        for (int relativeIndex = 0;
             relativeIndex < programLimitedCount;
             relativeIndex++)
        {
            MeshDrawOp draw = (MeshDrawOp)ops.GetPayloadForPrimaryDispatch(
                startIndex + relativeIndex);
            draw.Draw.Renderer.GetRecordedBufferBindingCounts(
                out int vertexCount,
                out int indexCount);
            int descriptorSetCount = Math.Max(
                draw.Draw.ProgramBindingSnapshot is null ? 0 : 1,
                draw.Draw.Renderer.GetRecordedDescriptorSetCount(
                    draw.Draw.PreparedProgram));

            bool nextDrawFits = vertexIdentityCount <=
                    VulkanRecordedBufferIdentityBuffer.Capacity - vertexCount &&
                auxiliaryIdentityCount <=
                    VulkanRecordedBufferIdentityBuffer.Capacity - indexCount &&
                descriptorSetIdentityCount <=
                    VulkanRecordedDescriptorSetIdentityBuffer.Capacity -
                    descriptorSetCount;
            if (!nextDrawFits)
                return Math.Max(1, relativeIndex);

            vertexIdentityCount += vertexCount;
            auxiliaryIdentityCount += indexCount;
            descriptorSetIdentityCount += descriptorSetCount;
        }

        return programLimitedCount;
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

        // Multi-draw packets may switch graphics programs and descriptor layouts
        // per draw. Shadow packetization is currently capped at one draw because
        // cascade membership churn must not invalidate unrelated casters. Keep
        // the compatibility rule for an explicitly raised cap and retain the
        // main view's fine-grained program/descriptor grouping.
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

        ref readonly FrameOpContext candidateContext = ref candidate.ContextReference;
        ref readonly FrameOpContext firstContext = ref first.ContextReference;
        return FrameOpContextCompatibility.AreCommandChainBatchCompatible(
            in candidateContext,
            in firstContext);
    }

    private RenderPacket CreateRenderPacket(
        uint targetImageIndex,
        FrameOperationStream operations,
        int opIndex,
        bool dynamicOverlay,
        ulong resourcePlanRevision,
        DrawPacket preparedMeshDraw,
        in VulkanRecordedRenderTargetSnapshot preparedRecordingTarget)
    {
        ref readonly FrameOperationHeader header = ref operations.GetHeader(opIndex);
        FrameOp op = operations.GetPayloadForPrimaryDispatch(opIndex);
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
        int targetIdentity = header.TargetIdentity;
        string targetName = ResolveCommandChainTargetName(op);
        DescriptorBindingSnapshot descriptorSnapshot = CreateDescriptorSnapshot(op);
        VulkanRecordedRenderTargetSnapshot nativeTarget =
            CaptureRecordedRenderTargetSnapshot(op, preparedRecordingTarget);
        ResourcePlanSnapshot resourceSnapshot = new(
            resourcePlanRevision,
            nativeTarget.AttachmentCount > 0
                ? nativeTarget.GetAttachment(0).ImageGeneration
                : 0UL,
            nativeTarget.FramebufferGeneration,
            ResolvePipelineGeneration(op),
            ResourcePlanSnapshot.PackRenderArea(
                nativeTarget.Width,
                nativeTarget.Height),
            operations.GetContext(opIndex).SubmissionQueueFamily,
            nativeTarget);

        RenderPacket packet = RentRenderPacket();
        packet.Reset(
            GetActiveCommandChainPacketPayloadArena(),
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
        packet.SetRecordedPacketKey(CaptureRecordedPacketKey(
            op,
            nativeTarget,
            descriptorSnapshot,
            resourceSnapshot));
        packet.Seal();
        return packet;
    }

    private VulkanRecordedRenderTargetSnapshot CaptureRecordedRenderTargetSnapshot(
        FrameOp op,
        in VulkanRecordedRenderTargetSnapshot preparedRecordingTarget)
    {
        XRFrameBuffer? target = op.Target ?? op.ContextReference.OutputFrameBuffer;
        if (target is not null)
        {
            return ResourceRuntime.BackendObjects.Get(target) is VkFrameBuffer frameBuffer &&
                   frameBuffer.TryCaptureRecordedRenderTargetSnapshot(
                       out VulkanRecordedRenderTargetSnapshot explicitSnapshot)
                ? explicitSnapshot
                : default;
        }

        // Swapchain/OpenXR target identity is a producer-owned observation.
        // Scheduling consumes the frozen snapshot supplied with the frame plan;
        // it never reads output state or thread-local renderer context.
        return preparedRecordingTarget;
    }

    private RecordedPacketKey CaptureRecordedPacketKey(
        FrameOp op,
        in VulkanRecordedRenderTargetSnapshot nativeTarget,
        in DescriptorBindingSnapshot descriptorSnapshot,
        in ResourcePlanSnapshot resourceSnapshot)
    {
        Span<VulkanRecordedBufferIdentity> vertexScratch =
            stackalloc VulkanRecordedBufferIdentity[VulkanRecordedBufferIdentityBuffer.Capacity];
        Span<VulkanRecordedBufferIdentity> auxiliaryScratch =
            stackalloc VulkanRecordedBufferIdentity[VulkanRecordedBufferIdentityBuffer.Capacity];
        Span<VulkanRecordedProgramIdentity> programScratch =
            stackalloc VulkanRecordedProgramIdentity[VulkanRecordedProgramIdentityBuffer.Capacity];
        int vertexCount = 0;
        int auxiliaryCount = 0;
        bool vertexOverflow = false;
        bool auxiliaryOverflow = false;
        VulkanRecordedBufferIdentity indexBuffer = default;
        int programCount = 0;
        bool programOverflow = false;
        CaptureRecordedPacketOperationDependencies(
            op,
            ref indexBuffer,
            vertexScratch,
            ref vertexCount,
            ref vertexOverflow,
            auxiliaryScratch,
            ref auxiliaryCount,
            ref auxiliaryOverflow,
            programScratch,
            ref programCount,
            ref programOverflow);
        return CreateRecordedPacketKey(
            ResolveRenderPacketExecutionDomain(op),
            nativeTarget,
            descriptorSnapshot,
            resourceSnapshot,
            vertexScratch,
            vertexCount,
            vertexOverflow,
            auxiliaryScratch,
            auxiliaryCount,
            auxiliaryOverflow,
            indexBuffer,
            programScratch,
            programCount,
            programOverflow);
    }

    private RecordedPacketKey CaptureRecordedPacketKey(
        FrameOperationStream ops,
        int startIndex,
        int count,
        in VulkanRecordedRenderTargetSnapshot nativeTarget,
        in DescriptorBindingSnapshot descriptorSnapshot,
        in ResourcePlanSnapshot resourceSnapshot)
        => CaptureRecordedPacketKey(
            ops.GetEncoderPayloadRange(startIndex, count),
            nativeTarget,
            descriptorSnapshot,
            resourceSnapshot);

    private RecordedPacketKey CaptureRecordedPacketKey(
        FrameOp[] ops,
        int startIndex,
        int count,
        in VulkanRecordedRenderTargetSnapshot nativeTarget,
        in DescriptorBindingSnapshot descriptorSnapshot,
        in ResourcePlanSnapshot resourceSnapshot)
        => CaptureRecordedPacketKey(
            ops.AsSpan(startIndex, count),
            nativeTarget,
            descriptorSnapshot,
            resourceSnapshot);

    private RecordedPacketKey CaptureRecordedPacketKey(
        ReadOnlySpan<FrameOp> ops,
        in VulkanRecordedRenderTargetSnapshot nativeTarget,
        in DescriptorBindingSnapshot descriptorSnapshot,
        in ResourcePlanSnapshot resourceSnapshot)
    {
        Span<VulkanRecordedBufferIdentity> vertexScratch =
            stackalloc VulkanRecordedBufferIdentity[VulkanRecordedBufferIdentityBuffer.Capacity];
        Span<VulkanRecordedBufferIdentity> auxiliaryScratch =
            stackalloc VulkanRecordedBufferIdentity[VulkanRecordedBufferIdentityBuffer.Capacity];
        Span<VulkanRecordedProgramIdentity> programScratch =
            stackalloc VulkanRecordedProgramIdentity[VulkanRecordedProgramIdentityBuffer.Capacity];
        int vertexCount = 0;
        int auxiliaryCount = 0;
        bool vertexOverflow = false;
        bool auxiliaryOverflow = false;
        VulkanRecordedBufferIdentity indexBuffer = default;
        int programCount = 0;
        bool programOverflow = false;

        for (int i = 0; i < ops.Length; i++)
        {
            CaptureRecordedPacketOperationDependencies(
                ops[i],
                ref indexBuffer,
                vertexScratch,
                ref vertexCount,
                ref vertexOverflow,
                auxiliaryScratch,
                ref auxiliaryCount,
                ref auxiliaryOverflow,
                programScratch,
                ref programCount,
                ref programOverflow);
        }

        return CreateRecordedPacketKey(
            ops.Length == 0
                ? RenderPacketExecutionDomain.GraphicsRendering
                : ResolveRenderPacketExecutionDomain(ops[0]),
            nativeTarget,
            descriptorSnapshot,
            resourceSnapshot,
            vertexScratch,
            vertexCount,
            vertexOverflow,
            auxiliaryScratch,
            auxiliaryCount,
            auxiliaryOverflow,
            indexBuffer,
            programScratch,
            programCount,
            programOverflow);
    }

    private RecordedPacketKey CreateRecordedPacketKey(
        RenderPacketExecutionDomain executionDomain,
        in VulkanRecordedRenderTargetSnapshot nativeTarget,
        in DescriptorBindingSnapshot descriptorSnapshot,
        in ResourcePlanSnapshot resourceSnapshot,
        Span<VulkanRecordedBufferIdentity> vertexScratch,
        int vertexCount,
        bool vertexOverflow,
        Span<VulkanRecordedBufferIdentity> auxiliaryScratch,
        int auxiliaryCount,
        bool auxiliaryOverflow,
        in VulkanRecordedBufferIdentity indexBuffer,
        Span<VulkanRecordedProgramIdentity> programScratch,
        int programCount,
        bool programOverflow)
    {
        VulkanRecordedBufferIdentityBuffer vertexBuffers =
            FinalizeRecordedBufferIdentities(vertexScratch, vertexCount, vertexOverflow);
        VulkanRecordedBufferIdentityBuffer auxiliaryBuffers =
            FinalizeRecordedBufferIdentities(auxiliaryScratch, auxiliaryCount, auxiliaryOverflow);
        VulkanRecordedProgramIdentityBuffer programs =
            FinalizeRecordedProgramIdentities(programScratch, programCount, programOverflow);

        // Descriptor handles/payloads and concrete graphics pipelines are selected
        // during binding preparation. Never authorize reuse from these provisional
        // identities: a later prepared key replaces both incomplete fields.
        VulkanRecordedDescriptorSetIdentityBuffer descriptorSets = default;
        if (descriptorSnapshot.DescriptorSetCount == 0)
            descriptorSets.Initialize(0);

        return new RecordedPacketKey(
            executionDomain,
            nativeTarget,
            resourceSnapshot.RenderArea,
            resourceSnapshot.QueueFamily,
            descriptorSets,
            programs,
            indexBuffer,
            vertexBuffers,
            auxiliaryBuffers);
    }

    private static RenderPacketExecutionDomain ResolveRenderPacketExecutionDomain(
        FrameOp operation)
        => operation switch
        {
            ComputeDispatchOp or ComputeDispatchIndirectOp =>
                RenderPacketExecutionDomain.StandaloneCompute,
            MemoryBarrierOp =>
                RenderPacketExecutionDomain.StandaloneSynchronization,
            BufferCopyOp or TextureUploadFrameOp =>
                RenderPacketExecutionDomain.StandaloneTransfer,
            _ => RenderPacketExecutionDomain.GraphicsRendering,
        };

    private void CaptureRecordedPacketOperationDependencies(
        FrameOp op,
        ref VulkanRecordedBufferIdentity indexBuffer,
        Span<VulkanRecordedBufferIdentity> vertexScratch,
        ref int vertexCount,
        ref bool vertexOverflow,
        Span<VulkanRecordedBufferIdentity> auxiliaryScratch,
        ref int auxiliaryCount,
        ref bool auxiliaryOverflow,
        Span<VulkanRecordedProgramIdentity> programScratch,
        ref int programCount,
        ref bool programOverflow)
    {
        switch (op)
        {
            case MeshDrawOp draw:
                AddRecordedProgramIdentity(
                    CaptureRecordedProgramIdentity(draw.Draw.PreparedProgram, default),
                    programScratch,
                    ref programCount,
                    ref programOverflow);
                CaptureMeshBufferDependencies(
                    draw.Draw.Renderer,
                    ref indexBuffer,
                    vertexScratch,
                    ref vertexCount,
                    ref vertexOverflow,
                    auxiliaryScratch,
                    ref auxiliaryCount,
                    ref auxiliaryOverflow);
                break;
            case IndirectDrawOp indirect:
                AddRecordedProgramIdentity(
                    CaptureRecordedProgramIdentity(indirect.Draw.PreparedProgram, default),
                    programScratch,
                    ref programCount,
                    ref programOverflow);
                AddRecordedProgramIdentity(
                    CaptureRecordedProgramIdentity(
                        indirect.BindlessMaterialTextures?.Program,
                        default),
                    programScratch,
                    ref programCount,
                    ref programOverflow);
                CaptureMeshBufferDependencies(
                    indirect.MeshRenderer,
                    ref indexBuffer,
                    vertexScratch,
                    ref vertexCount,
                    ref vertexOverflow,
                    auxiliaryScratch,
                    ref auxiliaryCount,
                    ref auxiliaryOverflow);
                AddRecordedBufferIdentity(
                    CaptureRecordedBufferIdentity(
                        indirect.IndirectBuffer,
                        EVulkanRecordedBufferBindingKind.Indirect,
                        0u,
                        (ulong)indirect.ByteOffset,
                        ResolveRecordedRange(
                            indirect.IndirectBuffer,
                            (ulong)indirect.ByteOffset)),
                    auxiliaryScratch,
                    ref auxiliaryCount,
                    ref auxiliaryOverflow);
                AddRecordedBufferIdentity(
                    CaptureRecordedBufferIdentity(
                        indirect.ParameterBuffer,
                        EVulkanRecordedBufferBindingKind.IndirectCount,
                        0u,
                        (ulong)indirect.CountByteOffset,
                        ResolveRecordedRange(
                            indirect.ParameterBuffer,
                            (ulong)indirect.CountByteOffset)),
                    auxiliaryScratch,
                    ref auxiliaryCount,
                    ref auxiliaryOverflow);
                break;
            case ComputeDispatchOp compute:
                AddRecordedProgramIdentity(
                    CaptureRecordedProgramIdentity(
                        compute.Program,
                        compute.Program.ComputePipeline),
                    programScratch,
                    ref programCount,
                    ref programOverflow);
                CaptureComputeBufferDependencies(
                    compute.Snapshot,
                    auxiliaryScratch,
                    ref auxiliaryCount,
                    ref auxiliaryOverflow);
                break;
            case ComputeDispatchIndirectOp computeIndirect:
                AddRecordedProgramIdentity(
                    CaptureRecordedProgramIdentity(
                        computeIndirect.Program,
                        computeIndirect.Program.ComputePipeline),
                    programScratch,
                    ref programCount,
                    ref programOverflow);
                AddRecordedBufferIdentity(
                    CaptureRecordedBufferIdentity(
                        computeIndirect.ArgumentOwner,
                        EVulkanRecordedBufferBindingKind.DispatchArguments,
                        0u,
                        0UL,
                        computeIndirect.ArgumentOwner.AllocatedByteSize),
                    auxiliaryScratch,
                    ref auxiliaryCount,
                    ref auxiliaryOverflow);
                CaptureComputeBufferDependencies(
                    computeIndirect.Snapshot,
                    auxiliaryScratch,
                    ref auxiliaryCount,
                    ref auxiliaryOverflow);
                break;
            case MeshTaskDispatchIndirectCountOp meshTask:
                AddRecordedProgramIdentity(
                    CaptureRecordedProgramIdentity(
                        meshTask.BindlessMaterialTextures?.Program,
                        default),
                    programScratch,
                    ref programCount,
                    ref programOverflow);
                AddRecordedBufferIdentity(
                    CaptureRecordedBufferIdentity(
                        meshTask.IndirectBuffer,
                        EVulkanRecordedBufferBindingKind.Indirect,
                        0u,
                        (ulong)meshTask.ByteOffset,
                        ResolveRecordedRange(
                            meshTask.IndirectBuffer,
                            (ulong)meshTask.ByteOffset)),
                    auxiliaryScratch,
                    ref auxiliaryCount,
                    ref auxiliaryOverflow);
                AddRecordedBufferIdentity(
                    CaptureRecordedBufferIdentity(
                        meshTask.CountBuffer,
                        EVulkanRecordedBufferBindingKind.IndirectCount,
                        0u,
                        (ulong)meshTask.CountByteOffset,
                        ResolveRecordedRange(
                            meshTask.CountBuffer,
                            (ulong)meshTask.CountByteOffset)),
                    auxiliaryScratch,
                    ref auxiliaryCount,
                    ref auxiliaryOverflow);
                break;
        }
    }

    private void CaptureMeshBufferDependencies(
        VkMeshRenderer meshRenderer,
        ref VulkanRecordedBufferIdentity indexBuffer,
        Span<VulkanRecordedBufferIdentity> vertexScratch,
        ref int vertexCount,
        ref bool vertexOverflow,
        Span<VulkanRecordedBufferIdentity> auxiliaryScratch,
        ref int auxiliaryCount,
        ref bool auxiliaryOverflow)
    {
        Span<VulkanRecordedBufferIdentity> capturedVertices =
            stackalloc VulkanRecordedBufferIdentity[VulkanRecordedBufferIdentityBuffer.Capacity];
        Span<VulkanRecordedBufferIdentity> capturedIndices =
            stackalloc VulkanRecordedBufferIdentity[3];
        meshRenderer.CaptureRecordedBufferBindings(
            capturedVertices,
            out int capturedVertexCount,
            out bool verticesComplete,
            capturedIndices,
            out int capturedIndexCount,
            out bool indicesComplete);

        bool vertexOverflowBeforeCapture = vertexOverflow;
        int aggregateVertexCountBeforeCapture = vertexCount;
        vertexOverflow |= !verticesComplete;
        auxiliaryOverflow |= !indicesComplete;
        for (int i = 0; i < capturedVertexCount; i++)
            AddRecordedBufferIdentity(
                capturedVertices[i],
                vertexScratch,
                ref vertexCount,
                ref vertexOverflow);

        if (!vertexOverflowBeforeCapture &&
            vertexOverflow &&
            FrameDataReuseDiagnosticsEnabled)
        {
            Debug.VulkanEvery(
                $"Vulkan.CommandChains.VertexIdentity.{meshRenderer.GetHashCode()}",
                TimeSpan.FromSeconds(1),
                "[Vulkan.CommandChains] Vertex-buffer identity capture failed mesh='{0}' aggregateBefore={1} captured={2} capacity={3}: {4}.",
                meshRenderer.Mesh?.Name ?? "<unnamed mesh>",
                aggregateVertexCountBeforeCapture,
                capturedVertexCount,
                VulkanRecordedBufferIdentityBuffer.Capacity,
                meshRenderer.DescribeRecordedVertexBindingCapture());
        }

        for (int i = 0; i < capturedIndexCount; i++)
        {
            VulkanRecordedBufferIdentity capturedIndex = capturedIndices[i];
            if (!indexBuffer.IsBound)
                indexBuffer = capturedIndex;
            AddRecordedBufferIdentity(
                capturedIndex,
                auxiliaryScratch,
                ref auxiliaryCount,
                ref auxiliaryOverflow);
        }
    }

    private void CaptureComputeBufferDependencies(
        ComputeDispatchSnapshot snapshot,
        Span<VulkanRecordedBufferIdentity> scratch,
        ref int count,
        ref bool overflow)
    {
        foreach (KeyValuePair<uint, VulkanComputeBufferBinding> pair in snapshot.Buffers)
        {
            VulkanComputeBufferBinding binding = pair.Value;
            AddRecordedBufferIdentity(
                CaptureRecordedBufferIdentity(
                    binding.Buffer.Handle,
                    EVulkanRecordedBufferBindingKind.Descriptor,
                    pair.Key,
                    0UL,
                    binding.Range),
                scratch,
                ref count,
                ref overflow);
        }
    }

    private VulkanRecordedBufferIdentity CaptureRecordedBufferIdentity(
        VkDataBuffer? buffer,
        EVulkanRecordedBufferBindingKind kind,
        uint binding,
        ulong offset,
        ulong range)
        => CaptureRecordedBufferIdentity(
            buffer?.BufferHandle?.Handle ?? 0UL,
            kind,
            binding,
            offset,
            range);

    private VulkanRecordedBufferIdentity CaptureRecordedBufferIdentity(
        ulong bufferHandle,
        EVulkanRecordedBufferBindingKind kind,
        uint binding,
        ulong offset,
        ulong range)
        => new(
            kind,
            binding,
            bufferHandle,
            bufferHandle == 0UL
                ? 0UL
                : GetCurrentVulkanResourceGeneration(ObjectType.Buffer, bufferHandle),
            offset,
            range);

    private static ulong ResolveRecordedRange(VkDataBuffer? buffer, ulong offset)
    {
        ulong allocatedSize = buffer?.AllocatedByteSize ?? 0UL;
        return offset < allocatedSize ? allocatedSize - offset : 0UL;
    }

    private static void AddRecordedBufferIdentity(
        in VulkanRecordedBufferIdentity identity,
        Span<VulkanRecordedBufferIdentity> scratch,
        ref int count,
        ref bool overflow)
    {
        if (!identity.IsBound)
            return;

        if (count >= scratch.Length)
        {
            overflow = true;
            return;
        }

        scratch[count++] = identity;
    }

    private static VulkanRecordedBufferIdentityBuffer FinalizeRecordedBufferIdentities(
        Span<VulkanRecordedBufferIdentity> scratch,
        int count,
        bool overflow)
    {
        if (overflow)
        {
            VulkanRecordedBufferIdentityBuffer incomplete = default;
            incomplete.Invalidate();
            return incomplete;
        }

        scratch[..count].Sort(static (left, right) =>
        {
            int kindComparison = left.Kind.CompareTo(right.Kind);
            if (kindComparison != 0)
                return kindComparison;

            int bindingComparison = left.Binding.CompareTo(right.Binding);
            if (bindingComparison != 0)
                return bindingComparison;

            int offsetComparison = left.Offset.CompareTo(right.Offset);
            if (offsetComparison != 0)
                return offsetComparison;

            return left.BufferHandle.CompareTo(right.BufferHandle);
        });

        VulkanRecordedBufferIdentityBuffer result = default;
        result.Initialize(count);
        for (int i = 0; i < count; i++)
            result.Set(i, scratch[i]);
        return result;
    }

    private VulkanRecordedProgramIdentity CaptureRecordedProgramIdentity(
        VkRenderProgram? program,
        Pipeline pipeline)
    {
        ulong layoutHandle = program?.PipelineLayout.Handle ?? 0UL;
        ulong pipelineHandle = pipeline.Handle;
        return new VulkanRecordedProgramIdentity(
            program?.BindingId ?? 0u,
            program?.LinkGeneration ?? 0UL,
            layoutHandle,
            layoutHandle == 0UL
                ? 0UL
                : GetCurrentVulkanResourceGeneration(
                    ObjectType.PipelineLayout,
                    layoutHandle),
            pipelineHandle,
            pipelineHandle == 0UL
                ? 0UL
                : GetCurrentVulkanResourceGeneration(
                    ObjectType.Pipeline,
                    pipelineHandle));
    }

    private static void AddRecordedProgramIdentity(
        in VulkanRecordedProgramIdentity identity,
        Span<VulkanRecordedProgramIdentity> scratch,
        ref int count,
        ref bool overflow)
    {
        if (identity.ProgramBindingId == 0u)
            return;

        // A packet may contain dozens of draws that use the same physical
        // program/pipeline. Reuse depends on the identity set, not occurrence
        // count; retaining duplicates could overflow the bounded hot-path buffer
        // and permanently disable otherwise valid command-chain reuse.
        for (int i = 0; i < count; i++)
            if (scratch[i] == identity)
                return;

        if (count >= scratch.Length)
        {
            overflow = true;
            return;
        }

        scratch[count++] = identity;
    }

    private static VulkanRecordedProgramIdentityBuffer FinalizeRecordedProgramIdentities(
        Span<VulkanRecordedProgramIdentity> scratch,
        int count,
        bool overflow)
    {
        VulkanRecordedProgramIdentityBuffer result = default;
        if (overflow)
        {
            result.Invalidate();
            return result;
        }

        result.Initialize(count);
        for (int i = 0; i < count; i++)
            result.Set(i, scratch[i]);
        return result;
    }

    /// <summary>
    /// Starts one immutable packet-payload publication. Cached chains and
    /// prepared workers can retain an older arena, so only an unleased arena is
    /// reset; otherwise a fresh arena is selected without invalidating ranges.
    /// </summary>
    private void BeginCommandChainPacketPayloadPublication(int operationCapacity)
    {
        for (int index = 0; index < _commandChainPacketPayloadArenas.Count; index++)
        {
            RenderPacketPayloadArena candidate = _commandChainPacketPayloadArenas[index];
            if (candidate.IsLeased)
                continue;

            candidate.ResetForPublication();
            candidate.EnsurePublicationCapacity(
                operationCapacity,
                operationCapacity,
                operationCapacity);
            _activeCommandChainPacketPayloadArena = candidate;
            return;
        }

        RenderPacketPayloadArena created = new();
        created.EnsurePublicationCapacity(
            operationCapacity,
            operationCapacity,
            operationCapacity);
        _commandChainPacketPayloadArenas.Add(created);
        _activeCommandChainPacketPayloadArena = created;
    }

    private RenderPacketPayloadArena GetActiveCommandChainPacketPayloadArena()
        => _activeCommandChainPacketPayloadArena
            ?? throw new InvalidOperationException(
                "Command-chain packet payload publication has not been started.");

    private RenderPacket RentRenderPacket()
    {
        while ((uint)_commandChainPacketPoolCursor <
               (uint)_commandChainPacketPool.Count)
        {
            RenderPacket reusablePacket =
                _commandChainPacketPool[_commandChainPacketPoolCursor++];
            if (reusablePacket.IsLeased)
                continue;

            reusablePacket.PrepareForReuse();
            return reusablePacket;
        }

        RenderPacket packet = new();
        _commandChainPacketPool.Add(packet);
        _commandChainPacketPoolCursor = _commandChainPacketPool.Count;
        return packet;
    }

    private static int BuildCommandChainOrdinal(
        RenderPacket packet,
        Dictionary<ulong, int> structuralOccurrences)
    {
        if (packet.ViewKey.Kind == RenderViewKind.Shadow &&
            packet.DrawCount > 1)
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
        FrameOperationStream staticOps,
        FrameOperationStream volatileOps,
        ulong resourcePlanRevision,
        List<RenderPacket> parallelPackets)
    {
        List<RenderPacket> sequential = new(staticOps.Count + volatileOps.Count);
        LowerFrameOpsToRenderPackets(0u, staticOps, dynamicOverlay: false, resourcePlanRevision, sequential, default, profileDetail: false);
        LowerFrameOpsToRenderPackets(0u, volatileOps, dynamicOverlay: true, resourcePlanRevision, sequential, default, profileDetail: false);
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
            !string.Equals(expected.GetDiagnosticTargetName(), actual.GetDiagnosticTargetName(), StringComparison.Ordinal) ||
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
        if (IsExplicitDynamicCommandRange(op, dynamicOverlay))
            return RenderPacketVolatility.DynamicCommand;

        return op switch
        {
            MeshDrawOp => RenderPacketVolatility.FrameDataOnly,
            ClearOp => RenderPacketVolatility.StaticStructural,
            BlitOp => RenderPacketVolatility.StaticStructural,
            IndirectDrawOp => RenderPacketVolatility.FrameDataOnly,
            MeshTaskDispatchIndirectCountOp => RenderPacketVolatility.FrameDataOnly,
            ComputeDispatchOp => RenderPacketVolatility.FrameDataOnly,
            // Frozen packet buffer/range identities make the command topology
            // reusable; GPU-produced bytes and indirect counts refresh in place.
            ComputeDispatchIndirectOp => RenderPacketVolatility.FrameDataOnly,
            BufferCopyOp => RenderPacketVolatility.FrameDataOnly,
            SubmissionMarkerOp => RenderPacketVolatility.DynamicCommand,
            MemoryBarrierOp => RenderPacketVolatility.FrameDataOnly,
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
    {
        if (ClassifyRenderPacketVolatility(op, dynamicOverlay) !=
            RenderPacketVolatility.FrameDataOnly)
            return false;

        return op switch
        {
            MeshDrawOp draw => IsStableSecondaryCommandRange(draw, dynamicOverlay),
            ComputeDispatchOp or ComputeDispatchIndirectOp or MemoryBarrierOp =>
                !IsExplicitDynamicCommandRange(op, dynamicOverlay),
            // Indirect/count graphics draws retain their dedicated inheritance-
            // aware persistent secondary path.
            IndirectDrawOp => false,
            _ => false,
        };
    }

    private static bool IsSchedulableCommandChainFrameOp(
        FrameOperationStream operations,
        int operationIndex,
        bool dynamicOverlay)
    {
        ref readonly FrameOperationHeader header = ref operations.GetHeader(operationIndex);
        if (header.OpCode is not (
                EVulkanPrimaryPlanNodeKind.MeshDraw or
                EVulkanPrimaryPlanNodeKind.IndirectDraw or
                EVulkanPrimaryPlanNodeKind.MeshTaskDispatchIndirectCount or
                EVulkanPrimaryPlanNodeKind.ComputeDispatch or
                EVulkanPrimaryPlanNodeKind.ComputeDispatchIndirect or
                EVulkanPrimaryPlanNodeKind.BufferCopy or
                EVulkanPrimaryPlanNodeKind.MemoryBarrier))
        {
            return false;
        }

        // The numerical opcode rejects all non-command-chain kinds before the
        // final typed payload check needed for mesh stability policy.
        return IsSchedulableCommandChainFrameOp(
            operations.GetPayloadForPrimaryDispatch(operationIndex),
            dynamicOverlay);
    }

    /// <summary>
    /// Stable secondary admission is semantic and generation-backed. Pass names
    /// are diagnostics only: they are neither unique nor a physical-lifetime
    /// contract, and parsing them here previously admitted output-sensitive
    /// work when a pipeline renamed a pass.
    /// </summary>
    private static bool IsStableSecondaryCommandRange(
        MeshDrawOp draw,
        bool dynamicOverlay)
        => !IsExplicitDynamicCommandRange(draw, dynamicOverlay) &&
           draw.Draw.ProgramBindingSnapshot?.HasMutableFrameSourceSamplerBindings != true;

    private static bool IsExplicitDynamicCommandRange(
        FrameOp op,
        bool dynamicOverlay)
    {
        if (dynamicOverlay || IsUiBatchTextDrawOp(op))
            return true;

        ref readonly FrameOpContext context = ref op.ContextReference;
        if (context.PipelineInstance?.Pipeline is UserInterfaceRenderPipeline)
            return true;

        if (ResolveSecondaryCachePolicy(in context, op.PassIndex) !=
            ERenderPassSecondaryCachePolicy.Stable)
        {
            return true;
        }

        return context.ContextKind is
            EVulkanFrameOpContextKind.OpenXrMirror or
            EVulkanFrameOpContextKind.SceneCapture or
            EVulkanFrameOpContextKind.LightProbeCapture or
            EVulkanFrameOpContextKind.UiPreview or
            EVulkanFrameOpContextKind.DiagnosticCapture;
    }

    private static ERenderPassSecondaryCachePolicy ResolveSecondaryCachePolicy(
        in FrameOpContext context,
        int passIndex)
    {
        return TryGetPassMetadata(in context, passIndex, out RenderPassMetadata pass)
            ? pass.SecondaryCachePolicy
            : ERenderPassSecondaryCachePolicy.Stable;
    }

    private static bool IsOverlayLikePass(string? name)
    {
        return !string.IsNullOrWhiteSpace(name) &&
            (name.Contains("UI", StringComparison.OrdinalIgnoreCase) ||
             name.Contains("Text", StringComparison.OrdinalIgnoreCase) ||
             name.Contains("Debug", StringComparison.OrdinalIgnoreCase) ||
             name.Contains("Dynamic", StringComparison.OrdinalIgnoreCase) ||
             name.Contains("Output", StringComparison.OrdinalIgnoreCase) ||
             name.Contains("Present", StringComparison.OrdinalIgnoreCase) ||
             name.Contains("Swapchain", StringComparison.OrdinalIgnoreCase) ||
             name.Contains("Overlay", StringComparison.OrdinalIgnoreCase) ||
             name.Contains("Profiler", StringComparison.OrdinalIgnoreCase) ||
             name.Contains("Gizmo", StringComparison.OrdinalIgnoreCase) ||
             name.Contains("ImGui", StringComparison.OrdinalIgnoreCase));
    }

    internal static RenderViewKey BuildRenderViewKey(FrameOp op, bool dynamicOverlay)
    {
        ref readonly FrameOpContext context = ref op.ContextReference;
        string? passName = TryGetPassName(in context, op.PassIndex);
        RenderViewKind kind = dynamicOverlay || IsOverlayLikePass(passName)
            ? RenderViewKind.Overlay
            : ResolveRenderViewKind(op, in context, passName);
        int viewIndex = ResolveCommandChainViewIndex(op, kind);
        int lightIdentity = ResolveCommandChainLightIdentity(op, kind, in context);
        int cascadeIndex = ResolveCommandChainCascadeIndex(op, kind);
        return new RenderViewKey(
            context.PipelineIdentity,
            context.ViewportIdentity,
            viewIndex,
            kind,
            lightIdentity,
            cascadeIndex);
    }

    private static RenderViewKind ResolveRenderViewKind(FrameOp op)
    {
        ref readonly FrameOpContext context = ref op.ContextReference;
        return ResolveRenderViewKind(
            op,
            in context,
            TryGetPassName(in context, op.PassIndex));
    }

    private static RenderViewKind ResolveRenderViewKind(
        FrameOp op,
        in FrameOpContext context,
        string? passName)
    {
        if (context.PipelineInstance?.Pipeline is ShadowRenderPipeline)
            return RenderViewKind.Shadow;

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
        ref readonly FrameOpContext context = ref op.ContextReference;
        return ResolveCommandChainLightIdentity(op, kind, in context);
    }

    private static int ResolveCommandChainLightIdentity(
        FrameOp op,
        RenderViewKind kind,
        in FrameOpContext context)
    {
        if (kind != RenderViewKind.Shadow)
            return 0;

        int identity = HashCode.Combine(
            context.SchedulingIdentity,
            ResolveCommandChainTargetIdentity(op, in context));
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
        ref readonly FrameOpContext context = ref op.ContextReference;
        return TryGetPassName(in context, op.PassIndex);
    }

    private static string? TryGetPassName(in FrameOpContext context, int passIndex)
        => TryGetPassMetadata(in context, passIndex, out RenderPassMetadata pass)
            ? pass.Name
            : null;

    private static string ResolvePassName(IReadOnlyCollection<RenderPassMetadata>? passMetadata, int passIndex)
        => TryGetPassMetadata(passMetadata, passIndex, out RenderPassMetadata pass)
            ? pass.Name
            : "<unknown>";

    private static bool TryGetPassMetadata(
        in FrameOpContext context,
        int passIndex,
        out RenderPassMetadata pass)
        => TryGetPassMetadata(context.PassMetadata, passIndex, out pass);

    private static bool TryGetPassMetadata(
        IReadOnlyCollection<RenderPassMetadata>? passMetadata,
        int passIndex,
        out RenderPassMetadata pass)
    {
        if (passMetadata is RenderPassMetadataSnapshot snapshot)
            return snapshot.TryGetPass(passIndex, out pass);

        if (passMetadata is IReadOnlyList<RenderPassMetadata> passList)
        {
            for (int i = 0; i < passList.Count; i++)
            {
                RenderPassMetadata candidate = passList[i];
                if (candidate.PassIndex == passIndex)
                {
                    pass = candidate;
                    return true;
                }
            }
        }
        else if (passMetadata is not null)
        {
            foreach (RenderPassMetadata candidate in passMetadata)
            {
                if (candidate.PassIndex == passIndex)
                {
                    pass = candidate;
                    return true;
                }
            }
        }

        pass = null!;
        return false;
    }

    internal static int ResolveCommandChainTargetIdentity(FrameOp op)
    {
        ref readonly FrameOpContext context = ref op.ContextReference;
        return ResolveCommandChainTargetIdentity(op, in context);
    }

    private static int ResolveCommandChainTargetIdentity(
        FrameOp op,
        in FrameOpContext context)
        => op switch
        {
            BlitOp blit => blit.OutFbo?.GetHashCode() ?? context.OutputTargetIdentity,
            _ => op.Target?.GetHashCode() ?? context.OutputTargetIdentity,
        };

    internal static string ResolveCommandChainTargetName(FrameOp op)
    {
        ref readonly FrameOpContext context = ref op.ContextReference;
        return op switch
        {
            BlitOp blit => blit.OutFbo?.Name ?? context.OutputTargetName ?? "<swapchain>",
            _ => op.Target?.Name ?? context.OutputTargetName ?? "<swapchain>",
        };
    }

    private static ulong ResolvePipelineGeneration(FrameOp op)
    {
        FrameOpSignatureHasher hash = new();
        hash.Add(op.ContextReference.PipelineIdentity);

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
        if (!hasMutableFrameSourceBindings && !draw.IsSealedForFramePlan)
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

    internal static int ResolveCommandChainInlineOperationIndex(FrameOperationSequence ops, int sourceIndex)
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
        ref readonly FrameOpContext context = ref op.ContextReference;
        FrameOpSignatureHasher hash = new();
        hash.Add(0x434F4D5055444553UL);
        hash.Add(descriptorBindingOrdinal);
        hash.Add(op.PassIndex);
        hash.Add(ResolveCommandChainTargetIdentity(op));
        hash.Add(context.PipelineIdentity);
        hash.Add(context.ViewportIdentity);
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
        ref readonly FrameOpContext context = ref op.ContextReference;
        FrameOpSignatureHasher hash = new();
        hash.Add(GetCommandChainFrameOpKindId(op));
        hash.Add(op.PassIndex);
        hash.Add(ResolveCommandChainTargetIdentity(op));
        hash.Add(context.PipelineIdentity);
        hash.Add(context.ViewportIdentity);
        hash.Add((int)volatility);

        switch (op)
        {
            case MeshDrawOp draw:
                RenderViewKind drawKind = ResolveRenderViewKind(
                    draw,
                    in context,
                    TryGetPassName(in context, op.PassIndex));
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
                hash.Add(compute.Program.BindingId);
                hash.Add(compute.Program.LinkGeneration);
                hash.Add(compute.GroupsX);
                hash.Add(compute.GroupsY);
                hash.Add(compute.GroupsZ);
                break;
            case ComputeDispatchIndirectOp computeIndirect:
                hash.Add(computeIndirect.Program.BindingId);
                hash.Add(computeIndirect.Program.LinkGeneration);
                hash.Add(ComputeCommandBufferDataBufferSignature(computeIndirect.ArgumentOwner));
                hash.Add(computeIndirect.ArgumentBuffer.Handle);
                hash.Add(computeIndirect.ArgumentOffset);
                break;
            case MemoryBarrierOp barrier:
                hash.Add((int)barrier.Mask);
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
