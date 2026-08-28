using Silk.NET.Vulkan;
using XREngine.Rendering.Shaders;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Defines the Vulkan descriptor-set ABI shared by advanced shaders and the
/// frame-slot advanced-scene resource runtime.
/// </summary>
internal static class VulkanAdvancedSceneProgramBindingContract
{
    private static readonly uint[] GlobalStorageBindings =
    [
        AdvancedGlobalResourceBindings.Draws,
        AdvancedGlobalResourceBindings.Instances,
        AdvancedGlobalResourceBindings.Meshes,
        AdvancedGlobalResourceBindings.Materials,
        AdvancedGlobalResourceBindings.Views,
        AdvancedGlobalResourceBindings.Lights,
        AdvancedGlobalResourceBindings.Shadows,
        AdvancedGlobalResourceBindings.Textures,
        AdvancedGlobalResourceBindings.Samplers,
        AdvancedGlobalResourceBindings.Deformations,
        AdvancedGlobalResourceBindings.Diagnostics,
        AdvancedGlobalResourceBindings.MaterialConstants,
        AdvancedGlobalResourceBindings.MaterialTextureBindings,
        AdvancedGlobalResourceBindings.Probes,
        AdvancedGlobalResourceBindings.Environments,
        AdvancedGlobalResourceBindings.Decals,
        AdvancedGlobalResourceBindings.GiResources,
        AdvancedGlobalResourceBindings.Transforms,
        AdvancedGlobalResourceBindings.RenderStates,
        AdvancedGlobalResourceBindings.EncodedTextures,
        AdvancedGlobalResourceBindings.EncodedSamplers,
        AdvancedGlobalResourceBindings.ShadingKernels,
        AdvancedGlobalResourceBindings.MaterialLayouts,
        AdvancedGlobalResourceBindings.EditorIdentities,
        AdvancedGlobalResourceBindings.HandleLookups,
    ];

    internal const uint GlobalSetIndex = VulkanDescriptorManager.PerPassSetIndex;
    internal const uint VisibilitySetIndex = VulkanDescriptorManager.ComputeSetIndex;
    internal const uint ResourceSetIndex = VulkanDescriptorManager.MaterialSetIndex;
    internal const uint StandardSetIndex = VulkanDescriptorManager.GlobalsSetIndex;
    // Set 1 is a complete, frame-slot-owned visibility preparation ABI. It is
    // intentionally separate from the canonical set-3 scene tables so a
    // visibility stage never repacks or reads a CPU result mid-frame.
    internal const uint VisibilityCandidatesBinding = 20u;
    internal const uint VisibilityPersistentStateBinding = 21u;
    internal const uint VisibilityDeferredIndicesBinding = 22u;
    internal const uint VisibilityVisibleIndicesBinding = 23u;
    internal const uint VisibilityPayloadBinding = 24u;
    internal const uint VisibilityProducersBinding = 25u;
    internal const uint VisibilityRangeIndicesBinding = 26u;
    internal const uint VisibilityRangeOffsetsBinding = 27u;
    internal const uint VisibilityRangeCountsBinding = 28u;
    internal const uint VisibilityCountersBinding = 29u;
    internal const uint VisibilityIndexedArgumentsBinding = 30u;
    internal const uint VisibilityMeshArgumentsBinding = 31u;
    internal const uint VisibilityMeshPayloadsBinding = 32u;
    // Mesh visibility consumes the canonical geometry publication directly.
    // These remain declared even while the producer is fail-closed so compiled
    // mesh pipelines cannot drift from the eventual native ABI.
    internal const uint VisibilityStaticVerticesBinding = 33u;
    internal const uint VisibilityCurrentVerticesBinding = 34u;
    internal const uint VisibilityPreviousVerticesBinding = 35u;
    internal const uint VisibilityMeshletDescriptorsBinding = 36u;
    internal const uint VisibilityMeshletVertexIndicesBinding = 37u;
    internal const uint VisibilityMeshletTriangleWordsBinding = 38u;
    // Late visibility owns a distinct output stream. Recovered candidates
    // therefore cannot race or overwrite the early indirect producer.
    internal const uint VisibilityLateVisibleIndicesBinding = 39u;
    internal const uint VisibilityLateRangeCountsBinding = 40u;
    internal const uint VisibilityLateIndexedArgumentsBinding = 41u;
    internal const uint VisibilityLateMeshArgumentsBinding = 44u;
    internal const uint VisibilityLateMeshPayloadsBinding = 45u;
    internal const uint VisibilityDepthPyramidSampledBinding = 42u;
    internal const uint VisibilityDepthPyramidStorageBinding = 43u;
    internal const uint ExternallyOwnedSetMask =
        (1u << (int)GlobalSetIndex) |
        (1u << (int)VisibilitySetIndex) |
        (1u << (int)ResourceSetIndex);

    internal static ReadOnlySpan<uint> RequiredGlobalStorageBindings
        => GlobalStorageBindings;

    internal static bool IsCandidate(
        IReadOnlyList<DescriptorBindingInfo> bindings)
    {
        bool hasTextureDescriptors = false;
        bool hasSamplerDescriptors = false;
        bool hasGlobalHandleLookups = false;
        for (int index = 0; index < bindings.Count; ++index)
        {
            DescriptorBindingInfo binding = bindings[index];
            hasTextureDescriptors |=
                binding.Set == ResourceSetIndex &&
                binding.Binding == AdvancedGlobalResourceBindings.TextureDescriptors;
            hasSamplerDescriptors |=
                binding.Set == ResourceSetIndex &&
                binding.Binding == AdvancedGlobalResourceBindings.SamplerDescriptors;
            hasGlobalHandleLookups |=
                binding.Set == GlobalSetIndex &&
                binding.Binding == AdvancedGlobalResourceBindings.HandleLookups;
        }

        // The three-coordinate signature avoids treating unrelated legacy
        // per-pass bindings at these high binding numbers as advanced ABI use.
        return hasGlobalHandleLookups &&
            hasTextureDescriptors &&
            hasSamplerDescriptors;
    }

    internal static bool TryValidate(
        IReadOnlyList<DescriptorBindingInfo> bindings,
        uint resourceDescriptorCapacity,
        uint externallyOwnedSetMask,
        out string reason)
    {
        for (int index = 0; index < bindings.Count; ++index)
        {
            DescriptorBindingInfo binding = bindings[index];
            if (!IsExternallyOwnedSet(externallyOwnedSetMask, binding.Set))
                continue;
            if (!TryValidateExternallyOwnedBinding(
                    binding,
                    resourceDescriptorCapacity,
                    out reason))
                return false;
        }

        reason = "Ready";
        return true;
    }

    /// <summary>
    /// An explicit external-layout declaration is a compatibility assertion,
    /// not a requirement that every ABI binding survive shader optimization.
    /// Each reflected coordinate must still match its runtime-owned layout.
    /// </summary>
    private static bool TryValidateExternallyOwnedBinding(
        in DescriptorBindingInfo binding,
        uint resourceDescriptorCapacity,
        out string reason)
    {
        if (binding.Set == GlobalSetIndex)
        {
            if (ContainsGlobalStorageBinding(binding.Binding) &&
                binding.DescriptorType == DescriptorType.StorageBuffer &&
                binding.Count == 1u)
            {
                reason = "Ready";
                return true;
            }

            reason = $"advanced global set {GlobalSetIndex} binding {binding.Binding} is not compatible with the runtime layout";
            return false;
        }
        if (binding.Set == ResourceSetIndex)
        {
            bool recognized = binding.Binding switch
            {
                AdvancedGlobalResourceBindings.TextureDescriptors or
                AdvancedGlobalResourceBindings.SamplerDescriptors => true,
                _ => false,
            };
            DescriptorType expectedType = binding.Binding ==
                AdvancedGlobalResourceBindings.TextureDescriptors
                    ? DescriptorType.SampledImage
                    : DescriptorType.Sampler;
            if (recognized && binding.DescriptorType == expectedType &&
                binding.Count == resourceDescriptorCapacity)
            {
                reason = "Ready";
                return true;
            }

            reason = $"advanced resource set {ResourceSetIndex} binding {binding.Binding} is not compatible with the runtime layout";
            return false;
        }
        if (binding.Set == VisibilitySetIndex &&
            ContainsVisibilityStorageBinding(binding.Binding) &&
            binding.DescriptorType == DescriptorType.StorageBuffer && binding.Count == 1u)
        {
            reason = "Ready";
            return true;
        }
        if (binding.Set == VisibilitySetIndex &&
            binding.Binding == VisibilityDepthPyramidSampledBinding &&
            binding.DescriptorType == DescriptorType.CombinedImageSampler &&
            binding.Count == 1u)
        {
            reason = "Ready";
            return true;
        }
        if (binding.Set == VisibilitySetIndex &&
            binding.Binding == VisibilityDepthPyramidStorageBinding &&
            binding.DescriptorType == DescriptorType.StorageImage &&
            binding.Count == 1u)
        {
            reason = "Ready";
            return true;
        }

        reason = $"advanced externally owned set {binding.Set} binding {binding.Binding} is not compatible with the runtime layout";
        return false;
    }

    internal static string BuildShaderPreamble(
        VulkanAdvancedSceneResourceRuntime runtime,
        bool diagnosticBounds = false)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        if (!runtime.IsReady)
            throw new InvalidOperationException(runtime.AvailabilityReason);

        return AdvancedShaderAccessLibrary.BuildPreamble(
            RuntimeGraphicsApiKind.Vulkan,
            runtime.TextureIndirectionMode,
            diagnosticBounds,
            GlobalSetIndex,
            ResourceSetIndex,
            runtime.DescriptorCapacity);
    }

    internal static bool IsExternallyOwnedSet(uint mask, uint setIndex)
        => setIndex < 32u && (mask & (1u << (int)setIndex)) != 0u;

    internal static bool IsSupportedExternalSetMask(uint mask)
        => mask != 0u && (mask & ~ExternallyOwnedSetMask) == 0u;

    private static bool ContainsGlobalStorageBinding(uint binding)
    {
        for (int index = 0; index < GlobalStorageBindings.Length; ++index)
            if (GlobalStorageBindings[index] == binding)
                return true;

        return false;
    }

    private static bool ContainsVisibilityStorageBinding(uint binding)
        => binding is VisibilityCandidatesBinding or
            VisibilityPersistentStateBinding or
            VisibilityDeferredIndicesBinding or
            VisibilityVisibleIndicesBinding or
            VisibilityPayloadBinding or
            VisibilityProducersBinding or
            VisibilityRangeIndicesBinding or
            VisibilityRangeOffsetsBinding or
            VisibilityRangeCountsBinding or
            VisibilityCountersBinding or
            VisibilityIndexedArgumentsBinding or
            VisibilityMeshArgumentsBinding or
            VisibilityMeshPayloadsBinding or
            VisibilityStaticVerticesBinding or
            VisibilityCurrentVerticesBinding or
            VisibilityPreviousVerticesBinding or
            VisibilityMeshletDescriptorsBinding or
            VisibilityMeshletVertexIndicesBinding or
            VisibilityMeshletTriangleWordsBinding or
            VisibilityLateVisibleIndicesBinding or
            VisibilityLateRangeCountsBinding or
            VisibilityLateIndexedArgumentsBinding or
            VisibilityLateMeshArgumentsBinding or
            VisibilityLateMeshPayloadsBinding;
}
