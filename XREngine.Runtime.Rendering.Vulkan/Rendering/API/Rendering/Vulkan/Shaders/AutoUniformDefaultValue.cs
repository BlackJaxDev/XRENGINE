using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Vulkan;

internal readonly record struct AutoUniformDefaultValue(
    EShaderVarType Type,
    object Value);
