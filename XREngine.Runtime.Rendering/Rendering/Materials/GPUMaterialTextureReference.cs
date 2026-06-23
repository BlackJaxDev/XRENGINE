namespace XREngine.Rendering.Materials
{
    public readonly record struct GPUMaterialTextureReference(EGPUMaterialTextureReferenceKind Kind, ulong Payload)
    {
        public static GPUMaterialTextureReference None => default;

        public static GPUMaterialTextureReference FromOpenGLBindlessHandle(ulong handle)
            => handle == 0ul
                ? None
                : new GPUMaterialTextureReference(EGPUMaterialTextureReferenceKind.OpenGLBindlessHandle, handle);

        public static GPUMaterialTextureReference FromVulkanDescriptorIndex(uint descriptorIndex)
            => descriptorIndex == GPUMaterialTable.InvalidTextureHandleIndex
                ? None
                : new GPUMaterialTextureReference(EGPUMaterialTextureReferenceKind.VulkanDescriptorIndex, descriptorIndex);

        public uint VulkanDescriptorIndex
            => Kind == EGPUMaterialTextureReferenceKind.VulkanDescriptorIndex
                ? checked((uint)Payload)
                : GPUMaterialTable.InvalidTextureHandleIndex;
    }
}
