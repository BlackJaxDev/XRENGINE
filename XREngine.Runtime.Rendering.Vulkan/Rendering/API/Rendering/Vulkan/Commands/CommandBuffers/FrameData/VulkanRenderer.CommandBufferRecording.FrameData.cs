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

namespace XREngine.Rendering.Vulkan
{
    public unsafe partial class VulkanRenderer
    {
        /// <summary>
        /// Normalizes producer-owned pass indices before a frame plan is sealed.
        /// </summary>
        /// <param name="operations">The array of frame operations whose pass indices need to be normalized.</param>
        private void NormalizePrimaryPlanPassIndicesForPublication(FrameOp[] operations)
        {
            // The pass index of each operation must be valid and consistent with the pass metadata.
            // If an operation's pass index is invalid, it will be adjusted to a valid value based on the pass metadata.
            for (int index = 0; index < operations.Length; index++)
            {
                // Ensure that the pass index of the operation is valid and consistent with the pass metadata.
                // If the pass index is invalid, it will be adjusted to a valid value.
                FrameOp operation = operations[index];
                int resolvedPassIndex = EnsureValidPassIndex(
                    operation.PassIndex,
                    GetFrameOpDiagnosticName(operation),
                    operation.Context.PassMetadata);

                // If the resolved pass index is different from the current pass index, update the operation's PassIndex.
                if (resolvedPassIndex != operation.PassIndex)
                    operation.PassIndex = resolvedPassIndex;
            }
        }

        /// <summary>
        /// Validates a sealed frame-plan stream at recording time without
        /// rewriting it. Recording must consume the publication verbatim.
        /// </summary>
        private void ValidatePrimaryPlanPassIndicesForRecording(FrameOperationSequence operations)
        {
            for (int index = 0; index < operations.Length; index++)
            {
                FrameOp operation = operations[index];
                int resolvedPassIndex = EnsureValidPassIndex(
                    operation.PassIndex,
                    GetFrameOpDiagnosticName(operation),
                    operation.Context.PassMetadata);
                if (resolvedPassIndex != operation.PassIndex)
                {
                    throw new InvalidOperationException(
                        $"Sealed frame-plan operation {index} has pass index {operation.PassIndex}, but recording requires {resolvedPassIndex}.");
                }
            }
        }

        /// <summary>
        /// Resolves the appropriate access mask for a given image layout, which is used in Vulkan image memory barriers.
        /// </summary>
        /// <param name="op">The frame operation for which to get the profile scope name.</param>
        /// <returns>The profile scope name for the given frame operation.</returns>
        private static string GetRecordPrimaryFrameOpProfileScopeName(FrameOp op)
            => op switch
            {
                BlitOp => "Vulkan.RecordPrimary.Op.Blit",
                ClearOp => "Vulkan.RecordPrimary.Op.Clear",
                TransformFeedbackOp => "Vulkan.RecordPrimary.Op.TransformFeedback",
                MeshDrawOp => "Vulkan.RecordPrimary.Op.MeshDraw",
                IndirectDrawOp => "Vulkan.RecordPrimary.Op.IndirectDraw",
                MeshTaskDispatchIndirectCountOp => "Vulkan.RecordPrimary.Op.MeshTaskDispatch",
                ComputeDispatchOp => "Vulkan.RecordPrimary.Op.ComputeDispatch",
                ComputeDispatchIndirectOp => "Vulkan.RecordPrimary.Op.ComputeDispatchIndirect",
                BufferCopyOp => "Vulkan.RecordPrimary.Op.BufferCopy",
                SubmissionMarkerOp => "Vulkan.RecordPrimary.Op.SubmissionMarker",
                MemoryBarrierOp => "Vulkan.RecordPrimary.Op.MemoryBarrier",
                PublishFramebufferForSamplingOp => "Vulkan.RecordPrimary.Op.PublishFramebufferForSampling",
                DlssUpscaleOp => "Vulkan.RecordPrimary.Op.DlssUpscale",
                DlssFrameGenerationOp => "Vulkan.RecordPrimary.Op.DlssFrameGeneration",
                TextureUploadFrameOp => "Vulkan.RecordPrimary.Op.TextureUpload",
                _ => "Vulkan.RecordPrimary.Op.Unknown"
            };

        /// <summary>
        /// Returns an interned diagnostic label without runtime type inspection
        /// or enum formatting on the planning and recording paths.
        /// </summary>
        private static string GetFrameOpDiagnosticName(FrameOp op)
            => op switch
            {
                BlitOp => "Blit",
                ClearOp => "Clear",
                TransformFeedbackOp => "TransformFeedback",
                MeshDrawOp => "MeshDraw",
                IndirectDrawOp => "IndirectDraw",
                MeshTaskDispatchIndirectCountOp => "MeshTaskDispatch",
                ComputeDispatchOp => "ComputeDispatch",
                ComputeDispatchIndirectOp => "ComputeDispatchIndirect",
                BufferCopyOp => "BufferCopy",
                SubmissionMarkerOp => "SubmissionMarker",
                MemoryBarrierOp => "MemoryBarrier",
                PublishFramebufferForSamplingOp => "PublishFramebufferForSampling",
                DlssUpscaleOp => "DlssUpscale",
                DlssFrameGenerationOp => "DlssFrameGeneration",
                TextureUploadFrameOp => "TextureUpload",
                QueryOp => "Query",
                _ => "Unknown"
            };

        /// <summary>
        /// Collects the mesh frame data requirements for recording based on the provided frame operations and updates the renderer family draw slots and family strides accordingly.
        /// </summary>
        /// <param name="ops">The array of frame operations to process.</param>
        /// <param name="frameDataSlot">The frame data slot index.</param>
        /// <param name="streamKind">The kind of mesh frame data stream.</param>
        /// <param name="rendererFamilyDrawSlots">The dictionary mapping renderer family keys to draw slots.</param>
        /// <param name="familyStrides">The dictionary mapping family keys to strides.</param>
        /// <param name="append">Whether to append to the existing data or clear it first.</param>
        internal static void CollectMeshFrameDataRequirementsForRecording(
            FrameOperationSequence ops,
            int frameDataSlot,
            EVulkanMeshFrameDataStreamKind streamKind,
            Dictionary<VulkanMeshFrameDataRendererFamilyKey, int> rendererFamilyDrawSlots,
            Dictionary<VulkanMeshFrameDataFamilyKey, int> familyStrides,
            bool append = false)
        {
            // If not appending, clear the existing renderer family draw slots and family strides to start fresh.
            if (!append)
            {
                rendererFamilyDrawSlots.Clear();
                familyStrides.Clear();
            }

            // Iterate through each frame operation to collect mesh frame data requirements.
            for (int i = 0; i < ops.Length; i++)
            {
                FrameOp op = ops[i];

                // Only process MeshDrawOp and IndirectDrawOp types, as they are relevant for mesh frame data requirements.
                VkMeshRenderer? renderer;
                PendingMeshDraw draw;
                switch (op)
                {
                    case MeshDrawOp drawOp:
                        renderer = drawOp.Draw.Renderer;
                        draw = drawOp.Draw;
                        break;
                    case IndirectDrawOp indirectDrawOp:
                        renderer = indirectDrawOp.MeshRenderer;
                        draw = indirectDrawOp.Draw;
                        break;
                    default:
                        continue;
                }

                // Create a family key based on the frame data slot, stream kind, operation context, and draw information.
                VulkanMeshFrameDataFamilyKey family =
                    VulkanMeshFrameDataFamilyKey.From(frameDataSlot, streamKind, op.Context, draw);

                // Create a renderer family key based on the renderer and the family key.
                VulkanMeshFrameDataRendererFamilyKey rendererFamily = 
                    new(renderer, family);
                
                // Update the renderer family draw slots and family strides based on the collected requirements.
                rendererFamilyDrawSlots.TryGetValue(rendererFamily, out int count);
                int requiredDrawSlots = count + 1;
                rendererFamilyDrawSlots[rendererFamily] = requiredDrawSlots;
                if (!familyStrides.TryGetValue(family, out int stride) || stride < requiredDrawSlots)
                    familyStrides[family] = requiredDrawSlots;
            }
        }

        private static int GetFrameWideMeshDrawUniformSlot(
            Dictionary<VulkanMeshFrameDataRendererFamilyKey, int> slotsByRendererFamily,
            Dictionary<VulkanMeshFrameDataRendererFamilyKey, int> familyBases,
            VkMeshRenderer renderer,
            int frameDataSlot,
            EVulkanMeshFrameDataStreamKind streamKind,
            in FrameOpContext context,
            in PendingMeshDraw draw)
        {
            VulkanMeshFrameDataFamilyKey family =
                VulkanMeshFrameDataFamilyKey.From(frameDataSlot, streamKind, context, draw);
            VulkanMeshFrameDataRendererFamilyKey rendererFamily = new(renderer, family);
            if (!familyBases.TryGetValue(rendererFamily, out int baseSlot))
            {
                throw new InvalidOperationException(
                    $"Mesh frame-data output family {family} was not published before draw-slot resolution.");
            }

            ref int ordinalRef = ref CollectionsMarshal.GetValueRefOrAddDefault(
                slotsByRendererFamily,
                rendererFamily,
                out _);
            int slot = checked(baseSlot + ordinalRef);
            ordinalRef++;
            return slot;
        }

        private bool TryRegisterFrameWideMeshFrameDataRequirements(
            FrameOperationSequence primaryOps,
            FrameOperationSequence secondaryOps,
            int frameDataSlot,
            bool sealAfterRegister,
            Dictionary<VkMeshRenderer, int> requirements,
            CommandBufferRecordingScratch scratch,
            Dictionary<VulkanMeshFrameDataRendererFamilyKey, int> resolvedFamilyBases,
            out ulong manifestGeneration,
            out string reason)
        {
            CollectMeshFrameDataRequirementsForRecording(
                primaryOps,
                frameDataSlot,
                EVulkanMeshFrameDataStreamKind.Primary,
                scratch.MeshDrawSlotsByRendererFamily,
                scratch.MeshFrameDataFamilyStrides);
            if (secondaryOps.Length > 0)
            {
                CollectMeshFrameDataRequirementsForRecording(
                    secondaryOps,
                    frameDataSlot,
                    EVulkanMeshFrameDataStreamKind.DynamicUi,
                    scratch.MeshDrawSlotsByRendererFamily,
                    scratch.MeshFrameDataFamilyStrides,
                    append: true);
            }

            bool registered = _frameWideMeshFrameDataManifest.TryRegister(
                RuntimeEngine.Rendering.State.RenderFrameId,
                requirements,
                scratch.MeshDrawSlotsByRendererFamily,
                scratch.MeshFrameDataFamilyStrides,
                resolvedFamilyBases,
                sealAfterRegister,
                out manifestGeneration,
                out bool manifestLayoutChanged,
                out reason);
            if (registered)
            {
                if (manifestLayoutChanged)
                    ObserveMeshFrameDataManifestGeneration(manifestGeneration);

                BuildReusableFrameDataRefreshRequests(
                    primaryOps,
                    frameDataSlot,
                    EVulkanMeshFrameDataStreamKind.Primary,
                    dynamicUi: false,
                    resolvedFamilyBases,
                    scratch);
                BuildReusableFrameDataRefreshRequests(
                    secondaryOps,
                    frameDataSlot,
                    EVulkanMeshFrameDataStreamKind.DynamicUi,
                    dynamicUi: true,
                    resolvedFamilyBases,
                    scratch);
            }
            else
            {
                scratch.BeginReusableFrameDataRefreshRequests();
            }
            PublishFrameWideMeshFrameDataManifestGauges();
            return registered;
        }

        private void BuildReusableFrameDataRefreshRequests(
            FrameOperationSequence operations,
            int frameDataSlot,
            EVulkanMeshFrameDataStreamKind streamKind,
            bool dynamicUi,
            Dictionary<VulkanMeshFrameDataRendererFamilyKey, int> familyBases,
            CommandBufferRecordingScratch scratch)
        {
            if (!dynamicUi)
                scratch.BeginReusableFrameDataRefreshRequests();

            Dictionary<VulkanMeshFrameDataRendererFamilyKey, int>
                slotsByRendererFamily =
                    scratch.ReusableMeshDrawSlotsByRendererFamily;
            slotsByRendererFamily.Clear();
            slotsByRendererFamily.EnsureCapacity(
                scratch.ReusableMeshDrawSlotCapacityHint);
            FrameOpSignatureHasher stableMeshHash = new();
            stableMeshHash.Add((int)streamKind);
            stableMeshHash.Add(MappedFrameArena?.Generation ?? 0UL);
            int meshRequestCount = 0;
            bool supportsDirectOwnerOnlyRefresh = true;

            for (int operationIndex = 0;
                 operationIndex < operations.Length;
                 operationIndex++)
            {
                FrameOp operation = operations[operationIndex];
                VulkanFrameOpPlannerStateKey plannerKey =
                    BuildFrameOpPlannerStateKey(operation.Context);
                VulkanReusableFrameDataRefreshRequest request;
                switch (operation)
                {
                    case MeshDrawOp meshDraw:
                        request =
                            VulkanReusableFrameDataRefreshRequest.CreateMesh(
                                EVulkanReusableFrameDataRefreshKind.Mesh,
                                operationIndex,
                                operations.Length,
                                operation.Context,
                                plannerKey,
                                meshDraw.Draw.Renderer,
                                meshDraw.Draw,
                                GetFrameWideMeshDrawUniformSlot(
                                    slotsByRendererFamily,
                                    familyBases,
                                    meshDraw.Draw.Renderer,
                                    frameDataSlot,
                                    streamKind,
                                    operation.Context,
                                    meshDraw.Draw));
                        break;
                    case IndirectDrawOp indirectDraw:
                        request =
                            VulkanReusableFrameDataRefreshRequest.CreateMesh(
                                EVulkanReusableFrameDataRefreshKind.IndirectMesh,
                                operationIndex,
                                operations.Length,
                                operation.Context,
                                plannerKey,
                                indirectDraw.MeshRenderer,
                                indirectDraw.Draw,
                                GetFrameWideMeshDrawUniformSlot(
                                    slotsByRendererFamily,
                                    familyBases,
                                    indirectDraw.MeshRenderer,
                                    frameDataSlot,
                                    streamKind,
                                    operation.Context,
                                    indirectDraw.Draw));
                        break;
                    case ComputeDispatchOp computeDispatch:
                        request =
                            VulkanReusableFrameDataRefreshRequest.CreateCompute(
                                operationIndex,
                                operations.Length,
                                operation.Context,
                                plannerKey,
                                computeDispatch.Program,
                                computeDispatch.Snapshot,
                                ComputeReusableComputeDescriptorBindingKey(
                                    computeDispatch,
                                    ResolveCommandChainInlineOperationIndex(
                                        operations,
                                        operationIndex)),
                                computeDispatch.GroupsX,
                                computeDispatch.GroupsY,
                                computeDispatch.GroupsZ);
                        break;
                    default:
                        continue;
                }

                scratch.AddReusableFrameDataRefreshRequest(
                    dynamicUi,
                    request);
                if (request.Kind is
                    EVulkanReusableFrameDataRefreshKind.Mesh or
                    EVulkanReusableFrameDataRefreshKind.IndirectMesh)
                {
                    meshRequestCount++;
                    supportsDirectOwnerOnlyRefresh &=
                        request.MeshRenderer is not null &&
                        request.MeshRenderer
                            .SupportsOwnerOnlyReusableFrameDataRefresh(
                                request.Draw);
                    stableMeshHash.Add(
                        ComputeReusableMeshStableDataSignature(request));
                    AddReusableFrequencyOwnerWorkRequests(
                        request,
                        dynamicUi,
                        scratch);
                }
                else if (request.Kind ==
                         EVulkanReusableFrameDataRefreshKind.Compute)
                {
                    // Compute refresh has not migrated to frequency-owned
                    // publication yet. Keep it in the compact stable work list
                    // so mesh draws can still bypass their full refresh.
                    scratch.AddReusableFrameDataOwnerWorkRequest(
                        dynamicUi,
                        request);
                }
            }

            stableMeshHash.Add(meshRequestCount);
            scratch.SetReusableFrameDataRefreshBatchInfo(
                dynamicUi,
                new VulkanReusableFrameDataRefreshBatchInfo(
                    stableMeshHash.ToHash(),
                    meshRequestCount,
                    supportsDirectOwnerOnlyRefresh));
            scratch.ReusableMeshDrawSlotCapacityHint = Math.Max(
                1,
                slotsByRendererFamily.Count);
        }

        private static void AddReusableFrequencyOwnerWorkRequests(
            in VulkanReusableFrameDataRefreshRequest request,
            bool dynamicUi,
            CommandBufferRecordingScratch scratch,
            bool scheduledCommandChain = false)
        {
            if (request.MeshRenderer is not { } meshRenderer ||
                request.Draw.PreparedProgram is not { } program ||
                (request.Draw.MaterialOverride ??
                 meshRenderer.MeshRenderer.Material) is not { } material)
                return;

            EVulkanBindingFrequencyMask addedFrequencies =
                EVulkanBindingFrequencyMask.None;
            foreach (AutoUniformBlockInfo block in
                     program.AutoUniformBlockMap.Values)
            {
                EVulkanBindingFrequency frequency = block.Frequency;
                if (frequency is <= EVulkanBindingFrequency.Unknown or
                    >= EVulkanBindingFrequency.Count)
                    continue;

                EVulkanBindingFrequencyMask frequencyMask =
                    (EVulkanBindingFrequencyMask)(
                        1 << ((int)frequency - 1));
                if ((addedFrequencies & frequencyMask) !=
                    EVulkanBindingFrequencyMask.None)
                {
                    continue;
                }
                addedFrequencies |= frequencyMask;

                if (!meshRenderer.TryGetReusableAutoUniformOwner(
                        frequency,
                        material,
                        request.Draw,
                        out ulong ownerIdentity,
                        out ulong publicationLayoutSignature,
                        out ulong contentGeneration))
                {
                    continue;
                }

                VulkanReusableFrameOwnerKey ownerKey = new(
                    publicationLayoutSignature,
                    frequency,
                    ownerIdentity,
                    contentGeneration);
                VulkanReusableFrameDataRefreshRequest ownerRequest =
                    VulkanReusableFrameDataRefreshRequest.CreateMesh(
                    EVulkanReusableFrameDataRefreshKind.FrequencyOwnerMesh,
                    request.SourceOpIndex,
                    request.SourceOpCount,
                    request.Context,
                    request.PlannerKey,
                    meshRenderer,
                    request.Draw,
                    request.DrawUniformSlot,
                    frequencyMask);
                if (scheduledCommandChain)
                {
                    scratch.TryAddScheduledCommandChainFrameDataOwnerWorkRequest(
                        ownerKey,
                        ownerRequest);
                }
                else
                {
                    scratch.TryAddReusableFrameDataOwnerWorkRequest(
                        dynamicUi,
                        ownerKey,
                        ownerRequest);
                }
            }
        }

        private static ulong ComputeReusableMeshStableDataSignature(
            in VulkanReusableFrameDataRefreshRequest request)
        {
            PendingMeshDraw draw = request.Draw;
            XRMaterial? material =
                draw.MaterialOverride ?? request.MeshRenderer?.MeshRenderer.Material;
            ComputeDispatchSnapshot? snapshot = draw.ProgramBindingSnapshot;

            FrameOpSignatureHasher hash = new();
            hash.Add((int)request.Kind);
            hash.Add(request.SourceOpIndex);
            hash.Add(request.SourceOpCount);
            hash.Add(request.DrawUniformSlot);
            hash.Add(request.PlannerKey.GetHashCode());
            hash.Add(
                request.MeshRenderer is null
                    ? 0
                    : RuntimeHelpers.GetHashCode(request.MeshRenderer));
            hash.Add(
                request.MeshRenderer?.Mesh is null
                    ? 0
                    : RuntimeHelpers.GetHashCode(
                        request.MeshRenderer.Mesh));
            hash.Add(draw.PreparedProgram?.BindingId ?? 0u);
            hash.Add(draw.PreparedProgram?.LinkGeneration ?? 0UL);
            hash.Add(draw.PreparedProgramIdentity);
            hash.Add(
                material is null
                    ? 0
                    : RuntimeHelpers.GetHashCode(material));
            // This signature proves that the same structural request cohort can
            // consume the compact owner-work list. Mutable owner content belongs
            // exclusively to VulkanReusableFrameOwnerKey.ContentGeneration.
            // Hashing material values or view/object generations here would make
            // camera jitter and ordinary data edits force a full per-draw walk.
            hash.Add(material?.BindingResourceVersion ?? 0UL);
            hash.Add(snapshot?.DescriptorSetLayoutSignature ?? 0UL);
            hash.Add(snapshot?.ExactSamplerResourceSignature ?? 0UL);
            hash.Add(snapshot?.RuntimeUniformNameSignature ?? 0UL);
            hash.Add(
                snapshot?.MutableLegacyUniformNameSignature ?? 0UL);
            hash.Add(
                snapshot?.RuntimeUniformPublicationLayoutSignature ?? 0UL);
            hash.Add(snapshot?.BufferBindingLayoutSignature ?? 0UL);
            hash.Add(snapshot?.ImageBindingLayoutSignature ?? 0UL);
            hash.Add(draw.Instances);
            return hash.ToHash();
        }

        /// <summary>
        /// Invalidates command buffers whose baked dynamic offsets predate a frame-data
        /// family relocation. Append-only publications preserve existing offsets, so this
        /// runs only when an existing family base actually moves.
        /// </summary>
        private void ObserveMeshFrameDataManifestGeneration(ulong generation)
        {
            if (generation == 0)
                return;

            long generationValue = unchecked((long)generation);
            long previous;
            while (true)
            {
                previous = Volatile.Read(ref _observedMeshFrameDataManifestGeneration);
                if (unchecked((ulong)previous) >= generation)
                    return;
                if (Interlocked.CompareExchange(
                        ref _observedMeshFrameDataManifestGeneration,
                        generationValue,
                        previous) == previous)
                {
                    break;
                }
            }

            // The first publication has no older offsets to invalidate.
            if (previous == 0)
                return;

            int secondaryCount = InvalidateCommandChainSecondaryCommandBuffersForFrameDataLayoutChange();
            MarkOpenXrPrimaryCommandArtifactOwnersDirty();
            MarkCommandBuffersDirty("mesh frame-data layout generation changed");
            Debug.Vulkan(
                "[Vulkan] Mesh frame-data layout generation advanced from {0} to {1}; invalidated {2} cached command-chain secondaries with baked dynamic offsets.",
                unchecked((ulong)previous),
                generation,
                secondaryCount);
        }

        private void PublishFrameWideMeshFrameDataManifestGauges()
            => RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanFrameWideMeshFrameDataManifestGauges(
                MeshFrameDataManifestGeneration,
                MeshFrameDataManifestPublicationCount,
                MeshFrameDataManifestLateRegistrationCount,
                MeshFrameDataManifestRendererCount,
                MeshFrameDataManifestFamilyCount,
                MeshFrameDataManifestIsSealed);

    }
}
