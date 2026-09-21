using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Identifies the native pipeline-compilation dependencies replaced by one mutation.
/// An empty scope is reserved for device-wide invalidation.
/// </summary>
internal readonly record struct VulkanPipelineCompilationMutationScope(
    VkRenderProgram? Program,
    ulong ShaderModuleHandle,
    ulong PipelineLayoutHandle)
{
    internal bool IsDeviceWide
        => Program is null && ShaderModuleHandle == 0 && PipelineLayoutHandle == 0;

    internal string Description
        => IsDeviceWide
            ? "DeviceWide"
            : Program is not null
                ? "Program"
                : ShaderModuleHandle != 0
                    ? "ShaderModule"
                    : "PipelineLayout";

    internal static VulkanPipelineCompilationMutationScope ForProgram(VkRenderProgram program)
        => new(program, 0, program.PipelineLayout.Handle);

    internal static VulkanPipelineCompilationMutationScope ForShader(ShaderModule shaderModule)
        => new(null, shaderModule.Handle, 0);

    internal bool Matches(VulkanGraphicsPipelineBuildRequest request)
    {
        if (IsDeviceWide || Program is not null && ReferenceEquals(request.Program, Program))
            return true;
        if (PipelineLayoutHandle != 0 && request.PipelineLayout.Handle == PipelineLayoutHandle)
            return true;
        return ShaderModuleHandle != 0 &&
               (ContainsShaderModule(request.GraphicsStages) ||
                ContainsShaderModule(request.PreRasterStages) ||
                ContainsShaderModule(request.FragmentStages));
    }

    internal bool Matches(VulkanComputePipelineBuildRequest request)
        => IsDeviceWide ||
           Program is not null && ReferenceEquals(request.Program, Program) ||
           PipelineLayoutHandle != 0 && request.PipelineLayout.Handle == PipelineLayoutHandle ||
           ShaderModuleHandle != 0 && request.ComputeStage.Module.Handle == ShaderModuleHandle;

    internal bool Matches(VkRenderProgram program)
        => IsDeviceWide ||
           Program is not null && ReferenceEquals(program, Program) ||
           PipelineLayoutHandle != 0 && program.PipelineLayout.Handle == PipelineLayoutHandle ||
           ShaderModuleHandle != 0 && program.UsesShaderModule(ShaderModuleHandle);

    private bool ContainsShaderModule(PipelineShaderStageCreateInfo[] stages)
    {
        for (int stageIndex = 0; stageIndex < stages.Length; stageIndex++)
            if (stages[stageIndex].Module.Handle == ShaderModuleHandle)
                return true;
        return false;
    }
}