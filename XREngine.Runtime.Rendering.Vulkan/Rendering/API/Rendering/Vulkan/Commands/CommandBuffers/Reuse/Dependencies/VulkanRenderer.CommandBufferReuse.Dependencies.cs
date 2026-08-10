using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Silk.NET.Vulkan;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Rendering.Resources;
using XREngine.Rendering.Vulkan.RenderGraph;

namespace XREngine.Rendering.Vulkan
{
    internal sealed unsafe partial class VulkanCommandRuntime
    {
        private static bool HasQueryFrameOps(ReadOnlySpan<FrameOp> ops)
        {
            for (int i = 0; i < ops.Length; i++)
            {
                if (ops[i] is QueryOp)
                    return true;
            }

            return false;
        }

        private static void MarkPrimaryCommandArtifactOwnerTransient(PrimaryCommandArtifactOwner variant, string reason)
        {
            // The command buffer recorded immediately before this call is still the current
            // submit candidate. Transient means "record again next time"; erasing its recorded
            // context/dependency metadata here makes the current pre-submit guard reject a
            // command buffer that was just recorded successfully.
            variant.Dirty = true;
            variant.DirtyReason = reason;
        }

        private static void MarkPrimaryCommandArtifactOwnerDirtyAfterConcurrentInvalidation(PrimaryCommandArtifactOwner variant)
        {
            variant.Dirty = true;
            variant.DirtyReason = "concurrent invalidation during primary record";
        }

        private CommandBufferGenerationDomains CaptureCommandBufferGenerationDomains(
            uint imageIndex,
            ulong structuralSignature,
            ReadOnlySpan<FrameOp> staticOps,
            ReadOnlySpan<FrameOp> volatileOps,
            ulong overlaySignature,
            in FrameOpContext context,
            ulong frameOpContextFingerprint,
            bool profilerActive,
            int profilerFrameSlot)
            => new(
                Structural: structuralSignature,
                FrameData: ComputeFrameDataGeneration(staticOps, volatileOps),
                CameraPose: ResolveCameraPoseReplayGeneration(
                    frameOpContextFingerprint,
                    ComputeCameraPoseGeneration(staticOps, volatileOps, context)),
                TargetSlot: imageIndex + 1UL,
                Descriptor: context.DescriptorGeneration,
                ResourceAllocation: context.ResourceGeneration,
                Query: ComputeQueryGeneration(staticOps, volatileOps),
                Overlay: overlaySignature,
                Profiler: ((profilerActive ? 1UL : 0UL) << 32) | unchecked((uint)(profilerFrameSlot + 1)));

        private static CommandRecordingDependencySignature CaptureCommandRecordingDependencySignature(
            uint imageIndex,
            ulong resourcePlanGeneration,
            ulong volatileSuffixSignature,
            in FrameOpContext context,
            in CommandBufferGenerationDomains generations,
            ReadOnlySpan<FrameOp> preparedStaticOps,
            ulong sharedGraphicsPipelineGeneration)
        {
            VulkanFrameOpPlannerStateKey plannerState =
                VulkanFrameOpSnapshotSignatures.BuildPlannerStateKey(context);
            int passMetadataSignature = plannerState.PassMetadataSignature;
            int resourceRegistrySignature = plannerState.ResourceRegistrySignature;

            FrameOpSignatureHasher renderAreaHash = new();
            renderAreaHash.Add(context.DisplayWidth);
            renderAreaHash.Add(context.DisplayHeight);
            renderAreaHash.Add(context.InternalWidth);
            renderAreaHash.Add(context.InternalHeight);

            FrameOpSignatureHasher outputPassAttachmentHash = new();
            outputPassAttachmentHash.Add(context.OutputFrameBufferIdentity);
            outputPassAttachmentHash.Add(context.OutputTargetIdentity);
            outputPassAttachmentHash.Add(context.OutputTargetName);
            outputPassAttachmentHash.Add(passMetadataSignature);

            uint viewMask = context.MultiviewEnabled
                ? 0x3u
                : 0x1u;
            FrameOpSignatureHasher inheritanceHash = new();
            inheritanceHash.Add((int)context.ContextKind);
            inheritanceHash.Add(context.PipelineIdentity);
            inheritanceHash.Add(context.ViewportIdentity);
            inheritanceHash.Add(context.OutputFrameBufferIdentity);
            inheritanceHash.Add(context.StereoEnabled);
            inheritanceHash.Add(context.MultiviewEnabled);
            inheritanceHash.Add(passMetadataSignature);
            ulong inheritanceSignature = inheritanceHash.ToHash();

            FrameOpSignatureHasher descriptorBindingHash = new();
            descriptorBindingHash.Add(resourceRegistrySignature);
            descriptorBindingHash.Add(passMetadataSignature);
            ulong descriptorBindingIdentity = descriptorBindingHash.ToHash();

            CapturePreparedBindingIdentities(
                preparedStaticOps,
                commandChainPrimaryOnly: false,
                out ulong meshBindingIdentity,
                out ulong indexBufferBindingIdentity,
                out ulong vertexBufferBindingIdentity,
                out ulong preparedProgramIdentity);

            return new CommandRecordingDependencySignature(
                OutputPassAttachment: outputPassAttachmentHash.ToHash(),
                RenderArea: renderAreaHash.ToHash(),
                ViewMask: viewMask,
                QueueFamily: context.SubmissionQueueFamily,
                DynamicRenderingInheritance: inheritanceSignature,
                PipelineGeneration: sharedGraphicsPipelineGeneration,
                PipelineLayoutGeneration: preparedProgramIdentity,
                MeshBindingIdentity: meshBindingIdentity,
                IndexBufferBindingIdentity: indexBufferBindingIdentity,
                VertexBufferBindingIdentity: vertexBufferBindingIdentity,
                BufferAllocationGeneration: generations.ResourceAllocation,
                ImageAllocationGeneration: unchecked((ulong)(uint)resourceRegistrySignature),
                ImageViewGeneration: unchecked((ulong)(uint)context.OutputFrameBufferIdentity),
                // Immutable descriptor-set and sampler identity remain separate from
                // publication generation. The dependency classifier still treats a
                // publication change as binding state because ordinary descriptor
                // writes invalidate recorded command buffers.
                SamplerAllocationGeneration: descriptorBindingIdentity,
                DescriptorLayoutGeneration: unchecked((ulong)(uint)passMetadataSignature),
                DescriptorSetGeneration: descriptorBindingIdentity,
                ResourcePlanGeneration: resourcePlanGeneration,
                ExternalTargetVariant: imageIndex,
                // Primary artifacts are owned per acquired image and all native
                // descriptor/target dependencies below are captured for that same
                // command-buffer slot. FramePlan.FrameSlot instead rotates with CPU
                // synchronization ownership; it is not encoded command identity and
                // must not invalidate every cached primary on the following frame.
                FrameSlotVariant: checked((int)imageIndex),
                DescriptorPublicationGeneration: generations.Descriptor,
                DataPublicationGeneration: generations.FrameData,
                VolatileSuffixGeneration: volatileSuffixSignature);
        }

        /// <summary>
        /// Replaces aggregate mesh identities with the exact subset encoded inline
        /// by a mixed command-chain primary. Secondary-owned visibility and cascade
        /// membership are validated by their own chain signatures and must not make
        /// the thin primary appear structurally different.
        /// </summary>
        private static CommandRecordingDependencySignature
            CaptureCommandChainPrimaryPreparedBindingDependencies(
                in CommandRecordingDependencySignature signature,
                ReadOnlySpan<FrameOp> ops)
        {
            CapturePreparedBindingIdentities(
                ops,
                commandChainPrimaryOnly: true,
                out ulong meshBindingIdentity,
                out ulong indexBufferBindingIdentity,
                out ulong vertexBufferBindingIdentity,
                out ulong preparedProgramIdentity);
            ulong inlineDescriptorBindingIdentity =
                CaptureCommandChainPrimaryDescriptorBindingIdentity(ops);

            return signature with
            {
                PipelineLayoutGeneration = preparedProgramIdentity,
                MeshBindingIdentity = meshBindingIdentity,
                IndexBufferBindingIdentity = indexBufferBindingIdentity,
                VertexBufferBindingIdentity = vertexBufferBindingIdentity,
                // Inline indirect/copy/query buffer handles are already part of
                // the primary skeleton, while descriptor-backed buffers are
                // protected by descriptor publication identity. The renderer-wide
                // resource generation also includes secondary frame-data arenas
                // and therefore cannot be a thin-primary recording dependency.
                BufferAllocationGeneration = 0,
                // Descriptor-set updates invalidate only command buffers that bind
                // the updated sets. Track the exact inline descriptor snapshots;
                // renderer-wide publication also includes every secondary draw.
                DescriptorPublicationGeneration = inlineDescriptorBindingIdentity,
            };
        }

        private static ulong CaptureCommandChainPrimaryDescriptorBindingIdentity(
            ReadOnlySpan<FrameOp> ops)
        {
            FrameOpSignatureHasher hash = new();
            hash.Add(0x5052494D44455343UL);
            int bindingCount = 0;
            int queryBracketDepth = 0;
            int inlineOpIndex = 0;
            for (int i = 0; i < ops.Length; i++)
            {
                FrameOp op = ops[i];
                if (op is QueryOp queryOp)
                {
                    if (queryOp.Operation == ERenderQueryOperation.Begin)
                        queryBracketDepth++;
                    else if (queryOp.Operation == ERenderQueryOperation.End && queryBracketDepth > 0)
                        queryBracketDepth--;
                    inlineOpIndex++;
                    continue;
                }

                if (queryBracketDepth == 0 &&
                    IsSchedulableCommandChainFrameOp(op, dynamicOverlay: false))
                {
                    continue;
                }

                // Secondary-owned draw counts can change with visibility without
                // changing the primary command topology. Key inline bindings by
                // their ordinal in the thin primary, not by their raw source-op
                // index, so those secondary insertions do not invalidate an
                // otherwise identical compute or descriptor-set bind.
                int bindingOpIndex = inlineOpIndex++;
                if (op is ComputeDispatchOp computeDispatch)
                {
                    // Reusable compute dispatches bind descriptor sets from the
                    // per-image cache using this structural key. Their sampled
                    // images and uniform values are frame data: the completed
                    // frame slot refreshes those descriptor contents in place
                    // before submission. Hashing the mutable snapshot here made
                    // camera-dependent auto exposure invalidate the thin primary
                    // even though its vkCmdBindDescriptorSets command was unchanged.
                    hash.Add(bindingOpIndex);
                    hash.Add(ComputeReusableComputeDescriptorBindingKey(
                        computeDispatch,
                        bindingOpIndex));
                    hash.Add(computeDispatch.Program.BindingId);
                    hash.Add(computeDispatch.Program.LinkGeneration);
                    bindingCount++;
                    continue;
                }

                DescriptorBindingSnapshot snapshot = CreateDescriptorSnapshot(op);
                if (snapshot.DescriptorSetCount == 0 &&
                    snapshot.DescriptorGeneration == 0 &&
                    snapshot.DescriptorSetSignature == 0)
                {
                    continue;
                }

                hash.Add(bindingOpIndex);
                hash.Add(snapshot.DescriptorGeneration);
                hash.Add(snapshot.DescriptorSetCount);
                hash.Add(snapshot.DescriptorSetSignature);
                bindingCount++;
            }

            hash.Add(bindingCount);
            return hash.ToHash();
        }

        private static void CapturePreparedBindingIdentities(
            ReadOnlySpan<FrameOp> ops,
            bool commandChainPrimaryOnly,
            out ulong meshIdentity,
            out ulong indexIdentity,
            out ulong vertexIdentity,
            out ulong programIdentity)
        {
            FrameOpSignatureHasher meshHash = new();
            FrameOpSignatureHasher indexHash = new();
            FrameOpSignatureHasher vertexHash = new();
            FrameOpSignatureHasher programHash = new();
            int queryBracketDepth = 0;
            for (int i = 0; i < ops.Length; i++)
            {
                FrameOp op = ops[i];
                if (op is QueryOp queryOp)
                {
                    if (queryOp.Operation == ERenderQueryOperation.Begin)
                        queryBracketDepth++;
                    else if (queryOp.Operation == ERenderQueryOperation.End && queryBracketDepth > 0)
                        queryBracketDepth--;
                    continue;
                }

                if (commandChainPrimaryOnly &&
                    queryBracketDepth == 0 &&
                    IsSchedulableCommandChainFrameOp(op, dynamicOverlay: false))
                {
                    continue;
                }

                PendingMeshDraw draw = op switch
                {
                    MeshDrawOp direct => direct.Draw,
                    IndirectDrawOp indirect => indirect.Draw,
                    _ => default,
                };
                if (draw.Renderer is not { } renderer)
                    continue;

                int rendererIdentity = RuntimeHelpers.GetHashCode(renderer);
                int meshObjectIdentity = renderer.Mesh is null ? 0 : RuntimeHelpers.GetHashCode(renderer.Mesh);
                meshHash.Add(rendererIdentity);
                meshHash.Add(meshObjectIdentity);
                indexHash.Add(meshObjectIdentity);
                indexHash.Add((int)(renderer.Mesh?.Type ?? EPrimitiveType.Triangles));
                vertexHash.Add(rendererIdentity);
                vertexHash.Add(renderer.Mesh?.Buffers is null ? 0 : RuntimeHelpers.GetHashCode(renderer.Mesh.Buffers));
                programHash.Add(draw.PreparedProgramIdentity);
                programHash.Add(draw.PreparedProgram?.BindingId ?? 0u);
                programHash.Add(draw.PreparedProgram?.LinkGeneration ?? 0UL);
            }

            meshIdentity = meshHash.ToHash();
            indexIdentity = indexHash.ToHash();
            vertexIdentity = vertexHash.ToHash();
            programIdentity = programHash.ToHash();
        }

        private ulong ResolveCameraPoseReplayGeneration(ulong contextFingerprint, ulong rawPoseGeneration)
        {
            if (rawPoseGeneration == 0)
                return 0;

            ref CameraPoseReuseState? state = ref CollectionsMarshal.GetValueRefOrAddDefault(
                _cameraPoseReuseStates,
                contextFingerprint,
                out bool exists);
            state ??= new CameraPoseReuseState
            {
                RawPoseGeneration = rawPoseGeneration,
                LastObservedFrame = VulkanFrameCounter,
            };

            if (!exists)
                return CombineCameraPoseReplayGeneration(rawPoseGeneration, state.ReplayGeneration);

            ulong frame = VulkanFrameCounter;
            if (state.LastObservedFrame == frame)
                return CombineCameraPoseReplayGeneration(state.RawPoseGeneration, state.ReplayGeneration);

            state.LastObservedFrame = frame;
            if (state.RawPoseGeneration != rawPoseGeneration)
            {
                state.RawPoseGeneration = rawPoseGeneration;
                state.ReplayGeneration++;
                state.SettleInvalidationPending = true;
            }
            else if (state.SettleInvalidationPending)
            {
                // Previous-camera matrices and temporal history converge on the first frame after
                // input stops. Advance the replay generation once more so no inline primary from
                // the final moving frame can be selected for that boundary frame.
                state.ReplayGeneration++;
                state.SettleInvalidationPending = false;
            }

            return CombineCameraPoseReplayGeneration(state.RawPoseGeneration, state.ReplayGeneration);
        }

        private static ulong CombineCameraPoseReplayGeneration(ulong rawPoseGeneration, ulong replayGeneration)
        {
            FrameOpSignatureHasher hash = new();
            hash.Add(rawPoseGeneration);
            hash.Add(replayGeneration);
            return hash.ToHash();
        }

        private static ulong ComputeCameraPoseGeneration(
            ReadOnlySpan<FrameOp> staticOps,
            ReadOnlySpan<FrameOp> volatileOps,
            in FrameOpContext outputContext)
        {
            // A primary-cache camera transition only concerns the camera that owns the output
            // viewport. Shadow/capture cameras can legitimately move while the desktop view is
            // stationary; including them makes the swapchain primary re-record every frame.
            // Visibility and query-probe work can also change draw order/count without moving
            // that camera, so preserve only a deduplicated, order-independent pose set.
            Span<ulong> uniqueCameraPoseSignatures = stackalloc ulong[128];
            int uniqueCameraPoseCount = 0;
            bool exceededInlineCapacity = false;
            AddCameraPoseGenerationParts(
                staticOps,
                outputContext.ViewportIdentity,
                uniqueCameraPoseSignatures,
                ref uniqueCameraPoseCount,
                ref exceededInlineCapacity);
            AddCameraPoseGenerationParts(
                volatileOps,
                outputContext.ViewportIdentity,
                uniqueCameraPoseSignatures,
                ref uniqueCameraPoseCount,
                ref exceededInlineCapacity);

            if (uniqueCameraPoseCount == 0)
                return 0UL;

            if (exceededInlineCapacity)
            {
                return ComputeCameraPoseGenerationConservatively(
                    staticOps,
                    volatileOps,
                    outputContext.ViewportIdentity);
            }

            SortCameraPoseSignatures(uniqueCameraPoseSignatures, uniqueCameraPoseCount);
            FrameOpSignatureHasher hash = new();
            hash.Add(uniqueCameraPoseCount);
            for (int i = 0; i < uniqueCameraPoseCount; i++)
                hash.Add(uniqueCameraPoseSignatures[i]);
            return hash.ToHash();
        }

        private static void AddCameraPoseGenerationParts(
            ReadOnlySpan<FrameOp> ops,
            int outputViewportIdentity,
            Span<ulong> uniqueCameraPoseSignatures,
            ref int uniqueCameraPoseCount,
            ref bool exceededInlineCapacity)
        {
            for (int i = 0; i < ops.Length; i++)
            {
                if (!TryGetPrimaryViewportCameraPoseDraw(
                        ops[i],
                        outputViewportIdentity,
                        out PendingMeshDraw draw))
                {
                    continue;
                }

                ulong signature = ComputeCameraPoseSignature(draw);
                bool alreadyCaptured = false;
                for (int poseIndex = 0; poseIndex < uniqueCameraPoseCount; poseIndex++)
                {
                    if (uniqueCameraPoseSignatures[poseIndex] != signature)
                        continue;

                    alreadyCaptured = true;
                    break;
                }

                if (alreadyCaptured)
                    continue;

                if (uniqueCameraPoseCount >= uniqueCameraPoseSignatures.Length)
                {
                    exceededInlineCapacity = true;
                    continue;
                }

                uniqueCameraPoseSignatures[uniqueCameraPoseCount++] = signature;
            }
        }

        private static ulong ComputeCameraPoseGenerationConservatively(
            ReadOnlySpan<FrameOp> staticOps,
            ReadOnlySpan<FrameOp> volatileOps,
            int outputViewportIdentity)
        {
            FrameOpSignatureHasher hash = new();
            int cameraDrawCount = 0;
            AddCameraPoseGenerationPartsConservatively(
                ref hash,
                staticOps,
                outputViewportIdentity,
                ref cameraDrawCount);
            AddCameraPoseGenerationPartsConservatively(
                ref hash,
                volatileOps,
                outputViewportIdentity,
                ref cameraDrawCount);
            return cameraDrawCount == 0 ? 0UL : hash.ToHash();
        }

        private static void AddCameraPoseGenerationPartsConservatively(
            ref FrameOpSignatureHasher hash,
            ReadOnlySpan<FrameOp> ops,
            int outputViewportIdentity,
            ref int cameraDrawCount)
        {
            for (int i = 0; i < ops.Length; i++)
            {
                if (!TryGetPrimaryViewportCameraPoseDraw(
                        ops[i],
                        outputViewportIdentity,
                        out PendingMeshDraw draw))
                {
                    continue;
                }

                cameraDrawCount++;
                hash.Add(ComputeCameraPoseSignature(draw));
            }
        }

        private static bool TryGetPrimaryViewportCameraPoseDraw(
            FrameOp op,
            int outputViewportIdentity,
            out PendingMeshDraw draw)
        {
            switch (op)
            {
                case MeshDrawOp meshDraw when IsCameraAttachedToOutputViewport(
                    meshDraw.Draw.Camera,
                    outputViewportIdentity):
                    draw = meshDraw.Draw;
                    return true;
                case IndirectDrawOp indirectDraw when IsCameraAttachedToOutputViewport(
                    indirectDraw.Draw.Camera,
                    outputViewportIdentity):
                    draw = indirectDraw.Draw;
                    return true;
                default:
                    draw = default;
                    return false;
            }
        }

        private static bool IsCameraAttachedToOutputViewport(
            XRCamera? camera,
            int outputViewportIdentity)
        {
            if (camera is null)
                return false;

            if (outputViewportIdentity == 0)
                return true;

            int viewportCount = camera.Viewports.Count;
            if (viewportCount == 0)
                return true;

            for (int i = 0; i < viewportCount; i++)
            {
                XRViewport viewport = camera.Viewports[i];
                if (RuntimeHelpers.GetHashCode(viewport) != outputViewportIdentity)
                    continue;

                return viewport.RenderPipeline?.IsShadowPass != true;
            }

            return false;
        }

        private static ulong ComputeCameraPoseSignature(in PendingMeshDraw draw)
        {
            FrameOpSignatureHasher hash = new();
            hash.Add(draw.Camera is null ? 0 : RuntimeHelpers.GetHashCode(draw.Camera));
            hash.Add(draw.StereoRightEyeCamera is null ? 0 : RuntimeHelpers.GetHashCode(draw.StereoRightEyeCamera));
            hash.Add(draw.IsStereoPass);
            hash.Add(draw.UseUnjitteredProjection);
            AddVector3Signature(ref hash, draw.CameraPosition);
            AddVector3Signature(ref hash, draw.CameraForward);
            AddVector3Signature(ref hash, draw.CameraUp);
            AddVector3Signature(ref hash, draw.CameraRight);
            return hash.ToHash();
        }

        private static void SortCameraPoseSignatures(Span<ulong> signatures, int count)
        {
            for (int i = 1; i < count; i++)
            {
                ulong value = signatures[i];
                int insertionIndex = i - 1;
                while (insertionIndex >= 0 && signatures[insertionIndex] > value)
                {
                    signatures[insertionIndex + 1] = signatures[insertionIndex];
                    insertionIndex--;
                }

                signatures[insertionIndex + 1] = value;
            }
        }

        private static ulong ComputeFrameDataGeneration(
            ReadOnlySpan<FrameOp> staticOps,
            ReadOnlySpan<FrameOp> volatileOps)
        {
            FrameOpSignatureHasher hash = new();
            hash.Add(staticOps.Length);
            for (int i = 0; i < staticOps.Length; i++)
                hash.Add(ComputeFrameOpFrameDataSignature(staticOps[i], i));
            hash.Add(volatileOps.Length);
            for (int i = 0; i < volatileOps.Length; i++)
                hash.Add(ComputeFrameOpFrameDataSignature(volatileOps[i], i));
            return hash.ToHash();
        }

        private static ulong ComputeQueryGeneration(
            ReadOnlySpan<FrameOp> staticOps,
            ReadOnlySpan<FrameOp> volatileOps)
        {
            FrameOpSignatureHasher hash = new();
            int queryCount = 0;
            hash.Add(staticOps.Length);
            AddQueryGenerationParts(ref hash, staticOps, ref queryCount);
            hash.Add(volatileOps.Length);
            AddQueryGenerationParts(ref hash, volatileOps, ref queryCount);
            return queryCount == 0 ? 0UL : hash.ToHash();
        }

        private static void AddQueryGenerationParts(
            ref FrameOpSignatureHasher hash,
            ReadOnlySpan<FrameOp> ops,
            ref int queryCount)
        {
            int queryBracketDepth = 0;
            for (int i = 0; i < ops.Length; i++)
            {
                FrameOp op = ops[i];
                if (op is QueryOp query)
                {
                    queryCount++;
                    hash.Add(i);
                    hash.Add(ComputeFrameOpStructuralSignature(
                        query,
                        i,
                        RenderPacketVolatility.FrameDataOnly));

                    if (query.Operation == ERenderQueryOperation.Begin)
                        queryBracketDepth++;
                    else if (query.Operation == ERenderQueryOperation.End && queryBracketDepth > 0)
                        queryBracketDepth--;
                    continue;
                }

                // Query frame ops are intentionally omitted from command-chain groups and
                // stay inline in the primary. Include the exact bracket position and every
                // enclosed draw in the primary-cache identity so a previous layout cannot
                // attribute a proxy draw to the wrong query object.
                if (queryBracketDepth > 0)
                {
                    hash.Add(i);
                    hash.Add(ComputeFrameOpStructuralSignature(
                        op,
                        i,
                        RenderPacketVolatility.FrameDataOnly));
                }
            }
        }

        private string DescribePrimaryReuseMiss(
            PrimaryCommandArtifactOwner variant,
            in CommandBufferGenerationDomains current,
            in CommandRecordingDependencyMismatch dependencyMismatch,
            bool forcedDirty,
            bool imageForcedDirty,
            string? forcedVariantDirtyReason,
            bool frameOpSignatureDirty,
            bool plannerDirty,
            bool profilerDirty,
            bool frameDataDirty,
            bool dynamicUiDirty,
            bool swapchainLifecycleDirty,
            bool commandChainPrimaryDirty,
            PrimaryCommandBufferDirtyReason commandChainPrimaryDirtyReason,
            ulong commandChainScheduleSignature,
            ulong commandChainPrimaryGroupSignature,
            in VulkanCommandIdentityComponents commandChainPrimaryIdentityComponents,
            int commandChainPrimaryGroupCount,
            bool primaryFrameStateDirty,
            string? primaryFrameStateDirtyReason,
            in VulkanImageEntryStateMismatch primaryImageEntryStateMismatch,
            ulong plannerRevision,
            ulong imageLayoutStartSignature,
            bool swapchainImageEverPresented)
        {
            CommandBufferGenerationDomains previous = variant.RecordedGenerations;
            if (dependencyMismatch.RequiresRecording)
                return $"dependency-signature field={dependencyMismatch.Field} class={dependencyMismatch.InvalidationClass}";
            if (forcedDirty)
            {
                string reason = FormatForcedCommandBufferDirtyReason(
                    imageForcedDirty,
                    variant.Dirty,
                    forcedVariantDirtyReason);
                return $"cache-state old={(variant.Dirty ? "dirty" : "clean")} new=record-required reason={reason}";
            }
            if (frameOpSignatureDirty)
                return $"structural-generation old=0x{previous.Structural:X16} new=0x{current.Structural:X16}";
            if (plannerDirty)
                return $"resource-plan-generation old={variant.PlannerRevision} new={plannerRevision}";
            if (profilerDirty)
                return $"profiler-generation old=0x{previous.Profiler:X16} new=0x{current.Profiler:X16}";
            if (frameDataDirty)
                return $"frame-data-generation old=0x{previous.FrameData:X16} new=0x{current.FrameData:X16} refresh={_lastReusableFrameDataRefreshFailureReason ?? "failed"}";
            if (dynamicUiDirty)
                return $"overlay-generation old=0x{previous.Overlay:X16} new=0x{current.Overlay:X16}";
            if (swapchainLifecycleDirty)
                return $"target-slot-state slot={current.TargetSlot} presented={variant.RecordedSwapchainImageEverPresented}->{swapchainImageEverPresented}";
            if (commandChainPrimaryDirty)
                return DescribePrimaryCommandChainReuseMiss(
                    variant,
                    commandChainPrimaryDirtyReason,
                    commandChainScheduleSignature,
                    commandChainPrimaryGroupSignature,
                    commandChainPrimaryIdentityComponents,
                    commandChainPrimaryGroupCount,
                    current.Profiler);
            if (primaryFrameStateDirty)
            {
                if (string.Equals(primaryFrameStateDirtyReason, "query-pool-prepare", StringComparison.Ordinal))
                    return $"query-generation old=0x{previous.Query:X16} new=0x{current.Query:X16}";
                if (string.Equals(primaryFrameStateDirtyReason, "image-layout-entry-state", StringComparison.Ordinal))
                    return DescribePrimaryImageEntryStateMismatch(
                        primaryImageEntryStateMismatch,
                        variant.RecordedImageLayoutStartSignature,
                        imageLayoutStartSignature);
                return $"primary-frame-state old=cached new=record-required field={primaryFrameStateDirtyReason ?? "unknown"}";
            }

            if (previous.Descriptor != current.Descriptor)
                return $"descriptor-generation old={previous.Descriptor} new={current.Descriptor}";
            if (previous.ResourceAllocation != current.ResourceAllocation)
                return $"resource-allocation-generation old={previous.ResourceAllocation} new={current.ResourceAllocation}";
            return "cache-state old=unknown new=record-required reason=unclassified";
        }

        private static string DescribePrimaryImageEntryStateMismatch(
            in VulkanImageEntryStateMismatch mismatch,
            ulong recordedGlobalSignature,
            ulong currentGlobalSignature)
            => $"image-layout-entry-state kind={mismatch.Kind} image=0x{mismatch.ImageHandle:X} " +
               $"mip={mismatch.MipLevel} layer={mismatch.ArrayLayer} aspect={mismatch.Aspect} " +
               $"expected=(layout={mismatch.Expected.Layout},stage=0x{(ulong)mismatch.Expected.StageMask:X}," +
               $"access=0x{(ulong)mismatch.Expected.AccessMask:X},descriptor={mismatch.Expected.ExpectedDescriptorLayout}," +
               $"queue={mismatch.Expected.QueueFamilyIndex},generation={mismatch.Expected.ResourceGeneration}) " +
               $"actual=(layout={mismatch.Actual.Layout},stage=0x{(ulong)mismatch.Actual.StageMask:X}," +
               $"access=0x{(ulong)mismatch.Actual.AccessMask:X},descriptor={mismatch.Actual.ExpectedDescriptorLayout}," +
               $"queue={mismatch.Actual.QueueFamilyIndex},generation={mismatch.Actual.ResourceGeneration}) " +
               $"global=(recorded=0x{recordedGlobalSignature:X16},current=0x{currentGlobalSignature:X16})";

        private static string DescribePrimaryCommandChainReuseMiss(
            PrimaryCommandArtifactOwner variant,
            PrimaryCommandBufferDirtyReason reasons,
            ulong scheduleSignature,
            ulong groupSignature,
            in VulkanCommandIdentityComponents currentIdentityComponents,
            int groupCount,
            ulong profilerGeneration)
        {
            if ((reasons & PrimaryCommandBufferDirtyReason.ScheduleStructure) != 0)
                return $"primary-chain-schedule old=0x{variant.CommandChainScheduleSignature:X16} new=0x{scheduleSignature:X16}";
            if ((reasons & PrimaryCommandBufferDirtyReason.GroupStructure) != 0)
            {
                VulkanCommandIdentityMismatch mismatch =
                    variant.CommandChainPrimaryIdentityComponents.Compare(
                        currentIdentityComponents);
                return mismatch.RequiresRecording
                    ? $"primary-chain-groups component={mismatch.Component} old=0x{mismatch.Recorded:X16} new=0x{mismatch.Current:X16} groups={variant.CommandChainPrimaryGroupCount}->{groupCount}"
                    : $"primary-chain-groups old=0x{variant.CommandChainPrimaryGroupSignature:X16}/{variant.CommandChainPrimaryGroupCount} new=0x{groupSignature:X16}/{groupCount}";
            }
            if ((reasons & PrimaryCommandBufferDirtyReason.SecondaryArtifactSequence) != 0)
                return "primary-chain-secondary-artifact-sequence old=recorded new=changed";
            if ((reasons & PrimaryCommandBufferDirtyReason.ProfilerMode) != 0)
                return $"primary-chain-profiler old=0x{variant.RecordedGenerations.Profiler:X16} new=0x{profilerGeneration:X16}";
            return $"primary-chain-state old=clean new=record-required field={PrimaryCommandBufferDirtyReason.None}";
        }

        private static ulong ComputeCommandBufferFrameOpContextFingerprint(
            ReadOnlySpan<FrameOp> ops,
            FrameOp[] dynamicUiBatchTextOps,
            in FrameOpContext fallbackContext)
        {
            FrameOpSignatureHasher hash = new();
            hash.Add(0x434D444354584654UL);
            AddFrameOpContextFingerprints(ref hash, ops);
            AddFrameOpContextFingerprints(ref hash, dynamicUiBatchTextOps);
            if (ops.Length == 0 && dynamicUiBatchTextOps.Length == 0)
                hash.Add(fallbackContext.RecordingFingerprint);

            return hash.ToHash();
        }

        private static void AddFrameOpContextFingerprints(
            ref FrameOpSignatureHasher hash,
            ReadOnlySpan<FrameOp> ops)
        {
            hash.Add(ops.Length);
            for (int i = 0; i < ops.Length; i++)
            {
                hash.Add(ops[i].Context.RecordingFingerprint);
                hash.Add((int)ops[i].Context.ContextKind);
            }
        }

        private static ulong ResolveCommandBufferFrameOpContextId(
            ReadOnlySpan<FrameOp> ops,
            ReadOnlySpan<FrameOp> dynamicUiBatchTextOps,
            in FrameOpContext fallbackContext)
        {
            if (ops.Length > 0)
                return ops[0].Context.ContextId;
            if (dynamicUiBatchTextOps.Length > 0)
                return dynamicUiBatchTextOps[0].Context.ContextId;
            return fallbackContext.ContextId;
        }

        private static bool IsCommandBufferVariantFrameOpContextDirty(
            PrimaryCommandArtifactOwner variant,
            ulong frameOpContextFingerprint)
            => variant.RecordedFrameOpContextFingerprint != frameOpContextFingerprint;

        private bool TryValidateCommandBufferVariantContext(
            uint imageIndex,
            PrimaryCommandArtifactOwner variant,
            ulong frameOpContextFingerprint,
            ulong frameOpContextId,
            string reusePath)
        {
            if (!IsCommandBufferVariantFrameOpContextDirty(variant, frameOpContextFingerprint))
                return true;

            LogCommandBufferFrameOpContextMismatch(
                imageIndex,
                variant,
                frameOpContextFingerprint,
                frameOpContextId,
                reusePath);
            return false;
        }

        internal void EnsureCommandBufferVariantContextBeforeSubmit(
            uint imageIndex,
            PrimaryCommandArtifactOwner variant,
            ulong frameOpContextFingerprint,
            ulong frameOpContextId,
            string submitPath)
        {
            if (!IsCommandBufferVariantFrameOpContextDirty(variant, frameOpContextFingerprint))
                return;

            LogCommandBufferFrameOpContextMismatch(
                imageIndex,
                variant,
                frameOpContextFingerprint,
                frameOpContextId,
                submitPath);
            throw new InvalidOperationException(
                $"Vulkan command buffer frame-op context mismatch before submit in {submitPath}. " +
                $"Image={imageIndex} RecordedContextId={variant.RecordedFrameOpContextId} " +
                $"Recorded=0x{variant.RecordedFrameOpContextFingerprint:X16} CurrentContextId={frameOpContextId} " +
                $"Current=0x{frameOpContextFingerprint:X16}.");
        }

        private void LogCommandBufferFrameOpContextMismatch(
            uint imageIndex,
            PrimaryCommandArtifactOwner variant,
            ulong frameOpContextFingerprint,
            ulong frameOpContextId,
            string reusePath)
        {
            string policy = ShouldFailFastOnFrameOpContextMismatch()
                ? "diagnostic-fail-fast-before-submit"
                : "discard-rerecord";
            Debug.VulkanWarningEvery(
                $"Vulkan.CommandBuffer.FrameOpContextMismatch.{GetHashCode()}.{imageIndex}.{reusePath}",
                TimeSpan.FromSeconds(1),
                "[Vulkan] frame-op context mismatch in {0}; rejecting cached primary before submit. Image={1} Policy={2} RecordedContextId={3} Recorded=0x{4:X16} CurrentContextId={5} Current=0x{6:X16}",
                reusePath,
                imageIndex,
                policy,
                variant.RecordedFrameOpContextId,
                variant.RecordedFrameOpContextFingerprint,
                frameOpContextId,
                frameOpContextFingerprint);
        }

        private bool ShouldFailFastOnFrameOpContextMismatch()
            => _frameTelemetry._diagnosticOptions.EnableValidationLayers ||
               _frameTelemetry._diagnosticOptions.EnableCrashBreadcrumbs ||
               _frameTelemetry._diagnosticOptions.Preset == EVulkanDiagnosticPreset.CrashDiagnostics;

    }
}
