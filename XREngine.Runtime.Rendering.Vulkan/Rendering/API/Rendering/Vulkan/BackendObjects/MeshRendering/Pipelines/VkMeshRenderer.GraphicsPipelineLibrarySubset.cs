namespace XREngine.Rendering.Vulkan;


internal unsafe partial class VkMeshRenderer
{
    internal enum GraphicsPipelineLibrarySubset : byte
    {
        VertexInputInterface,
        PreRasterizationShaders,
        FragmentShader,
        FragmentOutputInterface,
    }
}
