using System.Runtime.CompilerServices;
using Silk.NET.Vulkan;
using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Models.Materials;
using VkBufferHandle = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

internal unsafe partial class VkMeshRenderer
{
    internal void ReleaseCanonicalPublicationBridge(
        in AdvancedGpuSceneDrawIdentitySnapshot canonicalDraw)
        => _meshRequests?.ReleaseCanonicalPublicationBridge(canonicalDraw);

    internal bool TryGetResidentDrawTemplate(
        in VulkanMeshRenderRequest request,
        out VulkanResidentDrawTemplateHandle handle,
        out VulkanResidentDrawTemplate? template)
    {
        handle = default;
        template = null;
        if (!IsResidentTemplateRequestEligible(in request) ||
            !TryCaptureResidentTemplateVariant(
                request.PassIndex,
                request.Context,
                out VulkanResidentDrawTemplateVariantKey variant))
        {
            return false;
        }

        if (request.ResidentTemplateHandle.IsValid &&
            BackendContext.Resources.ResidentDrawTemplates.TryGetResolved(
                request.ResidentTemplateHandle,
                variant,
                out template))
        {
            handle = request.ResidentTemplateHandle;
            return true;
        }

        if (!TryCaptureResidentTemplateGenerations(
                request.CanonicalDrawIdentitySnapshot,
                request.ResolvedMaterial.Material,
                request.PreparationCompatibilitySignature,
                request.Context,
                out VulkanResidentDrawTemplateGenerationDomains generations) ||
            !BackendContext.Resources.ResidentDrawTemplates.TryResolve(
                request.CanonicalDrawIdentitySnapshot,
                variant,
                generations,
                out handle,
                out template))
        {
            return false;
        }

        PublishResidentTemplateHandle(handle, variant);
        return true;
    }

    private VulkanResidentDrawTemplateHandle CapturePublishedResidentTemplateHandle(
        in AdvancedGpuSceneDrawIdentitySnapshot canonicalDraw,
        int passIndex,
        in FrameOpContext context)
    {
        if (!TryCaptureResidentTemplateVariant(
                passIndex,
                context,
                out VulkanResidentDrawTemplateVariantKey variant))
        {
            return default;
        }

        VulkanResidentDrawTemplatePublication? publication =
            Volatile.Read(ref _residentTemplatePublication);
        return publication is not null &&
            publication.Matches(canonicalDraw, variant)
                ? publication.Handle
                : default;
    }

    private void PublishResidentTemplateHandle(
        in VulkanResidentDrawTemplateHandle handle,
        in VulkanResidentDrawTemplateVariantKey variant)
    {
        if (!handle.IsValid)
            return;

        Volatile.Write(
            ref _residentTemplatePublication,
            new VulkanResidentDrawTemplatePublication(handle, variant));
    }

    internal bool TryGetResidentDrawTemplate(
        in PendingMeshDraw draw,
        int passIndex,
        XRMaterial material,
        in FrameOpContext context,
        out VulkanResidentDrawTemplateHandle handle,
        out VulkanResidentDrawTemplateVariantKey variant,
        out VulkanResidentDrawTemplateGenerationDomains generations,
        out VulkanResidentDrawTemplate? template)
    {
        handle = default;
        variant = default;
        generations = default;
        template = null;
        if (!IsResidentTemplateDrawEligible(in draw, material))
            return false;

        if (draw.ResidentTemplateHandle.IsValid)
        {
            handle = draw.ResidentTemplateHandle;
            if (BackendContext.Resources.ResidentDrawTemplates
                .TryGetResolvedAndRetain(handle, out template))
            {
                return true;
            }
        }

        if (!TryCaptureResidentTemplateLookup(
                draw.CanonicalDrawIdentitySnapshot,
                passIndex,
                material,
                draw.PreparationCompatibilitySignature,
                context,
                out variant,
                out generations))
        {
            return false;
        }

        if (!BackendContext.Resources.ResidentDrawTemplates.TryResolve(
            draw.CanonicalDrawIdentitySnapshot,
            variant,
            generations,
            out handle,
            out template) ||
            template is null)
        {
            return false;
        }

        if (template.TryAcquireUse())
            return true;

        handle = default;
        template = null;
        return false;
    }

    private bool IsResidentTemplateRequestEligible(
        in VulkanMeshRenderRequest request)
    {
        XRMaterial material = request.ResolvedMaterial.Material;
        return request.CanonicalDrawIdentitySnapshot.Handles?.Count == 1 &&
               request.Pipeline is not null &&
               IsActive &&
               BackendContext.IsDeviceOperational &&
               request.DeferredBindings.IsEmpty &&
               !request.ResolvedMaterial.IsShadowVariant &&
               request.RenderOptionsOverride is null &&
               !request.Context.PreserveSubmissionOrderBlock &&
               !request.ViewSnapshot.ShadowUniformState.IsShadowPass &&
               !request.Producer.IsExternalSwapchainTarget &&
               !request.Producer.IsPrewarmingExternalSwapchainTarget &&
               request.Producer.IndexedViewportScissors.Count <= 1 &&
               !MeshRenderer.HasRenderDataPreparation &&
               !MeshRenderer.HasSettingUniformsHandlers &&
               !material.HasSettingUniformsHandlers &&
               RuntimeEngine.Rendering.State.RenderingPipelineState
                   ?.HasActiveScopedBindings != true;
    }

    private bool IsResidentTemplateDrawEligible(
        in PendingMeshDraw draw,
        XRMaterial material)
    {
        ComputeDispatchSnapshot? bindings = draw.ProgramBindingSnapshot;
        return draw.CanonicalDrawIdentitySnapshot.Handles?.Count == 1 &&
               IsActive &&
               BackendContext.IsDeviceOperational &&
               draw.IndexedViewports is null &&
               draw.IndexedScissors is null &&
               !draw.ShadowUniformState.IsShadowPass &&
               !MeshRenderer.HasRenderDataPreparation &&
               !MeshRenderer.HasSettingUniformsHandlers &&
               !material.HasSettingUniformsHandlers &&
               draw.PreparedProgram is { IsActive: true, IsLinked: true } &&
               draw.PreparedProgramLinkGeneration != 0 &&
               (bindings is null ||
                (bindings.IsImmutableBindingArtifact &&
                 !bindings.HasMutableFrameSourceSamplerBindings));
    }

    private bool TryCaptureResidentTemplateLookup(
        in AdvancedGpuSceneDrawIdentitySnapshot canonicalDraw,
        int passIndex,
        XRMaterial material,
        ulong preparationSignature,
        in FrameOpContext context,
        out VulkanResidentDrawTemplateVariantKey variant,
        out VulkanResidentDrawTemplateGenerationDomains generations)
    {
        if (!TryCaptureResidentTemplateVariant(passIndex, context, out variant))
        {
            generations = default;
            return false;
        }

        return TryCaptureResidentTemplateGenerations(
            canonicalDraw,
            material,
            preparationSignature,
            context,
            out generations);
    }

    /// <summary>
    /// Captures only the cheap routing fields needed to validate a published
    /// direct handle. Structural and artifact fingerprints remain on the cold
    /// miss path.
    /// </summary>
    private static bool TryCaptureResidentTemplateVariant(
        int passIndex,
        in FrameOpContext context,
        out VulkanResidentDrawTemplateVariantKey variant)
    {
        if (passIndex < 0)
        {
            variant = default;
            return false;
        }

        EMeshSubmissionStrategy strategy =
            RuntimeEngine.Rendering.ResolveMeshSubmissionStrategy();
        ulong instrumentationSchema = strategy is
            EMeshSubmissionStrategy.GpuIndirectInstrumented or
            EMeshSubmissionStrategy.GpuMeshletInstrumented
                ? 1UL
                : 0UL;
        // Phase 3 templates currently encode vkCmdDraw/vkCmdDrawIndexed only.
        // Strategy remains part of the key, while the advertised native dialect
        // truthfully describes the recorded command family.
        EVulkanResidentTemplateMeshDialect dialect =
            EVulkanResidentTemplateMeshDialect.VertexInput;
        ulong outputProfile = MixResident(
            unchecked((ulong)(uint)context.PipelineIdentity),
            context.StereoEnabled ? 1UL : 0UL,
            context.MultiviewEnabled ? 1UL : 0UL);
        variant = new(
            passIndex,
            strategy,
            instrumentationSchema,
            dialect,
            outputProfile);
        return true;
    }

    private bool TryCaptureResidentTemplateGenerations(
        in AdvancedGpuSceneDrawIdentitySnapshot canonicalDraw,
        XRMaterial material,
        ulong preparationSignature,
        in FrameOpContext context,
        out VulkanResidentDrawTemplateGenerationDomains generations)
    {
        generations = default;
        if (!canonicalDraw.IsValid ||
            canonicalDraw.Handles?.Count != 1 ||
            _program is not { IsActive: true, IsLinked: true } program ||
            program.LinkGeneration == 0)
        {
            return false;
        }
        bool hasExactArtifactGeneration =
            TryCaptureResidentProgramBindingGeneration(
                material,
                program,
                out PersistentProgramBindingArtifactGeneration artifactGeneration);
        ulong artifactGenerationSignature = hasExactArtifactGeneration
            ? MixResident(
                artifactGeneration.MaterialLayoutVersion,
                artifactGeneration.MaterialValueVersion,
                artifactGeneration.MaterialResourceVersion,
                unchecked((ulong)artifactGeneration.MaterialShaderRevision),
                unchecked((ulong)artifactGeneration.MaterialUberRevision),
                artifactGeneration.ProgramLinkGeneration,
                artifactGeneration.TypedPublisherSignature,
                artifactGeneration.EngineUniformSignature,
                artifactGeneration.EngineResourceSignature,
                artifactGeneration.PipelineUniformGeneration,
                unchecked((ulong)artifactGeneration.EngineRequirements),
                artifactGeneration.CaptureUniformsOnRender ? 1UL : 0UL)
            : MixResident(
                material.BindingLayoutVersion,
                material.BindingValueVersion,
                material.BindingResourceVersion,
                unchecked((ulong)material.ShaderStateRevision),
                unchecked((ulong)material.UberStateRevision),
                program.LinkGeneration,
                RuntimeEngine.Rendering.State.CurrentRenderingPipeline?.Variables
                    .UniformContentGeneration ?? 0UL,
                MeshRenderer.CaptureUniformsOnRender ? 1UL : 0UL);

        // Canonical owner deltas and the resident table's exact reverse
        // manifests evict only templates that reference a mutated draw,
        // geometry, material, or resource row. Aggregate publication
        // generations must not participate in a per-draw cache key: doing so
        // turns one local table mutation into a miss for every resident draw.
        generations = new(
            DataContent: material.BindingValueVersion,
            ResourceTable: MixResident(
                material.BindingResourceVersion,
                context.DescriptorGeneration,
                artifactGenerationSignature),
            LayoutTopology: MixResident(
                preparationSignature,
                material.BindingLayoutVersion,
                unchecked((ulong)material.ShaderStateRevision),
                unchecked((ulong)material.UberStateRevision),
                program.LinkGeneration,
                _geometryLayoutSignature.StableHash),
            Recording: MixResident(
                context.RecordingFingerprint,
                context.ResourceGeneration));
        return true;
    }

    private bool TryPublishResidentDrawTemplate(
        in PendingMeshDraw draw,
        int passIndex,
        XRMaterial material,
        RenderPass renderPass,
        bool useDynamicRendering,
        in FrameOpContext context,
        PipelineLayout pipelineLayout,
        in VulkanPreparedMeshPrimitive primitive0,
        in VulkanPreparedMeshPrimitive primitive1,
        in VulkanPreparedMeshPrimitive primitive2,
        int primitiveCount,
        ReadOnlySpan<VkBufferHandle> vertexBuffers,
        ReadOnlySpan<uint> vertexBindings,
        in VulkanResidentDrawTemplateVariantKey expectedVariant,
        in VulkanResidentDrawTemplateGenerationDomains expectedGenerations)
    {
        Span<VulkanResidentTemplateDependencyRequest> requests =
            stackalloc VulkanResidentTemplateDependencyRequest[64];
        int requestCount = 0;
        ulong dependencySignature = 14695981039346656037UL;
        if (!TryAppendResidentDependency(
                requests,
                ref requestCount,
                EVulkanResidentTemplateDependencyKind.PipelineLayout,
                pipelineLayout.Handle,
                ref dependencySignature))
        {
            return false;
        }

        if (!TryAppendPrimitiveDependencies(
                requests, ref requestCount, in primitive0, ref dependencySignature) ||
            !TryAppendPrimitiveDependencies(
                requests, ref requestCount, in primitive1, ref dependencySignature) ||
            !TryAppendPrimitiveDependencies(
                requests, ref requestCount, in primitive2, ref dependencySignature))
        {
            return false;
        }
        for (int index = 0; index < vertexBuffers.Length; ++index)
        {
            if (!TryAppendResidentDependency(
                    requests,
                    ref requestCount,
                    EVulkanResidentTemplateDependencyKind.Buffer,
                    vertexBuffers[index].Handle,
                    ref dependencySignature))
            {
                return false;
            }
        }
        if (!useDynamicRendering && renderPass.Handle != 0 &&
            !TryAppendResidentDependency(
                requests,
                ref requestCount,
                EVulkanResidentTemplateDependencyKind.RenderPass,
                renderPass.Handle,
                ref dependencySignature))
        {
            return false;
        }

        if (!BackendContext.Resources.TryAcquireResidentTemplateDependencies(
                requests[..requestCount],
                out VulkanResidentTemplateDependencyLease? dependencyLease,
                out _))
        {
            return false;
        }

        if (draw.ProgramBindingSnapshot is { } bindingSnapshot &&
            (!bindingSnapshot.IsImmutableBindingArtifact ||
             bindingSnapshot.HasMutableFrameSourceSamplerBindings))
        {
            dependencyLease?.Dispose();
            return false;
        }

        if (!TryCaptureResidentTemplateLookup(
                draw.CanonicalDrawIdentitySnapshot,
                passIndex,
                material,
                draw.PreparationCompatibilitySignature,
                context,
                out VulkanResidentDrawTemplateVariantKey currentVariant,
                out VulkanResidentDrawTemplateGenerationDomains currentGenerations) ||
            currentVariant != expectedVariant ||
            currentGenerations != expectedGenerations)
        {
            dependencyLease?.Dispose();
            return false;
        }

        ulong pipelineSignature = MixResident(
            pipelineLayout.Handle,
            primitive0.Pipeline.Handle,
            primitive1.Pipeline.Handle,
            primitive2.Pipeline.Handle,
            unchecked((ulong)(uint)primitiveCount));
        VkRenderProgram program = draw.PreparedProgram!;
        VulkanResidentDrawTemplateStructuralIdentity structuralIdentity = new(
            draw.CanonicalDrawIdentitySnapshot,
            MixResident(
                unchecked((ulong)(uint)RuntimeHelpers.GetHashCode(program)),
                program.LinkGeneration),
            pipelineSignature,
            _geometryLayoutSignature.StableHash,
            dependencySignature);
        ulong vertexSignature = 14695981039346656037UL;
        for (int index = 0; index < vertexBuffers.Length; ++index)
            vertexSignature = MixResident(
                vertexSignature,
                vertexBuffers[index].Handle,
                vertexBindings[index]);
        bool dependencyOwnershipTransferred = false;
        try
        {
            VulkanResidentDrawTemplateNativeState nativeState = new(
                pipelineLayout,
                primitive0,
                primitive1,
                primitive2,
                checked((byte)primitiveCount),
                vertexBuffers,
                vertexBindings,
                vertexSignature,
                draw);
            bool published = BackendContext.Resources.ResidentDrawTemplates
                .TryCreateOrReplace(
                    structuralIdentity,
                    expectedVariant,
                    expectedGenerations,
                    nativeState,
                    dependencyLease,
                    out _,
                    out VulkanResidentDrawTemplateHandle residentHandle);
            dependencyOwnershipTransferred = true;
            if (published)
                PublishResidentTemplateHandle(residentHandle, expectedVariant);
            return published;
        }
        finally
        {
            if (!dependencyOwnershipTransferred)
                dependencyLease?.Dispose();
        }
    }

    private bool TryAppendPrimitiveDependencies(
        Span<VulkanResidentTemplateDependencyRequest> requests,
        ref int requestCount,
        in VulkanPreparedMeshPrimitive primitive,
        ref ulong signature)
    {
        if (primitive.Pipeline.Handle == 0)
            return true;
        if (!TryAppendResidentDependency(
                requests,
                ref requestCount,
                EVulkanResidentTemplateDependencyKind.Pipeline,
                primitive.Pipeline.Handle,
                ref signature))
        {
            return false;
        }
        return !primitive.Indexed ||
            TryAppendResidentDependency(
                requests,
                ref requestCount,
                EVulkanResidentTemplateDependencyKind.Buffer,
                primitive.IndexBuffer.Handle,
                ref signature);
    }

    private bool TryAppendResidentDependency(
        Span<VulkanResidentTemplateDependencyRequest> requests,
        ref int requestCount,
        EVulkanResidentTemplateDependencyKind kind,
        ulong handle,
        ref ulong signature)
    {
        if (handle == 0)
            return false;
        ulong generation = BackendContext.Resources.GetPublishedGeneration(
            kind switch
            {
                EVulkanResidentTemplateDependencyKind.Pipeline => ObjectType.Pipeline,
                EVulkanResidentTemplateDependencyKind.PipelineLayout => ObjectType.PipelineLayout,
                EVulkanResidentTemplateDependencyKind.DescriptorSetLayout => ObjectType.DescriptorSetLayout,
                EVulkanResidentTemplateDependencyKind.Buffer => ObjectType.Buffer,
                EVulkanResidentTemplateDependencyKind.BufferView => ObjectType.BufferView,
                EVulkanResidentTemplateDependencyKind.RenderPass => ObjectType.RenderPass,
                _ => ObjectType.Unknown,
            },
            handle);
        if (generation == 0)
            return false;

        for (int index = 0; index < requestCount; ++index)
        {
            VulkanResidentTemplateDependencyRequest existing = requests[index];
            if (existing.Kind == kind && existing.Handle == handle)
                return existing.Generation == generation;
            if (existing.Handle == handle)
                return false;
        }
        if ((uint)requestCount >= (uint)requests.Length)
            return false;

        requests[requestCount++] = new(kind, handle, generation);
        signature = MixResident(
            signature,
            unchecked((ulong)(byte)kind),
            handle,
            generation);
        return true;
    }

    private static ulong MixResident(params ReadOnlySpan<ulong> values)
    {
        ulong hash = 14695981039346656037UL;
        for (int index = 0; index < values.Length; ++index)
            hash = (hash ^ values[index]) * 1099511628211UL;
        return hash == 0 ? 1UL : hash;
    }
}
