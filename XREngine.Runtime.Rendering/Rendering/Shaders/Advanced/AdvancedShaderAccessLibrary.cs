using System.Globalization;
using System.Text;

namespace XREngine.Rendering.Shaders;

/// <summary>
/// Builds the backend preamble for the logically shared advanced GLSL access
/// library. Insert it immediately after a shader's <c>#version</c> directive.
/// </summary>
public static class AdvancedShaderAccessLibrary
{
    public const string IncludePath = "Advanced/Access/AdvancedAccess.glslinc";

    public static string BuildPreamble(
        RuntimeGraphicsApiKind backend,
        EAdvancedTextureIndirectionMode textureEncoding,
        bool diagnosticBounds = false,
        uint descriptorSet = 0u,
        uint? resourceDescriptorSet = null,
        uint vulkanResourceDescriptorCapacity = 1024u)
    {
        ValidateBackendEncoding(backend, textureEncoding);
        if (backend == RuntimeGraphicsApiKind.Vulkan &&
            textureEncoding is EAdvancedTextureIndirectionMode.VulkanDescriptorIndexing or
                EAdvancedTextureIndirectionMode.VulkanDescriptorHeap &&
            vulkanResourceDescriptorCapacity == 0u)
        {
            throw new ArgumentOutOfRangeException(
                nameof(vulkanResourceDescriptorCapacity),
                "Vulkan advanced resource descriptor capacity must be greater than zero.");
        }

        StringBuilder source = new(4096);
        AppendRequiredExtensions(source, backend, textureEncoding);
        source.Append(AdvancedShaderRecordLayout.BuildCpuLayoutDefines());
        AdvancedStandardMaterialShaderContract.AppendDefines(source);
        AppendDefine(source, "XR_ADV_GLOBAL_SET", descriptorSet);
        AppendDefine(
            source,
            "XR_ADV_RESOURCE_SET",
            resourceDescriptorSet ?? descriptorSet);
        AppendDefine(source, "XR_ADV_VISIBILITY_PAYLOAD_VERSION", AdvancedVisibilityBufferContract.PayloadVersion);
        AppendDefine(source, "XR_ADV_SURFACE_CONTRACT_VERSION", AdvancedSurfaceContract.ContractVersion);
        AppendDefine(source, "XR_ADV_BINDING_DRAWS", AdvancedGlobalResourceBindings.Draws);
        AppendDefine(source, "XR_ADV_BINDING_INSTANCES", AdvancedGlobalResourceBindings.Instances);
        AppendDefine(source, "XR_ADV_BINDING_MESHES", AdvancedGlobalResourceBindings.Meshes);
        AppendDefine(source, "XR_ADV_BINDING_MATERIALS", AdvancedGlobalResourceBindings.Materials);
        AppendDefine(source, "XR_ADV_BINDING_VIEWS", AdvancedGlobalResourceBindings.Views);
        AppendDefine(source, "XR_ADV_BINDING_LIGHTS", AdvancedGlobalResourceBindings.Lights);
        AppendDefine(source, "XR_ADV_BINDING_SHADOWS", AdvancedGlobalResourceBindings.Shadows);
        AppendDefine(source, "XR_ADV_BINDING_TEXTURES", AdvancedGlobalResourceBindings.Textures);
        AppendDefine(source, "XR_ADV_BINDING_SAMPLERS", AdvancedGlobalResourceBindings.Samplers);
        AppendDefine(source, "XR_ADV_BINDING_DEFORMATIONS", AdvancedGlobalResourceBindings.Deformations);
        AppendDefine(source, "XR_ADV_BINDING_DIAGNOSTICS", AdvancedGlobalResourceBindings.Diagnostics);
        AppendDefine(source, "XR_ADV_BINDING_MATERIAL_CONSTANTS", AdvancedGlobalResourceBindings.MaterialConstants);
        AppendDefine(source, "XR_ADV_BINDING_MATERIAL_TEXTURE_BINDINGS", AdvancedGlobalResourceBindings.MaterialTextureBindings);
        AppendDefine(source, "XR_ADV_BINDING_PROBES", AdvancedGlobalResourceBindings.Probes);
        AppendDefine(source, "XR_ADV_BINDING_ENVIRONMENTS", AdvancedGlobalResourceBindings.Environments);
        AppendDefine(source, "XR_ADV_BINDING_DECALS", AdvancedGlobalResourceBindings.Decals);
        AppendDefine(source, "XR_ADV_BINDING_GI_RESOURCES", AdvancedGlobalResourceBindings.GiResources);
        AppendDefine(source, "XR_ADV_BINDING_TRANSFORMS", AdvancedGlobalResourceBindings.Transforms);
        AppendDefine(source, "XR_ADV_BINDING_RENDER_STATES", AdvancedGlobalResourceBindings.RenderStates);
        AppendDefine(source, "XR_ADV_BINDING_ENCODED_TEXTURES", AdvancedGlobalResourceBindings.EncodedTextures);
        AppendDefine(source, "XR_ADV_BINDING_ENCODED_SAMPLERS", AdvancedGlobalResourceBindings.EncodedSamplers);
        AppendDefine(source, "XR_ADV_BINDING_SHADING_KERNELS", AdvancedGlobalResourceBindings.ShadingKernels);
        AppendDefine(source, "XR_ADV_BINDING_MATERIAL_LAYOUTS", AdvancedGlobalResourceBindings.MaterialLayouts);
        AppendDefine(source, "XR_ADV_BINDING_EDITOR_IDENTITIES", AdvancedGlobalResourceBindings.EditorIdentities);
        AppendDefine(source, "XR_ADV_BINDING_TEXTURE_DESCRIPTORS", AdvancedGlobalResourceBindings.TextureDescriptors);
        AppendDefine(source, "XR_ADV_BINDING_SAMPLER_DESCRIPTORS", AdvancedGlobalResourceBindings.SamplerDescriptors);
        AppendDefine(source, "XR_ADV_BINDING_TEXTURE_2D_ARRAY_DESCRIPTORS", AdvancedGlobalResourceBindings.Texture2DArrayDescriptors);
        AppendDefine(source, "XR_ADV_BINDING_TEXTURE_CUBE_DESCRIPTORS", AdvancedGlobalResourceBindings.TextureCubeDescriptors);
        AppendDefine(source, "XR_ADV_BINDING_TEXTURE_ARRAY", AdvancedGlobalResourceBindings.TextureArray);
        AppendDefine(source, "XR_ADV_BINDING_HANDLE_LOOKUPS", AdvancedGlobalResourceBindings.HandleLookups);
        if (backend == RuntimeGraphicsApiKind.OpenGL)
            AppendOpenGlVisibilityBindingDefines(source);
        bool vulkan = backend == RuntimeGraphicsApiKind.Vulkan;
        AppendDefine(
            source,
            "XR_ADV_BINDING_STATIC_VERTICES",
            vulkan
                ? AdvancedReconstructionShaderBindings.VulkanStaticVertices
                : AdvancedReconstructionShaderBindings.StaticVertices);
        AppendDefine(
            source,
            "XR_ADV_BINDING_PRESKINNED_CURRENT_VERTICES",
            vulkan
                ? AdvancedReconstructionShaderBindings.VulkanPreSkinnedCurrentVertices
                : AdvancedReconstructionShaderBindings.PreSkinnedCurrentVertices);
        AppendDefine(
            source,
            "XR_ADV_BINDING_PRESKINNED_PREVIOUS_VERTICES",
            vulkan
                ? AdvancedReconstructionShaderBindings.VulkanPreSkinnedPreviousVertices
                : AdvancedReconstructionShaderBindings.PreSkinnedPreviousVertices);
        if (vulkan)
            AppendDefine(
                source,
                "XR_ADV_BINDING_PREPARED_DRAW_DEFORMATIONS",
                AdvancedReconstructionShaderBindings.VulkanPreparedDrawDeformations);
        AppendDefine(
            source,
            "XR_ADV_BINDING_RECONSTRUCTION_INDICES",
            vulkan
                ? AdvancedReconstructionShaderBindings.VulkanIndices
                : AdvancedReconstructionShaderBindings.Indices);
        AppendDefine(
            source,
            "XR_ADV_BINDING_RECONSTRUCTION_COUNTERS",
            vulkan
                ? AdvancedReconstructionShaderBindings.VulkanCounters
                : AdvancedReconstructionShaderBindings.Counters);
        source.AppendLine(backend == RuntimeGraphicsApiKind.OpenGL
            ? "#define XR_ADV_BACKEND_OPENGL 1"
            : "#define XR_ADV_BACKEND_VULKAN 1");
        source.Append("#define XR_ADV_TEXTURE_MODE_")
            .AppendLine(GetTextureModeDefine(textureEncoding));
        if (backend == RuntimeGraphicsApiKind.Vulkan &&
            textureEncoding is EAdvancedTextureIndirectionMode.VulkanDescriptorIndexing or
                EAdvancedTextureIndirectionMode.VulkanDescriptorHeap)
        {
            AppendDefine(
                source,
                "XR_ADV_RESOURCE_DESCRIPTOR_CAPACITY",
                vulkanResourceDescriptorCapacity);
        }
        if (diagnosticBounds)
            source.AppendLine("#define XR_ADV_DIAGNOSTIC_BOUNDS 1");

        source.Append("#include \"").Append(IncludePath).AppendLine("\"");
        return source.ToString();
    }

    private static void ValidateBackendEncoding(
        RuntimeGraphicsApiKind backend,
        EAdvancedTextureIndirectionMode textureEncoding)
    {
        if (backend is not RuntimeGraphicsApiKind.OpenGL and not RuntimeGraphicsApiKind.Vulkan)
            throw new ArgumentOutOfRangeException(nameof(backend));

        bool valid = textureEncoding switch
        {
            EAdvancedTextureIndirectionMode.None or
            EAdvancedTextureIndirectionMode.TextureArray => true,
            EAdvancedTextureIndirectionMode.OpenGlBindlessHandles =>
                backend == RuntimeGraphicsApiKind.OpenGL,
            EAdvancedTextureIndirectionMode.VulkanDescriptorIndexing or
            EAdvancedTextureIndirectionMode.VulkanDescriptorHeap =>
                backend == RuntimeGraphicsApiKind.Vulkan,
            _ => false,
        };
        if (!valid)
            throw new ArgumentException(
                $"Texture encoding {textureEncoding} is not valid for {backend}.",
                nameof(textureEncoding));
    }

    private static void AppendRequiredExtensions(
        StringBuilder source,
        RuntimeGraphicsApiKind backend,
        EAdvancedTextureIndirectionMode textureEncoding)
    {
        if (backend == RuntimeGraphicsApiKind.OpenGL &&
            textureEncoding == EAdvancedTextureIndirectionMode.OpenGlBindlessHandles)
        {
            source.AppendLine("#extension GL_ARB_bindless_texture : require");
            source.AppendLine("#extension GL_ARB_gpu_shader_int64 : require");
            return;
        }

        if (backend == RuntimeGraphicsApiKind.Vulkan &&
            textureEncoding is EAdvancedTextureIndirectionMode.TextureArray or
                EAdvancedTextureIndirectionMode.VulkanDescriptorIndexing or
                EAdvancedTextureIndirectionMode.VulkanDescriptorHeap)
        {
            source.AppendLine("#extension GL_EXT_nonuniform_qualifier : require");
        }
    }

    private static string GetTextureModeDefine(EAdvancedTextureIndirectionMode textureEncoding)
        => textureEncoding switch
        {
            EAdvancedTextureIndirectionMode.None => "NONE 1",
            EAdvancedTextureIndirectionMode.TextureArray => "ARRAY 1",
            EAdvancedTextureIndirectionMode.OpenGlBindlessHandles => "OPENGL_BINDLESS 1",
            EAdvancedTextureIndirectionMode.VulkanDescriptorIndexing => "VULKAN_INDEXING 1",
            EAdvancedTextureIndirectionMode.VulkanDescriptorHeap => "VULKAN_HEAP 1",
            _ => throw new ArgumentOutOfRangeException(nameof(textureEncoding)),
        };

    private static void AppendDefine(StringBuilder source, string name, uint value)
        => source
            .Append("#define ")
            .Append(name)
            .Append(' ')
            .Append(value.ToString(CultureInfo.InvariantCulture))
            .AppendLine();

    // Vulkan places these tables in set 1, where their numeric bindings may
    // overlap the global set. OpenGL has one SSBO binding namespace, so reserve
    // a disjoint range above the global tables and reconstruction inputs.
    private static void AppendOpenGlVisibilityBindingDefines(StringBuilder source)
    {
        const uint first = 48u;
        AppendDefine(source, "XR_ADV_BINDING_VISIBILITY_CANDIDATES", first + 0u);
        AppendDefine(source, "XR_ADV_BINDING_VISIBILITY_PERSISTENT_STATE", first + 1u);
        AppendDefine(source, "XR_ADV_BINDING_VISIBILITY_DEFERRED_INDICES", first + 2u);
        AppendDefine(source, "XR_ADV_BINDING_VISIBILITY_VISIBLE_INDICES", first + 3u);
        AppendDefine(source, "XR_ADV_BINDING_VISIBILITY_PAYLOADS", first + 4u);
        AppendDefine(source, "XR_ADV_BINDING_VISIBILITY_PRODUCERS", first + 5u);
        AppendDefine(source, "XR_ADV_BINDING_VISIBILITY_RANGE_INDICES", first + 6u);
        AppendDefine(source, "XR_ADV_BINDING_VISIBILITY_RANGE_OFFSETS", first + 7u);
        AppendDefine(source, "XR_ADV_BINDING_VISIBILITY_RANGE_COUNTS", first + 8u);
        AppendDefine(source, "XR_ADV_BINDING_VISIBILITY_COUNTERS", first + 9u);
        AppendDefine(source, "XR_ADV_BINDING_VISIBILITY_INDEXED_ARGUMENTS", first + 10u);
        AppendDefine(source, "XR_ADV_BINDING_VISIBILITY_MESH_ARGUMENTS", first + 11u);
        AppendDefine(source, "XR_ADV_BINDING_VISIBILITY_MESH_PAYLOADS", first + 12u);
        AppendDefine(source, "XR_ADV_BINDING_VISIBILITY_STATIC_VERTICES", first + 13u);
        AppendDefine(source, "XR_ADV_BINDING_VISIBILITY_CURRENT_VERTICES", first + 14u);
        AppendDefine(source, "XR_ADV_BINDING_VISIBILITY_PREVIOUS_VERTICES", first + 15u);
        AppendDefine(source, "XR_ADV_BINDING_VISIBILITY_MESHLET_DESCRIPTORS", first + 16u);
        AppendDefine(source, "XR_ADV_BINDING_VISIBILITY_MESHLET_VERTEX_INDICES", first + 17u);
        AppendDefine(source, "XR_ADV_BINDING_VISIBILITY_MESHLET_TRIANGLE_WORDS", first + 18u);
        AppendDefine(source, "XR_ADV_BINDING_PREPARED_DRAW_DEFORMATIONS", first + 19u);
        AppendDefine(source, "XR_ADV_BINDING_VISIBILITY_LATE_VISIBLE_INDICES", first + 20u);
        AppendDefine(source, "XR_ADV_BINDING_VISIBILITY_LATE_RANGE_COUNTS", first + 21u);
        AppendDefine(source, "XR_ADV_BINDING_VISIBILITY_LATE_INDEXED_ARGUMENTS", first + 22u);
        // Sampler and image units are separate from shader-storage bindings.
        AppendDefine(source, "XR_ADV_BINDING_VISIBILITY_DEPTH_PYRAMID_SAMPLED", 4u);
        AppendDefine(source, "XR_ADV_BINDING_VISIBILITY_DEPTH_PYRAMID_STORAGE", 5u);
        AppendDefine(source, "XR_ADV_BINDING_VISIBILITY_LATE_MESH_ARGUMENTS", first + 25u);
        AppendDefine(source, "XR_ADV_BINDING_VISIBILITY_LATE_MESH_PAYLOADS", first + 26u);
    }
}
