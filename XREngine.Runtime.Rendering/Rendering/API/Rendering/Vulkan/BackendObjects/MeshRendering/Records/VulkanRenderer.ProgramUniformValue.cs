using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    internal readonly record struct ProgramUniformValue(EShaderVarType Type, object Value, bool IsArray);
}