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
        uint targetImageIndex,
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
            LowerFrameOpsToRenderPacketsExcludingQueryBrackets(targetImageIndex, staticOps, resourcePlanRevision, packets);
        else
            LowerFrameOpsToRenderPackets(targetImageIndex, staticOps, dynamicOverlay: false, resourcePlanRevision, packets);
        LowerFrameOpsToRenderPackets(targetImageIndex, volatileOps, dynamicOverlay: true, resourcePlanRevision, packets);
    }

    private void LowerFrameOpsToRenderPacketsExcludingQueryBrackets(
        uint targetImageIndex,
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
                    targetImageIndex,
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
                        targetImageIndex, ops[i], i, dynamicOverlay: false, resourcePlanRevision, preparedMeshDraw));
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
        FrameOp[] ops,
        bool dynamicOverlay,
        ulong resourcePlanRevision,
        List<RenderPacket> packets)
    {
        for (int i = 0; i < ops.Length; i++)
        {
            int consumed = TryLowerCompatibleMeshPacket(
                targetImageIndex,
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
                    targetImageIndex, ops[i], i, dynamicOverlay, resourcePlanRevision, preparedMeshDraw));
        }
    }

    private int TryLowerCompatibleMeshPacket(
        uint targetImageIndex,
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
        VulkanRecordedRenderTargetSnapshot nativeTarget =
            CaptureRecordedRenderTargetSnapshot(first, targetImageIndex);
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
            first.Context.SubmissionQueueFamily,
            nativeTarget);
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
        packet.SetRecordedPacketKey(CaptureRecordedPacketKey(
            ops,
            startIndex,
            runCount,
            nativeTarget,
            descriptorSnapshot,
            resourceSnapshot));
        packet.Seal();
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
        uint targetImageIndex,
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
        VulkanRecordedRenderTargetSnapshot nativeTarget =
            CaptureRecordedRenderTargetSnapshot(op, targetImageIndex);
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
            op.Context.SubmissionQueueFamily,
            nativeTarget);

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
        uint targetImageIndex)
    {
        XRFrameBuffer? target = op.Target ?? op.Context.OutputFrameBuffer;
        if (target is not null)
        {
            return TryGetAPIRenderObject(target, out var apiObject) &&
                   apiObject is VkFrameBuffer frameBuffer &&
                   frameBuffer.TryCaptureRecordedRenderTargetSnapshot(
                       out VulkanRecordedRenderTargetSnapshot explicitSnapshot)
                ? explicitSnapshot
                : default;
        }

        if (IsRenderingExternalSwapchainTarget)
        {
            OpenXrEyeRenderTargetContext openXrTarget =
                _openXrBackend.CurrentThreadExecutionState.NativeTargetContext;
            if (!openXrTarget.IsValid)
                return default;

            VulkanRecordedRenderTargetSnapshot openXrSnapshot = default;
            openXrSnapshot.Initialize(
                framebufferHandle: 0UL,
                framebufferGeneration: 0UL,
                openXrTarget.Extent.Width,
                openXrTarget.Extent.Height,
                viewMask: 0u,
                attachmentCount: 2);
            openXrSnapshot.SetAttachment(
                0,
                new VulkanNativeAttachmentIdentity(
                    openXrTarget.Image.Handle,
                    GetCurrentVulkanResourceGeneration(
                        ObjectType.Image,
                        openXrTarget.Image.Handle),
                    openXrTarget.ImageView.Handle,
                    GetCurrentVulkanResourceGeneration(
                        ObjectType.ImageView,
                        openXrTarget.ImageView.Handle),
                    ImageLayout.ColorAttachmentOptimal));
            openXrSnapshot.SetAttachment(
                1,
                new VulkanNativeAttachmentIdentity(
                    openXrTarget.DepthImage.Handle,
                    GetCurrentVulkanResourceGeneration(
                        ObjectType.Image,
                        openXrTarget.DepthImage.Handle),
                    openXrTarget.DepthView.Handle,
                    GetCurrentVulkanResourceGeneration(
                        ObjectType.ImageView,
                        openXrTarget.DepthView.Handle),
                    ImageLayout.DepthStencilAttachmentOptimal));
            return openXrSnapshot;
        }

        if (swapChainImages is null ||
            swapChainImageViews is null ||
            targetImageIndex >= swapChainImages.Length ||
            targetImageIndex >= swapChainImageViews.Length)
        {
            return default;
        }

        Image colorImage = swapChainImages[targetImageIndex];
        ImageView colorView = swapChainImageViews[targetImageIndex];
        VulkanSwapchainDepthResources? depth = CurrentSwapchainDepthResources;
        int attachmentCount = depth is null ? 1 : 2;
        Framebuffer framebuffer = !UseDynamicRenderingRenderTargets &&
                                  swapChainFramebuffers is not null &&
                                  targetImageIndex < swapChainFramebuffers.Length
            ? swapChainFramebuffers[targetImageIndex]
            : default;
        VulkanRecordedRenderTargetSnapshot snapshot = default;
        snapshot.Initialize(
            framebuffer.Handle,
            framebuffer.Handle == 0UL
                ? 0UL
                : GetCurrentVulkanResourceGeneration(
                    ObjectType.Framebuffer,
                    framebuffer.Handle),
            swapChainExtent.Width,
            swapChainExtent.Height,
            op.Context.MultiviewEnabled ? 0b11u : 0u,
            attachmentCount);
        snapshot.SetAttachment(
            0,
            new VulkanNativeAttachmentIdentity(
                colorImage.Handle,
                GetCurrentVulkanResourceGeneration(
                    ObjectType.Image,
                    colorImage.Handle),
                colorView.Handle,
                GetCurrentVulkanResourceGeneration(
                    ObjectType.ImageView,
                    colorView.Handle),
                ImageLayout.ColorAttachmentOptimal));
        if (depth is { } depthTarget)
        {
            snapshot.SetAttachment(
                1,
                new VulkanNativeAttachmentIdentity(
                    depthTarget.Image.Handle,
                    GetCurrentVulkanResourceGeneration(
                        ObjectType.Image,
                        depthTarget.Image.Handle),
                    depthTarget.View.Handle,
                    GetCurrentVulkanResourceGeneration(
                        ObjectType.ImageView,
                        depthTarget.View.Handle),
                    ImageLayout.DepthStencilAttachmentOptimal));
        }

        return snapshot;
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

        // Descriptor handles and payloads are only selected during binding
        // preparation. Never authorize reuse from the old aggregate fingerprint:
        // a later prepared key replaces this incomplete placeholder.
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

        vertexOverflow |= !verticesComplete;
        auxiliaryOverflow |= !indicesComplete;
        for (int i = 0; i < capturedVertexCount; i++)
            AddRecordedBufferIdentity(
                capturedVertices[i],
                vertexScratch,
                ref vertexCount,
                ref vertexOverflow);

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
        LowerFrameOpsToRenderPackets(0u, staticOps, dynamicOverlay: false, resourcePlanRevision, sequential);
        LowerFrameOpsToRenderPackets(0u, volatileOps, dynamicOverlay: true, resourcePlanRevision, sequential);
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

        if (op.Context.PipelineInstance?.Pipeline is UserInterfaceRenderPipeline)
            return true;

        if (ResolveSecondaryCachePolicy(op) !=
            ERenderPassSecondaryCachePolicy.Stable)
        {
            return true;
        }

        return op.Context.ContextKind is
            EVulkanFrameOpContextKind.OpenXrMirror or
            EVulkanFrameOpContextKind.SceneCapture or
            EVulkanFrameOpContextKind.LightProbeCapture or
            EVulkanFrameOpContextKind.UiPreview or
            EVulkanFrameOpContextKind.DiagnosticCapture;
    }

    private static ERenderPassSecondaryCachePolicy ResolveSecondaryCachePolicy(
        FrameOp op)
    {
        if (op.Context.PassMetadata is not { Count: > 0 } metadata)
            return ERenderPassSecondaryCachePolicy.Stable;

        foreach (RenderPassMetadata pass in metadata)
        {
            if (pass.PassIndex == op.PassIndex)
                return pass.SecondaryCachePolicy;
        }

        return ERenderPassSecondaryCachePolicy.Stable;
    }

    private static bool IsOverlayLikePass(FrameOp op)
    {
        string? name = TryGetPassName(op);
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
