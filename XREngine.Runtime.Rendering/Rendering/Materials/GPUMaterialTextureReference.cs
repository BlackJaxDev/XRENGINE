namespace XREngine.Rendering.Materials
{
    public readonly record struct GPUMaterialTextureReference(
        EGPUMaterialTextureReferenceKind Kind,
        ulong Payload,
        uint VulkanDescriptorGeneration = 0u)
    {
        public static GPUMaterialTextureReference None => default;

        public static GPUMaterialTextureReference FromOpenGLBindlessHandle(ulong handle)
            => handle == 0ul
                ? None
                : new GPUMaterialTextureReference(EGPUMaterialTextureReferenceKind.OpenGLBindlessHandle, handle);

        public static GPUMaterialTextureReference FromVulkanDescriptorIndex(
            uint descriptorIndex,
            uint descriptorGeneration = 0u)
            => descriptorIndex == GPUMaterialTable.InvalidTextureHandleIndex
                ? None
                : new GPUMaterialTextureReference(
                    EGPUMaterialTextureReferenceKind.VulkanDescriptorIndex,
                    descriptorIndex,
                    descriptorGeneration);

        public uint VulkanDescriptorIndex
            => Kind == EGPUMaterialTextureReferenceKind.VulkanDescriptorIndex
                ? checked((uint)Payload)
                : GPUMaterialTable.InvalidTextureHandleIndex;
    }
}
