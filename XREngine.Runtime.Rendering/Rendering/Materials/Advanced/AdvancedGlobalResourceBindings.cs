namespace XREngine.Rendering;

/// <summary>
/// Stable SSBO bindings used by the shared advanced shader access library.
/// Vulkan places them in the advanced global descriptor set; OpenGL consumes
/// the same numeric binding points.
/// </summary>
public static class AdvancedGlobalResourceBindings
{
    public const uint Draws = 0u;
    public const uint Instances = 1u;
    public const uint Meshes = 2u;
    public const uint Materials = 3u;
    public const uint Views = 4u;
    public const uint Lights = 5u;
    public const uint Shadows = 6u;
    public const uint Textures = 7u;
    public const uint Samplers = 8u;
    public const uint Deformations = 9u;
    public const uint Diagnostics = 10u;
    public const uint MaterialConstants = 11u;
    public const uint MaterialTextureBindings = 12u;
    public const uint Probes = 13u;
    public const uint Environments = 14u;
    public const uint Decals = 15u;
    public const uint GiResources = 16u;
    public const uint Transforms = 17u;
    public const uint RenderStates = 18u;
    public const uint EncodedTextures = 19u;
    public const uint EncodedSamplers = 20u;
    public const uint ShadingKernels = 21u;
    public const uint MaterialLayouts = 22u;
    public const uint EditorIdentities = 23u;
    public const uint TextureDescriptors = 24u;
    public const uint SamplerDescriptors = 25u;
    public const uint TextureArray = 26u;
    public const uint HandleLookups = 27u;
}
