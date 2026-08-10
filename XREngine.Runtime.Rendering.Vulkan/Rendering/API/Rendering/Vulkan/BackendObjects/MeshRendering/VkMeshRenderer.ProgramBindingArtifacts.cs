using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Models.Materials.Textures;
using XREngine.Scene;

namespace XREngine.Rendering.Vulkan;

internal unsafe partial class VkMeshRenderer
{
    private const EUniformRequirements PersistentArtifactEngineRequirements =
        EUniformRequirements.Camera |
        EUniformRequirements.Lights |
        EUniformRequirements.RenderTime |
        EUniformRequirements.ViewportDimensions |
        EUniformRequirements.AmbientOcclusion |
        EUniformRequirements.ClipSpacePolicy;

    private const EUniformRequirements RetainedArtifactEngineRequirements =
        EUniformRequirements.Lights |
        EUniformRequirements.AmbientOcclusion;

    /// <summary>
    /// Cross-frame artifacts are restricted to inputs with an explicit owner
    /// generation. Lighting/AO use one immutable scope publication and its
    /// content/resource signatures. Shadow state, mutable callbacks, and active
    /// pipeline scopes remain on the conservative frame-local path.
    /// </summary>
    private bool CanUsePersistentProgramBindingArtifact(
        XRMaterial material,
        XRRenderProgram programData,
        in LayeredShadowUniformState shadowUniformState,
        IRenderBindingPublisher[] materialBindingPublishers,
        IRenderBindingPublisher[] meshBindingPublishers,
        out EUniformRequirements engineRequirements,
        out ComputeDispatchSnapshot? engineBindingSnapshot,
        out EVulkanProgramBindingArtifactFallbackReason fallbackReason)
    {
        fallbackReason =
            EVulkanProgramBindingArtifactFallbackReason.None;
        engineBindingSnapshot = null;
        engineRequirements =
            (material.RenderOptions?.RequiredEngineUniforms ??
             EUniformRequirements.None) |
            programData.GetActiveEngineUniformRequirements();

        if (shadowUniformState.IsShadowPass)
        {
            fallbackReason =
                EVulkanProgramBindingArtifactFallbackReason.ShadowPass;
            return false;
        }
        if (MeshRenderer.HasSettingUniformsHandlers)
        {
            fallbackReason =
                EVulkanProgramBindingArtifactFallbackReason.RendererCallback;
            return false;
        }
        if (material.HasSettingUniformsHandlers)
        {
            fallbackReason =
                EVulkanProgramBindingArtifactFallbackReason.MaterialCallback;
            return false;
        }

        XRRenderPipelineInstance.RenderingState? renderingState =
            RuntimeEngine.Rendering.State.RenderingPipelineState;
        if (renderingState?.HasActiveScopedBindings == true)
        {
            fallbackReason =
                EVulkanProgramBindingArtifactFallbackReason.ActiveScopedBindings;
            return false;
        }

        if ((engineRequirements & ~PersistentArtifactEngineRequirements) != 0)
        {
            fallbackReason = EVulkanProgramBindingArtifactFallbackReason
                .UnsupportedEngineRequirements;
            return false;
        }

        if ((engineRequirements & EUniformRequirements.Lights) != 0)
        {
            Lights3DCollection? lights =
                RuntimeEngine.Rendering.State.RenderingWorld?.Lights;
            if (lights is null || _program is null)
            {
                fallbackReason = EVulkanProgramBindingArtifactFallbackReason
                    .MissingLightingOwner;
                return false;
            }

            engineBindingSnapshot =
                BackendContext.MeshServices.GetForwardLightingBindingSnapshotForArtifact(
                    lights,
                    programData,
                    _program);
            if (engineBindingSnapshot is null)
            {
                fallbackReason = EVulkanProgramBindingArtifactFallbackReason
                    .LightingPublicationUnavailable;
                return false;
            }
        }
        else if ((engineRequirements &
                  EUniformRequirements.AmbientOcclusion) != 0)
        {
            if (!HasExactPersistentRequirementOwner(
                    materialBindingPublishers,
                    meshBindingPublishers,
                    EUniformRequirements.AmbientOcclusion))
            {
                fallbackReason = EVulkanProgramBindingArtifactFallbackReason
                    .AmbientOcclusionOnly;
                return false;
            }
        }

        return true;
    }

    private static bool HasExactPersistentRequirementOwner(
        IRenderBindingPublisher[] materialPublishers,
        IRenderBindingPublisher[] meshPublishers,
        EUniformRequirements requirement)
    {
        int ownerCount = 0;
        if (!CountExactPersistentRequirementOwners(
                materialPublishers,
                requirement,
                ref ownerCount) ||
            !CountExactPersistentRequirementOwners(
                meshPublishers,
                requirement,
                ref ownerCount))
        {
            return false;
        }

        return ownerCount == 1;
    }

    private static bool CountExactPersistentRequirementOwners(
        IRenderBindingPublisher[] publishers,
        EUniformRequirements requirement,
        ref int ownerCount)
    {
        for (int index = 0; index < publishers.Length; index++)
        {
            if (publishers[index] is not
                IPersistentProgramBindingRequirementOwner owner)
            {
                continue;
            }

            ownerCount++;
            if (owner.OwnedPersistentArtifactRequirement != requirement ||
                ownerCount > 1)
            {
                return false;
            }
        }

        return true;
    }

    private PersistentProgramBindingArtifactGeneration
        CreatePersistentProgramBindingArtifactGeneration(
            XRMaterial material,
            VkRenderProgram program,
            ulong typedPublisherSignature,
            EUniformRequirements engineRequirements,
            ComputeDispatchSnapshot? engineBindingSnapshot)
        => new(
            material.BindingLayoutVersion,
            material.BindingValueVersion,
            material.BindingResourceVersion,
            material.ShaderStateRevision,
            material.UberStateRevision,
            program.LinkGeneration,
            typedPublisherSignature,
            engineBindingSnapshot?.PersistentEngineUniformSignature ?? 0UL,
            engineBindingSnapshot?.PersistentEngineResourceSignature ?? 0UL,
            RuntimeEngine.Rendering.State.CurrentRenderingPipeline?.Variables
                .UniformContentGeneration ?? 0UL,
            engineRequirements,
            MeshRenderer.CaptureUniformsOnRender);

    private bool TryCreatePersistentProgramBindingArtifact(
        XRMaterial material,
        ComputeDispatchSnapshot snapshot,
        EUniformRequirements engineRequirements,
        bool hasGenerationOwnedPublisherResources,
        out ComputeDispatchSnapshot artifact,
        out EVulkanProgramBindingArtifactFallbackReason fallbackReason,
        out string? fallbackDetail)
    {
        artifact = null!;
        fallbackReason = EVulkanProgramBindingArtifactFallbackReason.None;
        fallbackDetail = null;
        if (snapshot.MutableLegacyUniformNames.Count != 0)
        {
            fallbackReason = EVulkanProgramBindingArtifactFallbackReason
                .MutableLegacyUniform;
            foreach (string name in snapshot.MutableLegacyUniformNames)
            {
                fallbackDetail = name;
                break;
            }
            return false;
        }

        bool hasGenerationOwnedEngineResources =
            (engineRequirements & RetainedArtifactEngineRequirements) != 0;
        if (!hasGenerationOwnedEngineResources &&
            !hasGenerationOwnedPublisherResources &&
            !SnapshotContainsOnlyMaterialSamplers(material, snapshot))
        {
            fallbackReason = EVulkanProgramBindingArtifactFallbackReason
                .UnownedDescriptorResource;
            return false;
        }

        foreach ((string name, ProgramUniformValue _) in snapshot.Uniforms)
        {
            if (snapshot.RuntimeUniformPublications.ContainsKey(name))
                continue;
            if (UniformRequirementsDetection.GetRequirement(name) !=
                EUniformRequirements.None)
            {
                continue;
            }

            fallbackReason = EVulkanProgramBindingArtifactFallbackReason
                .UnownedUniform;
            fallbackDetail = name;
            return false;
        }

        foreach (string name in snapshot.RuntimeUniformPublications.Keys)
            if (!snapshot.Uniforms.ContainsKey(name))
            {
                fallbackReason = EVulkanProgramBindingArtifactFallbackReason
                    .IncompleteRuntimeUniformPublication;
                fallbackDetail = name;
                return false;
            }

        artifact = snapshot.CreatePersistentProgramBindingArtifact(
            BackendContext,
            RuntimeEngine.Rendering.State.CurrentRenderingPipeline,
            RetainedArtifactEngineRequirements);
        return true;
    }

    private static bool SnapshotContainsOnlyMaterialSamplers(
        XRMaterial material,
        ComputeDispatchSnapshot snapshot)
    {
        if (snapshot.Images.Count != 0 ||
            snapshot.Buffers.Count != 0 ||
            snapshot.BuffersByName.Count != 0)
        {
            return false;
        }

        for (int index = 0; index < material.Textures.Count; index++)
        {
            XRTexture? materialTexture = material.Textures[index];
            if (materialTexture is null)
                continue;

            if (snapshot.Samplers.TryGetValue(
                    unchecked((uint)index),
                    out XRTexture? capturedTexture) &&
                !ReferenceEquals(materialTexture, capturedTexture))
            {
                return false;
            }
        }

        foreach ((uint unit, XRTexture texture) in snapshot.Samplers)
        {
            if (unit > int.MaxValue ||
                unit >= (uint)material.Textures.Count ||
                !ReferenceEquals(material.Textures[(int)unit], texture))
            {
                return false;
            }
        }

        foreach ((uint unit, string name) in snapshot.SamplerNamesByUnit)
        {
            if (!snapshot.Samplers.TryGetValue(unit, out XRTexture? texture) ||
                unit > int.MaxValue ||
                !IsExpectedMaterialSamplerName(texture, (int)unit, name))
            {
                return false;
            }
        }

        foreach ((string name, XRTexture texture) in snapshot.SamplersByName)
        {
            bool matched = false;
            for (int index = 0; index < material.Textures.Count; index++)
            {
                if (!ReferenceEquals(material.Textures[index], texture) ||
                    !IsExpectedMaterialSamplerName(texture, index, name))
                {
                    continue;
                }

                matched = true;
                break;
            }

            if (!matched)
                return false;
        }

        return true;
    }

    private static bool IsExpectedMaterialSamplerName(
        XRTexture texture,
        int textureIndex,
        string name)
    {
        string resolvedName = texture.ResolveSamplerName(textureIndex, null);
        return string.Equals(name, resolvedName, StringComparison.Ordinal) ||
            string.Equals(
                name,
                XRTexture.GetIndexedSamplerName(textureIndex),
                StringComparison.Ordinal);
    }
}
