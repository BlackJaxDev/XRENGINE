namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    public partial class VkRenderProgram
    {
        // Auto-uniform storage must be unique per recorded dispatch. Reusing one
        // buffer for several dispatches from the same program makes every command
        // observe the final snapshot written while the command buffer was built.
        private readonly record struct ComputeUniformBufferKey(
            EComputeUniformBufferKind Kind,
            uint ImageIndex,
            uint Set,
            uint Binding,
            string Name,
            ulong DispatchKey);
    }
}
