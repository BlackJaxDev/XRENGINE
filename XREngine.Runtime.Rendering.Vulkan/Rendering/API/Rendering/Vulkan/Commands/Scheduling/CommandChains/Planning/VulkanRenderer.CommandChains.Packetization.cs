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

internal sealed partial class VulkanCommandRuntime
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
        // visibility changed; actual Vulkan recording belongs in coarse,
        // lane-affine render-domain batches instead.
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
                ref readonly QueryPayload query = ref ops.GetQuery(i);
                if (query.Operation == ERenderQueryOperation.Begin)
                    queryBracketDepth++;
                else if (query.Operation == ERenderQueryOperation.End && queryBracketDepth > 0)
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
                else if (ops.GetHeader(i).OpCode !=
                             EVulkanPrimaryPlanNodeKind.MeshDraw &&
                         IsSchedulableCommandChainFrameOp(
                             ops,
                             i,
                             dynamicOverlay: false))
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
            else if (ops.GetHeader(i).OpCode !=
                         EVulkanPrimaryPlanNodeKind.MeshDraw &&
                     IsSchedulableCommandChainFrameOp(
                         ops,
                         i,
                         dynamicOverlay))
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
        ref readonly MeshDrawPayload first = ref ops.GetMeshDraw(startIndex);
        ref readonly FrameOperationHeader firstHeader = ref ops.GetHeader(startIndex);
        ref readonly FrameOpContext firstContext = ref ops.GetContext(startIndex);

        DrawPacket firstDraw;
        RenderViewKey viewKey;
        int targetIdentity;
        int runCount;
        using (VulkanCpuStageScope compatibilityStage = new(
                   _frameTelemetry,
                   EVulkanCpuStage.CommandChainCompatibilityScan,
                   profileDetail))
        {
            firstDraw = CreateDrawPacket(startIndex, first.Draw, firstHeader, in firstContext);
            preparedMeshDraw = firstDraw;
            _commandChainDrawPacketScratch[0] = firstDraw;
            viewKey = BuildRenderViewKey(first.Draw, firstHeader.PassIndex, in firstContext, dynamicOverlay: false);
            targetIdentity = firstHeader.TargetIdentity;
            DescriptorBindingSnapshot firstDescriptorSnapshot =
                CreateMeshDrawDescriptorSnapshot(first.Draw);
            runCount = 1;
            int packetDrawLimit = viewKey.Kind == RenderViewKind.Shadow
                ? MaxShadowMeshDrawsPerRenderPacket
                : MaxMeshDrawsPerRenderPacket;
            int available = Math.Min(ops.Count - startIndex, packetDrawLimit);
            while (runCount < available &&
                   ops.GetHeader(startIndex + runCount).OpCode == EVulkanPrimaryPlanNodeKind.MeshDraw &&
                   IsMeshDrawPacketCompatible(
                        first.Draw,
                        firstHeader,
                        firstContext,
                       firstDraw,
                       viewKey,
                       targetIdentity,
                       firstDescriptorSnapshot,
                        ops.GetMeshDraw(startIndex + runCount).Draw,
                        ops.GetHeader(startIndex + runCount),
                        ops.GetContext(startIndex + runCount),
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
        using (VulkanCpuStageScope dependencyStage = new(
                   _frameTelemetry,
                   EVulkanCpuStage.CommandChainDependencyAggregation,
                   profileDetail))
        {
            for (int i = 0; i < runCount; i++)
            {
                ref readonly MeshDrawPayload drawOp = ref ops.GetMeshDraw(startIndex + i);
                DrawPacket draw = draws[i];
                structuralHash.Add(draw.StructuralSignature);
                frameDataHash.Add(draw.FrameDataSignature);
                pipelineGenerationHash.Add(ResolvePipelineGeneration(drawOp.Draw, in ops.GetContext(startIndex + i)));

                // A secondary command buffer may bind a different material descriptor
                // set and graphics program for every draw. Track the complete ordered
                // dependency set instead of splitting an otherwise compatible shadow
                // draw run into one secondary per material. Schema/identity and ordinary
                // descriptor publication changes require re-recording.
                DescriptorBindingSnapshot drawDescriptors = CreateMeshDrawDescriptorSnapshot(drawOp.Draw);
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

        string targetName = ResolveCommandChainTargetName(ops.GetTarget(startIndex), in firstContext);
        VulkanRecordedRenderTargetSnapshot nativeTarget =
            CaptureRecordedRenderTargetSnapshot(ops.GetTarget(startIndex), in firstContext, preparedRecordingTarget);
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
            firstContext.SubmissionQueueFamily,
            nativeTarget);
        RenderPacket packet = RentRenderPacket();
        packet.Reset(
            GetActiveCommandChainPacketPayloadArena(),
            viewKey,
            firstHeader.PassIndex,
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
    /// overflow. A capacity-limited prefix below the coarse-recording floor is
    /// left inline; accepting an oversized packet would make its prepared key
    /// permanently incomplete and force the whole run back into primary inline
    /// recording every frame.
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
            ref readonly MeshDrawPayload draw = ref ops.GetMeshDraw(
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
        in PendingMeshDraw first,
        in FrameOperationHeader firstHeader,
        in FrameOpContext firstContext,
        DrawPacket firstDraw,
        RenderViewKey viewKey,
        int targetIdentity,
        DescriptorBindingSnapshot descriptorSnapshot,
        in PendingMeshDraw candidate,
        in FrameOperationHeader candidateHeader,
        in FrameOpContext candidateContext,
        int candidateIndex,
        out DrawPacket candidateDraw)
    {
        candidateDraw = default;
        if (candidateHeader.PassIndex != firstHeader.PassIndex ||
            candidateHeader.TargetIdentity != targetIdentity ||
            BuildRenderViewKey(candidate, candidateHeader.PassIndex, in candidateContext, dynamicOverlay: false) != viewKey ||
            IsExplicitDynamicCommandRange(in candidateContext, candidateHeader.PassIndex, false, IsUiBatchTextDraw(candidate)) ||
            candidate.ProgramBindingSnapshot?.HasMutableFrameSourceSamplerBindings == true)
        {
            return false;
        }

        candidateDraw = CreateDrawPacket(candidateIndex, candidate, candidateHeader, in candidateContext);
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
        // per draw. Shadow packetization uses a smaller bounded packet cap so
        // cascade membership churn cannot invalidate an unbounded caster set.
        // Retain the main view's fine-grained program/descriptor grouping.
        if (viewKey.Kind != RenderViewKind.Shadow)
        {
            DescriptorBindingSnapshot candidateDescriptors = CreateMeshDrawDescriptorSnapshot(candidate);
            if (candidateDraw.ProgramIdentity != firstDraw.ProgramIdentity ||
                candidateDescriptors.DescriptorSetCount != descriptorSnapshot.DescriptorSetCount ||
                candidateDescriptors.DescriptorSetSignature != descriptorSnapshot.DescriptorSetSignature)
            {
                return false;
            }
        }

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
        ref readonly FrameOpContext context = ref operations.GetContext(opIndex);
        RenderPacketVolatility volatility = ClassifyRenderPacketVolatility(header.OpCode, dynamicOverlay);
        DrawPacket firstDraw = default;
        DispatchPacket firstDispatch = default;
        int drawCount = 0;
        int dispatchCount = 0;
        DescriptorBindingSnapshot descriptorSnapshot = default;
        ulong pipelineGeneration = ResolvePipelineGeneration(in context);
        if (header.OpCode == EVulkanPrimaryPlanNodeKind.MeshDraw)
        {
            ref readonly MeshDrawPayload mesh = ref operations.GetMeshDraw(opIndex);
            firstDraw = preparedMeshDraw;
            drawCount = 1;
            descriptorSnapshot = CreateMeshDrawDescriptorSnapshot(mesh.Draw);
            pipelineGeneration = ResolvePipelineGeneration(mesh.Draw, in context);
        }
        else if (header.OpCode == EVulkanPrimaryPlanNodeKind.ComputeDispatch)
        {
            ref readonly ComputeDispatchPayload compute = ref operations.GetComputeDispatch(opIndex);
            firstDispatch = CreateDispatchPacket(opIndex, compute, header, in context);
            dispatchCount = 1;
            descriptorSnapshot = CreateComputeDispatchDescriptorSnapshot(compute.Snapshot);
            pipelineGeneration = ResolvePipelineGeneration(compute.Program, in context);
        }
        else if (header.OpCode == EVulkanPrimaryPlanNodeKind.ComputeDispatchIndirect)
        {
            ref readonly ComputeDispatchIndirectPayload compute = ref operations.GetComputeDispatchIndirect(opIndex);
            pipelineGeneration = ResolvePipelineGeneration(compute.Program, in context);
        }
        RenderViewKey viewKey = BuildRenderViewKey(in context, header.PassIndex, dynamicOverlay);
        ulong structuralSignature = drawCount != 0 ? firstDraw.StructuralSignature :
            ComputeFrameOpStructuralSignature(header, in context, opIndex, volatility);
        ulong frameDataSignature = drawCount != 0 ? firstDraw.FrameDataSignature :
            ComputeFrameOpFrameDataSignature(header, in context, opIndex);
        int targetIdentity = header.TargetIdentity;
        string targetName = ResolveCommandChainTargetName(operations.GetTarget(opIndex), in context);
        VulkanRecordedRenderTargetSnapshot nativeTarget =
            CaptureRecordedRenderTargetSnapshot(operations.GetTarget(opIndex), in context, preparedRecordingTarget);
        ResourcePlanSnapshot resourceSnapshot = new(
            resourcePlanRevision,
            nativeTarget.AttachmentCount > 0
                ? nativeTarget.GetAttachment(0).ImageGeneration
                : 0UL,
            nativeTarget.FramebufferGeneration,
            pipelineGeneration,
            ResourcePlanSnapshot.PackRenderArea(
                nativeTarget.Width,
                nativeTarget.Height),
            context.SubmissionQueueFamily,
            nativeTarget);

        RenderPacket packet = RentRenderPacket();
        packet.Reset(
            GetActiveCommandChainPacketPayloadArena(),
            viewKey,
            header.PassIndex,
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
        packet.SetRecordedPacketKey(CreateRecordedPacketKey(
            ResolveRenderPacketExecutionDomain(header.OpCode),
            nativeTarget, descriptorSnapshot, resourceSnapshot,
            Span<VulkanRecordedBufferIdentity>.Empty, 0, false,
            Span<VulkanRecordedBufferIdentity>.Empty, 0, false, default,
            Span<VulkanRecordedProgramIdentity>.Empty, 0, false));
        packet.Seal();
        return packet;
    }

    private VulkanRecordedRenderTargetSnapshot CaptureRecordedRenderTargetSnapshot(
        XRFrameBuffer? target,
        in FrameOpContext context,
        in VulkanRecordedRenderTargetSnapshot preparedRecordingTarget)
    {
        XRFrameBuffer? resolvedTarget = target ?? context.OutputFrameBuffer;
        return resolvedTarget is not null &&
               ResourceRuntime.BackendObjects.Get(resolvedTarget) is VkFrameBuffer frameBuffer &&
               frameBuffer.TryCaptureRecordedRenderTargetSnapshot(out VulkanRecordedRenderTargetSnapshot snapshot)
            ? snapshot
            : preparedRecordingTarget;
    }

    private RecordedPacketKey CaptureRecordedPacketKey(
        FrameOperationStream ops,
        int startIndex,
        int count,
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
        int vertexCount = 0, auxiliaryCount = 0, programCount = 0;
        bool vertexOverflow = false, auxiliaryOverflow = false, programOverflow = false;
        VulkanRecordedBufferIdentity indexBuffer = default;
        RenderPacketExecutionDomain domain = RenderPacketExecutionDomain.GraphicsRendering;
        for (int index = 0; index < count; index++)
        {
            int operationIndex = startIndex + index;
            ref readonly FrameOperationHeader header = ref ops.GetHeader(operationIndex);
            domain = ResolveRenderPacketExecutionDomain(header.OpCode);
            if (header.OpCode != EVulkanPrimaryPlanNodeKind.MeshDraw)
                continue;
            ref readonly MeshDrawPayload mesh = ref ops.GetMeshDraw(operationIndex);
            PendingMeshDraw draw = mesh.Draw;
            AddRecordedProgramIdentity(CaptureRecordedProgramIdentity(draw.PreparedProgram, default),
                programScratch, ref programCount, ref programOverflow);
            CaptureMeshBufferDependencies(draw.Renderer, ref indexBuffer, vertexScratch,
                ref vertexCount, ref vertexOverflow, auxiliaryScratch, ref auxiliaryCount, ref auxiliaryOverflow);
        }
        return CreateRecordedPacketKey(domain, nativeTarget, descriptorSnapshot, resourceSnapshot,
            vertexScratch, vertexCount, vertexOverflow, auxiliaryScratch, auxiliaryCount,
            auxiliaryOverflow, indexBuffer, programScratch, programCount, programOverflow);
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
            DrawPacket firstDraw = packet.GetDraw(0);
            int bucket = ResolveShadowCommandChainBucket(
                firstDraw.RendererIdentity,
                firstDraw.MaterialIdentity);
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

    /// <summary>
    /// Returns whether the primary recorder can execute this frame op through a
    /// scheduled command-chain secondary. Other op kinds keep their existing
    /// inline or dedicated-secondary paths and must not occupy this cache.
    /// </summary>
    private static bool IsSchedulableCommandChainFrameOp(
        FrameOperationStream operations,
        int operationIndex,
        bool dynamicOverlay)
    {
        ref readonly FrameOperationHeader header = ref operations.GetHeader(operationIndex);
        if (header.OpCode is not (
                EVulkanPrimaryPlanNodeKind.MeshDraw or
                EVulkanPrimaryPlanNodeKind.IndirectDraw or
                EVulkanPrimaryPlanNodeKind.ComputeDispatch or
                EVulkanPrimaryPlanNodeKind.ComputeDispatchIndirect or
                EVulkanPrimaryPlanNodeKind.BufferCopy or
                EVulkanPrimaryPlanNodeKind.MemoryBarrier))
        {
            return false;
        }

        if (header.OpCode == EVulkanPrimaryPlanNodeKind.ComputeDispatch &&
            ComputeSnapshotHasSampledStorageImageAlias(
                operations.GetComputeDispatch(operationIndex).Snapshot))
        {
            return false;
        }

        if (header.OpCode == EVulkanPrimaryPlanNodeKind.ComputeDispatchIndirect &&
            ComputeSnapshotHasSampledStorageImageAlias(
                operations.GetComputeDispatchIndirect(operationIndex).Snapshot))
        {
            return false;
        }

        if (header.OpCode != EVulkanPrimaryPlanNodeKind.MeshDraw)
            return !IsExplicitDynamicCommandRange(
                in operations.GetContext(operationIndex),
                header.PassIndex,
                dynamicOverlay,
                isUiBatchText: false);

        return !IsExplicitDynamicCommandRange(
                in operations.GetContext(operationIndex),
                header.PassIndex,
                dynamicOverlay,
                IsUiBatchTextDraw(operations.GetMeshDraw(operationIndex).Draw)) &&
            operations.GetMeshDraw(operationIndex).Draw.ProgramBindingSnapshot?.HasMutableFrameSourceSamplerBindings != true;
    }

    /// <summary>
    /// Stable secondary admission is semantic and generation-backed. Pass names
    /// are diagnostics only: they are neither unique nor a physical-lifetime
    /// contract, and parsing them here previously admitted output-sensitive
    /// work when a pipeline renamed a pass.
    /// </summary>
    private static bool IsExplicitDynamicCommandRange(
        in FrameOpContext context,
        int passIndex,
        bool dynamicOverlay,
        bool isUiBatchText)
    {
        if (dynamicOverlay || isUiBatchText ||
            context.PipelineInstance?.Pipeline is UserInterfaceRenderPipeline)
            return true;
        if (ResolveSecondaryCachePolicy(in context, passIndex) != ERenderPassSecondaryCachePolicy.Stable)
            return true;
        return context.ContextKind is EVulkanFrameOpContextKind.OpenXrMirror or
            EVulkanFrameOpContextKind.SceneCapture or EVulkanFrameOpContextKind.LightProbeCapture or
            EVulkanFrameOpContextKind.UiPreview or EVulkanFrameOpContextKind.DiagnosticCapture;
    }

    private static bool IsUiBatchTextDraw(in PendingMeshDraw draw)
    {
        XRMeshRenderer renderer = draw.Renderer.MeshRenderer;
        XRMaterial? material = draw.MaterialOverride ?? renderer.Material;
        return string.Equals(material?.Name, "UIBatchTextMaterial", StringComparison.Ordinal) ||
            string.Equals(renderer.Name, "UIBatchTextRenderer", StringComparison.Ordinal) ||
            string.Equals(renderer.Mesh?.Name, "UIBatchTextQuadMesh", StringComparison.Ordinal);
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

    private static RenderViewKey BuildRenderViewKey(
        in FrameOpContext context,
        int passIndex,
        bool dynamicOverlay)
    {
        string? passName = TryGetPassName(in context, passIndex);
        RenderViewKind kind = dynamicOverlay || IsOverlayLikePass(passName)
            ? RenderViewKind.Overlay
            : context.PipelineInstance?.Pipeline is ShadowRenderPipeline
                ? RenderViewKind.Shadow
                : RenderViewKind.Main;
        return new RenderViewKey(context.PipelineIdentity, context.ViewportIdentity, 0, kind,
            ResolveCommandChainLightIdentity(context.OutputTargetIdentity, kind, in context),
            kind == RenderViewKind.Shadow ? Math.Max(0, passIndex) : -1);
    }

    private static RenderViewKey BuildRenderViewKey(
        in PendingMeshDraw draw,
        int passIndex,
        in FrameOpContext context,
        bool dynamicOverlay)
    {
        string? passName = TryGetPassName(in context, passIndex);
        RenderViewKind kind = dynamicOverlay || IsOverlayLikePass(passName)
            ? RenderViewKind.Overlay
            : ResolveRenderViewKind(draw, in context, passName);
        return new RenderViewKey(context.PipelineIdentity, context.ViewportIdentity,
            ResolveCommandChainViewIndex(draw, kind), kind,
            ResolveCommandChainLightIdentity(context.OutputTargetIdentity, kind, in context),
            ResolveCommandChainCascadeIndex(draw, passIndex, kind));
    }

    private static RenderViewKind ResolveRenderViewKind(
        in PendingMeshDraw draw,
        in FrameOpContext context,
        string? passName)
    {
        if (context.PipelineInstance?.Pipeline is ShadowRenderPipeline || draw.ShadowUniformState.IsShadowPass)
            return RenderViewKind.Shadow;
        if (draw.IsStereoPass || draw.Camera?.StereoEyeLeft.HasValue == true || draw.StereoRightEyeCamera is not null)
            return RenderViewKind.VREye;
        if (passName?.Contains("Shadow", StringComparison.OrdinalIgnoreCase) == true) return RenderViewKind.Shadow;
        if (passName?.Contains("Reflection", StringComparison.OrdinalIgnoreCase) == true) return RenderViewKind.Reflection;
        if (passName?.Contains("Probe", StringComparison.OrdinalIgnoreCase) == true) return RenderViewKind.Probe;
        return RenderViewKind.Main;
    }

    private static int ResolveCommandChainViewIndex(in PendingMeshDraw draw, RenderViewKind kind)
    {
        if (kind == RenderViewKind.VREye) return ResolveStereoViewIndex(draw);
        if (kind != RenderViewKind.Shadow) return 0;
        var state = draw.ShadowUniformState;
        if (state.DirectionalCascadeInstancedLayeredShadowPass) return Math.Max(0, state.DirectionalCascadeShadowLayerCount - 1);
        return state.PointLightInstancedLayeredShadowPass ? Math.Max(0, state.PointLightShadowFaceCount - 1) : 0;
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

    private static int ResolveCommandChainLightIdentity(int targetIdentity, RenderViewKind kind, in FrameOpContext context)
    {
        if (kind != RenderViewKind.Shadow) return 0;
        int identity = HashCode.Combine(context.SchedulingIdentity, targetIdentity);
        return identity == 0 ? 1 : identity;
    }

    private static int ResolveCommandChainCascadeIndex(in PendingMeshDraw draw, int passIndex, RenderViewKind kind)
    {
        if (kind != RenderViewKind.Shadow) return -1;
        var state = draw.ShadowUniformState;
        if (state.DirectionalCascadeInstancedLayeredShadowPass) return Math.Max(0, state.DirectionalCascadeShadowLayerCount - 1);
        if (state.PointLightInstancedLayeredShadowPass) return Math.Max(0, state.PointLightShadowFaceCount - 1);
        return Math.Max(0, passIndex);
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

    internal static int ResolveCommandChainTargetIdentity(
        XRFrameBuffer? target,
        in FrameOpContext context)
        => target?.GetHashCode() ?? context.OutputTargetIdentity;

    private static RenderPacketExecutionDomain ResolveRenderPacketExecutionDomain(
        EVulkanPrimaryPlanNodeKind kind)
        => kind is EVulkanPrimaryPlanNodeKind.ComputeDispatch or EVulkanPrimaryPlanNodeKind.ComputeDispatchIndirect
            ? RenderPacketExecutionDomain.StandaloneCompute
            : kind == EVulkanPrimaryPlanNodeKind.MemoryBarrier
                ? RenderPacketExecutionDomain.StandaloneSynchronization
                : kind is EVulkanPrimaryPlanNodeKind.BufferCopy or EVulkanPrimaryPlanNodeKind.TextureUpload
                    ? RenderPacketExecutionDomain.StandaloneTransfer
                    : RenderPacketExecutionDomain.GraphicsRendering;
    private static RenderPacketVolatility ClassifyRenderPacketVolatility(
        EVulkanPrimaryPlanNodeKind kind, bool dynamicOverlay)
    {
        if (dynamicOverlay) return RenderPacketVolatility.DynamicCommand;
        return kind switch
        {
            EVulkanPrimaryPlanNodeKind.MeshDraw or EVulkanPrimaryPlanNodeKind.IndirectDraw or
            EVulkanPrimaryPlanNodeKind.MeshTaskDispatchIndirectCount or EVulkanPrimaryPlanNodeKind.ComputeDispatch or
            EVulkanPrimaryPlanNodeKind.ComputeDispatchIndirect or EVulkanPrimaryPlanNodeKind.BufferCopy or
            EVulkanPrimaryPlanNodeKind.MemoryBarrier => RenderPacketVolatility.FrameDataOnly,
            EVulkanPrimaryPlanNodeKind.Clear or EVulkanPrimaryPlanNodeKind.Blit or
            EVulkanPrimaryPlanNodeKind.PublishFramebufferForSampling => RenderPacketVolatility.StaticStructural,
            _ => RenderPacketVolatility.DynamicCommand,
        };
    }

    private static string ResolveCommandChainTargetName(XRFrameBuffer? target, in FrameOpContext context)
        => target?.Name ?? context.OutputTargetName ?? "<swapchain>";

    private static void AddProgramGeneration(
        ref FrameOpSignatureHasher hash,
        VkRenderProgram? program)
    {
        hash.Add(program?.BindingId ?? 0u);
        hash.Add(program?.LinkGeneration ?? 0UL);
    }

    internal static DescriptorBindingSnapshot CreateCommandChainDescriptorSnapshot(
        FrameOperationStream operations,
        int index)
    {
        ref readonly FrameOperationHeader header = ref operations.GetHeader(index);
        return header.OpCode switch
        {
            EVulkanPrimaryPlanNodeKind.MeshDraw =>
                CreateMeshDrawDescriptorSnapshot(operations.GetMeshDraw(index).Draw),
            EVulkanPrimaryPlanNodeKind.ComputeDispatch =>
                CreateComputeDispatchDescriptorSnapshot(operations.GetComputeDispatch(index).Snapshot),
            EVulkanPrimaryPlanNodeKind.IndirectDraw =>
                CreateMeshDrawDescriptorSnapshot(operations.GetIndirectDraw(index).Draw),
            EVulkanPrimaryPlanNodeKind.MeshTaskDispatchIndirectCount =>
                CreateComputeDispatchDescriptorSnapshot(operations.GetMeshTask(index).ProgramBindingSnapshot),
            _ => default,
        };
    }

    private static DescriptorBindingSnapshot CreateComputeDispatchDescriptorSnapshot(ComputeDispatchSnapshot snapshot)
    {
        ulong descriptorGeneration = ComputeDispatchSnapshotSignature(snapshot);
        ulong descriptorSetSignature = ComputeDispatchSnapshotDescriptorSetSignature(snapshot);
        return new DescriptorBindingSnapshot(descriptorGeneration,
            descriptorSetSignature == 0UL ? 0 : 1, descriptorSetSignature);
    }

    private static DescriptorBindingSnapshot CreateMeshDrawDescriptorSnapshot(in PendingMeshDraw draw)
    {
        ulong descriptorGeneration = 0UL;
        ulong descriptorSetSignature = 0UL;
        int setCount = 0;
        if (draw.ProgramBindingSnapshot is { } snapshot)
        {
            descriptorGeneration = ComputeDispatchSnapshotSignature(snapshot);
            descriptorSetSignature = ComputeDispatchSnapshotDescriptorSetSignature(snapshot);
            setCount = descriptorSetSignature == 0 ? 0 : 1;
        }
        XRMaterial? material = draw.MaterialOverride ?? draw.Renderer.MeshRenderer.Material;
        if (material is not null)
        {
            ulong resources = draw.Renderer.ComputeRecordedDescriptorResourceSignature(material, draw.PreparedProgram, draw.ProgramBindingSnapshot);
            if (resources != 0UL) descriptorGeneration = MixSignature(descriptorGeneration, resources);
        }
        ulong schema = draw.Renderer.ComputeRecordedDescriptorSchemaSignature(draw.PreparedProgram);
        if (schema != 0UL) descriptorSetSignature = MixSignature(descriptorSetSignature, schema);
        if (draw.PreparedProgram is { } program) descriptorSetSignature = MixSignature(descriptorSetSignature, program.BindingId);
        setCount = Math.Max(setCount, draw.Renderer.GetRecordedDescriptorSetCount(draw.PreparedProgram));
        return new DescriptorBindingSnapshot(descriptorGeneration, setCount, descriptorSetSignature);
    }

    private static DescriptorBindingSnapshot CreateDescriptorSnapshotFromSignature(ulong signature)
    {
        int setCount = signature == 0 ? 0 : 1;
        return new DescriptorBindingSnapshot(signature, setCount, signature);
    }

    private static DrawPacket CreateDrawPacket(
        int opIndex,
        in PendingMeshDraw draw,
        in FrameOperationHeader header,
        in FrameOpContext context)
    {
        XRMaterial? material = draw.MaterialOverride ?? draw.Renderer.MeshRenderer.Material;
        int meshIdentity = draw.Renderer.MeshRenderer.Mesh?.GetHashCode() ?? 0;
        int materialIdentity = material?.GetHashCode() ?? 0;
        int programIdentity = draw.PreparedProgram is { } preparedProgram
            ? unchecked((int)preparedProgram.BindingId)
            : material?.RenderOptions?.GetHashCode() ?? materialIdentity;
        return new DrawPacket(opIndex, draw.Renderer.GetHashCode(), meshIdentity,
            materialIdentity, programIdentity, draw.Instances, draw.BlendEnabled,
            ComputeFrameOpStructuralSignature(draw, header, in context, opIndex, RenderPacketVolatility.FrameDataOnly),
            ComputeFrameOpFrameDataSignature(draw, header, in context, opIndex));
    }
    private static DispatchPacket CreateDispatchPacket(
        int opIndex,
        in ComputeDispatchPayload compute,
        in FrameOperationHeader header,
        in FrameOpContext context)
        => new(opIndex, compute.Program.GetHashCode(), compute.GroupsX, compute.GroupsY,
            compute.GroupsZ, ComputeFrameOpStructuralSignature(header, in context, opIndex, RenderPacketVolatility.FrameDataOnly),
            ComputeFrameOpFrameDataSignature(header, in context, opIndex));

    internal static int ResolveCommandChainInlineOperationIndex(FrameOperationStream ops, int sourceIndex)
    {
        int inlineOpIndex = 0;
        int queryBracketDepth = 0;
        int lastIndex = Math.Min(sourceIndex, ops.Count - 1);
        for (int opIndex = 0; opIndex <= lastIndex; opIndex++)
        {
            ref readonly FrameOperationHeader header = ref ops.GetHeader(opIndex);
            bool isQuery = header.OpCode == EVulkanPrimaryPlanNodeKind.Query;
            bool secondaryOwned =
                !isQuery &&
                queryBracketDepth == 0 &&
                IsSchedulableCommandChainFrameOp(ops, opIndex, dynamicOverlay: false);

            if (opIndex == sourceIndex)
                return inlineOpIndex;

            if (!secondaryOwned)
                inlineOpIndex++;

            if (isQuery)
            {
                ERenderQueryOperation operation = ops.GetQuery(opIndex).Operation;
                if (operation == ERenderQueryOperation.Begin)
                    queryBracketDepth++;
                else if (operation == ERenderQueryOperation.End && queryBracketDepth > 0)
                    queryBracketDepth--;
            }
        }

        return Math.Max(sourceIndex, 0);
    }

    /// <summary>
    /// Returns the stable occurrence ordinal for a direct compute dispatch in a
    /// sealed operation stream. The source operation index identifies the
    /// dispatch, while the ordinal deliberately counts only direct compute
    /// dispatches: inserting a draw, copy, or barrier must not reshape reusable
    /// descriptor identities. Descriptor sets are prepared per dispatch, so
    /// this identity must include secondary-owned dispatches rather than using
    /// the thin-primary ordinal.
    /// </summary>
    internal static int ResolveComputeDispatchOccurrenceOrdinal(
        FrameOperationStream ops,
        int sourceIndex)
    {
        int occurrenceOrdinal = 0;
        int lastIndex = Math.Min(sourceIndex, ops.Count - 1);
        for (int operationIndex = 0; operationIndex <= lastIndex; operationIndex++)
        {
            if (ops.GetHeader(operationIndex).OpCode != EVulkanPrimaryPlanNodeKind.ComputeDispatch)
                continue;

            if (operationIndex == sourceIndex)
                return occurrenceOrdinal;

            occurrenceOrdinal++;
        }

        return Math.Max(occurrenceOrdinal, 0);
    }

    internal static ulong ComputeReusableComputeDescriptorBindingKey(
        in ComputeDispatchPayload dispatch,
        in FrameOperationHeader header,
        in FrameOpContext context,
        EVulkanAcceptedFrameLane streamNamespace,
        int descriptorBindingOrdinal)
    {
        FrameOpSignatureHasher hash = new();
        hash.Add(0x434F4D5055444553UL);
        hash.Add((int)streamNamespace);
        hash.Add(descriptorBindingOrdinal);
        hash.Add(header.PassIndex);
        hash.Add(header.TargetIdentity);
        hash.Add(context.PipelineIdentity);
        hash.Add(context.ViewportIdentity);
        hash.Add(dispatch.Program.BindingId);
        hash.Add(dispatch.Program.LinkGeneration);
        hash.Add(dispatch.GroupsX);
        hash.Add(dispatch.GroupsY);
        hash.Add(dispatch.GroupsZ);
        return hash.ToHash();
    }

    private static ulong ComputeFrameOpStructuralSignature(
        in FrameOperationHeader header,
        in FrameOpContext context,
        int opIndex,
        RenderPacketVolatility volatility)
    {
        FrameOpSignatureHasher hash = new();
        hash.Add((int)header.OpCode); hash.Add(header.PassIndex); hash.Add(header.TargetIdentity);
        hash.Add(context.PipelineIdentity); hash.Add(context.ViewportIdentity); hash.Add((int)volatility); hash.Add(opIndex);
        return hash.ToHash();
    }

    private static ulong ResolvePipelineGeneration(in PendingMeshDraw draw, in FrameOpContext context)
    {
        FrameOpSignatureHasher hash = new();
        hash.Add(context.PipelineIdentity);
        AddProgramGeneration(ref hash, draw.PreparedProgram);
        return hash.ToHash();
    }

    private static ulong ResolvePipelineGeneration(in FrameOpContext context)
    {
        FrameOpSignatureHasher hash = new();
        hash.Add(context.PipelineIdentity);
        return hash.ToHash();
    }

    private static ulong ResolvePipelineGeneration(VkRenderProgram program, in FrameOpContext context)
    {
        FrameOpSignatureHasher hash = new();
        hash.Add(context.PipelineIdentity);
        AddProgramGeneration(ref hash, program);
        return hash.ToHash();
    }

    private static ulong ComputeFrameOpStructuralSignature(
        in PendingMeshDraw draw,
        in FrameOperationHeader header,
        in FrameOpContext context,
        int opIndex,
        RenderPacketVolatility volatility)
    {
        FrameOpSignatureHasher hash = new();
        hash.Add((int)header.OpCode);
        hash.Add(header.PassIndex);
        hash.Add(header.TargetIdentity);
        hash.Add(context.PipelineIdentity);
        hash.Add(context.ViewportIdentity);
        hash.Add((int)volatility);
        RenderViewKind kind = ResolveRenderViewKind(draw, in context, TryGetPassName(in context, header.PassIndex));
        hash.Add((int)kind);
        hash.Add(ResolveCommandChainViewIndex(draw, kind));
        hash.Add(ResolveCommandChainLightIdentity(header.TargetIdentity, kind, in context));
        hash.Add(ResolveCommandChainCascadeIndex(draw, header.PassIndex, kind));
        hash.Add(draw.Renderer.GetHashCode());
        hash.Add(draw.MaterialOverride?.GetHashCode() ?? 0);
        hash.Add(draw.Instances);
        hash.Add(draw.BlendEnabled);
        hash.Add(draw.AlphaToCoverageEnabled);
        hash.Add((int)draw.ColorBlendOp); hash.Add((int)draw.AlphaBlendOp);
        hash.Add((int)draw.SrcColorBlendFactor); hash.Add((int)draw.DstColorBlendFactor);
        hash.Add((int)draw.SrcAlphaBlendFactor); hash.Add((int)draw.DstAlphaBlendFactor);
        hash.Add((int)draw.ColorWriteMask); hash.Add((int)draw.CullMode); hash.Add((int)draw.FrontFace);
        hash.Add((int)draw.RasterizationSamples); hash.Add(draw.DepthTestEnabled); hash.Add(draw.DepthWriteEnabled);
        hash.Add((int)draw.DepthCompareOp); hash.Add(draw.StencilTestEnabled); hash.Add(draw.StencilWriteMask);
        AddViewportScissorSignature(ref hash, draw);
        hash.Add(draw.PreparedProgramIdentity); hash.Add(draw.PreparedProgram?.BindingId ?? 0u);
        hash.Add(ComputeShadowCommandChainStructuralSignature(draw.ShadowUniformState));
        hash.Add(draw.ShadowCasterRelevance.DirectionalCascadeTargetMask);
        hash.Add(draw.ShadowCasterRelevance.PointLightShadowFaceMask);
        return hash.ToHash();
    }

    private static ulong ComputeFrameOpFrameDataSignature(
        in PendingMeshDraw draw,
        in FrameOperationHeader header,
        in FrameOpContext context,
        int opIndex)
    {
        FrameOpSignatureHasher hash = new();
        hash.Add((int)header.OpCode); hash.Add(opIndex); hash.Add(header.PassIndex);
        hash.Add(header.TargetIdentity); hash.Add(context.RecordingFingerprint);
        hash.Add(draw.Renderer.GetHashCode()); hash.Add(draw.Instances);
        hash.Add(draw.PreparedProgramIdentity); hash.Add(draw.ProgramBindingSnapshot?.GetHashCode() ?? 0);
        return hash.ToHash();
    }

    private static ulong ComputeFrameOpFrameDataSignature(
        in FrameOperationHeader header,
        in FrameOpContext context,
        int opIndex)
    {
        FrameOpSignatureHasher hash = new();
        hash.Add((int)header.OpCode); hash.Add(opIndex); hash.Add(header.PassIndex);
        hash.Add(header.TargetIdentity); hash.Add(context.RecordingFingerprint);
        return hash.ToHash();
    }

    private static void AddViewportScissorSignature(
        ref FrameOpSignatureHasher hash,
        in PendingMeshDraw draw)
    {
        AddViewportSignature(ref hash, draw.Viewport);
        AddRectSignature(ref hash, draw.Scissor);
        hash.Add(draw.ViewportScissorCount);
        if (draw.ViewportScissorCount <= 1 ||
            draw.IndexedViewports is not { } indexedViewports ||
            draw.IndexedScissors is not { } indexedScissors)
        {
            return;
        }

        int indexedCount = (int)Math.Min(
            draw.ViewportScissorCount,
            (uint)Math.Min(indexedViewports.Length, indexedScissors.Length));
        hash.Add(indexedCount);
        for (int i = 0; i < indexedCount; i++)
        {
            AddViewportSignature(ref hash, indexedViewports[i]);
            AddRectSignature(ref hash, indexedScissors[i]);
        }
    }

    private static void AddViewportSignature(
        ref FrameOpSignatureHasher hash,
        in Viewport viewport)
    {
        hash.Add(viewport.X);
        hash.Add(viewport.Y);
        hash.Add(viewport.Width);
        hash.Add(viewport.Height);
        hash.Add(viewport.MinDepth);
        hash.Add(viewport.MaxDepth);
    }

    private static void AddRectSignature(
        ref FrameOpSignatureHasher hash,
        in Rect2D rect)
    {
        hash.Add(rect.Offset.X);
        hash.Add(rect.Offset.Y);
        hash.Add(rect.Extent.Width);
        hash.Add(rect.Extent.Height);
    }
}
