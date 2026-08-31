using XREngine.Rendering.Materials;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Non-owning bindless descriptor metadata. The immutable row token is transferred into the owning
/// sealed dispatch snapshot; keeping it here would make authoring-operation clones share IDisposable state.
/// </summary>
internal sealed class VulkanBindlessMaterialDescriptorBinding : IDisposable
{
    private readonly GPUMaterialTablePublication? _publication;

    internal VulkanBindlessMaterialDescriptorBinding(
        VkRenderProgram program,
        string consumer,
        GPUMaterialTablePublication? publication)
        => (Program, Consumer, _publication) = (program, consumer, publication);

    internal VkRenderProgram Program { get; }
    internal string Consumer { get; }
    internal GPUMaterialTablePublication? Publication => _publication;

    internal VulkanBindlessMaterialDescriptorBinding Retain()
        => new(Program, Consumer, _publication);

    public void Dispose() { }
}
