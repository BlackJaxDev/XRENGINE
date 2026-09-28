namespace XREngine.Rendering.Shaders.Compilation;

/// <summary>
/// Identifies the format and backend target of a compiled shader artifact.
/// </summary>
public enum ShaderCompileTarget
{
    Vulkan14Spirv16,
    WebGPUWgsl,
}
