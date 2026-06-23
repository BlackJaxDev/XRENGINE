namespace XREngine.Rendering.Materials
{
    public enum EGPUMaterialTextureReferenceKind : byte
    {
        None = 0,
        OpenGLBindlessHandle,
        VulkanDescriptorIndex,
    }
}
