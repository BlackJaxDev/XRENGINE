using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Vulkan;

internal sealed record AutoUniformBlockInfo(
    string BlockName,
    string InstanceName,
    uint Set,
    uint Binding,
    uint Size,
    IReadOnlyList<AutoUniformMember> Members,
    EShaderType ShaderType,
    EVulkanBindingFrequency Frequency = EVulkanBindingFrequency.Unknown);
