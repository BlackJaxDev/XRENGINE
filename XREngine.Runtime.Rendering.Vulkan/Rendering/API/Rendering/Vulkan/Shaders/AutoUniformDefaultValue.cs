using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Vulkan;

public readonly record struct AutoUniformDefaultValue(
    EShaderVarType Type,
    object Value);
