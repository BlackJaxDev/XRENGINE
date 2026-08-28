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
    internal const uint ExternallyOwnedSetMask =
        (1u << (int)GlobalSetIndex) |
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
        out string reason)
    {
        for (int expectedIndex = 0;
             expectedIndex < GlobalStorageBindings.Length;
             ++expectedIndex)
        {
            uint expectedBinding = GlobalStorageBindings[expectedIndex];
            if (!TryFindBinding(
                    bindings,
                    GlobalSetIndex,
                    expectedBinding,
                    out DescriptorBindingInfo reflected) ||
                reflected.DescriptorType != DescriptorType.StorageBuffer ||
                reflected.Count != 1u)
            {
                reason =
                    $"advanced global set {GlobalSetIndex} binding {expectedBinding} must be one storage buffer";
                return false;
            }
        }

        if (!TryValidateResourceBinding(
                bindings,
                AdvancedGlobalResourceBindings.TextureDescriptors,
                DescriptorType.SampledImage,
                resourceDescriptorCapacity,
                out reason) ||
            !TryValidateResourceBinding(
                bindings,
                AdvancedGlobalResourceBindings.SamplerDescriptors,
                DescriptorType.Sampler,
                resourceDescriptorCapacity,
                out reason))
        {
            return false;
        }

        for (int index = 0; index < bindings.Count; ++index)
        {
            DescriptorBindingInfo binding = bindings[index];
            if (binding.Set == GlobalSetIndex &&
                !ContainsGlobalStorageBinding(binding.Binding))
            {
                reason =
                    $"advanced global set {GlobalSetIndex} contains unsupported binding {binding.Binding}";
                return false;
            }
            if (binding.Set == ResourceSetIndex &&
                binding.Binding != AdvancedGlobalResourceBindings.TextureDescriptors &&
                binding.Binding != AdvancedGlobalResourceBindings.SamplerDescriptors)
            {
                reason =
                    $"advanced resource set {ResourceSetIndex} contains unsupported binding {binding.Binding}";
                return false;
            }
        }

        reason = "Ready";
        return true;
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

    private static bool TryValidateResourceBinding(
        IReadOnlyList<DescriptorBindingInfo> bindings,
        uint expectedBinding,
        DescriptorType expectedType,
        uint expectedCount,
        out string reason)
    {
        if (!TryFindBinding(
                bindings,
                ResourceSetIndex,
                expectedBinding,
                out DescriptorBindingInfo reflected) ||
            reflected.DescriptorType != expectedType ||
            reflected.Count != expectedCount)
        {
            reason =
                $"advanced resource set {ResourceSetIndex} binding {expectedBinding} must be {expectedType} x{expectedCount}";
            return false;
        }

        reason = "Ready";
        return true;
    }

    private static bool TryFindBinding(
        IReadOnlyList<DescriptorBindingInfo> bindings,
        uint set,
        uint binding,
        out DescriptorBindingInfo result)
    {
        for (int index = 0; index < bindings.Count; ++index)
            if (bindings[index].Set == set && bindings[index].Binding == binding)
            {
                result = bindings[index];
                return true;
            }

        result = default;
        return false;
    }

    private static bool ContainsGlobalStorageBinding(uint binding)
    {
        for (int index = 0; index < GlobalStorageBindings.Length; ++index)
            if (GlobalStorageBindings[index] == binding)
                return true;

        return false;
    }
}
