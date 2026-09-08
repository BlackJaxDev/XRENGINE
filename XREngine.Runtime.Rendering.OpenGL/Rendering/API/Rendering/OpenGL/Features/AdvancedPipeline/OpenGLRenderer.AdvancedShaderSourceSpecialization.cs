namespace XREngine.Rendering.OpenGL;

public partial class OpenGLRenderer
{
    private static readonly HashSet<string> OpenGlAdvancedKnownMacros =
    [
        "XR_ADV_BACKEND_OPENGL",
        "XR_ADV_BACKEND_VULKAN",
        "XR_ADV_BACKEND_DX12",
        "XR_ADV_TEXTURE_MODE_NONE",
        "XR_ADV_TEXTURE_MODE_ARRAY",
        "XR_ADV_TEXTURE_MODE_OPENGL_BINDLESS",
        "XR_ADV_TEXTURE_MODE_VULKAN_INDEXING",
        "XR_ADV_TEXTURE_MODE_VULKAN_HEAP",
        "XR_ADV_INDEXED_ONLY",
    ];

    private static readonly HashSet<string> OpenGlAdvancedDefinedMacros =
    [
        "XR_ADV_BACKEND_OPENGL",
        "XR_ADV_TEXTURE_MODE_OPENGL_BINDLESS",
        "XR_ADV_INDEXED_ONLY",
    ];

    /// <summary>
    /// Specializes resolved Advanced GLSL to the GL indexed producer before
    /// dead-source trimming. Only the backend and texture-mode facts emitted
    /// by this runtime are evaluated; all unrelated conditional structure is
    /// left for the GLSL compiler.
    /// </summary>
    internal static string SpecializeOpenGlAdvancedIndexedSource(string source)
        => ResolvedShaderSourceOptimizer.PruneKnownConditionalBlocks(
            source,
            OpenGlAdvancedKnownMacros,
            OpenGlAdvancedDefinedMacros);
}
