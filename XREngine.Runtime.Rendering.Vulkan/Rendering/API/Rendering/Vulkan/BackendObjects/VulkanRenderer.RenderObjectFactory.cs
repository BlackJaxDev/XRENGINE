using System;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Resources;

namespace XREngine.Rendering.Vulkan
{
    public partial class VulkanRenderer
    {
        private readonly VulkanBackendObjectRegistry _backendObjectRegistry = new();
        private VulkanBackendObjectContext? _backendObjectContext;
        internal VulkanBackendObjectRegistry BackendObjectRegistry =>
            _backendObjectRegistry;
        internal VulkanBackendObjectContext BackendObjectContext =>
            _backendObjectContext ??= new VulkanBackendObjectContext(
                _deviceContext,
                _backendObjectRegistry);

        protected override AbstractRenderAPIObject CreateAPIRenderObject(GenericRenderObject renderObject)
            => renderObject switch
            {
                //Meshes
                XRMaterial data => new VkMaterial(this, data),
                XRMeshRenderer.BaseVersion data => new VkMeshRenderer(this, data),
                XRRenderProgramPipeline data => new VkRenderProgramPipeline(this, data),
                XRRenderProgram data => new VkRenderProgram(this, data),
                XRDataBuffer data => new VkDataBuffer(this, data),
                XRSampler s => new VkSampler(this, s),
                XRShader s => new VkShader(this, s),

                //FBOs
                XRRenderBuffer data => new VkRenderBuffer(this, data),
                XRFrameBuffer data => new VkFrameBuffer(this, data),

                //Texture 1D
                XRTexture1D data => new VkTexture1D(this, data),
                XRTexture1DArray data => new VkTexture1DArray(this, data),
                XRTextureViewBase data => new VkTextureView(this, data),

                //Texture 2D
                XRTexture2D data => new VkTexture2D(this, data),
                XRTexture2DArray data => new VkTexture2DArray(this, data),
                XRTextureRectangle data => new VkTextureRectangle(this, data),

                //Texture 3D
                XRTexture3D data => new VkTexture3D(this, data),

                //Texture Cube
                XRTextureCube data => new VkTextureCube(this, data),
                XRTextureCubeArray data => new VkTextureCubeArray(this, data),

                //Texture Buffer
                XRTextureBuffer data => new VkTextureBuffer(this, data),

                //Feedback
                XRRenderQuery data => new VkRenderQuery(this, data),
                XRTransformFeedback data => new VkTransformFeedback(this, data),

                _ => throw new InvalidOperationException($"Render object type {renderObject.GetType()} is not supported.")
            };
    }
}
