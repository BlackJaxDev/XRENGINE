namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{

    public partial class VkMeshRenderer
    {
        internal enum GraphicsPipelineLibrarySubset : byte
        {
            VertexInputInterface,
            PreRasterizationShaders,
            FragmentShader,
            FragmentOutputInterface,
        }
    }
}